#!/usr/bin/env python3
"""
prueba_hablantes.py — «Persona 1: … / Persona 2: … / Yo: …» de punta a punta, contra la reunión de verdad conocida.

  python prueba_hablantes.py --carpeta banco-pistas [--motor parakeet+b1.5] [--yo martin]

Dos escenarios, con el MISMO código que usa TeamsTools (hablantes.py + transcribe_sherpa.py --cortes):
  A · sin micrófono   se separan las voces sobre la mezcla y se transcribe turno por turno
  B · con micrófono   una de las voces hace de «vos»: su pista va al micrófono (con eco de los demás a −24 dB y
                      40 ms, y ruido de fondo), el resto a la salida. «Yo» tiene que salir del micrófono.

Métricas:
  cpWER    WER con atribución de hablante (CHiME-6): lo de cada persona contra lo de su grupo, con el mejor
           emparejamiento. Si la diarización mezcla dos personas, sube; si la transcripción se come palabras, sube.
  Yo       precisión y cobertura de los intervalos «Yo» contra los turnos reales de esa voz (a 10 ms).
"""
import argparse
import json
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import numpy as np

import consola as ui
from motores import SR, escribir_wav, leer_wav
from texto import cpwer, normalizar

AQUI = Path(__file__).resolve().parent


def correr(args: list[str]) -> str:
    r = subprocess.run([sys.executable] + args, capture_output=True, text=True, encoding="utf-8", cwd=AQUI)
    if r.returncode != 0:
        raise RuntimeError(f"{args[0]} falló:\n{r.stdout[-800:]}\n{r.stderr[-800:]}")
    return r.stdout


def por_hablante_verdad(verdad: dict) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    for t in sorted(verdad["turnos"], key=lambda t: t["ini"]):
        out.setdefault(t["hablante"], []).extend(normalizar(t["texto"]))
    return out


def por_hablante_hip(json_ruta: Path) -> dict[str, list[str]]:
    d = json.loads(json_ruta.read_text("utf-8"))
    out: dict[str, list[str]] = {}
    for s in d["segments"]:
        out.setdefault(s.get("speaker", "?"), []).extend(normalizar(s["text"]))
    return out


def escenario(nombre: str, audio: Path, extra: list[str], motor: str, verdad: dict, tmp: Path) -> dict:
    t0 = time.time()
    correr(["hablantes.py", str(audio), "--cache", str(tmp / "diar")] + extra)
    turnos_json = audio.with_suffix(".hablantes.json")
    h = json.loads(turnos_json.read_text("utf-8"))
    cortes = tmp / f"{audio.stem}.cortes.json"
    cortes.write_text(json.dumps(h["turnos"], ensure_ascii=False), encoding="utf-8")
    t1 = time.time()
    correr(["transcribe_sherpa.py", str(audio), "--motor", motor, "--cortes", str(cortes)])
    t2 = time.time()
    e, mapa = cpwer(por_hablante_verdad(verdad), por_hablante_hip(audio.with_suffix(".json")))
    return {"nombre": nombre, "hablantes": h["hablantes"], "turnos": len(h["turnos"]), "cpwer": e, "mapa": mapa,
            "t_diar": t1 - t0, "t_asr": t2 - t1, "texto": audio.with_suffix(".hablantes.txt").read_text("utf-8"),
            "turnos_lista": h["turnos"]}


