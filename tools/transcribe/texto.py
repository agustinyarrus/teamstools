"""
texto.py — la vara con la que se mide a todos los motores: normalización, alineación y WER con desglose.

La normalización es IGUAL para la referencia y para cada motor, y solo borra diferencias que no son errores de oído:
mayúsculas, puntuación, tildes (la «ñ» se respeta: año ≠ ano), números en cifras («12» = «doce»), siglas deletreadas
(«u a t» = «uat») y un puñado de grafías equivalentes («okey» = «ok»). Una palabra mal oída sigue siendo un error.
"""
import re
import unicodedata
from dataclasses import dataclass

import numpy as np
from num2words import num2words
from rapidfuzz.distance import Levenshtein
from scipy.optimize import linear_sum_assignment

EQUIVALENTES = {"okey": "ok", "okay": "ok", "okei": "ok", "oquei": "ok"}
VOCALES_SUELTAS = set("aeou")           # letras que en castellano también son palabras: solas no forman sigla
CONJUNCIONES = {"y", "o", "e"}          # cortan una corrida de letras: «u a t y la app» = «uat y la app»
_SIGLA_CON_PUNTOS = re.compile(r"\b(?:[a-zñ]\.){2,}")
_NUMERO = re.compile(r"\d+(?:[.,]\d+)*")
_NO_PALABRA = re.compile(r"[^0-9a-zñ]+")
_TILDES = {"̀", "́", "̈"}  # grave, aguda, diéresis (la virgulilla de la ñ NO)


def _numero_en_palabras(m: re.Match) -> str:
    s = m.group(0)
    partes = re.split(r"[.,]", s)
    if len(partes) > 1 and all(len(p) == 3 for p in partes[1:]):      # 1.000 / 12,500 → miles
        return " " + num2words(int("".join(partes)), lang="es") + " "
    if len(partes) == 1:
        return " " + num2words(int(s), lang="es") + " "
    sep = " punto " if "." in s else " coma "                          # 2,5 / 1.1.1001 → «dos coma cinco»…
    return " " + sep.join(num2words(int(p), lang="es") for p in partes) + " "


def _sin_tildes(t: str) -> str:
    return unicodedata.normalize("NFC", "".join(c for c in unicodedata.normalize("NFD", t) if c not in _TILDES))


def _unir_siglas(ws: list[str]) -> list[str]:
    """«u a t» → «uat»: una corrida de letras sueltas es una sigla si alguna no es una palabra castellana. O(n)."""
    out, i = [], 0
    while i < len(ws):
        j = i
        while j < len(ws) and len(ws[j]) == 1 and ws[j].isalpha() and ws[j] not in CONJUNCIONES:
            j += 1
        corrida = ws[i:j]
        if len(corrida) >= 2 and any(c not in VOCALES_SUELTAS for c in corrida):
            out.append("".join(corrida)); i = j
        else:
            out.append(ws[i]); i += 1
    return out


def normalizar(t: str) -> list[str]:
    t = _sin_tildes(unicodedata.normalize("NFC", t.lower()))
    t = _SIGLA_CON_PUNTOS.sub(lambda m: m.group(0).replace(".", "") + " ", t)   # «u.a.t.» → «uat»
    t = t.replace("%", " por ciento ")
    t = _NUMERO.sub(_numero_en_palabras, t)
    t = _sin_tildes(t)                                                  # num2words devuelve «veintidós»
    ws = _unir_siglas(_NO_PALABRA.sub(" ", t).split())
    return [EQUIVALENTES.get(w, w) for w in ws]


@dataclass(frozen=True, slots=True)
class Errores:
    """Conteo de una alineación: sustituciones, borrados («palabras comidas») e inserciones sobre N de referencia."""
    sus: int
    bor: int
    ins: int
    ref: int

    @property
    def total(self) -> int:
        return self.sus + self.bor + self.ins

    @property
    def wer(self) -> float:
        return self.total / self.ref if self.ref else float(self.ins > 0)

    def __add__(self, o: "Errores") -> "Errores":
        return Errores(self.sus + o.sus, self.bor + o.bor, self.ins + o.ins, self.ref + o.ref)


CERO = Errores(0, 0, 0, 0)


def alinear(ref: list[str], hip: list[str]) -> tuple[Errores, np.ndarray]:
    """
    Levenshtein por palabras con retroceso (rapidfuzz, C++): O(n·m) tiempo pero en nativo.
    Devuelve el conteo y, por cada palabra de la REFERENCIA, su destino: 0 = bien, 1 = sustituida, 2 = comida.
    """
    ops = Levenshtein.editops(ref, hip)
    destino = np.zeros(len(ref), np.int8)
    s = b = i = 0
    for op in ops:
        if op.tag == "replace":
            s += 1; destino[op.src_pos] = 1
        elif op.tag == "delete":
            b += 1; destino[op.src_pos] = 2
        else:
            i += 1
    return Errores(s, b, i, len(ref)), destino


def cpwer(ref: dict[str, list[str]], hip: dict[str, list[str]]) -> tuple[Errores, dict[str, str]]:
    """
    WER con atribución de hablante (cpWER, CHiME-6): se concatena lo de cada hablante y se busca la asignación
    hablante real ↔ grupo que minimiza los errores (húngaro sobre una matriz cuadrada con «nadie» de relleno).
    Un grupo sin pareja cuenta todo como inserción; un hablante sin pareja, todo como borrado. O((S+K)^3) + S·K alineaciones.
    """
    rs, hs = list(ref), list(hip)
    S, K = len(rs), len(hs)
    n = S + K
    grande = 10 ** 9
    costo = np.full((n, n), grande, np.int64)
    alin = {}
    for i, r in enumerate(rs):
        for j, h in enumerate(hs):
            e, _ = alinear(ref[r], hip[h]); alin[(i, j)] = e; costo[i, j] = e.total
        costo[i, K + i] = len(ref[r])                    # hablante real sin grupo: todo comido
    for j, h in enumerate(hs):
        costo[S + j, j] = len(hip[h])                    # grupo sin hablante real: todo inserción
    costo[S:, K:] = 0
    fil, col = linear_sum_assignment(costo)
    tot, mapa = CERO, {}
    for i, j in zip(fil, col):
        if i < S and j < K:
            tot = tot + alin[(i, j)]; mapa[hs[j]] = rs[i]
        elif i < S:
            tot = tot + Errores(0, len(ref[rs[i]]), 0, len(ref[rs[i]]))
        elif j < K:
            tot = tot + Errores(0, 0, len(hip[hs[j]]), 0)
    return tot, mapa
