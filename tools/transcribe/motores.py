"""
motores.py — los motores de reconocimiento de voz y el corte en tramos, en UN solo lugar.
(Los usan transcribe_sherpa.py, banco_asr.py y banco_verdad.py: cero copia y pega.)

Todos corren LOCAL: el audio de una reunión no sale de la máquina.
  parakeet   NVIDIA Parakeet TDT 0.6B v3 · transductor, 25 idiomas · tiempo y confianza por token
  conformer  NVIDIA FastConformer transductor SOLO castellano (1424 h) · tiempo por token
  qwen3      Qwen3-ASR 0.6B (Alibaba) · codificador + decodificador tipo LLM · acepta palabras clave de contexto
  cohere     Cohere Transcribe 2B (#1 del Open ASR Leaderboard, mar-2026) · puntuación y números en cifras
  whisper    whisper large-v3 (faster-whisper) con los ajustes «rápido» del pipeline viejo, con su VAD

Por qué no hay VAD en los motores sherpa: un detector de voz DESCARTA lo que cree que no es habla, y ahí es donde un
transcriptor «se come» palabras (una voz baja, alguien lejos del micrófono). Acá el audio se corta en tramos de hasta
20 s en el marco MÁS CALLADO cerca de cada límite y se transcribe el 100 %; los tramos se decodifican EN LOTE.
"""
import math
import os
import time
import wave
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Sequence

import numpy as np

MODELOS = Path(os.environ.get("ASR_MODELOS", r"C:\Apps\asr-modelos"))
WHISPER = Path(os.environ.get("WHISPER_MODEL_DIR", r"C:\Apps\whisper\models\large-v3"))
SR = 16000
MARCO_S = 0.25          # resolución de la energía para elegir cortes
TRAMO_MAX_S = 20.0      # Qwen3/Cohere tienen techo de tokens; un transductor rinde igual con 20 s
TRAMO_BUSCA_S = 6.0     # el corte se busca en los últimos 6 s de cada tramo
LOTE = 6                # tramos por llamada a decode_streams
NOMBRES = ("parakeet", "conformer", "qwen3", "cohere", "whisper")
PUNTUACION = set(",.;:!?¿¡…)]»") | {chr(34), chr(39)}
HILOS = os.cpu_count() or 8


# ------------------------------------------------------------------------------------------------ tipos

@dataclass(frozen=True, slots=True)
class Palabra:
    w: str
    ini: float
    fin: float
    conf: float = 1.0       # probabilidad media de sus tokens; 1.0 = el motor no la informa


@dataclass(frozen=True, slots=True)
class Transcripcion:
    texto: str
    palabras: tuple[Palabra, ...]


@dataclass(frozen=True)
class Motor:
    """Audio float32 a 16 kHz → Transcripcion con tiempos ABSOLUTOS (se le suma el inicio de cada trozo)."""
    nombre: str
    marcas_reales: bool      # True: tiempos por token del propio motor · False: interpolados por largo de palabra
    carga_s: float
    _lote: Callable[[Sequence[np.ndarray], Sequence[float]], list[Transcripcion]] = field(repr=False)

    def __call__(self, x: np.ndarray, t0: float = 0.0) -> Transcripcion:
        return self._lote([x], [t0])[0]

    def lote(self, trozos: Sequence[np.ndarray], inicios: Sequence[float]) -> list[Transcripcion]:
        return self._lote(trozos, inicios)


# ------------------------------------------------------------------------------------------------ audio

def leer_wav(ruta) -> tuple[np.ndarray, int]:
    with wave.open(str(ruta), "rb") as w:
        if w.getsampwidth() != 2:
            raise ValueError(f"{Path(ruta).name}: solo PCM de 16 bit")
        sr, n, ch = w.getframerate(), w.getnframes(), w.getnchannels()
        x = np.frombuffer(w.readframes(n), dtype=np.int16).astype(np.float32) / 32768.0
    return (x.reshape(-1, ch).mean(axis=1) if ch > 1 else x), sr


