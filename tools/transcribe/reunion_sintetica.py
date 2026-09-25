#!/usr/bin/env python3
"""
reunion_sintetica.py — una daily de desarrollo en castellano rioplatense con VERDAD CONOCIDA palabra por palabra.

Para medir de verdad cuántas palabras «se come» un transcriptor y si la diarización sabe quién habló, hace falta
una reunión de la que se sepa TODO: qué se dijo, quién, y en qué milisegundo. Esta la arma sola:

  guion     48 turnos entre 4 personas (voseo, muletillas, jerga: deploy, UAT, pull request, hotfix…, números,
            nombres propios, respuestas de una palabra y 3 que se PISAN con el turno anterior)
  voces     neuronales de Microsoft Edge: Tomás y Elena (Argentina), Mateo y Valentina (Uruguay); cada una con su
            ritmo (Lucas habla rápido). Cada palabra llega con su tiempo exacto (WordBoundary).
  línea     la MISMA línea de tiempo para todas las condiciones (semilla fija): pausas naturales y solapamientos
  condición limpio · teams (niveles desparejos, Camila 12 dB más baja, banda de 100 Hz a 7 kHz, Opus 20 kbps VOIP
            ida y vuelta, ruido rosa a 25 dB SNR) · duro (Camila 18 dB abajo, Opus 12 kbps, ruido a 15 dB SNR)

Salida en --salida (por defecto Box/transcribe/banco): reunion-<condición>.wav (16 kHz mono), reunion.json (la
verdad), reunion-guion.txt y, con --pistas, una pista por persona (reunion-voz-<hablante>.wav, nivel de «limpio»):
sirven para simular «tu micrófono» y «lo que salió por los parlantes» por separado. La síntesis se guarda en
tts-cache/ por huella del texto: volver a correr no baja nada.
"""
import argparse
import asyncio
import hashlib
import json
import subprocess
import sys
import tempfile
from dataclasses import dataclass, asdict
from pathlib import Path

import numpy as np

import consola as ui
from motores import SR, escribir_wav, leer_wav
from texto import normalizar

AQUI = Path(__file__).resolve().parent
SEMILLA = 23
TICKS_POR_S = 10_000_000          # edge-tts informa offset/duración en unidades de 100 ns
RMS_OBJETIVO_DB = -23.0           # cada turno se lleva a este nivel sobre sus marcos con voz, antes de la condición
INTENTOS_RED = 4

VOCES = {
    "martin": ("es-AR-TomasNeural", "+4%"),
    "sofia": ("es-AR-ElenaNeural", "+0%"),
    "lucas": ("es-UY-MateoNeural", "+14%"),
    "camila": ("es-UY-ValentinaNeural", "-3%"),
}

