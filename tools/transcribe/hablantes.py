#!/usr/bin/env python3
"""
hablantes.py — «quién habló cuándo» de una reunión ENTERA, para transcribir turno por turno y escribir
«Persona 1: …» / «Persona 2: …» / «Yo: …».

  python hablantes.py <audio16.wav> [--salida salida16.wav --mic mic16.wav] [--max-hablantes N] [--cache DIR]

  · Se diariza la reunión completa UNA vez (si se hiciera por trozo, «Persona 1» de un trozo no sería la misma que
    la del siguiente). Lo caro (segmentación y huellas de voz) queda en --cache: re-etiquetar es instantáneo.
  · Con --mic (tu micrófono, alineado) y --salida (lo que salió por los parlantes, sin tu voz): tus turnos salen
    del MICRÓFONO —«Yo», sin adivinar— y la diarización se hace sobre la salida, donde solo están los demás.
  · Los turnos cubren el 100 % del audio (partición): transcribir turno por turno no pierde ni un segundo.

Salida: <audio>.hablantes.json = {"config", "hablantes": ["Yo", "Persona 1", …], "turnos": [[ini, fin, "Persona 1"], …]}
Renglones para TeamsTools: [info] … duracion=Ns · [progreso] P% … · [ok] …
"""
import argparse
import json
import os
import sys
import time
from dataclasses import asdict
from pathlib import Path

import numpy as np

import diarizacion as Dz
from motores import SR, leer_wav

# elegida con banco_hablantes.py el 23-sep (daily de 4 voces AR/UY, umbral ÚNICO para limpio y Teams, collar 0,25 s):
#   pyannote + ERes2Net + enlace promedio 0,22 → DER 16,2 % limpio · 9,5 % Teams · 4 de 4 personas en ambas
#   (wespeaker completo 0,28: 11,3 % / 18,0 % con 5 grupos · reverb peor en todo · el viejo centroide 0,70 juntaba
#    las voces del mismo género: 2 grupos de 4). Con voces reales el tope de participantes de Teams frena el sobrecorte.
CONFIG = Dz.Config(segmentador="pyannote", extractor="eres2net", paso_s=2.0, metodo="average", umbral=0.22)
MARCO_YO_S = 0.1
YO_SOBRE_PISO_DB = 15.0      # tu voz: el micrófono 15 dB sobre su ruido de fondo…
YO_VS_SALIDA_DB = -3.0       # …y a la par (o más) que lo que suena por los parlantes: el eco llega mucho más bajo
YO_MIN_S = 0.25              # un «sí» corto es un turno; un golpe de 100 ms, no
YO_HUECO_S = 0.35            # pausas más cortas dentro de tu turno no lo cortan…
YO_PAUSA_S = 1.0             # …y hasta 1 s tampoco, si en esa pausa NO habla nadie por la salida
SALIDA_VOZ_DB = 10.0         # «habla alguien por la salida» = 10 dB sobre su piso
YO_MARGEN_S = 0.15           # el arranque y el final suaves de tus palabras quedan bajo el umbral: margen a cada lado


def db_por_marco(x: np.ndarray, marco: int) -> np.ndarray:
    k = len(x) // marco
    return 20 * np.log10(np.sqrt(np.mean(x[: k * marco].reshape(k, marco) ** 2, axis=1)) + 1e-9)


def corridas(mask: np.ndarray, paso: float, hueco: float, minimo: float) -> list[tuple[float, float]]:
    """Marcos activos → intervalos: huecos cortos se rellenan, los tramos cortos se tiran. O(marcos)."""
    borde = np.diff(np.concatenate([[0], mask.astype(np.int8), [0]]))
    ini, fin = np.flatnonzero(borde == 1), np.flatnonzero(borde == -1)
    out = []
    for a, b in zip(ini * paso, fin * paso):
        if out and a - out[-1][1] < hueco:
            out[-1][1] = b
        else:
            out.append([a, b])
    return [(round(a, 3), round(b, 3)) for a, b in out if b - a >= minimo]


def turnos_yo(mic: np.ndarray, salida: np.ndarray) -> list[tuple[float, float]]:
    """Tus turnos, del micrófono: bien por encima de su piso y comparable con la salida (no es eco). O(n)."""
    m = int(SR * MARCO_YO_S)
    n = min(len(mic), len(salida)) // m
    dm, ds = db_por_marco(mic[: n * m], m), db_por_marco(salida[: n * m], m)
    piso = float(np.percentile(dm, 10))
    activo = (dm > piso + YO_SOBRE_PISO_DB) & (dm > ds + YO_VS_SALIDA_DB)
    # mediana de 3 marcos: un clic suelto no es voz
    suave = activo.copy()
    suave[1:-1] = (activo[:-2].astype(int) + activo[1:-1] + activo[2:]) >= 2
    # tus pausas entre frases (hasta 1 s) no cortan tu turno si en ese hueco nadie habla por la salida: si no, el
    # silencio quedaba como un turno ajeno en el medio de tu frase. O(marcos).
    otros = ds > float(np.percentile(ds, 10)) + SALIDA_VOZ_DB
    borde = np.diff(np.concatenate([[0], suave.astype(np.int8), [0]]))
    ini, fin = np.flatnonzero(borde == 1), np.flatnonzero(borde == -1)
    for a, b in zip(fin[:-1], ini[1:]):                 # huecos entre corridas: [fin_i, ini_i+1)
        if (b - a) * MARCO_YO_S <= YO_PAUSA_S and not otros[a:b].any():
            suave[a:b] = True
    dur = n * MARCO_YO_S
    return [(max(0.0, round(a - YO_MARGEN_S, 3)), min(dur, round(b + YO_MARGEN_S, 3)))
            for a, b in corridas(suave, MARCO_YO_S, YO_HUECO_S, YO_MIN_S)]


