"""
Extractor de chats de Microsoft Teams desde el IndexedDB local.

Junta las tres piezas que hacen falta para reconstruir una conversacion:
perfiles (quien es cada MRI), conversaciones (como se llama cada hilo) y
replychains (los mensajes propiamente dichos).

Uso:
    python extraer.py <dir_leveldb_copiado> <dir_salida>

No toca la base original: se espera que le pasen una copia.
"""

import html
import json
import os
import re
import sys
from collections import defaultdict
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import idb
import leveldb
import v8serial

# Tu propio id de Teams (el GUID del MRI 8:orgid:<guid>). Opcional: `isSentByCurrentUser` ya marca lo tuyo; esto
# solo suma cuando ese campo falta. Se toma de la variable de entorno TEAMS_YO.
YO = os.environ.get("TEAMS_YO", "").strip()


# --------------------------------------------------------------------------
# limpieza de html
# --------------------------------------------------------------------------

# Teams incrusta el mensaje citado entero dentro de la respuesta. Duplicarlo
# infla el volumen sin aportar nada: lo dejamos en un marcador con el autor.
RE_QUOTE = re.compile(r"<blockquote[^>]*Reply[^>]*>(.*?)</blockquote>", re.I | re.S)
RE_QUOTE_AUTOR = re.compile(r"<strong[^>]*>(.*?)</strong>", re.I | re.S)

RE_EMOJI = re.compile(r'<img[^>]*alt="([^"]*)"[^>]*>', re.I)
RE_MENTION = re.compile(r"<at[^>]*>(.*?)</at>", re.I | re.S)
RE_BR = re.compile(r"<br\s*/?>", re.I)
RE_BLOCK = re.compile(r"</(p|div|li|tr|h[1-6])>", re.I)
RE_LI = re.compile(r"<li[^>]*>", re.I)
RE_TAG = re.compile(r"<[^>]+>")
RE_WS = re.compile(r"[ \t]+")
RE_NL = re.compile(r"\n{3,}")


def _marcar_cita(match):
    """Reemplaza el bloque citado por un [re: Autor] y nada mas."""
    autor = RE_QUOTE_AUTOR.search(match.group(1))
    if autor:
        nombre = RE_TAG.sub("", autor.group(1)).strip()
        nombre = nombre.split(",")[0]
        if nombre:
            return "[re: %s] " % nombre
    return "[re] "


def html_a_texto(raw):
    """Pasa el HTML de un mensaje de Teams a texto plano legible."""
    if not raw:
        return ""
    text = str(raw)
    text = RE_QUOTE.sub(_marcar_cita, text)
    text = RE_EMOJI.sub(lambda m: m.group(1) or "", text)
    text = RE_MENTION.sub(lambda m: "@" + RE_TAG.sub("", m.group(1)), text)
    text = RE_BR.sub("\n", text)
    text = RE_LI.sub("\n  - ", text)
    text = RE_BLOCK.sub("\n", text)
    text = RE_TAG.sub("", text)
    text = html.unescape(text)
    text = RE_WS.sub(" ", text)
    text = RE_NL.sub("\n\n", text)
    return text.strip()


# --------------------------------------------------------------------------
# carga de los stores
# --------------------------------------------------------------------------

def stores_por_nombre(kv, nombre):
    """Devuelve los (db_id, store_id) cuyo store se llama asi."""
    metadata = idb.scan_metadata(kv)
    encontrados = []
    for db_id, info in metadata.items():
        for store_id, store_name in info.get("stores", {}).items():
            if store_name == nombre:
                encontrados.append((db_id, store_id, info.get("name") or ""))
    return encontrados


def cargar(kv, db_id, store_id):
    """Deserializa todos los registros de un object store."""
    salida = []
    for _, raw in idb.iter_records(kv, db_id, store_id):
        try:
            salida.append(v8serial.to_plain(
                v8serial.loads(idb.strip_value_version(raw))))
        except Exception:
            continue
    return salida


def construir_perfiles(kv):
    """MRI -> nombre para mostrar."""
    perfiles = {}
    for db_id, store_id, _ in stores_por_nombre(kv, "profiles"):
        for p in cargar(kv, db_id, store_id):
            mri = p.get("mri")
            nombre = p.get("displayName") or p.get("givenName")
            if mri and nombre:
                perfiles.setdefault(mri, nombre)
    return perfiles