# (hablante, texto, pisa_al_anterior)
GUION = [
    ("martin", "Bueno, arrancamos con la daily. Hoy somos cuatro, así que vamos rápido.", False),
    ("sofia", "Dale. Yo ayer terminé el fix del login y lo subí a la rama develop.", False),
    ("martin", "¿Pasaron los tests?", False),
    ("sofia", "Sí, todos en verde. Lo único que me falta es que alguien me apruebe el pull request.", False),
    ("lucas", "Yo te lo reviso después de la reunión, mandame el link por el chat.", False),
    ("sofia", "Genial, gracias.", False),
    ("martin", "Lucas, ¿vos cómo venís con lo de las cerraduras que no aparecían en el dashboard?", False),
    ("lucas", "Bien, encontré el problema. Era una consulta que filtraba mal por tipo de dispositivo y dejaba afuera "
              "a las cerraduras wifi. Ya lo corregí en local y ahora aparecen las doce que teníamos de prueba.", False),
    ("sofia", "Claro.", True),
    ("martin", "Buenísimo. ¿Lo probaste contra la base de UAT?", False),
    ("lucas", "Todavía no, porque me faltan tres columnas nuevas en la tabla de modelos. Si hago el deploy sin eso, "
              "se rompe la sincronización de cuentas.", False),
    ("camila", "Esas columnas las agrego yo, tengo el script casi listo.", False),
    ("martin", "Perfecto, Camila. ¿Para cuándo lo tendrías?", False),
    ("camila", "Hoy a la tarde, a más tardar a las cinco.", False),
    ("lucas", "Ah, bárbaro. Entonces mañana a primera hora hago el deploy.", False),
    ("martin", "Ojo con eso, que mañana a las diez tenemos la demo con el cliente y no quiero sorpresas en el ambiente.",
     False),
    ("lucas", "Tenés razón. Lo dejo para después del mediodía.", False),
    ("sofia", "Una pregunta, ¿la demo es con la gente de Nimbus o con los de Acme?", False),
    ("martin", "Con los dos. Van a estar los de producto y dos personas de soporte.", False),
    ("sofia", "Ok, entonces preparo la parte de invitados, que es lo que más les interesa.", False),
    ("camila", "Si querés te paso los casos de prueba que armé la semana pasada.", False),
    ("sofia", "Sí, por favor, me vienen bárbaro.", False),
    ("martin", "Bien. Camila, contanos lo tuyo.", False),
    ("camila", "Yo estuve con el tema de las notificaciones. En Android llegan bien, pero en iOS a veces se pierden "
               "cuando la app está cerrada. Creo que es por el token, que se vence y no lo estamos renovando.", False),
    ("martin", "Sí, sí.", True),
    ("lucas", "Eso me pasó también con el timbre, ¿te acordás?", False),
    ("camila", "Sí, puede ser lo mismo. Mañana lo reviso con los logs del servidor.", False),
    ("martin", "¿Hay algún ticket abierto por eso?", False),
    ("camila", "Sí, lo abrí ayer. Le puse prioridad alta porque ya hubo dos reclamos.", False),
    ("martin", "Bien. Sofía, ¿vos podés darle una mano con eso cuando termines lo de la demo?", False),
    ("sofia", "Sí, obvio. Igual yo no conozco mucho la parte de iOS, así que me va a tener que explicar un poco.",
     False),
    ("camila", "No hay problema, lo vemos juntas.", False),
    ("martin", "Ahora, un tema aparte. El jueves se congela la versión, así que lo que no esté mergeado para el "
               "miércoles a la noche queda para el próximo sprint.", False),
    ("sofia", "Dale.", True),
    ("lucas", "¿Y el hotfix del firmware? Ese no puede esperar.", False),
    ("martin", "Ese sí entra, lo hablé con el líder técnico. Es el único que tiene excepción.", False),
    ("lucas", "Ok, perfecto.", False),
    ("sofia", "Che, otra cosa. El pipeline de integración continua anda lentísimo. Ayer tardó cuarenta minutos en "
              "compilar.", False),
    ("lucas", "Sí, lo vi. Es porque se llenó el disco del agente. Hay que limpiar las imágenes viejas.", False),
    ("martin", "¿Quién se encarga de eso?", False),
    ("lucas", "Lo hago yo, son cinco minutos.", False),
    ("martin", "Dale. Bueno, ¿algún bloqueo que no hayamos hablado?", False),
    ("sofia", "Por mi parte no.", False),
    ("camila", "Yo necesito acceso a la consola de Firebase para ver los envíos.", False),
    ("martin", "Te lo pido hoy mismo. Anotalo en el canal así no me olvido.", False),
    ("camila", "Listo, ya lo anoto.", False),
    ("martin", "Bueno, eso es todo. Nos vemos mañana a la misma hora.", False),
    ("lucas", "Chau, buen día.", False),
]

PALABRAS_CLAVE = ["Nimbus", "Acme", "UAT", "deploy", "develop", "pull request", "dashboard", "hotfix",
                  "firmware", "sprint", "pipeline", "Firebase", "iOS", "Android", "token", "Martín", "Sofía",
                  "Lucas", "Camila"]


@dataclass(frozen=True)
class Condicion:
    nombre: str
    ganancia_db: dict            # por hablante
    vaiven_db: float             # desvío uniforme por turno (±)
    banda: tuple | None          # (pasa-altos Hz, pasa-bajos Hz)
    opus_kbps: int | None
    snr_db: float | None