def fundir(turnos_otros: list, yo: list, dur: float) -> list[tuple[float, float, str]]:
    """
    Tu voz manda donde está: la partición de los demás se recorta con tus intervalos y se vuelve a unir. Turnos de
    menos de 0,25 s que quedan sueltos se pegan al vecino. O(turnos + intervalos).
    """
    eventos = []
    i = 0
    for a, b in yo:
        while i < len(turnos_otros) and turnos_otros[i][1] <= a:
            eventos.append(list(turnos_otros[i])); i += 1
        # el turno que contiene a «a» se parte
        if i < len(turnos_otros) and turnos_otros[i][0] < a:
            eventos.append([turnos_otros[i][0], a, turnos_otros[i][2]])
        eventos.append([a, b, "Yo"])
        while i < len(turnos_otros) and turnos_otros[i][1] <= b:
            i += 1
        if i < len(turnos_otros) and turnos_otros[i][0] < b:
            turnos_otros[i] = (b, turnos_otros[i][1], turnos_otros[i][2])
    eventos += [list(t) for t in turnos_otros[i:]]
    # unir vecinos del mismo hablante y absorber astillas
    out = []
    for a, b, h in eventos:
        if b - a <= 1e-3:
            continue
        if out and (out[-1][2] == h or b - a < 0.25):
            out[-1][1] = b
        else:
            out.append([a, b, h])
    if out:
        out[0][0], out[-1][1] = 0.0, round(dur, 3)
    return [(round(a, 3), round(b, 3), h) for a, b, h in out]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("audio", help="la mezcla a 16 kHz (audio16.wav)")
    ap.add_argument("--salida", default="", help="solo lo que salió por los parlantes (sin tu micrófono), alineado")
    ap.add_argument("--mic", default="", help="solo tu micrófono (sin los tramos en silencio de Teams), alineado")
    ap.add_argument("--max-hablantes", type=int, default=0, help="tope de personas (Teams sabe cuántos hay)")
    ap.add_argument("--cache", default="", help="carpeta para guardar segmentación y huellas")
    ap.add_argument("--hilos", type=int, default=Dz.HILOS)
    a = ap.parse_args()

    ruta = Path(a.audio).resolve()
    x, sr = leer_wav(ruta)
    if sr != SR:
        print(f"ERROR: se esperaba {SR} Hz", file=sys.stderr)
        return 1
    dur = len(x) / sr
    print(f"[info] hablantes · duracion={dur:.1f}s", flush=True)
    t0 = time.time()

    yo = []
    base = x
    if a.mic and a.salida and Path(a.mic).exists() and Path(a.salida).exists():
        mic, _ = leer_wav(a.mic)
        sal, _ = leer_wav(a.salida)
        yo = turnos_yo(mic, sal)
        base = sal                                   # los demás, sin tu voz: la diarización no se confunde con vos
        print(f"[info] tu micrófono: {len(yo)} turnos · {sum(b - a_ for a_, b in yo):.0f} s de tu voz", flush=True)

    cfg = Dz.Config(**{**asdict(CONFIG), "max_hablantes": a.max_hablantes or None})

    def aviso(que: str, f: float) -> None:
        print(f"[progreso] {f * 100:5.1f}%  {que}", flush=True)

    d = Dz.diarizar(base, cfg, hilos=a.hilos, cache=Path(a.cache) if a.cache else None, al_avanzar=aviso)
    otros = [(i0, i1, f"Persona {k + 1}") for i0, i1, k in d.turnos]
    turnos = fundir(otros, yo, dur) if yo else otros
    nombres = (["Yo"] if yo else []) + [f"Persona {k + 1}" for k in range(d.hablantes)]
    salida = ruta.with_suffix(".hablantes.json")
    tmp = salida.with_suffix(".json.tmp")
    tmp.write_text(json.dumps({"config": asdict(cfg), "duracion": round(dur, 3), "hablantes": nombres,
                               "turnos": [[i0, i1, h] for i0, i1, h in turnos]}, ensure_ascii=False, indent=1), encoding="utf-8")
    os.replace(tmp, salida)
    print(f"[progreso] 100.0%  listo", flush=True)
    print(f"[ok] {len(turnos)} turnos · {len(nombres)} hablantes ({', '.join(nombres)}) · {time.time() - t0:.0f}s", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
