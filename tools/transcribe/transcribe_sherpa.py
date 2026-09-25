#!/usr/bin/env python3
"""
transcribe_sherpa.py — transcripción local con los motores de motores.py y EL MISMO CONTRATO que transcribe.py
(whisper), así TeamsTools lo usa sin saber qué motor hay abajo.

  python transcribe_sherpa.py <audio.wav 16 kHz> [--motor cohere|parakeet+b1.5|parakeet|qwen3+kw|conformer]
                              [--lang es] [--threads N] [--palabras-clave "Nimbus,Acme,…"]
                              [--cortes turnos.json]     ← turnos de hablante (de hablantes.py), relativos a ESTE audio

Con --cortes se transcribe TURNO POR TURNO (los de más de 20 s se parten en su punto más callado): cada segmento
sale con su «speaker» y además se escribe <base>.hablantes.txt, un renglón por turno:
  [00:01:12] Persona 2: Bien, encontré el problema. Era una consulta que filtraba mal…

Motores (medidos el 23-sep contra una daily rioplatense con verdad conocida, 557 palabras, ver banco_verdad.py):
  cohere          Cohere Transcribe 2B · WER 3,0 % con audio de Teams y 6,8 % en condición dura · el que MENOS se
                  come (voz baja 18 dB abajo: 6 % comida) · ~0,5× tiempo real en esta máquina       ← por defecto
  parakeet+b1.5   Parakeet TDT 0.6B con penalización de «blanco» 1,5 · WER 4,1 % / 11,1 % · ~0,2× tiempo real
  qwen3+kw        Qwen3-ASR 0.6B con palabras clave de contexto · el mejor con la jerga (83 %) pero 6–16 % de WER

Salidas junto a la entrada:
  <base>.txt   el texto corrido
  <base>.srt   subtítulos, un bloque por tramo
  <base>.json  {"language", "duration", "motor", "segments": [{id, start, end, text, words?: [{w, t, f, c}]}]}

Renglones que lee TeamsTools (los mismos que transcribe.py):
  [modelo] … cargado en Ns · [transcribir] … · [info] idioma=es … duracion=Ns · [progreso] P% … · [ok] …

Sin VAD: el audio se corta en tramos de hasta 20 s en el marco más callado y se transcribe el 100 % (un detector
de voz DESCARTA lo que cree que no es habla, y ahí es donde un transcriptor se come palabras).
"""
import argparse
import json
import os
import sys
import time
from pathlib import Path

import motores as M


