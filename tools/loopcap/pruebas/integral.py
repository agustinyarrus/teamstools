#!/usr/bin/env python3
"""
integral.py — prueba de punta a punta de loopcap 3 con audio REAL y sin hacer ruido.

  1. arranca loopcap con -all -mic -levels (como lo lanza TeamsTools) por DURACION segundos
  2. a los 2 s abre el MICRÓFONO desde este proceso (como haría Teams): loopcap tiene que sumarlo solo
  3. a los 4 s reproduce un tono de 1 kHz a −30 dBFS en los parlantes VIRTUALES de NoMachine (no se oye)
  4. a los 7 s suelta el micrófono: loopcap tiene que dejarlo «en espera» ~4 s después
  5. verifica: eventos DEV, niveles LV, pistas WAV, que el tono caiga en su segundo exacto (alineación con el
     reloj de la grabación aunque esa salida no entregue paquetes cuando nadie suena) y que el mic arranque
     con silencio de relleno (se sumó tarde)
"""
import json
import subprocess
import sys
import threading
import time
import wave
from datetime import datetime
from pathlib import Path

import numpy as np
import psutil
import sounddevice as sd

AQUI = Path(__file__).resolve().parent
EXE = AQUI.parent / "loopcap-v3.exe"
DURACION = 16
MIC_ABRE, MIC_CIERRA, TONO_INI, TONO_DUR = 2.0, 7.0, 4.0, 2.0
C = {"ok": "\033[38;2;181;223;168m", "no": "\033[38;2;243;185;210m", "ap": "\033[38;2;88;93;110m",
     "cy": "\033[38;2;143;214;204m", "x": "\033[0m"}


def dispositivo(nombre_parcial: str, entrada: bool) -> int:
    w = next(i for i, h in enumerate(sd.query_hostapis()) if "WASAPI" in h["name"])
    for i, d in enumerate(sd.query_devices()):
        canales = d["max_input_channels"] if entrada else d["max_output_channels"]
        if d["hostapi"] == w and canales > 0 and nombre_parcial.lower() in d["name"].lower():
            return i
    raise SystemExit(f"no encuentro «{nombre_parcial}»")


def leer_wav(p: Path):
    with wave.open(str(p)) as w:
        sr, n, ch = w.getframerate(), w.getnframes(), w.getnchannels()
        x = np.frombuffer(w.readframes(n), np.int16).astype(np.float32) / 32768
    return x.reshape(-1, ch).mean(1), sr


