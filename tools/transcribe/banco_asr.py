#!/usr/bin/env python3
"""
banco_asr.py — compara motores de reconocimiento de voz sobre audio REAL de reuniones, sin referencia humana.

Todos corren LOCAL (el audio de una reunión no sale de la máquina):
  whisper      whisper large-v3 con faster-whisper, con los MISMOS ajustes que usa hoy el pipeline («rápido»)
  parakeet     NVIDIA Parakeet TDT 0.6B v3 (transductor, 25 idiomas) · sherpa-onnx int8
  qwen3        Qwen3-ASR 0.6B (Alibaba, ene-2026, 52 idiomas) · sherpa-onnx int8
  cohere       Cohere Transcribe 2B (mar-2026, #1 del Open ASR Leaderboard) · sherpa-onnx int8
  conformer    NVIDIA FastConformer transductor SOLO castellano (1424 h) · sherpa-onnx int8

Métricas — ninguna necesita una transcripción de referencia:
  velocidad    RTF = tiempo de proceso / duración del audio (menos es mejor)
  completitud  palabras por minuto de VOZ, y «tramos comidos»: tramos con ≥3 s de voz y <2 palabras
  cobertura    motores con marcas de tiempo: fracción de la voz con alguna palabra a ±0,75 s
  acuerdo      WER de cada motor contra cada otro; el más CENTRAL (menor WER medio contra el resto) suele ser el
               mejor: los errores de cada motor son distintos, los aciertos coinciden (el principio de ROVER)
  control      un clip con transcripción conocida (es1.wav): el único WER «de verdad»

Uso:
  python banco_asr.py --audio reunion16.wav [--fragmentos 3] [--duracion 120] [--motores parakeet,qwen3,cohere,conformer,whisper]
"""
import argparse
import json
import math
import os
import re
import sys
import time
import unicodedata
import wave
from pathlib import Path

import numpy as np

MODELOS = Path(r"C:\Apps\asr-modelos")
WHISPER = Path(r"C:\Apps\whisper\models\large-v3")
CONTROL_WAV = MODELOS / "sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25" / "test_wavs" / "es1.wav"
CONTROL_REF = "Esta prenda es amplia, recomiendo elegir una talla menor a la habitual."

MARCO_S = 0.25          # resolución de la energía: 250 ms
SEG_MAX_S = 20.0        # tramo máximo que se le da a un motor sherpa (Qwen3/Cohere tienen techo de tokens)
SEG_BUSCA_S = 6.0       # el corte se busca en los últimos 6 s del tramo, en el marco más callado
TOL_COBERTURA_S = 0.75

C = {"cy": "\033[38;2;143;214;204m", "ok": "\033[38;2;181;223;168m", "ro": "\033[38;2;243;185;210m",
     "du": "\033[38;2;246;192;160m", "ma": "\033[38;2;196;181;253m", "cr": "\033[38;2;238;223;184m",
     "ap": "\033[38;2;88;93;110m", "tx": "\033[38;2;211;214;223m", "x": "\033[0m"}


# ------------------------------------------------------------------------------------------------ audio y energía

def leer_wav(ruta: Path) -> tuple[np.ndarray, int]:
    with wave.open(str(ruta), "rb") as w:
        assert w.getsampwidth() == 2, "solo PCM 16 bit"
        sr, n, ch = w.getframerate(), w.getnframes(), w.getnchannels()
        x = np.frombuffer(w.readframes(n), dtype=np.int16).astype(np.float32) / 32768.0
    if ch > 1:
        x = x.reshape(-1, ch).mean(axis=1)
    return x, sr


def energia_db(x: np.ndarray, sr: int) -> np.ndarray:
    """RMS en dB por marco de 250 ms. O(n) con un reshape: nada de bucles por muestra."""
    m = int(sr * MARCO_S)
    k = len(x) // m
    rms = np.sqrt(np.mean(x[: k * m].reshape(k, m) ** 2, axis=1) + 1e-12)
    return 20 * np.log10(rms)


def con_voz(db: np.ndarray) -> np.ndarray:
    """Umbral ADAPTATIVO: piso de ruido (percentil 10) + 12 dB, nunca por debajo de −50 dB."""
    piso = np.percentile(db, 10)
    return db > max(-50.0, piso + 12.0)


