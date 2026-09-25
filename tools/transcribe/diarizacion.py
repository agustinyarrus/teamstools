"""
diarizacion.py — «quién habló cuándo», en local: el pipeline de pyannote 3.x reconstruido sobre ONNX + sherpa.

  1 SEGMENTACIÓN   pyannote-3.0 o reverb-v1 (ONNX, 1,5–2,4 MB): ventanas de 10 s cada `paso` s; por marco de ~17 ms
                   y por ventana, hasta 3 hablantes LOCALES (decodificación powerset: 7 clases → 3 etiquetas).
  2 CONTEO         cuántos hablan en cada marco = promedio, sobre las ventanas que lo cubren, de los locales activos.
  3 HUELLAS        por (ventana, local) con habla suficiente: una huella de voz (wespeaker / titanet / eres2net) sobre
                   los marcos donde habla SOLO él. Es lo caro: se calcula UNA vez (y se puede guardar en disco).
  4 AGRUPAMIENTO   aglomerativo sobre huellas unitarias. El dendrograma se arma una vez y se corta en cualquier umbral
                   gratis; los grupos chicos se reparten al grande más cercano; tope opcional de hablantes.
  5 ASIGNACIÓN     por ventana, húngaro entre sus locales y los centroides: dos locales de la MISMA ventana nunca caen
                   en el mismo grupo (hablaban a la vez: no son la misma persona).
  6 RECONSTRUCCIÓN superposición promediada de las ventanas → en cada marco, los `conteo` hablantes más activos.
  7 TURNOS         partición COMPLETA de la línea de tiempo en turnos de un solo hablante: se transcribe cada turno
                   sin perder ni un segundo de audio (los silencios se reparten entre los turnos vecinos).
"""
import hashlib
import os
import time
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
from scipy.cluster.hierarchy import fcluster, linkage
from scipy.optimize import linear_sum_assignment

SR = 16000
D = Path(os.environ.get("DIARIZACION_MODELOS", r"C:\Apps\asr-modelos\diarizacion"))
SEGMENTADORES = {"pyannote": D / "sherpa-onnx-pyannote-segmentation-3-0" / "model.int8.onnx",
                 "reverb": D / "sherpa-onnx-reverb-diarization-v1" / "model.int8.onnx"}
EXTRACTORES = {"wespeaker": D / "wespeaker_en_voxceleb_resnet34_LM.onnx",
               "titanet": D / "nemo_en_titanet_large.onnx",
               "eres2net": D / "3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx"}
HILOS = os.cpu_count() or 8
LOTE_SEGMENTACION = 32
MIN_MARCOS_HUELLA = 10          # ~0,17 s: menos que eso no da una huella confiable (igual que la referencia de sherpa)
MIN_ACTIVO_ENTRENAR = 0.2       # pyannote 3.1: solo las huellas con ≥ 20 % de la ventana hablando arman los grupos
MIN_TAM_GRUPO = 12              # pyannote 3.1; se achica a 10 % de las huellas en reuniones cortas
MIN_TURNO_S = 0.25              # un turno más corto que esto se funde con el vecino (salvo que sea el único)


# ------------------------------------------------------------------------------------------------ 1-2 segmentación

@dataclass(frozen=True)
class Segmentacion:
    etiquetas: np.ndarray       # (C, F, L) uint8: actividad de cada hablante local, por ventana y marco
    paso: int                   # muestras entre ventanas
    ventana: int                # muestras por ventana (160000 = 10 s)
    rf_paso: int                # muestras entre marcos (270 = 16,9 ms)
    rf_tam: int                 # muestras que abarca un marco (991)
    n_muestras: int

    @property
    def F(self) -> int:
        return self.etiquetas.shape[1]

    @property
    def marco_s(self) -> float:
        return self.rf_paso / SR

    @property
    def n_marcos(self) -> int:
        """Marcos globales que cubren el audio real (sin el relleno de la última ventana)."""
        return int(self.n_muestras / self.rf_paso)

    def desde(self, c: int) -> int:
        """Primer marco global de la ventana c."""
        return int(c * self.paso / self.rf_paso + 0.5)

    def tiempo(self, g) -> np.ndarray:
        """Centro, en segundos, del marco global g (escalar o arreglo)."""
        return np.asarray(g) * self.marco_s + self.rf_tam / SR / 2


