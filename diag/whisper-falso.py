#!/usr/bin/env python3
"""
whisper-falso.py — un transcribe.py DETERMINISTA y sin modelo, para probar el pipeline de TeamsTools en segundos.

Habla el mismo idioma que Box\\transcribe\\transcribe.py (mismos renglones [modelo] / [info] duracion= /
[progreso] N% y los mismos .txt/.srt/.json al lado de la entrada), pero en vez de reconocer voz escribe un segmento
cada 5 s cuyo TEXTO es «<trozo>-<segundo local>» (ej. «t03-045»). Así, en la transcripción unida, cada segmento dice
de qué trozo vino y en qué segundo local empezaba: la prueba verifica que su tiempo global = inicio del trozo + ese
segundo, al centésimo.

FALSO_FALLA_EN=t03  hace que el trozo t03 falle la PRIMERA vez (deja una marca .fallo): sirve para probar que un
                    reintento retoma sin rehacer los trozos que ya estaban verificados.
"""
import json
import os
import sys
import time
import wave
from pathlib import Path


def main() -> int:
    entrada = Path(sys.argv[1]).resolve()
    with wave.open(str(entrada), "rb") as w:
        duracion = w.getnframes() / w.getframerate()
    print("[modelo] falso · cargado en 0.0s", flush=True)
    print(f"[transcribir] {entrada.name}  lang=falso", flush=True)
    print(f"[info] idioma=es (p=1.00)  duracion={duracion:.0f}s", flush=True)

    falla = os.environ.get("FALSO_FALLA_EN", "")
    marca = Path(str(entrada) + ".fallo")
    if falla and entrada.stem == falla and not marca.exists():
        marca.write_text("falló a propósito una vez", encoding="utf-8")
        print(f"ERROR: falla simulada en {entrada.stem}", file=sys.stderr, flush=True)
        return 3

    segmentos, t, i = [], 0.0, 1
    while t < duracion - 0.25:
        fin = min(duracion, t + 5.0)
        segmentos.append({"id": i, "start": round(t, 3), "end": round(fin, 3), "text": f"{entrada.stem}-{int(round(t)):03d}"})
        sys.stdout.write(f"\r[progreso] {fin / duracion * 100:5.1f}%  seg {i:4d}  t={fin:7.1f}s  (    0s proc) ")
        sys.stdout.flush()
        t, i = fin, i + 1
        time.sleep(0.02)
    print(flush=True)

    base = entrada.with_suffix("")
    Path(f"{base}.txt").write_text(" ".join(s["text"] for s in segmentos) + "\n", encoding="utf-8")
    Path(f"{base}.srt").write_text("\n".join(f'{s["id"]}\n{s["start"]} --> {s["end"]}\n{s["text"]}\n' for s in segmentos), encoding="utf-8")
    Path(f"{base}.json").write_text(json.dumps({"language": "es", "duration": duracion, "segments": segmentos}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[ok] escrito: {base.name}.txt / .srt / .json · {len(segmentos)} segmentos", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