CONDICIONES = {
    "limpio": Condicion("limpio", {h: 0.0 for h in VOCES}, 0.0, None, None, None),
    "teams": Condicion("teams", {"martin": 0.0, "sofia": -3.0, "lucas": 1.0, "camila": -12.0}, 1.5,
                       (100, 7000), 20, 25.0),
    "duro": Condicion("duro", {"martin": 0.0, "sofia": -5.0, "lucas": 2.0, "camila": -18.0}, 3.0,
                      (150, 3800), 12, 15.0),
}


# ------------------------------------------------------------------------------------------------ síntesis

def _huella(voz: str, ritmo: str, texto: str) -> str:
    return hashlib.sha1(f"{voz}|{ritmo}|{texto}".encode("utf-8")).hexdigest()[:14]


async def _sintetizar(voz: str, ritmo: str, texto: str, destino: Path, sem: asyncio.Semaphore) -> None:
    """Un turno → mp3 + tiempos por palabra. Idempotente (caché por huella) y con reintentos con espera creciente."""
    import edge_tts
    mp3, marcas = destino.with_suffix(".mp3"), destino.with_suffix(".json")
    if mp3.exists() and marcas.exists() and mp3.stat().st_size > 0:
        return
    async with sem:
        for intento in range(INTENTOS_RED):
            try:
                audio, palabras = bytearray(), []
                com = edge_tts.Communicate(texto, voz, rate=ritmo, boundary="WordBoundary")
                async for ch in com.stream():
                    if ch["type"] == "audio":
                        audio += ch["data"]
                    elif ch["type"] == "WordBoundary":
                        palabras.append({"w": ch["text"], "ini": ch["offset"] / TICKS_POR_S,
                                         "fin": (ch["offset"] + ch["duration"]) / TICKS_POR_S})
                if not audio or not palabras:
                    raise RuntimeError("respuesta vacía")
                tmp = mp3.with_suffix(".mp3.tmp")
                tmp.write_bytes(bytes(audio)); tmp.replace(mp3)
                marcas.write_text(json.dumps(palabras, ensure_ascii=False), encoding="utf-8")
                return
            except Exception as e:                                      # red: se reintenta, y si no, se avisa
                if intento == INTENTOS_RED - 1:
                    raise RuntimeError(f"no se pudo sintetizar «{texto[:40]}…»: {e}") from e
                await asyncio.sleep(2 ** intento)


def _mp3_a_pcm(mp3: Path) -> np.ndarray:
    r = subprocess.run(["ffmpeg", "-v", "error", "-i", str(mp3), "-ac", "1", "-ar", str(SR), "-f", "s16le", "-"],
                       capture_output=True, check=True)
    return np.frombuffer(r.stdout, dtype="<i2").astype(np.float32) / 32768.0


def _retardo_mp3(clips: list[tuple[np.ndarray, list[dict]]]) -> float:
    """
    El mp3 decodificado llega corrido unos ms respecto de los tiempos de la síntesis (retardo del códec). Se estima
    como la mediana de (arranque de energía − inicio de la primera palabra) sobre todos los turnos: robusto a outliers.
    """
    m = int(0.01 * SR)
    difs = []
    for x, ps in clips:
        k = len(x) // m
        db = 20 * np.log10(np.sqrt(np.mean(x[: k * m].reshape(k, m) ** 2, axis=1) + 1e-12))
        on = np.flatnonzero(db > -45.0)
        if len(on) and ps:
            difs.append(on[0] * 0.01 - ps[0]["ini"])
    return float(np.clip(np.median(difs), 0.0, 0.2)) if difs else 0.0


# ------------------------------------------------------------------------------------------------ mezcla

def _rms_voz_db(x: np.ndarray) -> float:
    m = int(0.02 * SR)
    k = len(x) // m
    if k == 0:
        return -120.0
    e = np.mean(x[: k * m].reshape(k, m) ** 2, axis=1)
    voz = e[e > np.max(e) * 1e-3]
    return float(10 * np.log10(np.mean(voz) + 1e-12)) if len(voz) else -120.0


def _ruido_rosa(n: int, rng: np.random.Generator) -> np.ndarray:
    """Ruido rosa por FFT (1/√f en amplitud): espectro de oficina/ventilador, no un siseo blanco."""
    blanco = rng.standard_normal(n)
    f = np.fft.rfft(blanco)
    esc = np.ones(len(f)); esc[1:] = 1 / np.sqrt(np.arange(1, len(f)))
    rosa = np.fft.irfft(f * esc, n)
    return (rosa / (np.std(rosa) + 1e-12)).astype(np.float32)