def _powerset(num_clases: int, locales: int, max_simultaneos: int) -> np.ndarray:
    """Clase powerset → etiquetas: {∅, {0}, {1}, {2}, {0,1}, {0,2}, {1,2}} para 3 locales y 2 simultáneos."""
    m = np.zeros((num_clases, locales), np.uint8)
    k = 1
    for j in range(locales):
        m[k, j] = 1; k += 1
    if max_simultaneos >= 2:
        for j in range(locales):
            for i in range(j + 1, locales):
                m[k, j] = m[k, i] = 1; k += 1
    return m


def segmentar(x: np.ndarray, modelo: str = "pyannote", paso_s: float = 1.0, hilos: int = HILOS) -> Segmentacion:
    """Ventanas de 10 s cada `paso_s`, en lotes de 32 por el ONNX. O(C) inferencias; la última ventana se rellena."""
    import onnxruntime as ort
    op = ort.SessionOptions(); op.intra_op_num_threads = hilos; op.inter_op_num_threads = 1
    ses = ort.InferenceSession(str(SEGMENTADORES[modelo]), op, providers=["CPUExecutionProvider"])
    meta = ses.get_modelmeta().custom_metadata_map
    ventana, rf_paso, rf_tam = int(meta["window_size"]), int(meta["receptive_field_shift"]), int(meta["receptive_field_size"])
    mapa = _powerset(int(meta["num_classes"]), int(meta["num_speakers"]), int(meta["powerset_max_classes"]))
    paso = max(rf_paso, int(round(paso_s * SR)))
    n = len(x)
    C = 1 + max(0, int(np.ceil((n - ventana) / paso)))
    xp = np.zeros((C - 1) * paso + ventana, np.float32)
    xp[:n] = x
    vistas = np.lib.stride_tricks.sliding_window_view(xp, ventana)[::paso][:C]      # sin copiar el audio
    entrada, salida = ses.get_inputs()[0].name, ses.get_outputs()[0].name
    clases = []
    for k in range(0, C, LOTE_SEGMENTACION):
        y = ses.run([salida], {entrada: np.ascontiguousarray(vistas[k: k + LOTE_SEGMENTACION])[:, None, :]})[0]
        clases.append(np.argmax(y, axis=-1).astype(np.uint8))
    etiquetas = mapa[np.concatenate(clases)]                                           # (C, F, L)
    return Segmentacion(etiquetas, paso, ventana, rf_paso, rf_tam, n)


def _superponer(seg: Segmentacion, por_ventana: np.ndarray) -> np.ndarray:
    """
    Suma superpuesta de un valor (C, F, K) sobre la línea global, promediada por cuántas ventanas cubren cada
    marco. O(C·F·K) con cortes de numpy; recorta al audio real.
    """
    C, F = por_ventana.shape[:2]
    G = seg.desde(C - 1) + F
    acc = np.zeros((G,) + por_ventana.shape[2:], np.float32)
    cob = np.zeros(G, np.float32)
    for c in range(C):
        g = seg.desde(c)
        acc[g: g + F] += por_ventana[c]
        cob[g: g + F] += 1
    acc /= np.maximum(cob, 1)[(slice(None),) + (None,) * (acc.ndim - 1)]
    return acc[: seg.n_marcos]


def conteo(seg: Segmentacion) -> np.ndarray:
    """Hablantes simultáneos por marco global: promedio de locales activos, redondeado (pyannote)."""
    return np.rint(_superponer(seg, seg.etiquetas.sum(axis=-1).astype(np.float32))).astype(np.int8)


# ------------------------------------------------------------------------------------------------ 3 huellas

