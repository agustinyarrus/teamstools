#!/usr/bin/env python3
"""
banco_hablantes.py — elige la mejor diarización («quién habló cuándo») contra la reunión con VERDAD CONOCIDA.

Métrica: DER (diarization error rate, NIST) a 10 ms, CON solapamientos:
    DER = Σ_marcos [ max(N_ref, N_hip) − N_correctos ] / Σ N_ref
con el emparejamiento ÓPTIMO grupo ↔ hablante (húngaro sobre la co-ocurrencia) y, como es costumbre, un «collar»
de ±0,25 s alrededor de cada borde de la verdad que no se cuenta (nadie acuerda al milisegundo dónde empieza una voz).

Barrido (lo caro se calcula UNA vez por combinación; los cortes del dendrograma son gratis):
  segmentación  pyannote-3.0 · reverb-v1
  huella        wespeaker ResNet34-LM · NVIDIA TitaNet-large · 3D-Speaker ERes2Net
  enlace        centroid · average · complete, con umbrales finos
  y de referencia, con el número EXACTO de hablantes (lo que da TeamsTools si conoce a los participantes)

El umbral se elige por el DER PROMEDIO de las condiciones (limpio, teams, duro): uno que sirva en todas, no el
que mejor le queda a una sola.

  python banco_hablantes.py [--condiciones limpio,teams,duro] [--pasos 2] [--sherpa]
"""
import argparse
import itertools
import json
import sys
import time
from pathlib import Path

import numpy as np
from scipy.optimize import linear_sum_assignment

import consola as ui
import diarizacion as Dz
from motores import leer_wav

AQUI = Path(__file__).resolve().parent
PASO = 0.01
COLLAR_S = 0.25
HUECO_VERDAD_S = 0.35          # pausas más cortas que esto, dentro de un turno, cuentan como habla
# grillas finas y que BAJAN (el 23-sep el mejor quedó en el borde inferior de la grilla vieja): el corte del
# dendrograma es gratis, lo caro (huellas) va a la caché de disco
UMBRALES = {"centroid": np.round(np.arange(0.10, 0.901, 0.025), 3),
            "average": np.round(np.arange(0.04, 0.601, 0.02), 3),
            "complete": np.round(np.arange(0.08, 0.801, 0.02), 3)}
CACHE = AQUI / "banco" / "diar-cache"


# ------------------------------------------------------------------------------------------------ verdad y DER

def verdad_marcos(verdad: dict) -> tuple[np.ndarray, list[str], np.ndarray]:
    """(T, S) bool con la verdad; los segmentos salen de las palabras (pausas < 0,35 s se rellenan)."""
    hablantes = sorted({t["hablante"] for t in verdad["turnos"]})
    T = int(verdad["dur"] / PASO) + 1
    R = np.zeros((T, len(hablantes)), bool)
    bordes = []
    for t in verdad["turnos"]:
        s = hablantes.index(t["hablante"])
        segs = []
        for p in t["palabras"]:
            if segs and p["ini"] - segs[-1][1] < HUECO_VERDAD_S:
                segs[-1][1] = max(segs[-1][1], p["fin"])
            else:
                segs.append([p["ini"], p["fin"]])
        for a, b in segs:
            R[int(a / PASO): int(b / PASO), s] = True
            bordes += [a, b]
    collar = np.zeros(T, bool)
    for b in bordes:
        collar[max(0, int((b - COLLAR_S) / PASO)): int((b + COLLAR_S) / PASO) + 1] = True
    return R, hablantes, collar


def hip_marcos(segmentos: list, T: int, K: int) -> np.ndarray:
    H = np.zeros((T, max(K, 1)), bool)
    for a, b, k in segmentos:
        H[max(0, int(a / PASO)): min(T, int(b / PASO)), k] = True
    return H


def der(R: np.ndarray, H: np.ndarray, excluir: np.ndarray | None = None) -> dict:
    """DER con solapamientos y emparejamiento óptimo. O(T·S·K) para la co-ocurrencia, O((S+K)³) el húngaro."""
    v = ~excluir if excluir is not None else np.ones(len(R), bool)
    R, H = R[v], H[v]
    nr, nh = R.sum(1), H.sum(1)
    co = R.T.astype(np.float32) @ H.astype(np.float32)
    fil, col = linear_sum_assignment(-co)
    correctos = np.zeros(len(R), np.int32)
    for i, j in zip(fil, col):
        correctos += R[:, i] & H[:, j]
    total = max(1, int(nr.sum()))
    perdida = int(np.maximum(0, nr - nh).sum())
    falsa = int(np.maximum(0, nh - nr).sum())
    confusion = int((np.minimum(nr, nh) - correctos).sum())
    return {"der": (perdida + falsa + confusion) / total, "perdida": perdida / total, "falsa": falsa / total,
            "confusion": confusion / total, "grupos": int(H.any(0).sum())}