def elegir_fragmentos(voz: np.ndarray, n: int, dur_s: float) -> list[tuple[float, float]]:
    """
    n ventanas de dur_s con la MAYOR cantidad de voz, una por cada n-ésimo del archivo (para no elegir tres
    veces el mismo pasaje). Ventana deslizante con sumas prefijas: O(marcos).
    """
    w = int(dur_s / MARCO_S)
    pref = np.concatenate([[0], np.cumsum(voz.astype(np.int32))])
    res, parte = [], len(voz) // n
    for i in range(n):
        a, b = i * parte, min(len(voz) - w, (i + 1) * parte - 1)
        if b <= a:
            continue
        sumas = pref[a + w: b + w + 1] - pref[a: b + 1]
        mejor = a + int(np.argmax(sumas))
        res.append((mejor * MARCO_S, mejor * MARCO_S + dur_s))
    return res


def tramos(db: np.ndarray, ini_s: float, fin_s: float) -> list[tuple[float, float]]:
    """
    Corta [ini, fin] en tramos de hasta SEG_MAX_S, cada corte en el marco MÁS CALLADO de los últimos SEG_BUSCA_S.
    Cubre el 100 % del audio: nada se descarta (un VAD sí descarta, y eso es justo lo que «se come» palabras).
    """
    res, s = [], ini_s
    while fin_s - s > SEG_MAX_S:
        a, b = int((s + SEG_MAX_S - SEG_BUSCA_S) / MARCO_S), int((s + SEG_MAX_S) / MARCO_S)
        corte = (a + int(np.argmin(db[a:b]))) * MARCO_S + MARCO_S / 2
        res.append((s, corte))
        s = corte
    res.append((s, fin_s))
    return res


# ------------------------------------------------------------------------------------------------ texto y WER

def normalizar(t: str) -> list[str]:
    t = unicodedata.normalize("NFC", t.lower())
    t = re.sub(r"[^0-9a-záéíóúüñ]+", " ", t)
    return t.split()


def wer(ref: list[str], hip: list[str]) -> float:
    """Distancia de edición por palabras con DOS filas: O(n·m) tiempo, O(m) memoria."""
    if not ref:
        return 0.0 if not hip else 1.0
    prev = list(range(len(hip) + 1))
    for i, r in enumerate(ref, 1):
        cur = [i] + [0] * len(hip)
        for j, h in enumerate(hip, 1):
            cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (r != h))
        prev = cur
    return prev[-1] / len(ref)


# ------------------------------------------------------------------------------------------------ motores

def motor_sherpa(nombre: str, hilos: int):
    import sherpa_onnx as so
    R = so.OfflineRecognizer
    if nombre == "parakeet":
        d = MODELOS / "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"
        rec = R.from_transducer(encoder=str(d / "encoder.int8.onnx"), decoder=str(d / "decoder.int8.onnx"),
                                joiner=str(d / "joiner.int8.onnx"), tokens=str(d / "tokens.txt"),
                                num_threads=hilos, model_type="nemo_transducer", decoding_method="greedy_search")
    elif nombre == "conformer":
        d = MODELOS / "sherpa-onnx-nemo-fast-conformer-transducer-es-1424-int8"
        rec = R.from_transducer(encoder=str(d / "encoder.int8.onnx"), decoder=str(d / "decoder.int8.onnx"),
                                joiner=str(d / "joiner.int8.onnx"), tokens=str(d / "tokens.txt"),
                                num_threads=hilos, model_type="nemo_transducer", decoding_method="greedy_search")
    elif nombre == "qwen3":
        d = MODELOS / "sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25"
        rec = R.from_qwen3_asr(conv_frontend=str(d / "conv_frontend.onnx"), encoder=str(d / "encoder.int8.onnx"),
                               decoder=str(d / "decoder.int8.onnx"), tokenizer=str(d / "tokenizer"),
                               num_threads=hilos, max_new_tokens=320)
    elif nombre == "cohere":
        d = next(MODELOS.glob("sherpa-onnx-cohere-transcribe-*"))
        enc = next(d.glob("encoder*.onnx")); dec = next(d.glob("decoder*.onnx"))
        rec = R.from_cohere_transcribe(encoder=str(enc), decoder=str(dec), tokens=str(d / "tokens.txt"),
                                       num_threads=hilos, language="es")
    else:
        raise ValueError(nombre)

    def transcribir(x: np.ndarray, sr: int):
        s = rec.create_stream()
        s.accept_waveform(sr, x)
        rec.decode_stream(s)
        r = s.result
        ts = list(getattr(r, "timestamps", []) or [])
        return r.text.strip(), ts
    return transcribir


