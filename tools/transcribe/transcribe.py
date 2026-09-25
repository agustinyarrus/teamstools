#!/usr/bin/env python3
"""
Transcripcion local de video/audio -> texto, MAXIMA CALIDAD.
faster-whisper + large-v3 (modelo cacheado local). 100% offline, sin nube.

Uso:
    python transcribe.py <archivo> [--lang es] [--compute float32] [--beam 10] ...

Salidas (junto al archivo de entrada): <base>.txt  <base>.srt  <base>.json
Defaults afinados para precision (large-v3, beam 10, float32, VAD, word timestamps).
"""
import os, sys, time, json, argparse, ctypes
from pathlib import Path


def set_high_priority():
    try:
        if os.name == "nt":
            k = ctypes.windll.kernel32
            k.SetPriorityClass(k.GetCurrentProcess(), 0x00000080)  # HIGH_PRIORITY_CLASS
    except Exception:
        pass


def fmt_ts(sec):
    h = int(sec // 3600); m = int((sec % 3600) // 60); s = int(sec % 60)
    ms = int(round((sec - int(sec)) * 1000))
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input")
    ap.add_argument("--model",   default=os.environ.get("WHISPER_MODEL_DIR", r"C:\Apps\whisper\models\large-v3"))
    ap.add_argument("--lang",    default=os.environ.get("WHISPER_LANG", "es"))
    ap.add_argument("--compute", default=os.environ.get("WHISPER_COMPUTE", "float32"))
    ap.add_argument("--beam",    type=int, default=int(os.environ.get("WHISPER_BEAM", "10")))
    ap.add_argument("--best-of", type=int, default=int(os.environ.get("WHISPER_BEST_OF", "10")))
    ap.add_argument("--threads", type=int, default=int(os.environ.get("WHISPER_THREADS", str(os.cpu_count() or 8))))
    ap.add_argument("--no-vad",  action="store_true")
    args = ap.parse_args()

    os.environ.setdefault("HF_HUB_OFFLINE", "1")
    set_high_priority()

    from faster_whisper import WhisperModel

    inp = Path(args.input).resolve()
    if not inp.exists():
        print(f"ERROR: no existe {inp}", file=sys.stderr); sys.exit(1)

    print(f"[modelo] {args.model}  compute={args.compute}  threads={args.threads}", flush=True)
    t0 = time.time()
    model = WhisperModel(args.model, device="cpu", compute_type=args.compute, cpu_threads=args.threads)
    print(f"[modelo] cargado en {time.time()-t0:.1f}s", flush=True)

    print(f"[transcribir] {inp.name}  lang={args.lang}  beam={args.beam} best_of={args.best_of}", flush=True)
    segments, info = model.transcribe(
        str(inp),
        language=args.lang,
        beam_size=args.beam,
        best_of=args.best_of,
        patience=2.0,
        temperature=[0.0, 0.2, 0.4, 0.6, 0.8, 1.0],
        condition_on_previous_text=True,
        vad_filter=not args.no_vad,
        vad_parameters=dict(min_silence_duration_ms=500),
        word_timestamps=True,
    )
    dur = info.duration or 0
    print(f"[info] idioma={info.language} (p={info.language_probability:.2f})  duracion={dur:.0f}s", flush=True)

    base = inp.with_suffix("")
    txt, srt, segs = [], [], []
    t1 = time.time()
    for i, seg in enumerate(segments, 1):
        text = seg.text.strip()
        txt.append(text)
        srt.append(f"{i}\n{fmt_ts(seg.start)} --> {fmt_ts(seg.end)}\n{text}\n")
        segs.append({"id": i, "start": round(seg.start, 3), "end": round(seg.end, 3), "text": text})
        pct = (seg.end / dur * 100) if dur else 0
        sys.stdout.write(f"\r[progreso] {pct:5.1f}%  seg {i:4d}  t={seg.end:7.1f}s  ({time.time()-t1:5.0f}s proc) ")
        sys.stdout.flush()
    print(flush=True)

    Path(f"{base}.txt").write_text(" ".join(txt).strip() + "\n", encoding="utf-8")
    Path(f"{base}.srt").write_text("\n".join(srt), encoding="utf-8")
    Path(f"{base}.json").write_text(
        json.dumps({"language": info.language, "duration": dur, "segments": segs}, ensure_ascii=False, indent=2),
        encoding="utf-8")

    proc = max(time.time() - t1, 0.001)
    print(f"[ok] escrito: {base.name}.txt / .srt / .json", flush=True)
    print(f"[ok] {time.time()-t0:.0f}s totales  |  {dur/proc:.2f}x realtime  |  {len(segs)} segmentos", flush=True)


if __name__ == "__main__":
    main()