def construir_conversaciones(kv, perfiles):
    """conversationId -> {nombre, tipo, miembros}."""
    convs = {}
    for db_id, store_id, _ in stores_por_nombre(kv, "conversations"):
        for c in cargar(kv, db_id, store_id):
            cid = c.get("id")
            if not cid:
                continue
            props = c.get("threadProperties") or {}
            miembros = [m.get("id") for m in (c.get("members") or [])
                        if isinstance(m, dict) and m.get("id")]

            nombre = props.get("topic") or props.get("topicThreadTopic")
            if not nombre:
                otros = [perfiles.get(m, m) for m in miembros if not (YO and YO in str(m))]
                otros = [o for o in otros if o]
                if otros:
                    nombre = ", ".join(otros[:4])
                    if len(otros) > 4:
                        nombre += " (+%d)" % (len(otros) - 4)

            previo = convs.get(cid)
            if previo and previo["nombre"] and not nombre:
                continue
            convs[cid] = {
                "nombre": nombre or "",
                "tipo": c.get("type") or "",
                "miembros": miembros,
            }
    return convs


# --------------------------------------------------------------------------
# mensajes
# --------------------------------------------------------------------------

def fecha_de(msg):
    """Saca un datetime UTC del mensaje, probando varios campos."""
    for campo in ("originalArrivalTime", "clientArrivalTime"):
        valor = msg.get(campo)
        if isinstance(valor, str) and len(valor) >= 10:
            try:
                return datetime.fromisoformat(valor.replace("Z", "+00:00"))
            except ValueError:
                pass
    ident = msg.get("id")
    if isinstance(ident, str) and ident.isdigit() and len(ident) >= 12:
        try:
            return datetime.fromtimestamp(int(ident) / 1000, timezone.utc)
        except (ValueError, OSError):
            pass
    return None


def extraer_mensajes(kv):
    """Recorre todos los replychains y devuelve la lista plana de mensajes."""
    vistos = {}
    for db_id, store_id, _ in stores_por_nombre(kv, "replychains"):
        for _, raw in idb.iter_records(kv, db_id, store_id):
            try:
                chain = v8serial.loads(idb.strip_value_version(raw))
            except Exception:
                continue
            mapa = chain.get("messageMap")
            if not isinstance(mapa, dict):
                continue
            for msg in mapa.values():
                if not isinstance(msg, dict):
                    continue
                ident = msg.get("id")
                if not ident:
                    continue
                clave = (msg.get("conversationId"), ident)
                anterior = vistos.get(clave)
                if anterior is not None:
                    try:
                        if int(msg.get("version") or 0) <= int(anterior.get("version") or 0):
                            continue
                    except (TypeError, ValueError):
                        continue
                vistos[clave] = msg
    return list(vistos.values())


RE_PART = re.compile(r'<part identity="([^"]*)"[^>]*>(.*?)</part>', re.S)
RE_PART_DN = re.compile(r'<displayName>(.*?)</displayName>', re.S)
RE_PART_DUR = re.compile(r'<duration>(\d+)</duration>')
RE_PART_EST = re.compile(r'<(ended|started|missed|cancelled|connecting)\s*/>')


def partlist(contenido, perfiles):
    """
    Los mensajes Event/Call traen la lista EXACTA de quienes estuvieron en la llamada:

        <ended/><partlist count="3">
          <part identity="8:orgid:..."><displayName>Fulano</displayName><duration>92</duration></part>
          ...

    Es la unica fuente que dice con certeza quien hablo con quien, y estaba tirandose a la basura
    porque los Event/Call se marcan como ruido. Devuelve None si el mensaje no es una llamada.
    """
    c = contenido or ""
    if "<partlist" not in c:
        return None
    gente = []
    for ident, cuerpo in RE_PART.findall(c):
        dn = RE_PART_DN.search(cuerpo)
        nombre = (dn.group(1).strip() if dn else "")
        if not nombre:
            nombre = perfiles.get(ident, "") or ""
        du = RE_PART_DUR.search(cuerpo)
        gente.append({
            "id": ident,
            "nombre": nombre,
            "dur": int(du.group(1)) if du else 0,
        })
    est = RE_PART_EST.search(c)
    return {
        "estado": est.group(1) if est else "",
        "gente": gente,
    }