def escribir_wav(ruta, x: np.ndarray, sr: int = SR) -> None:
    pcm = (np.clip(x, -1.0, 1.0) * 32767.0).astype("<i2")
    with wave.open(str(ruta), "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(sr); w.writeframes(pcm.tobytes())


def energia_db(x: np.ndarray, sr: int = SR) -> np.ndarray:
    """RMS en dB por marco de 250 ms. O(n) con un reshape: nada de bucles por muestra."""
    m = int(sr * MARCO_S)
    k = len(x) // m
    if k == 0:
        return np.zeros(0, np.float32)
    return 20 * np.log10(np.sqrt(np.mean(x[: k * m].reshape(k, m) ** 2, axis=1) + 1e-12))


def tramos(db: np.ndarray, ini_s: float, fin_s: float, maximo: float = TRAMO_MAX_S,
           busca: float = TRAMO_BUSCA_S) -> list[tuple[float, float]]:
    """
    Parte [ini, fin] en tramos de hasta `maximo` s; cada corte cae en el marco MÁS CALLADO de los últimos `busca` s.
    Cubre el 100 % del intervalo. O(marcos): cada marco se mira a lo sumo una vez por corte vecino.
    """
    res, s = [], ini_s
    while fin_s - s > maximo:
        a = int((s + maximo - busca) / MARCO_S)
        b = min(len(db), int((s + maximo) / MARCO_S))
        corte = (a + int(np.argmin(db[a:b]))) * MARCO_S + MARCO_S / 2 if b > a else s + maximo
        if corte <= s + 0.5:                      # defensa: nunca un tramo degenerado
            corte = s + maximo
        res.append((s, corte))
        s = corte
    if fin_s > s:
        res.append((s, fin_s))
    return res


# ------------------------------------------------------------------------------------------------ palabras

def _palabras_de_tokens(r, t0: float) -> list[Palabra]:
    """
    Tokens de un transductor → palabras. Un token que empieza con espacio abre palabra; la puntuación suelta se
    pega a la anterior. Fin de palabra = inicio + duración del último token (TDT) o el inicio del siguiente token.
    Confianza = exp(media de log-probabilidades de sus tokens). O(tokens).
    """
    toks = list(r.tokens or [])
    ts = list(r.timestamps or [])
    durs = list(getattr(r, "durations", None) or [])
    lps = list(getattr(r, "ys_log_probs", None) or [])
    out: list[list] = []                          # [texto, ini, fin, suma_logp, n_tokens]
    for i, (tok, t) in enumerate(zip(toks, ts)):
        limpio = tok.replace("▁", " ").strip()
        if not limpio:
            continue
        fin = t + (durs[i] if i < len(durs) and durs[i] > 0 else
                   (ts[i + 1] - t if i + 1 < len(ts) else 0.08))
        lp = lps[i] if i < len(lps) else 0.0
        if all(c in PUNTUACION for c in limpio):
            if out:                               # la puntuación se pega a la palabra anterior…
                out[-1][0] += limpio
            continue                              # …y si abre el tramo, no es una palabra: se descarta
        if tok.startswith(" ") or tok.startswith("▁") or not out:
            out.append([limpio, t0 + t, t0 + fin, lp, 1])
        else:
            o = out[-1]; o[0] += limpio; o[2] = t0 + fin; o[3] += lp; o[4] += 1
    return [Palabra(w, round(a, 3), round(b, 3), round(math.exp(lp / n), 3)) for w, a, b, lp, n in out]


def _interpolar(texto: str, x: np.ndarray, t0: float) -> list[Palabra]:
    """
    Sin tiempos del motor: se reparte el tramo CON VOZ (del primer al último marco sobre el piso + 12 dB) en
    proporción al largo de cada palabra. Aproximado, pero no inventa silencios al principio ni al final.
    """
    ws = texto.split()
    if not ws:
        return []
    db = energia_db(x)
    if len(db):
        voz = np.flatnonzero(db > max(-50.0, float(np.percentile(db, 10)) + 12.0))
        a = (voz[0] * MARCO_S) if len(voz) else 0.0
        b = ((voz[-1] + 1) * MARCO_S) if len(voz) else len(x) / SR
    else:
        a, b = 0.0, len(x) / SR
    pesos = np.array([len(w) + 1.0 for w in ws])
    acum = np.concatenate([[0.0], np.cumsum(pesos) / pesos.sum()])
    return [Palabra(w, round(t0 + a + (b - a) * acum[i], 3), round(t0 + a + (b - a) * acum[i + 1], 3))
            for i, w in enumerate(ws)]


# ------------------------------------------------------------------------------------------------ motores

def _sherpa(nombre: str, hilos: int, idioma: str, palabras_clave: Sequence[str], penal_blanco: float,
            busqueda: str):
    import sherpa_onnx as so
    R = so.OfflineRecognizer
    if nombre in ("parakeet", "conformer"):
        d = MODELOS / ("sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8" if nombre == "parakeet"
                       else "sherpa-onnx-nemo-fast-conformer-transducer-es-1424-int8")
        return R.from_transducer(encoder=str(d / "encoder.int8.onnx"), decoder=str(d / "decoder.int8.onnx"),
                                 joiner=str(d / "joiner.int8.onnx"), tokens=str(d / "tokens.txt"),
                                 num_threads=hilos, model_type="nemo_transducer", decoding_method=busqueda,
                                 blank_penalty=penal_blanco), True
    if nombre == "qwen3":
        d = next(MODELOS.glob("sherpa-onnx-qwen3-asr-*"))
        return R.from_qwen3_asr(conv_frontend=str(d / "conv_frontend.onnx"), encoder=str(d / "encoder.int8.onnx"),
                                decoder=str(d / "decoder.int8.onnx"), tokenizer=str(d / "tokenizer"),
                                num_threads=hilos, max_new_tokens=320,
                                hotwords=", ".join(palabras_clave)), False
    if nombre == "cohere":
        d = next(MODELOS.glob("sherpa-onnx-cohere-transcribe-*"))
        # 🚨 puntuación y números EXPLÍCITOS: sin pasarlos, el enlace de Python no los activa (texto en minúscula y
        #    corrido). Medido el 23-sep: con ellos sale «Bueno, arrancamos con la daily. Hoy somos cuatro, …»
        return R.from_cohere_transcribe(encoder=str(next(d.glob("encoder*.onnx"))),
                                        decoder=str(next(d.glob("decoder*.onnx"))), tokens=str(d / "tokens.txt"),
                                        num_threads=hilos, language=idioma, use_punct=True, use_itn=True), False
    raise ValueError(f"motor desconocido «{nombre}» (hay: {', '.join(NOMBRES)})")


def crear(nombre: str, hilos: int = HILOS, idioma: str = "es", palabras_clave: Sequence[str] = (),
          penal_blanco: float = 0.0, busqueda: str = "greedy_search") -> Motor:
    t0 = time.time()
    if nombre == "whisper":
        return _whisper(hilos, idioma, palabras_clave, t0)
    rec, con_tiempos = _sherpa(nombre, hilos, idioma, palabras_clave, penal_blanco, busqueda)

    def lote(trozos, inicios):
        res: list[Transcripcion] = []
        for k in range(0, len(trozos), LOTE):
            streams = []
            for x in trozos[k: k + LOTE]:
                s = rec.create_stream(); s.accept_waveform(SR, np.ascontiguousarray(x, dtype=np.float32))
                streams.append(s)
            rec.decode_streams(streams)
            for s, x, t in zip(streams, trozos[k: k + LOTE], inicios[k: k + LOTE]):
                r = s.result
                texto = r.text.strip()
                ps = _palabras_de_tokens(r, t) if con_tiempos else _interpolar(texto, x, t)
                res.append(Transcripcion(texto, tuple(ps)))
        return res

    etiqueta = nombre + (f"+b{penal_blanco:g}" if penal_blanco else "")
    return Motor(etiqueta, con_tiempos, round(time.time() - t0, 1), lote)


def _whisper(hilos: int, idioma: str, palabras_clave: Sequence[str], t0: float) -> Motor:
    """Los MISMOS ajustes que el pipeline viejo en «rápido»: int8_float32, beam 5, fallback de temperatura, VAD."""
    from faster_whisper import WhisperModel
    m = WhisperModel(str(WHISPER), device="cpu", compute_type="int8_float32", cpu_threads=hilos)

    def lote(trozos, inicios):
        res = []
        for x, t in zip(trozos, inicios):
            segs, _ = m.transcribe(np.ascontiguousarray(x, dtype=np.float32), language=idioma, beam_size=5,
                                   best_of=10, patience=2.0, temperature=[0.0, 0.2, 0.4, 0.6, 0.8, 1.0],
                                   condition_on_previous_text=True, vad_filter=True,
                                   vad_parameters=dict(min_silence_duration_ms=500), word_timestamps=True,
                                   hotwords=(", ".join(palabras_clave) or None))
            ps = [Palabra(w.word.strip(), round(t + w.start, 3), round(t + w.end, 3), round(w.probability, 3))
                  for sg in segs for w in (sg.words or []) if w.word.strip()]
            res.append(Transcripcion(" ".join(p.w for p in ps), tuple(ps)))
        return res

    return Motor("whisper", True, round(time.time() - t0, 1), lote)


VOZ_SOBRE_PISO_DB = 10.0   # voz = 10 dB sobre el piso de ruido de la reunión…
VOZ_MARCO_S = 0.05         # …medida en marcos de 50 ms DENTRO del trozo…
VOZ_MIN_MARCOS = 2         # …y al menos 100 ms seguidos: la puntita de una palabra del vecino no cuenta


def con_voz(x: np.ndarray, ini: float, fin: float, umbral_db: float, sr: int = SR) -> bool:
    """
    ¿Hay voz en [ini, fin)? Al menos VOZ_MIN_MARCOS marcos SEGUIDOS de 50 ms sobre el umbral, contados solo dentro del
    trozo (el 23-sep, un trozo de silencio que rozaba 50 ms del arranque de una palabra pasaba con marcos de 250 ms y
    Parakeet le inventaba «Yeah.»). Un «sí» de 150 ms pasa. O(muestras del trozo).
    """
    m = int(VOZ_MARCO_S * sr)
    seg = x[int(ini * sr): int(fin * sr)]
    k = len(seg) // m
    if k < VOZ_MIN_MARCOS:
        return False
    db = 20 * np.log10(np.sqrt(np.mean(seg[: k * m].reshape(k, m) ** 2, axis=1)) + 1e-12)
    arriba = (db > umbral_db).astype(np.int8)
    # corrida más larga de marcos seguidos sobre el umbral: ventana deslizante de VOZ_MIN_MARCOS con suma acumulada
    acum = np.concatenate([[0], np.cumsum(arriba)])
    return bool(np.any(acum[VOZ_MIN_MARCOS:] - acum[:-VOZ_MIN_MARCOS] == VOZ_MIN_MARCOS))


def transcribir(motor: Motor, x: np.ndarray, sr: int = SR, cortes: Sequence[tuple[float, float]] | None = None,
                al_avanzar: Callable[[float], None] | None = None) -> list[tuple[float, float, Transcripcion]]:
    """
    Transcribe TODO el audio: por los `cortes` dados (p. ej. turnos de hablante) o por tramos en silencio.
    Devuelve (ini, fin, Transcripcion) por trozo. Lotes de LOTE trozos; avisa el avance en fracción de audio.

    🚨 Compuerta de voz: a un trozo de puro silencio un motor le INVENTA texto («Thank you.», «Mm-hmm.»: lo hizo
    Parakeet el 23-sep con los turnos de hablante, que a veces son solo el silencio entre dos personas). Un trozo sin
    100 ms seguidos 10 dB sobre el piso de ruido (percentil 10 de la reunión, en marcos de 250 ms) no se le da al
    motor. Es conservadora a propósito: un «sí» bajito de una voz 18 dB abajo queda >20 dB sobre el piso y pasa.
    """
    if sr != SR:
        raise ValueError(f"se esperaba {SR} Hz y llegó {sr} Hz")
    dur = len(x) / sr
    db = energia_db(x, sr)
    plan = list(cortes) if cortes is not None else tramos(db, 0.0, dur)
    umbral = (float(np.percentile(db, 10)) if len(db) else -120.0) + VOZ_SOBRE_PISO_DB
    vacia = Transcripcion("", ())
    out = []
    for k in range(0, len(plan), LOTE):
        grupo = plan[k: k + LOTE]
        con = [(a, b) for a, b in grupo if con_voz(x, a, b, umbral, sr)]
        hechas = dict(zip(con, motor.lote([x[int(a * sr): int(b * sr)] for a, b in con], [a for a, _ in con]))) if con else {}
        for a, b in grupo:
            out.append((a, b, hechas.get((a, b), vacia)))
        if al_avanzar:
            al_avanzar(min(1.0, grupo[-1][1] / dur) if dur else 1.0)
    return out