def main() -> int:
    tmp = Path(sys.argv[1]) if len(sys.argv) > 1 else AQUI / "salida-integral"
    tmp.mkdir(parents=True, exist_ok=True)
    for f in tmp.glob("*"):
        f.unlink()
    estado = tmp / "estado.json"
    t_lanzado = time.time()
    p = subprocess.Popen([str(EXE), "-o", str(tmp / "audio.wav"), "-all", "-mic", "-levels", "-keepsilent", "-quiet",
                          "-status", str(estado), "-t", str(DURACION)],
                         stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    lv, dev, err = [], [], []

    def leer_out():
        for l in p.stdout:
            (lv if l.startswith("LV") else dev).append((time.time(), l.strip()))

    def leer_err():
        for l in p.stderr:
            err.append(l.rstrip())
    threading.Thread(target=leer_out, daemon=True).start()
    threading.Thread(target=leer_err, daemon=True).start()

    # el cero de loopcap sale de su estado (RFC3339 con nanos): contra eso se miden los tiempos
    cero = None
    while cero is None and time.time() - t_lanzado < 5:
        try:
            cero = datetime.fromisoformat(json.loads(estado.read_text("utf-8"))["started"]).timestamp()
        except Exception:
            time.sleep(0.05)
    if cero is None:
        print(f"{C['no']}✗ loopcap no publicó su estado{C['x']}"); p.kill(); return 1

    mic_id, parl_id = dispositivo("Microphone Array", True), dispositivo("NoMachine", False)
    sr_out = int(sd.query_devices(parl_id)["default_samplerate"])
    marcas = {}
    while time.time() < cero + MIC_ABRE:
        time.sleep(0.01)
    with sd.InputStream(device=mic_id, channels=1, samplerate=48000) as _mic:
        marcas["mic_abre"] = time.time() - cero
        while time.time() < cero + TONO_INI:
            time.sleep(0.01)
        t = np.arange(int(TONO_DUR * sr_out)) / sr_out
        tono = (0.0316 * np.sin(2 * np.pi * 1000 * t)).astype(np.float32)      # −30 dBFS
        marcas["tono"] = time.time() - cero
        sd.play(tono, sr_out, device=parl_id, blocking=True)
        while time.time() < cero + MIC_CIERRA:
            time.sleep(0.01)
    marcas["mic_cierra"] = time.time() - cero
    # CPU de loopcap: se mide un segundo antes del final (medir, no suponer)
    while time.time() < cero + DURACION - 1:
        time.sleep(0.05)
    try:
        ct = psutil.Process(p.pid).cpu_times()
        cpu_pct = (ct.user + ct.system) / (time.time() - cero) * 100
    except psutil.Error:
        cpu_pct = None
    p.wait(timeout=DURACION + 30)
    time.sleep(0.3)

    ok_total = True

    def chequeo(bien: bool, texto: str):
        nonlocal ok_total
        ok_total &= bien
        print(f"  {C['ok'] + '✓' if bien else C['no'] + '✗'}{C['x']} {texto}")

    print(f"\n{C['cy']}loopcap 3 · prueba integral{C['x']} {C['ap']}· {DURACION} s · marcas {json.dumps({k: round(v, 2) for k, v in marcas.items()})}{C['x']}")
    evs = [(round(t - cero, 2), l) for t, l in dev]
    for t, l in evs:
        print(f"  {C['ap']}{t:6.2f} s  {l}{C['x']}")
    mic_mas = [t for t, l in evs if l.startswith("DEV + ") and " mic " in l]
    mic_z = [t for t, l in evs if l.startswith("DEV z ") and " mic " in l]
    chequeo(sum(1 for _, l in evs if l.startswith("DEV + ") and " salida " in l) == 3, "las 3 salidas se sumaron al arrancar")
    chequeo(len(mic_mas) == 1 and 0 <= mic_mas[0] - marcas["mic_abre"] <= 0.8,
            f"el micrófono se sumó solo cuando otro lo abrió ({mic_mas[0] - marcas['mic_abre']:.2f} s después)" if mic_mas else "el micrófono NO se sumó")
    chequeo(len(mic_z) == 1 and 3.5 <= mic_z[0] - marcas["mic_cierra"] <= 5.5,
            f"y quedó en espera {mic_z[0] - marcas['mic_cierra']:.1f} s después de que lo soltaron" if mic_z else "el micrófono no pasó a espera")
    ms = np.array([int(l.split()[1]) for _, l in lv])
    paso = float(np.median(np.diff(ms))) if len(ms) > 2 else 0
    rate_lv = len(lv) / max(1e-9, (ms[-1] - ms[0]) / 1000) if len(ms) > 2 else 0
    chequeo(rate_lv >= 12 and 40 <= paso <= 70,
            f"{len(lv)} renglones de nivel · {rate_lv:.1f} por segundo · paso mediano {paso:.0f} ms (reloj de loopcap)")

    # niveles del parlante virtual mientras suena el tono
    idx_parl = next((l.split()[2] for _, l in evs if l.startswith("DEV + ") and "NoMachine" in l), None)
    en_tono = [float(tok.split(":")[1]) for t, l in lv if marcas["tono"] + 0.3 <= t - cero <= marcas["tono"] + 1.7
               for tok in l.split()[2:] if tok.split(":")[0] == idx_parl]
    chequeo(bool(en_tono) and abs(np.median(en_tono) + 30) < 3, f"el nivel en vivo del tono da {np.median(en_tono):.1f} dB (se esperaba −30)" if en_tono else "no hubo niveles del parlante virtual")

    wavs = sorted(tmp.glob("*.wav"))
    for w in wavs:
        x, sr = leer_wav(w)
        print(f"  {C['ap']}{w.name:70s} {len(x) / sr:6.2f} s{C['x']}")
    parl = next(w for w in wavs if "NoMachine" in w.name)
    x, sr = leer_wav(parl)
    m = int(sr * 0.01)
    k = len(x) // m
    db = 20 * np.log10(np.sqrt(np.mean(x[: k * m].reshape(k, m) ** 2, axis=1)) + 1e-9)
    on = np.flatnonzero(db > -45)
    # la vara de la alineación: el primer nivel alto del parlante según el RELOJ DE LOOPCAP (campo ms del LV)
    t_lv = next((int(l.split()[1]) / 1000 for _, l in lv for tok in l.split()[2:]
                 if tok.split(":")[0] == idx_parl and float(tok.split(":")[1]) > -40), None)
    # el archivo ubica el tono por la marca QPC de CAPTURA: tiene que caer después de pedirlo y no después de que
    # loopcap lo vio llegar (la llegada tiene la demora del dispositivo que se despierta)
    chequeo(len(on) > 0 and t_lv is not None and marcas["tono"] <= on[0] * 0.01 <= t_lv + 0.02,
            f"el tono cae en el segundo {on[0] * 0.01:.2f} del archivo: después de pedirlo ({marcas['tono']:.2f}) y antes de que loopcap lo recibiera ({t_lv:.2f})" if len(on) and t_lv else "el tono no quedó grabado")
    fin = json.loads(estado.read_text("utf-8"))
    chequeo(abs(len(x) / sr - fin["elapsed"]) < 0.3,
            f"la pista del parlante dura {len(x) / sr:.2f} s y loopcap grabó {fin['elapsed']:.2f} s, aunque casi todo el tiempo nadie sonó")
    mic = next((w for w in wavs if ".mic__" in w.name), None)
    if mic:
        xm, srm = leer_wav(mic)
        pre = xm[: int((marcas["mic_abre"] - 0.3) * srm)]
        chequeo(len(pre) > 0 and np.max(np.abs(pre)) == 0, f"la pista del mic arranca con {marcas['mic_abre']:.1f} s de silencio de relleno (se sumó tarde)")
        chequeo(np.sqrt(np.mean(xm[int(marcas['mic_abre'] * srm):] ** 2)) > 1e-5, "y después tiene audio del micrófono")
    else:
        chequeo(False, "no hay pista de micrófono")
    chequeo(fin["state"] == "done" and fin.get("levels_dropped", 0) == 0, f"estado final «{fin['state']}» · niveles descartados {fin.get('levels_dropped')}")
    if cpu_pct is not None:
        chequeo(cpu_pct < 5, f"loopcap usó {cpu_pct:.1f} % de UN núcleo con 4 pistas, vigía y niveles")
    if err:
        print(f"  {C['ap']}— consola de loopcap —{C['x']}")
        for l in err[-14:]:
            print(f"  {C['ap']}{l}{C['x']}")
    print(f"\n  {C['ok'] + 'TODO BIEN' if ok_total else C['no'] + 'HAY FALLAS'}{C['x']}\n")
    return 0 if ok_total else 1


if __name__ == "__main__":
    sys.exit(main())