def _ffmpeg_filtro(x: np.ndarray, cond: Condicion) -> np.ndarray:
    """Banda telefónica y Opus VOIP ida y vuelta, con ffmpeg (el mismo que usa TeamsTools)."""
    if not cond.banda and not cond.opus_kbps:
        return x
    with tempfile.TemporaryDirectory() as d:
        ent, med, sal = Path(d) / "e.wav", Path(d) / "m.opus", Path(d) / "s.wav"
        escribir_wav(ent, x)
        af = f"highpass=f={cond.banda[0]},lowpass=f={cond.banda[1]}" if cond.banda else "anull"
        if cond.opus_kbps:
            subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(ent), "-af", af, "-c:a", "libopus", "-b:a",
                            f"{cond.opus_kbps}k", "-application", "voip", str(med)], check=True)
            subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(med), "-ac", "1", "-ar", str(SR), str(sal)],
                           check=True)
        else:
            subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(ent), "-af", af, str(sal)], check=True)
        y, _ = leer_wav(sal)
    y = y[: len(x)]
    return np.pad(y, (0, len(x) - len(y))) if len(y) < len(x) else y


def construir(salida: Path, condiciones: list[str], pistas: bool = False) -> dict:
    salida.mkdir(parents=True, exist_ok=True)
    cache = salida / "tts-cache"; cache.mkdir(exist_ok=True)
    ui.titulo("reunión sintética", f"{len(GUION)} turnos · {len(VOCES)} voces · condiciones {', '.join(condiciones)}")

    # 1) síntesis en paralelo (4 a la vez), idempotente
    trabajos = []
    for hab, texto, _ in GUION:
        voz, ritmo = VOCES[hab]
        trabajos.append((voz, ritmo, texto, cache / _huella(voz, ritmo, texto)))
    faltan = sum(1 for *_, d in trabajos if not d.with_suffix(".mp3").exists())
    ui.paso(f"voces neuronales · {faltan} por sintetizar, {len(trabajos) - faltan} en caché")

    async def todas():
        sem = asyncio.Semaphore(4)
        await asyncio.gather(*[_sintetizar(v, r, t, d, sem) for v, r, t, d in trabajos])
    asyncio.run(todas())

    clips = []
    for _, _, _, d in trabajos:
        clips.append((_mp3_a_pcm(d.with_suffix(".mp3")), json.loads(d.with_suffix(".json").read_text("utf-8"))))
    retardo = _retardo_mp3(clips)
    ui.ok(f"{len(clips)} turnos listos · retardo del mp3 estimado {retardo * 1000:.0f} ms (se corrige en la verdad)")

    # 2) línea de tiempo ÚNICA (semilla fija): pausas naturales, y los turnos marcados pisan el final del anterior
    rng = np.random.default_rng(SEMILLA)
    t_fin_voz, lineas = 0.6, []
    for (hab, texto, pisa), (x, ps) in zip(GUION, clips):
        ini_voz = (t_fin_voz - rng.uniform(0.25, 0.6)) if pisa else (t_fin_voz + rng.uniform(0.25, 1.1))
        desfase = ini_voz - (ps[0]["ini"] + retardo)              # dónde cae la muestra 0 del clip
        palabras = [{"w": p["w"], "ini": round(desfase + retardo + p["ini"], 3),
                     "fin": round(desfase + retardo + p["fin"], 3)} for p in ps]
        lineas.append({"hablante": hab, "texto": texto, "pisa": pisa, "desfase": desfase,
                       "ini": palabras[0]["ini"], "fin": palabras[-1]["fin"], "palabras": palabras})
        if not pisa:
            t_fin_voz = palabras[-1]["fin"]
        else:
            t_fin_voz = max(t_fin_voz, palabras[-1]["fin"])
    dur = max(l["desfase"] + len(c[0]) / SR for l, c in zip(lineas, clips)) + 1.0
    n = int(dur * SR)

    # 3) cada condición: misma línea de tiempo, otras ganancias y otro camino de audio
    for nombre in condiciones:
        cond = CONDICIONES[nombre]
        rng_c = np.random.default_rng(SEMILLA + int(hashlib.sha1(nombre.encode()).hexdigest()[:6], 16))  # estable entre corridas
        mezcla = np.zeros(n, np.float32)
        for l, (x, _) in zip(lineas, clips):
            g_db = RMS_OBJETIVO_DB - _rms_voz_db(x) + cond.ganancia_db[l["hablante"]] + \
                   rng_c.uniform(-cond.vaiven_db, cond.vaiven_db)
            a = int(round(l["desfase"] * SR))
            seg = x * (10 ** (g_db / 20))
            if a < 0:
                seg, a = seg[-a:], 0
            mezcla[a: a + len(seg)] += seg[: n - a]
        mezcla = _ffmpeg_filtro(mezcla, cond)
        if cond.snr_db is not None:
            nivel = _rms_voz_db(mezcla) - cond.snr_db
            mezcla = mezcla + _ruido_rosa(n, rng_c) * (10 ** (nivel / 20))
        pico = float(np.max(np.abs(mezcla)))
        if pico > 0.98:
            mezcla *= 0.98 / pico
        ruta = salida / f"reunion-{nombre}.wav"
        escribir_wav(ruta, mezcla)
        ui.ok(f"{ruta.name} · {dur:.1f} s · pico {20 * np.log10(max(pico, 1e-9)):.1f} dBFS")

    # 3b) una pista por persona (misma línea de tiempo, nivel de «limpio»)
    if pistas:
        for hab in VOCES:
            voz = np.zeros(n, np.float32)
            for l, (x, _) in zip(lineas, clips):
                if l["hablante"] != hab:
                    continue
                a = int(round(l["desfase"] * SR))
                seg = x * (10 ** ((RMS_OBJETIVO_DB - _rms_voz_db(x)) / 20))
                if a < 0:
                    seg, a = seg[-a:], 0
                voz[a: a + len(seg)] += seg[: n - a]
            escribir_wav(salida / f"reunion-voz-{hab}.wav", voz)
        ui.ok(f"{len(VOCES)} pistas por persona (reunion-voz-*.wav)")

    # 4) la verdad: turnos, palabras con tiempo, y los segmentos de habla por hablante (para la diarización)
    verdad = {"dur": round(dur, 3), "sr": SR, "semilla": SEMILLA, "retardo_mp3": round(retardo, 4),
              "voces": {h: {"voz": v, "ritmo": r} for h, (v, r) in VOCES.items()},
              "condiciones": {k: asdict(CONDICIONES[k]) for k in condiciones},
              "palabras_clave": PALABRAS_CLAVE,
              "turnos": [{k: v for k, v in l.items() if k != "desfase"} for l in lineas]}
    (salida / "reunion.json").write_text(json.dumps(verdad, ensure_ascii=False, indent=1), encoding="utf-8")
    with open(salida / "reunion-guion.txt", "w", encoding="utf-8") as f:
        for l in lineas:
            f.write(f"[{l['ini']:7.2f} – {l['fin']:7.2f}] {l['hablante']:7s} {'(pisa) ' if l['pisa'] else ''}{l['texto']}\n")
    palabras = sum(len(normalizar(l["texto"])) for l in lineas)
    ui.tarjeta("reunión lista", ["dato", "valor"], [
        ["duración", f"{dur / 60:.1f} min"], ["turnos", str(len(lineas))], ["palabras", str(palabras)],
        ["hablantes", ", ".join(VOCES)], ["solapamientos", str(sum(l["pisa"] for l in lineas))],
        ["condiciones", ", ".join(condiciones)]], derecha={1}, pie=str(salida))
    return verdad


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--salida", default=str(AQUI / "banco"))
    ap.add_argument("--condiciones", default="limpio,teams,duro")
    ap.add_argument("--pistas", action="store_true", help="además, una pista por persona")
    a = ap.parse_args()
    construir(Path(a.salida), [c.strip() for c in a.condiciones.split(",") if c.strip()], a.pistas)
    return 0


if __name__ == "__main__":
    sys.exit(main())