# ------------------------------------------------------------------------------------------------ barrido

def barrer(x: np.ndarray, verdad: dict, seg_nombre: str, emb: str, paso_s: float, hilos: int) -> tuple[list, dict]:
    R, hablantes, collar = verdad_marcos(verdad)
    T, S = R.shape
    t0 = time.time()
    seg, h = Dz.preparar(x, seg_nombre, emb, paso_s, hilos, cache=CACHE)
    t_seg = time.time() - t0
    cuenta = Dz.conteo(seg)
    dur = len(x) / Dz.SR
    filas = []
    for metodo, umbrales in UMBRALES.items():
        d = Dz.dendrograma(h, metodo)
        for u in list(umbrales) + ["n"]:
            grupo, cent = Dz.cortar(h, d, 0.0 if u == "n" else float(u), n_hablantes=S if u == "n" else None)
            diar = Dz.reconstruir(seg, h, grupo, len(cent), cuenta, dur)
            H = hip_marcos(diar.segmentos, T, diar.hablantes)
            e = der(R, H, collar)
            e0 = der(R, H)
            filas.append({"metodo": metodo, "umbral": u, "der": e["der"], "der_sin_collar": e0["der"],
                          "perdida": e["perdida"], "falsa": e["falsa"], "confusion": e["confusion"],
                          "grupos": e["grupos"]})
    costo = {"preparar_s": round(t_seg, 1), "huellas_s": h.segundos, "huellas": len(h.vectores),
             "omitidas": h.omitidas, "ventanas": int(seg.etiquetas.shape[0])}
    return filas, costo


def sherpa_referencia(x: np.ndarray, verdad: dict, umbral: float | None, n: int | None, hilos: int) -> dict:
    """La diarización de sherpa tal cual (sin restricción de ventana ni filtro de huellas), para comparar."""
    import sherpa_onnx as so
    R, _, collar = verdad_marcos(verdad)
    cfg = so.OfflineSpeakerDiarizationConfig(
        segmentation=so.OfflineSpeakerSegmentationModelConfig(
            pyannote=so.OfflineSpeakerSegmentationPyannoteModelConfig(model=str(Dz.SEGMENTADORES["pyannote"])),
            num_threads=hilos),
        embedding=so.SpeakerEmbeddingExtractorConfig(model=str(Dz.EXTRACTORES["wespeaker"]), num_threads=hilos),
        clustering=so.FastClusteringConfig(num_clusters=n or -1, threshold=umbral or 0.5),
        min_duration_on=0.3, min_duration_off=0.5)
    t0 = time.time()
    segs = [(s.start, s.end, s.speaker) for s in so.OfflineSpeakerDiarization(cfg).process(x).sort_by_start_time()]
    K = 1 + max((k for *_, k in segs), default=0)
    e = der(R, hip_marcos(segs, len(R), K), collar)
    e["segundos"] = round(time.time() - t0, 1)
    return e


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--carpeta", default=str(AQUI / "banco"))
    ap.add_argument("--condiciones", default="limpio,teams,duro")
    ap.add_argument("--segmentadores", default="pyannote,reverb")
    ap.add_argument("--extractores", default="wespeaker,titanet,eres2net")
    ap.add_argument("--pasos", default="2")
    ap.add_argument("--hilos", type=int, default=Dz.HILOS)
    ap.add_argument("--sherpa", action="store_true", help="agrega la diarización de sherpa tal cual, de referencia")
    ap.add_argument("--salida", default="")
    a = ap.parse_args()
    carpeta = Path(a.carpeta)
    salida = Path(a.salida) if a.salida else carpeta / "resultados-hablantes.json"
    verdad = json.loads((carpeta / "reunion.json").read_text("utf-8"))
    conds = [c for c in a.condiciones.split(",") if c]
    audios = {c: leer_wav(carpeta / f"reunion-{c}.wav")[0] for c in conds}
    S = len({t["hablante"] for t in verdad["turnos"]})
    ui.titulo("banco de hablantes", f"{verdad['dur']:.0f} s · {S} hablantes · {len(verdad['turnos'])} turnos · "
                                    f"condiciones {', '.join(conds)} · collar ±{COLLAR_S} s")
    res = json.loads(salida.read_text("utf-8")) if salida.exists() else {}
    combos = list(itertools.product(a.segmentadores.split(","), a.extractores.split(","),
                                    [float(p) for p in a.pasos.split(",")]))
    for seg_n, emb, paso in combos:
        clave = f"{seg_n}+{emb}@{paso:g}s"
        ui.paso(clave)
        por_cond = {}
        for c in conds:
            filas, costo = barrer(audios[c], verdad, seg_n, emb, paso, a.hilos)
            por_cond[c] = {"filas": filas, "costo": costo}
            mejor = min((f for f in filas if f["umbral"] != "n"), key=lambda f: f["der"])
            oraculo = min((f for f in filas if f["umbral"] == "n"), key=lambda f: f["der"])
            ui.ok(f"{c:7s} mejor {mejor['metodo']}@{mejor['umbral']} DER {mejor['der']:6.1%} ({mejor['grupos']} grupos) · "
                  f"con n={S}: {oraculo['der']:6.1%} · {costo['huellas']} huellas en {costo['huellas_s']:.0f} s")
        res[clave] = por_cond
        salida.write_text(json.dumps(res, ensure_ascii=False, indent=1), encoding="utf-8")

    if a.sherpa:
        ui.paso("sherpa tal cual (pyannote + wespeaker, paso 1 s)")
        ref = {}
        for c in conds:
            for nombre, (u, n) in {"u0.5": (0.5, None), "u0.7": (0.7, None), f"n={S}": (None, S)}.items():
                e = sherpa_referencia(audios[c], verdad, u, n, a.hilos)
                ref.setdefault(nombre, {})[c] = e
                ui.ok(f"{c:7s} {nombre:5s} DER {e['der']:6.1%} · {e['grupos']} grupos · {e['segundos']:.0f} s")
        res["sherpa-tal-cual"] = ref
        salida.write_text(json.dumps(res, ensure_ascii=False, indent=1), encoding="utf-8")

    tarjeta(res, conds)
    return 0


