#!/usr/bin/env python3
"""
cpu.py — cuánto CPU gasta loopcap en régimen (sin el arranque), versión contra versión y función por función.

Cada variante corre DUR segundos; se toma el CPU del proceso entre el segundo 4 y el DUR−1 (el arranque abre
dispositivos y no es representativo). Se repite REP veces y se informa la mediana: la máquina tiene otra carga.
"""
import statistics
import subprocess
import sys
import threading
import time
from pathlib import Path

import psutil

AQUI = Path(__file__).resolve().parent
V2, V3 = AQUI.parent / "loopcap.exe.bak-v2-2026-09-23", AQUI.parent / "loopcap-v3.exe"
DUR, REP = 14, 3
VARIANTES = [
    ("v2 · -all", V2, ["-all", "-quiet", "-keepsilent"]),
    ("v3 · -all", V3, ["-all", "-quiet", "-keepsilent"]),
    ("v3 · -all -levels", V3, ["-all", "-quiet", "-keepsilent", "-levels"]),
    ("v3 · -all -mic -levels", V3, ["-all", "-quiet", "-keepsilent", "-mic", "-levels"]),
]


def medir(exe: Path, args: list[str], carpeta: Path) -> float:
    carpeta.mkdir(parents=True, exist_ok=True)
    for f in carpeta.glob("*"):
        f.unlink()
    exe_real = carpeta / "lc.exe"
    exe_real.write_bytes(exe.read_bytes())          # el .bak no termina en .exe: se copia con nombre ejecutable
    p = subprocess.Popen([str(exe_real), "-o", str(carpeta / "audio.wav"), "-t", str(DUR)] + args,
                         stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    threading.Thread(target=lambda: [None for _ in iter(lambda: p.stdout.read(4096), b"")], daemon=True).start()
    ps = psutil.Process(p.pid)
    t_ini = ps.create_time()
    while time.time() - t_ini < 4:
        time.sleep(0.02)
    t0, c0 = time.time(), sum(ps.cpu_times()[:2])
    while time.time() - t_ini < DUR - 1:
        time.sleep(0.02)
    t1, c1 = time.time(), sum(ps.cpu_times()[:2])
    p.wait(timeout=30)
    return (c1 - c0) / (t1 - t0) * 100


def main() -> int:
    base = AQUI / "salida-cpu"
    print(f"\n  loopcap · CPU en régimen · {REP} corridas de {DUR} s por variante (mediana)\n")
    for nombre, exe, args in VARIANTES:
        vals = [medir(exe, args, base / nombre.replace(" ", "").replace("·", "_")) for _ in range(REP)]
        print(f"  {nombre:26s} {statistics.median(vals):5.2f} % de un núcleo   ({', '.join(f'{v:.2f}' for v in vals)})")
    print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