def cobertura_yo(turnos: list, verdad: dict, yo: str, dur: float) -> tuple[float, float]:
    """Precisión y cobertura (a 10 ms) de «Yo» contra las palabras reales de esa voz."""
    n = int(dur * 100) + 1
    real, hip = np.zeros(n, bool), np.zeros(n, bool)
    for t in verdad["turnos"]:
        if t["hablante"] == yo:
            for p in t["palabras"]:
                real[int(p["ini"] * 100): int(p["fin"] * 100)] = True
    for a, b, q in turnos:
        if q == "Yo":
            hip[int(a * 100): int(b * 100)] = True
    precision = (real & hip).sum() / max(1, hip.sum())
    cobertura = (real & hip).sum() / max(1, real.sum())
    return float(precision), float(cobertura)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--carpeta", required=True)
    ap.add_argument("--motor", default="parakeet+b1.5")
    ap.add_argument("--yo", default="martin")
    a = ap.parse_args()
    try:   # prioridad baja (la heredan los hijos): la transcripción de una reunión real va primero
        import psutil
        psutil.Process().nice(psutil.BELOW_NORMAL_PRIORITY_CLASS)
    except Exception as ex:
        ui.aviso(f"no pude bajar la prioridad: {ex}")
    carpeta = Path(a.carpeta)
    verdad = json.loads((carpeta / "reunion.json").read_text("utf-8"))
    ui.titulo("hablantes de punta a punta", f"{len(verdad['turnos'])} turnos · 4 voces · motor {a.motor}")
    res = []
    with tempfile.TemporaryDirectory() as d:
        tmp = Path(d)
        # A · sin micrófono: la mezcla limpia
        mezcla = tmp / "mezcla.wav"
        x, _ = leer_wav(carpeta / "reunion-limpio.wav")
        escribir_wav(mezcla, x)
        ui.paso("A · sin micrófono (se separan las voces sobre la mezcla)")
        res.append(escenario("sin micrófono", mezcla, [], a.motor, verdad, tmp))
        ui.ok(f"cpWER {res[-1]['cpwer'].wer:.1%} · {res[-1]['turnos']} turnos · {', '.join(res[-1]['hablantes'])}")

        # B · con micrófono: «yo» al mic (con eco y ruido), el resto a la salida
        voces = {h: leer_wav(carpeta / f"reunion-voz-{h}.wav")[0] for h in verdad["voces"]}
        n = min(len(v) for v in voces.values())
        salida = sum(v[:n] for h, v in voces.items() if h != a.yo)
        eco = np.concatenate([np.zeros(int(0.04 * SR), np.float32), salida[: n - int(0.04 * SR)]]) * 0.063
        rng = np.random.default_rng(3)
        mic = voces[a.yo][:n] + eco + rng.standard_normal(n).astype(np.float32) * 10 ** (-65 / 20)
        mezcla_b = tmp / "mezcla-b.wav"
        escribir_wav(mezcla_b, np.clip(salida + mic, -1, 1))
        escribir_wav(tmp / "salida16.wav", salida)
        escribir_wav(tmp / "mic16.wav", mic)
        ui.paso(f"B · con micrófono («{a.yo}» hace de vos; eco de los demás a −24 dB)")
        r = escenario("con micrófono", mezcla_b, ["--mic", str(tmp / "mic16.wav"), "--salida", str(tmp / "salida16.wav")],
                      a.motor, verdad, tmp)
        r["yo"] = cobertura_yo(r["turnos_lista"], verdad, a.yo, n / SR)
        res.append(r)
        ui.ok(f"cpWER {r['cpwer'].wer:.1%} · «Yo» precisión {r['yo'][0]:.0%} · cobertura {r['yo'][1]:.0%} · mapa {r['mapa']}")

    filas = [[ui.color("crema", r["nombre"]), str(len(r["hablantes"])), str(r["turnos"]), f"{r['cpwer'].wer:.1%}",
              str(r["cpwer"].bor), f"{r['t_diar']:.0f} s", f"{r['t_asr']:.0f} s",
              (f"{r['yo'][0]:.0%} / {r['yo'][1]:.0%}" if "yo" in r else "—")] for r in res]
    ui.tarjeta("hablantes · resultado", ["escenario", "grupos", "turnos", "cpWER", "comidas", "separar", "transcribir",
                                         "«Yo» prec./cob."], filas, derecha=set(range(1, 8)),
               pie="la verdad tiene 4 personas (en B, una de ellas es «Yo»)")
    print("\n" + ui.color("apagado", "  así queda el texto (B, primeros renglones):"))
    for renglon in res[-1]["texto"].splitlines()[:14]:
        print("   " + renglon)
    return 0


if __name__ == "__main__":
    sys.exit(main())