def tarjeta(res: dict, conds: list[str]) -> None:
    """Por combinación y enlace: el umbral con MENOR DER PROMEDIO entre condiciones."""
    filas = []
    for clave, por_cond in res.items():
        if clave == "sherpa-tal-cual" or not all(c in por_cond for c in conds):
            continue
        indice = {}
        for c in conds:
            for f in por_cond[c]["filas"]:
                indice.setdefault((f["metodo"], str(f["umbral"])), {})[c] = f
        for (metodo, u), fs in indice.items():
            if len(fs) < len(conds):
                continue
            prom = float(np.mean([fs[c]["der"] for c in conds]))
            filas.append((prom, clave, metodo, u, fs))
    filas.sort(key=lambda t: t[0])
    vista, vistos = [], set()
    for prom, clave, metodo, u, fs in filas:
        if u == "n" or (clave, metodo) in vistos:
            continue
        vistos.add((clave, metodo))
        costo = sum(res[clave][c]["costo"]["huellas_s"] for c in conds) / len(conds)
        vista.append([ui.color("crema", clave), metodo, u] +
                     [f"{fs[c]['der']:.1%} ({fs[c]['grupos']})" for c in conds] +
                     [ui.color("verde", f"{prom:.1%}"), f"{costo:.0f} s"])
        if len(vista) >= 14:
            break
    ui.tarjeta("mejores diarizaciones (umbral único para todas las condiciones)",
               ["combinación", "enlace", "umbral"] + [f"DER {c} (grupos)" for c in conds] + ["promedio", "huellas"],
               vista, derecha=set(range(3, 5 + len(conds))),
               pie="DER con collar ±0,25 s y solapamientos · (grupos) = hablantes encontrados; la verdad tiene 4")
    if "sherpa-tal-cual" in res:
        ref = res["sherpa-tal-cual"]
        ui.tarjeta("sherpa tal cual, de referencia", ["config"] + [f"DER {c}" for c in conds],
                   [[k] + [f"{v[c]['der']:.1%} ({v[c]['grupos']})" for c in conds] for k, v in ref.items()],
                   derecha=set(range(1, 1 + len(conds))))


if __name__ == "__main__":
    sys.exit(main())