def normalizar(msg, perfiles, convs):
    """Deja el mensaje en una forma chata y comoda de leer."""
    cid = msg.get("conversationId") or ""
    conv = convs.get(cid, {})
    autor = msg.get("imDisplayName") or perfiles.get(msg.get("creator")) \
        or msg.get("creator") or "?"
    cuando = fecha_de(msg)

    tipo = msg.get("messageType") or ""
    contenido = msg.get("content")
    # eventos de llamada y avisos de sistema: no son conversacion
    ruido = (tipo.startswith("ThreadActivity")
             or tipo.startswith("Event/")
             or tipo.startswith("RichText/Media_Call")
             or msg.get("type") == "ThreadActivity")
    texto = "" if ruido else html_a_texto(contenido)

    # la lista de participantes de una llamada: se guarda aparte porque no es "texto"
    llamada = partlist(contenido, perfiles) if tipo == "Event/Call" else None

    salida = {
        "conv_id": cid,
        "conv": conv.get("nombre") or cid,
        "conv_tipo": conv.get("tipo") or "",
        "fecha": cuando.isoformat() if cuando else None,
        "autor": autor,
        "mio": bool(msg.get("isSentByCurrentUser")) or bool(YO and YO in str(msg.get("creator"))),
        "tipo": tipo,
        "texto": texto,
        "borrado": bool(msg.get("deletionInfo")),
        "id": msg.get("id"),
    }
    if llamada:
        salida["llamada"] = llamada
        salida["call_id"] = msg.get("callId") or ""
    return salida


# --------------------------------------------------------------------------
# salida
# --------------------------------------------------------------------------

def escribir(mensajes, convs, destino):
    os.makedirs(destino, exist_ok=True)

    ruta_jsonl = os.path.join(destino, "mensajes.jsonl")
    with open(ruta_jsonl, "w", encoding="utf-8") as fh:
        for m in mensajes:
            fh.write(json.dumps(m, ensure_ascii=False) + "\n")

    por_conv = defaultdict(list)
    for m in mensajes:
        por_conv[m["conv_id"]].append(m)

    filas = []
    for cid, items in por_conv.items():
        utiles = [m for m in items if m["texto"]]
        fechas = [m["fecha"] for m in items if m["fecha"]]
        filas.append({
            "conv": items[0]["conv"],
            "tipo": items[0]["conv_tipo"],
            "total": len(items),
            "con_texto": len(utiles),
            "desde": min(fechas)[:10] if fechas else "",
            "hasta": max(fechas)[:10] if fechas else "",
            "id": cid,
        })
    filas.sort(key=lambda f: -f["con_texto"])

    ruta_idx = os.path.join(destino, "indice.md")
    with open(ruta_idx, "w", encoding="utf-8") as fh:
        fh.write("# Indice de conversaciones\n\n")
        fh.write("| Conversacion | Tipo | Mensajes | Con texto | Desde | Hasta |\n")
        fh.write("|---|---|---:|---:|---|---|\n")
        for f in filas:
            nombre = (f["conv"] or f["id"])[:70].replace("|", "/")
            fh.write("| %s | %s | %d | %d | %s | %s |\n" % (
                nombre, f["tipo"], f["total"], f["con_texto"],
                f["desde"], f["hasta"]))

    return ruta_jsonl, ruta_idx, filas


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    origen, destino = sys.argv[1], sys.argv[2]

    print("leyendo leveldb...")
    kv = leveldb.read_database(origen)
    print("  %d claves" % len(kv))

    print("resolviendo perfiles y conversaciones...")
    perfiles = construir_perfiles(kv)
    convs = construir_conversaciones(kv, perfiles)
    print("  %d perfiles, %d conversaciones" % (len(perfiles), len(convs)))

    print("extrayendo mensajes...")
    crudos = extraer_mensajes(kv)
    mensajes = [normalizar(m, perfiles, convs) for m in crudos]
    mensajes.sort(key=lambda m: (m["conv"], m["fecha"] or ""))
    print("  %d mensajes" % len(mensajes))

    ruta_jsonl, ruta_idx, filas = escribir(mensajes, convs, destino)
    con_texto = sum(1 for m in mensajes if m["texto"])
    print()
    print("mensajes con texto: %d" % con_texto)
    print("conversaciones:     %d" % len(filas))
    print("salida:             %s" % ruta_jsonl)
    print("                    %s" % ruta_idx)
    return 0


if __name__ == "__main__":
    sys.exit(main())