def fmt_ts(sec: float) -> str:
    h, m, s = int(sec // 3600), int((sec % 3600) // 60), int(sec % 60)
    return f"{h:02d}:{m:02d}:{s:02d},{int(round((sec - int(sec)) * 1000)) % 1000:03d}"


def parsear_motor(spec: str) -> dict:
    """«parakeet+b1.5» → nombre + penalización; «qwen3+kw» → con palabras clave."""
    partes = spec.split("+")
    kw = {"nombre": partes[0], "penal_blanco": 0.0, "clave": False}
    for p in partes[1:]:
        if p.startswith("b"):
            kw["penal_blanco"] = float(p[1:])
        elif p == "kw":
            kw["clave"] = True
        else:
            raise SystemExit(f"ERROR: variante desconocida «{p}» en {spec}")
    if kw["nombre"] not in M.NOMBRES:
        raise SystemExit(f"ERROR: motor desconocido «{kw['nombre']}» (hay: {', '.join(M.NOMBRES)})")
    return kw


def plan_de_turnos(turnos: list, x, dur: float) -> tuple[list, dict]:
    """
    Turnos de hablante → trozos a transcribir: los de más de 20 s se parten en su punto más callado (Qwen3/Cohere
    tienen techo de tokens). Devuelve los cortes y, por corte, de quién es. O(turnos + marcos).
    """
    db = M.energia_db(x)
    cortes, quien = [], {}
    for ini, fin, hablante in turnos:
        ini, fin = max(0.0, float(ini)), min(dur, float(fin))
        if fin - ini < 0.05:
            continue
        for a, b in M.tramos(db, ini, fin):
            cortes.append((a, b))
            quien[(a, b)] = hablante
    return cortes, quien


def texto_por_hablante(segs: list) -> str:
    """Un renglón por turno: los segmentos seguidos de la misma persona se juntan. «[hh:mm:ss] Persona 1: …»"""
    renglones, actual, ini, partes = [], None, 0.0, []
    for s in segs:
        h = s.get("speaker", "")
        if h != actual and partes:
            renglones.append(f"[{fmt_ts(ini)[:8]}] {actual}: {' '.join(partes)}")
            partes = []
        if not partes:
            actual, ini = h, s["start"]
        partes.append(s["text"])
    if partes:
        renglones.append(f"[{fmt_ts(ini)[:8]}] {actual}: {' '.join(partes)}")
    return "\n".join(renglones) + "\n"


def escribir_atomico(ruta: Path, texto: str) -> None:
    """Primero a .tmp y después renombrado: un corte a mitad no deja un .json truncado que parezca bueno."""
    tmp = ruta.with_name(ruta.name + ".tmp")
    tmp.write_text(texto, encoding="utf-8")
    os.replace(tmp, ruta)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("input")
    ap.add_argument("--motor", default=os.environ.get("ASR_MOTOR", "cohere"))
    ap.add_argument("--lang", default="es")
    ap.add_argument("--threads", type=int, default=M.HILOS)
    ap.add_argument("--palabras-clave", default="", help="coma separadas; solo las usan qwen3 y whisper")
    ap.add_argument("--cortes", default="", help="JSON [[ini, fin, hablante], …] relativo a este audio")
    a = ap.parse_args()

    entrada = Path(a.input).resolve()
    if not entrada.exists():
        print(f"ERROR: no existe {entrada}", file=sys.stderr)
        return 1
    x, sr = M.leer_wav(entrada)
    if sr != M.SR:
        print(f"ERROR: se esperaba {M.SR} Hz y llegó {sr} Hz", file=sys.stderr)
        return 1
    dur = len(x) / sr
    kw = parsear_motor(a.motor)
    claves = [c.strip() for c in a.palabras_clave.split(",") if c.strip()] if kw["clave"] or kw["nombre"] == "whisper" else []

    t0 = time.time()
    motor = M.crear(kw["nombre"], hilos=a.threads, idioma=a.lang, palabras_clave=claves, penal_blanco=kw["penal_blanco"])
    print(f"[modelo] {a.motor} (sherpa-onnx) · cargado en {motor.carga_s:.1f}s", flush=True)
    print(f"[transcribir] {entrada.name}  lang={a.lang}  motor={a.motor}", flush=True)
    print(f"[info] idioma={a.lang} (p=1.00)  duracion={dur:.1f}s", flush=True)

    t1 = time.time()

    def avance(f: float) -> None:
        sys.stdout.write(f"\r[progreso] {f * 100:5.1f}%  t={f * dur:7.1f}s  ({time.time() - t1:5.0f}s proc) ")
        sys.stdout.flush()

    cortes, quien = None, {}
    if a.cortes:
        cortes, quien = plan_de_turnos(json.loads(Path(a.cortes).read_text("utf-8")), x, dur)
    trozos = M.transcribir(motor, x, sr, cortes=cortes, al_avanzar=avance)
    print(flush=True)
    segs = []
    for ini, fin, tr in trozos:
        if not tr.texto:
            continue
        seg = {"id": len(segs) + 1, "start": round(ini, 3), "end": round(fin, 3), "text": tr.texto}
        if (ini, fin) in quien:
            seg["speaker"] = quien[(ini, fin)]
        if motor.marcas_reales and tr.palabras:
            seg["words"] = [{"w": p.w, "t": p.ini, "f": p.fin, "c": p.conf} for p in tr.palabras]
        segs.append(seg)

    base = entrada.with_suffix("")
    if quien:
        escribir_atomico(Path(f"{base}.hablantes.txt"), texto_por_hablante(segs))
    escribir_atomico(Path(f"{base}.txt"), " ".join(s["text"] for s in segs).strip() + "\n")
    escribir_atomico(Path(f"{base}.srt"), "\n".join(f"{s['id']}\n{fmt_ts(s['start'])} --> {fmt_ts(s['end'])}\n{s['text']}\n" for s in segs))
    # el .json va ÚLTIMO: es la marca de «trozo terminado» que mira TeamsTools
    escribir_atomico(Path(f"{base}.json"), json.dumps({"language": a.lang, "duration": round(dur, 3), "motor": a.motor,
                                                       "hablantes": bool(quien), "segments": segs}, ensure_ascii=False, indent=1))
    proc = max(time.time() - t1, 0.001)
    print(f"[ok] escrito: {base.name}.txt / .srt / .json", flush=True)
    print(f"[ok] {time.time() - t0:.0f}s totales  |  {dur / proc:.2f}x tiempo real  |  {len(segs)} segmentos", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
