#!/usr/bin/env python3
"""
banco_verdad.py — mide cada motor contra una reunión con VERDAD CONOCIDA (la de reunion_sintetica.py).

Lo que importa es no comerse palabras, así que además del WER se mide DÓNDE se pierden:
  comidas       palabras de la referencia que no aparecen en la transcripción (borrados)
  por hablante  Camila habla 12 dB más bajo en «teams» y 18 dB en «duro»: ¿la escucha el motor?
  cortas        respuestas de una o dos palabras («Dale.», «Claro.», «Sí, sí.»), las que más se pierden
  arranques     primera palabra de cada turno (un corte o un VAD suelen morderla)
  jerga         deploy, UAT, pull request, Nimbus, Firebase, nombres propios: ¿las escribe bien?
  velocidad     RTF = tiempo de proceso / duración (menos es mejor; OJO si hay otra carga en la máquina)

Motores: parakeet, conformer, qwen3, cohere, whisper, con variantes:
  +bX   penalización del «blanco» X en los transductores (empuja a emitir palabras: menos borrados)
  +kw   palabras clave de contexto (Qwen3 y whisper) con la jerga y los nombres del guion

  python banco_verdad.py --motores parakeet,parakeet+b1,conformer,qwen3+kw,cohere --condiciones limpio,teams,duro
"""
import argparse
import gc
import json
import sys
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np

import consola as ui
import motores as M
from texto import CERO, Errores, alinear, normalizar

AQUI = Path(__file__).resolve().parent


@dataclass(frozen=True, slots=True)
class PalabraRef:
    w: str
    hablante: str
    turno: int
    primera: bool
    corta: bool           # el turno tiene ≤ 2 palabras
    clave: bool           # es parte de una palabra clave


def referencia(verdad: dict) -> list[PalabraRef]:
    """Palabras de la verdad en orden de aparición (por inicio de turno), con sus atributos para el desglose."""
    claves = {w for k in verdad["palabras_clave"] for w in normalizar(k)}
    turnos = sorted(enumerate(verdad["turnos"]), key=lambda it: it[1]["ini"])
    out = []
    for i, t in turnos:
        ws = normalizar(t["texto"])
        for j, w in enumerate(ws):
            out.append(PalabraRef(w, t["hablante"], i, j == 0, len(ws) <= 2, w in claves))
    return out


def parsear(spec: str) -> dict:
    partes = spec.split("+")
    kw = {"nombre": partes[0], "penal_blanco": 0.0, "clave": False}
    for p in partes[1:]:
        if p.startswith("b"):
            kw["penal_blanco"] = float(p[1:])
        elif p == "kw":
            kw["clave"] = True
        else:
            raise ValueError(f"variante desconocida «{p}» en {spec}")
    return kw


def medir(ref: list[PalabraRef], hip: list[str]) -> dict:
    """Una alineación y todos los desgloses. O(n·m) la alineación (nativa), O(n) el resto con máscaras."""
    e, destino = alinear([p.w for p in ref], hip)
    arr = lambda f: np.array([f(p) for p in ref], bool)
    comida = destino == 2
    tasa = lambda m: float(comida[m].mean()) if m.any() else 0.0
    bien = lambda m: float((destino[m] == 0).mean()) if m.any() else 0.0
    hablantes = sorted({p.hablante for p in ref})
    return {
        "wer": round(e.wer, 4), "sus": e.sus, "bor": e.bor, "ins": e.ins, "ref": e.ref,
        "comidas_por_hablante": {h: round(tasa(arr(lambda p, h=h: p.hablante == h)), 4) for h in hablantes},
        "comidas_cortas": round(tasa(arr(lambda p: p.corta)), 4),
        "comidas_arranque": round(tasa(arr(lambda p: p.primera)), 4),
        "jerga_bien": round(bien(arr(lambda p: p.clave)), 4),
        "palabras_comidas": [ref[i].w for i in np.flatnonzero(comida)][:80],
    }