def motor_whisper(hilos: int):
    """Los MISMOS ajustes que usa hoy el pipeline en «rápido» (transcribe.py --compute int8_float32 --beam 5)."""
    from faster_whisper import WhisperModel
    m = WhisperModel(str(WHISPER), device="cpu", compute_type="int8_float32", cpu_threads=hilos)

    def transcribir(x: np.ndarray, sr: int):
        segs, _ = m.transcribe(x, language="es", beam_size=5, best_of=10, patience=2.0,
                               temperature=[0.0, 0.2, 0.4, 0.6, 0.8, 1.0], condition_on_previous_text=True,
                               vad_filter=True, vad_parameters=dict(min_silence_duration_ms=500), word_timestamps=True)
        palabras, ts = [], []
        for sg in segs:
            for w in (sg.words or []):
                palabras.append(w.word.strip()); ts.append((w.start + w.end) / 2)
        return " ".join(palabras).strip(), ts
    return transcribir


# ------------------------------------------------------------------------------------------------ banco

def correr_motor(nombre, hilos, x, sr, db, voz, fragmentos):
    t0 = time.time()
    fn = motor_whisper(hilos) if nombre == "whisper" else motor_sherpa(nombre, hilos)
    carga = time.time() - t0
    out = {"motor": nombre, "carga_s": round(carga, 1), "fragmentos": []}
    total_audio = total_proc = 0.0
    for (fa, fb) in fragmentos:
        a, b = int(fa * sr), int(fb * sr)
        t1 = time.time()
        if nombre == "whisper":
            texto, ts = fn(x[a:b], sr)
            ts_abs = [fa + t for t in ts]
            por_tramo = None
        else:
            textos, ts_abs = [], []
            por_tramo = []
            for (sa, sb) in tramos(db, fa, fb):
                t, ts = fn(x[int(sa * sr): int(sb * sr)], sr)
                textos.append(t); ts_abs += [sa + u for u in ts]
                por_tramo.append({"ini": round(sa, 2), "fin": round(sb, 2), "texto": t})
            texto = " ".join(t for t in textos if t)
        proc = time.time() - t1
        total_audio += fb - fa; total_proc += proc
        out["fragmentos"].append({"ini": fa, "fin": fb, "texto": texto, "ts": [round(t, 2) for t in ts_abs],
                                  "tramos": por_tramo, "proc_s": round(proc, 1)})
        print(f"    {C['ap']}{nombre:9s} fragmento {fa/60:5.1f}–{fb/60:5.1f} min · {proc:6.1f} s{C['x']}", flush=True)
    # control con verdad conocida
    xc, src = leer_wav(CONTROL_WAV)
    tc, _ = fn(xc, src)
    out["control"] = {"texto": tc, "wer": round(wer(normalizar(CONTROL_REF), normalizar(tc)), 3)}
    out["rtf"] = round(total_proc / max(1e-9, total_audio), 3)
    return out


def metricas(res, db, voz, fragmentos):
    """Completitud, cobertura y «tramos comidos» con la MISMA vara para todos los motores."""
    for r in res:
        palabras = voz_min = 0.0
        comidos = tramos_total = 0
        cubiertos = voz_frames = 0
        for f in r["fragmentos"]:
            fa, fb = f["ini"], f["fin"]
            a, b = int(fa / MARCO_S), int(fb / MARCO_S)
            v = voz[a:b]
            voz_min += v.sum() * MARCO_S / 60
            ws = normalizar(f["texto"]); palabras += len(ws)
            ts = np.array(f["ts"]) if f["ts"] else None
            if ts is not None and len(ts):
                centros = fa + (np.arange(b - a) + 0.5) * MARCO_S
                # distancia al timestamp más cercano con búsqueda binaria: O(k log m)
                ts_ord = np.sort(ts)
                idx = np.clip(np.searchsorted(ts_ord, centros), 1, len(ts_ord) - 1)
                d = np.minimum(np.abs(centros - ts_ord[idx - 1]), np.abs(centros - ts_ord[idx]))
                cubiertos += int(((d <= TOL_COBERTURA_S) & v).sum()); voz_frames += int(v.sum())
            # tramos comidos: la vara son los tramos de corte en silencio, iguales para todos
            for (sa, sb) in tramos(db, fa, fb):
                va, vb = int(sa / MARCO_S), int(sb / MARCO_S)
                seg_voz = voz[va:vb].sum() * MARCO_S
                if seg_voz < 3:
                    continue
                tramos_total += 1
                if f.get("tramos"):
                    t = next((x["texto"] for x in f["tramos"] if abs(x["ini"] - sa) < 0.01), "")
                    n = len(normalizar(t))
                else:
                    n = int(((ts >= sa) & (ts < sb)).sum()) if ts is not None and len(ts) else 0
                if n < 2:
                    comidos += 1
        r["palabras"] = int(palabras)
        r["ppm_voz"] = round(palabras / max(1e-9, voz_min), 1)
        r["comidos"] = comidos; r["tramos_con_voz"] = tramos_total
        r["cobertura"] = round(cubiertos / voz_frames, 3) if voz_frames else None