@dataclass(frozen=True)
class Huellas:
    pares: np.ndarray           # (N, 2) int32: (ventana, local)
    vectores: np.ndarray        # (N, D) float32, UNITARIOS
    activo: np.ndarray          # (N,) fracción de la ventana en que ese local habla solo
    omitidas: int               # pares con habla pero sin huella posible (demasiado cortos)
    segundos: float             # costo de calcularlas


def huellas(x: np.ndarray, seg: Segmentacion, modelo: str = "wespeaker", hilos: int = HILOS,
            max_s: float = 10.0, al_avanzar=None) -> Huellas:
    """
    Una huella por (ventana, local) sobre los marcos donde ese local habla SOLO (si no alcanzan, todos los suyos).
    Las corridas de marcos se pasan a muestras por diferencias de un arreglo booleano: O(F) por par, sin bucles
    por marco. El costo real es el modelo: O(N) inferencias.
    """
    import sherpa_onnx as so
    t0 = time.time()
    ext = so.SpeakerEmbeddingExtractor(so.SpeakerEmbeddingExtractorConfig(model=str(EXTRACTORES[modelo]),
                                                                          num_threads=hilos))
    et = seg.etiquetas
    solo = et * (et.sum(axis=-1, keepdims=True) == 1)
    C, F, L = et.shape
    tope = int(max_s * SR)
    pares, vecs, activo, omitidas = [], [], [], 0
    esc = seg.ventana / F
    cada = max(1, C // 50)                        # avance cada ~2 %: es el paso largo (una hora de reunión = minutos)
    for c in range(C):
        if al_avanzar and c % cada == 0:
            al_avanzar(c / C)
        base = c * seg.paso
        for k in range(L):
            marcos = solo[c, :, k] if solo[c, :, k].sum() >= MIN_MARCOS_HUELLA else et[c, :, k]
            if marcos.sum() < MIN_MARCOS_HUELLA:
                continue
            borde = np.diff(np.concatenate([[0], marcos.astype(np.int8), [0]]))
            ini, fin = np.flatnonzero(borde == 1), np.flatnonzero(borde == -1)
            trozos = [x[base + int(a * esc): min(len(x), base + int(b * esc))] for a, b in zip(ini, fin)]
            audio = np.concatenate(trozos)[:tope] if trozos else np.zeros(0, np.float32)
            s = ext.create_stream()
            s.accept_waveform(SR, np.ascontiguousarray(audio, dtype=np.float32))
            s.input_finished()
            if len(audio) < SR // 10 or not ext.is_ready(s):
                omitidas += 1
                continue
            pares.append((c, k)); vecs.append(ext.compute(s)); activo.append(float(solo[c, :, k].mean()))
    V = np.asarray(vecs, np.float32).reshape(len(vecs), -1)
    V /= np.maximum(np.linalg.norm(V, axis=1, keepdims=True), 1e-9)
    return Huellas(np.asarray(pares, np.int32).reshape(-1, 2), V, np.asarray(activo, np.float32), omitidas,
                   round(time.time() - t0, 1))


# ------------------------------------------------------------------------------------------------ 4-5 agrupamiento

@dataclass(frozen=True)
class Dendrograma:
    Z: np.ndarray               # matriz de enlace de scipy
    entrenar: np.ndarray        # índices (en Huellas) que lo arman
    metodo: str


def dendrograma(h: Huellas, metodo: str = "centroid") -> Dendrograma:
    """
    Aglomerativo sobre las huellas «limpias» (≥ 20 % de la ventana hablando solo). centroid/ward exigen métrica
    euclídea: sobre vectores unitarios, ‖a−b‖² = 2 − 2·cos, así que ordena igual que el coseno.
    O(N² log N) en tiempo, O(N²) en memoria (N ≈ miles: decenas de MB).
    """
    entrenar = np.flatnonzero(h.activo >= MIN_ACTIVO_ENTRENAR)
    if len(entrenar) < 2:
        entrenar = np.arange(len(h.vectores))
    X = h.vectores[entrenar]
    if len(X) < 2:
        return Dendrograma(np.zeros((0, 4)), entrenar, metodo)
    metrica = "euclidean" if metodo in ("centroid", "ward", "median") else "cosine"
    return Dendrograma(linkage(X, method=metodo, metric=metrica), entrenar, metodo)


def cortar(h: Huellas, d: Dendrograma, umbral: float, n_hablantes: int | None = None,
           max_hablantes: int | None = None) -> tuple[np.ndarray, np.ndarray]:
    """
    Corta el dendrograma (por umbral, o en exactamente n grupos), descarta los grupos chicos, y asigna CADA huella
    al centroide más parecido con la restricción de ventana (húngaro). Devuelve (grupo por par, centroides).
    """
    N = len(h.vectores)
    if N == 0:
        return np.zeros(0, np.int32), np.zeros((0, 0), np.float32)
    if len(d.entrenar) < 2:
        cent = h.vectores[:1]
        return np.zeros(N, np.int32), cent
    if n_hablantes:
        et = fcluster(d.Z, n_hablantes, criterion="maxclust") - 1
    else:
        et = fcluster(d.Z, umbral, criterion="distance") - 1
    ids, cuenta = np.unique(et, return_counts=True)
    min_tam = min(MIN_TAM_GRUPO, max(1, round(0.1 * len(d.entrenar))))
    grandes = ids[cuenta >= min_tam] if not n_hablantes else ids
    if len(grandes) == 0:
        grandes = ids[np.argsort(-cuenta)[:1]]
    if max_hablantes and len(grandes) > max_hablantes:
        et = fcluster(d.Z, max_hablantes, criterion="maxclust") - 1
        grandes = np.unique(et)
    X = h.vectores[d.entrenar]
    cent = np.stack([X[et == g].mean(axis=0) for g in grandes])
    cent /= np.maximum(np.linalg.norm(cent, axis=1, keepdims=True), 1e-9)
    return asignar(h, cent), cent


def asignar(h: Huellas, cent: np.ndarray) -> np.ndarray:
    """
    Cada huella al centroide más parecido, con la RESTRICCIÓN de que dos locales de la misma ventana no comparten
    grupo: húngaro por ventana sobre la similitud coseno (≤ 3 × K por ventana: trivial). Si una ventana tiene más
    locales que grupos, los que sobran van al más parecido sin restricción.
    """
    sim = h.vectores @ cent.T                                           # (N, K) coseno: vectores unitarios
    grupo = np.argmax(sim, axis=1).astype(np.int32)
    K = len(cent)
    if K < 2:
        return grupo
    ventanas = h.pares[:, 0]
    orden = np.argsort(ventanas, kind="stable")
    cortes = np.flatnonzero(np.diff(ventanas[orden])) + 1
    for idx in np.split(orden, cortes):
        if len(idx) < 2:
            continue
        fil, col = linear_sum_assignment(-sim[idx])
        grupo[idx[fil]] = col
    return grupo


# ------------------------------------------------------------------------------------------------ 6 reconstrucción

@dataclass(frozen=True)
class Diarizacion:
    activo: np.ndarray          # (G, K) bool: quién habla en cada marco global
    fuerza: np.ndarray          # (G, K) float: actividad promedio (para desempatar)
    marco_s: float
    desfase_s: float            # centro del marco 0
    segmentos: list = field(default_factory=list)   # [(ini, fin, hablante)] por hablante, ordenados
    turnos: list = field(default_factory=list)      # partición completa [(ini, fin, hablante)]

    @property
    def hablantes(self) -> int:
        return self.activo.shape[1]


def reconstruir(seg: Segmentacion, h: Huellas, grupo: np.ndarray, K: int, cuenta: np.ndarray,
                dur_s: float, min_on: float = 0.0, min_off: float = 0.0) -> Diarizacion:
    """
    Por ventana: actividad (F, K) = máximo de los locales asignados a cada grupo; superposición promediada;
    en cada marco se quedan los `cuenta` más activos (y solo si tienen actividad). O(C·F·K).
    """
    C, F, L = seg.etiquetas.shape
    por_ventana = np.zeros((C, F, max(K, 1)), np.float32)
    for (c, k), g in zip(h.pares, grupo):
        np.maximum(por_ventana[c, :, g], seg.etiquetas[c, :, k], out=por_ventana[c, :, g])
    fuerza = _superponer(seg, por_ventana)
    G = min(len(fuerza), len(cuenta))
    fuerza, cuenta = fuerza[:G], cuenta[:G]
    rango = np.argsort(np.argsort(-fuerza, axis=1, kind="stable"), axis=1)      # 0 = el más activo del marco
    activo = (rango < cuenta[:, None]) & (fuerza > 0)
    # renumerar por orden de aparición: «Persona 1» es la primera que habla
    primera = np.array([np.argmax(activo[:, k]) if activo[:, k].any() else G + k for k in range(activo.shape[1])])
    orden = np.argsort(primera)
    orden = orden[[activo[:, k].any() for k in orden]]
    activo, fuerza = activo[:, orden], fuerza[:, orden]
    d = Diarizacion(activo, fuerza, seg.marco_s, seg.rf_tam / SR / 2)
    segs = a_segmentos(d, min_on, min_off)
    return Diarizacion(activo, fuerza, seg.marco_s, d.desfase_s, segs, turnos(d, dur_s))


def a_segmentos(d: Diarizacion, min_on: float = 0.0, min_off: float = 0.0) -> list[tuple[float, float, int]]:
    """Marcos activos → segmentos por hablante; se cierran huecos < min_off y se tiran segmentos < min_on."""
    out = []
    for k in range(d.activo.shape[1]):
        borde = np.diff(np.concatenate([[0], d.activo[:, k].astype(np.int8), [0]]))
        ini = np.flatnonzero(borde == 1) * d.marco_s + d.desfase_s
        fin = np.flatnonzero(borde == -1) * d.marco_s + d.desfase_s
        segs = []
        for a, b in zip(ini, fin):
            if segs and a - segs[-1][1] < min_off:
                segs[-1][1] = b
            else:
                segs.append([a, b])
        out += [(round(a, 3), round(b, 3), k) for a, b in segs if b - a >= min_on]
    return sorted(out)


# ------------------------------------------------------------------------------------------------ 7 turnos

def turnos(d: Diarizacion, dur_s: float, min_turno: float = MIN_TURNO_S) -> list[tuple[float, float, int]]:
    """
    Partición COMPLETA de [0, dur] en turnos de un solo hablante. En cada marco manda el más activo; los silencios
    se parten al medio entre los turnos vecinos; los turnos de menos de `min_turno` se funden con el vecino más
    largo (los dos vecinos iguales = un parpadeo dentro de un turno). O(G) + O(T log T) al fundir.
    """
    G = len(d.activo)
    if G == 0 or not d.activo.any():
        return [(0.0, round(dur_s, 3), 0)] if dur_s > 0 else []
    lab = np.where(d.activo.any(axis=1), np.argmax(np.where(d.activo, d.fuerza, -1.0), axis=1), -1)
    hay = np.flatnonzero(lab >= 0)
    # corridas de etiqueta, ignorando silencios (un silencio entre dos tramos del mismo hablante no corta el turno)
    corr = []
    for g in hay:
        if corr and corr[-1][2] == lab[g]:
            corr[-1][1] = g
        else:
            corr.append([g, g, int(lab[g])])
    # fundir corridas muy cortas
    cambio = True
    while cambio and len(corr) > 1:
        cambio = False
        largos = [(b - a + 1) * d.marco_s for a, b, _ in corr]
        i = int(np.argmin(largos))
        if largos[i] >= min_turno:
            break
        izq = corr[i - 1] if i > 0 else None
        der = corr[i + 1] if i + 1 < len(corr) else None
        if izq and der and izq[2] == der[2]:
            izq[1] = der[1]; del corr[i: i + 2]
        elif izq and (not der or (izq[1] - izq[0]) >= (der[1] - der[0])):
            izq[1] = corr[i][1]; del corr[i]
        else:
            der[0] = corr[i][0]; del corr[i]
        # dos vecinos del mismo hablante que quedaron pegados se unen
        j = 1
        while j < len(corr):
            if corr[j][2] == corr[j - 1][2]:
                corr[j - 1][1] = corr[j][1]; del corr[j]
            else:
                j += 1
        cambio = True
    t = lambda g: float(g * d.marco_s + d.desfase_s)
    out, ini = [], 0.0
    for i, (a, b, k) in enumerate(corr):
        fin = dur_s if i == len(corr) - 1 else (t(b) + d.marco_s / 2 + t(corr[i + 1][0]) - d.marco_s / 2) / 2
        fin = max(fin, ini)
        out.append((round(ini, 3), round(fin, 3), k))
        ini = fin
    return [o for o in out if o[1] > o[0]]


# ------------------------------------------------------------------------------------------------ todo junto

@dataclass(frozen=True)
class Config:
    segmentador: str = "pyannote"
    extractor: str = "wespeaker"
    paso_s: float = 1.0
    metodo: str = "centroid"
    umbral: float = 0.70
    n_hablantes: int | None = None
    max_hablantes: int | None = None
    min_on: float = 0.0
    min_off: float = 0.0


def huella_audio(x: np.ndarray) -> str:
    return hashlib.sha1(np.ascontiguousarray(x).view(np.uint8)).hexdigest()[:16]


def preparar(x: np.ndarray, segmentador: str, extractor: str, paso_s: float, hilos: int = HILOS,
             cache: Path | None = None, al_avanzar=None) -> tuple[Segmentacion, Huellas]:
    """
    Lo CARO de la diarización (segmentación y huellas de voz), con caché en disco por huella del audio + modelos +
    paso: re-diarizar con otro umbral, otro enlace u otro tope de hablantes cuesta milisegundos. Escritura atómica.
    """
    aviso = al_avanzar or (lambda *_: None)
    clave = f"{huella_audio(x)}-{segmentador}-{extractor}-{paso_s:g}"
    archivo = (cache / f"diar-{clave}.npz") if cache else None
    if archivo and archivo.exists():
        z = np.load(archivo)
        seg = Segmentacion(z["etiquetas"], int(z["paso"]), int(z["ventana"]), int(z["rf_paso"]), int(z["rf_tam"]),
                           int(z["n"]))
        h = Huellas(z["pares"], z["vectores"], z["activo"], int(z["omitidas"]), float(z["segundos"]))
        aviso("huellas en caché", 0.9)
        return seg, h
    aviso("segmentando", 0.05)
    seg = segmentar(x, segmentador, paso_s, hilos)
    aviso("huellas de voz", 0.3)
    h = huellas(x, seg, extractor, hilos, al_avanzar=lambda f: aviso("huellas de voz", 0.3 + 0.6 * f))
    if archivo:
        archivo.parent.mkdir(parents=True, exist_ok=True)
        tmp = archivo.with_name(archivo.stem + ".tmp.npz")
        np.savez_compressed(tmp, etiquetas=seg.etiquetas, paso=seg.paso, ventana=seg.ventana, rf_paso=seg.rf_paso,
                            rf_tam=seg.rf_tam, n=seg.n_muestras, pares=h.pares, vectores=h.vectores, activo=h.activo,
                            omitidas=h.omitidas, segundos=h.segundos)
        os.replace(tmp, archivo)
    return seg, h


def diarizar(x: np.ndarray, cfg: Config = Config(), hilos: int = HILOS, cache: Path | None = None,
             al_avanzar=None) -> Diarizacion:
    """El pipeline completo: preparar (con caché) → dendrograma → corte → asignación → reconstrucción → turnos."""
    aviso = al_avanzar or (lambda *_: None)
    seg, h = preparar(x, cfg.segmentador, cfg.extractor, cfg.paso_s, hilos, cache, aviso)
    aviso("agrupando", 0.95)
    d = dendrograma(h, cfg.metodo)
    grupo, cent = cortar(h, d, cfg.umbral, cfg.n_hablantes, cfg.max_hablantes)
    return reconstruir(seg, h, grupo, len(cent), conteo(seg), len(x) / SR, cfg.min_on, cfg.min_off)