def correr(specs: list[str], condiciones: list[str], hilos: int, carpeta: Path, salida: Path) -> dict:
    verdad = json.loads((carpeta / "reunion.json").read_text("utf-8"))
    ref = referencia(verdad)
    audios = {c: M.leer_wav(carpeta / f"reunion-{c}.wav")[0] for c in condiciones}
    previo = json.loads(salida.read_text("utf-8")) if salida.exists() else {}
    res = previo.get("resultados", {})
    ui.titulo("banco con verdad", f"{len(ref)} palabras · {len(verdad['turnos'])} turnos · {verdad['dur']:.0f} s · "
                                  f"condiciones {', '.join(condiciones)}")
    for spec in specs:
        kw = parsear(spec)
        ui.paso(f"{spec}")
        try:
            motor = M.crear(kw["nombre"], hilos=hilos, penal_blanco=kw["penal_blanco"],
                            palabras_clave=verdad["palabras_clave"] if kw["clave"] else ())
        except Exception as ex:
            ui.error(f"no cargó: {type(ex).__name__}: {ex}")
            continue
        for c in condiciones:
            x = audios[c]
            t0 = time.time()
            trozos = M.transcribir(motor, x, al_avanzar=lambda f: ui.progreso(f"{spec} · {c}", f))
            proc = time.time() - t0
            texto = " ".join(tr.texto for _, _, tr in trozos if tr.texto)
            m = medir(ref, normalizar(texto))
            m.update({"rtf": round(proc / (len(x) / M.SR), 4), "carga_s": motor.carga_s, "texto": texto})
            res.setdefault(spec, {})[c] = m
            ch = m["comidas_por_hablante"]
            ui.ok(f"{c:7s} WER {m['wer']:6.1%} · comidas {m['bor']:3d} · Camila {ch.get('camila', 0):5.1%} · "
                  f"cortas {m['comidas_cortas']:5.1%} · jerga {m['jerga_bien']:5.1%} · RTF {m['rtf']:.3f}")
            salida.write_text(json.dumps({"verdad": str(carpeta / 'reunion.json'), "resultados": res},
                                         ensure_ascii=False, indent=1), encoding="utf-8")
        del motor
        gc.collect()
    return res


def tarjetas(res: dict, condiciones: list[str]) -> None:
    for c in condiciones:
        filas = []
        orden = sorted((s for s in res if c in res[s]), key=lambda s: res[s][c]["wer"])
        for s in orden:
            m = res[s][c]; ch = m["comidas_por_hablante"]
            filas.append([ui.color("crema", s), f"{m['wer']:.1%}", f"{m['sus']}", ui.color("rosa", str(m["bor"])),
                          f"{m['ins']}", f"{ch.get('camila', 0):.0%}", f"{m['comidas_cortas']:.0%}",
                          f"{m['comidas_arranque']:.0%}", f"{m['jerga_bien']:.0%}", f"{m['rtf']:.3f}"])
        ui.tarjeta(f"condición {c}", ["motor", "WER", "sust.", "comidas", "insert.", "Camila", "cortas", "arranque",
                                      "jerga ok", "RTF"], filas, derecha=set(range(1, 10)),
                   pie="comidas = palabras de la referencia que no aparecen · Camila/cortas/arranque = % comido")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--motores", default="parakeet,conformer,qwen3,cohere")
    ap.add_argument("--condiciones", default="limpio,teams,duro")
    ap.add_argument("--carpeta", default=str(AQUI / "banco"))
    ap.add_argument("--salida", default="")
    ap.add_argument("--hilos", type=int, default=M.HILOS)
    ap.add_argument("--solo-tarjetas", action="store_true")
    a = ap.parse_args()
    carpeta = Path(a.carpeta)
    salida = Path(a.salida) if a.salida else carpeta / "resultados-asr.json"
    conds = [c.strip() for c in a.condiciones.split(",") if c.strip()]
    if a.solo_tarjetas:
        res = json.loads(salida.read_text("utf-8"))["resultados"]
    else:
        res = correr([s.strip() for s in a.motores.split(",") if s.strip()], conds, a.hilos, carpeta, salida)
    tarjetas(res, conds)
    return 0


if __name__ == "__main__":
    sys.exit(main())