def acuerdo(res):
    nombres = [r["motor"] for r in res]
    textos = {r["motor"]: normalizar(" ".join(f["texto"] for f in r["fragmentos"])) for r in res}
    m = {a: {b: (round(wer(textos[a], textos[b]), 3) if a != b else 0.0) for b in nombres} for a in nombres}
    for r in res:
        otros = [m[o][r["motor"]] for o in nombres if o != r["motor"]]
        r["centralidad"] = round(sum(otros) / len(otros), 3) if otros else None
    return m


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--audio", required=True)
    ap.add_argument("--fragmentos", type=int, default=3)
    ap.add_argument("--duracion", type=float, default=120)
    ap.add_argument("--motores", default="parakeet,qwen3,cohere,conformer,whisper")
    ap.add_argument("--hilos", type=int, default=os.cpu_count() or 8)
    ap.add_argument("--salida", default="")
    a = ap.parse_args()

    x, sr = leer_wav(Path(a.audio))
    db = energia_db(x, sr); voz = con_voz(db)
    frags = elegir_fragmentos(voz, a.fragmentos, a.duracion)
    print(f"\n{C['cy']}banco de ASR · {Path(a.audio).name}{C['x']} {C['ap']}· {len(x)/sr/60:.1f} min · voz {voz.mean():.0%} · "
          f"fragmentos {', '.join(f'{f[0]/60:.1f}–{f[1]/60:.1f}' for f in frags)} min{C['x']}\n", flush=True)
    res = []
    salida = Path(a.salida) if a.salida else Path(a.audio).with_name("banco-asr.json")
    for nombre in [m.strip() for m in a.motores.split(",") if m.strip()]:
        print(f"  {C['ma']}▸ {nombre}{C['x']}", flush=True)
        try:
            r = correr_motor(nombre, a.hilos, x, sr, db, voz, frags)
        except Exception as e:
            print(f"    {C['ro']}✗ {type(e).__name__}: {e}{C['x']}", flush=True)
            continue
        res.append(r)
        metricas([r], db, voz, frags)
        print(f"    {C['ok']}✓{C['x']} RTF {r['rtf']:.3f} · {r['palabras']} palabras · {r['ppm_voz']} por min de voz · "
              f"comidos {r['comidos']}/{r['tramos_con_voz']} · control WER {r['control']['wer']:.0%} «{r['control']['texto']}»", flush=True)
        json.dump({"fragmentos": frags, "motores": res}, open(salida, "w", encoding="utf-8"), ensure_ascii=False, indent=1)

    m = acuerdo(res)
    json.dump({"fragmentos": frags, "motores": res, "acuerdo": m}, open(salida, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    # --- tarjeta final
    print(f"\n  {C['cy']}╭─ resultado {'─' * 96}╮{C['x']}")
    print(f"  {C['cy']}│{C['x']} {'motor':10s} {'RTF':>7s} {'× tiempo real':>14s} {'palabras':>9s} {'por min voz':>12s} {'comidos':>9s} {'cobertura':>10s} {'centralidad':>12s} {'control':>8s}  {C['cy']}│{C['x']}")
    for r in sorted(res, key=lambda r: (r["centralidad"] if r["centralidad"] is not None else 9)):
        cob = f"{r['cobertura']:.0%}" if r["cobertura"] is not None else "—"
        print(f"  {C['cy']}│{C['x']} {r['motor']:10s} {r['rtf']:7.3f} {1/max(r['rtf'],1e-9):13.1f}× {r['palabras']:9d} {r['ppm_voz']:12.1f} "
              f"{str(r['comidos'])+'/'+str(r['tramos_con_voz']):>9s} {cob:>10s} {r['centralidad'] if r['centralidad'] is not None else 0:12.3f} {r['control']['wer']:8.0%}  {C['cy']}│{C['x']}")
    print(f"  {C['cy']}╰{'─' * 108}╯{C['x']}")
    print(f"\n  {C['ap']}acuerdo (WER fila→columna):{C['x']}")
    nombres = [r["motor"] for r in res]
    print("  " + " " * 11 + "".join(f"{n:>11s}" for n in nombres))
    for aa in nombres:
        print(f"  {aa:11s}" + "".join(f"{m[aa][bb]:11.3f}" for bb in nombres))
    print(f"\n  {C['ap']}detalle completo: {salida}{C['x']}\n")


if __name__ == "__main__":
    sys.exit(main())
