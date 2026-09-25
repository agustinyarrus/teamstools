"""
Decodificador del esquema IndexedDB de Chromium sobre LevelDB.

Traduce las claves crudas de la base a algo con sentido: que base de datos,
que object store y que clave de registro. Los strings van en UTF-16 big endian
con la cantidad de unidades adelante como varint.

Referencia: content/browser/indexed_db/indexed_db_leveldb_coding.cc
"""

import struct

from leveldb import uvarint

# tipos de clave dentro de la metadata global (database_id == 0)
GLOBAL_MAX_DATABASE_ID = 1
GLOBAL_DATABASE_NAME = 201

# tipos dentro de la metadata de una base
DB_ORIGIN_NAME = 0
DB_DATABASE_NAME = 1
DB_USER_VERSION = 2
DB_MAX_OBJECT_STORE_ID = 3
DB_USER_INT_VERSION = 4
DB_OBJECT_STORE_META = 50
DB_INDEX_META = 100
DB_OBJECT_STORE_FREE_LIST = 150
DB_INDEX_FREE_LIST = 151
DB_OBJECT_STORE_NAMES = 200
DB_INDEX_NAMES = 201

# tipos dentro de la metadata de un object store
OS_NAME = 0
OS_KEY_PATH = 1
OS_AUTO_INCREMENT = 2
OS_EVICTABLE = 3
OS_LAST_VERSION = 4
OS_MAX_INDEX_ID = 5
OS_HAS_KEY_PATH = 6
OS_KEY_GENERATOR = 7

# index_id reservados
IDX_OBJECT_STORE_DATA = 1
IDX_EXISTS_ENTRY = 2
IDX_BLOB_ENTRY = 3

# tipos de IDBKey
KEY_NULL = 0
KEY_STRING = 1
KEY_DATE = 2
KEY_NUMBER = 3
KEY_ARRAY = 4
KEY_MIN = 5
KEY_BINARY = 6


def decode_prefix(key):
    """Devuelve (database_id, object_store_id, index_id, posicion_siguiente)."""
    if not key:
        return None
    first = key[0]
    db_bytes = ((first >> 5) & 0x07) + 1
    os_bytes = ((first >> 2) & 0x07) + 1
    idx_bytes = (first & 0x03) + 1

    pos = 1
    if len(key) < pos + db_bytes + os_bytes + idx_bytes:
        return None
    db_id = int.from_bytes(key[pos:pos + db_bytes], "little")
    pos += db_bytes
    os_id = int.from_bytes(key[pos:pos + os_bytes], "little")
    pos += os_bytes
    idx_id = int.from_bytes(key[pos:pos + idx_bytes], "little")
    pos += idx_bytes
    return db_id, os_id, idx_id, pos


def decode_string(buf):
    """String plano: todo lo que queda, en UTF-16BE."""
    return buf.decode("utf-16-be", errors="replace")


def decode_string_with_length(buf, pos):
    """Varint con la cantidad de unidades UTF-16 y despues los bytes."""
    count, pos = uvarint(buf, pos)
    end = pos + count * 2
    return buf[pos:end].decode("utf-16-be", errors="replace"), end


def decode_idb_key(buf, pos=0):
    """Decodifica una IDBKey. Devuelve (valor_python, posicion_siguiente)."""
    if pos >= len(buf):
        return None, pos
    kind = buf[pos]
    pos += 1

    if kind == KEY_STRING:
        return decode_string_with_length(buf, pos)
    if kind in (KEY_DATE, KEY_NUMBER):
        value = struct.unpack_from("<d", buf, pos)[0]
        return value, pos + 8
    if kind == KEY_ARRAY:
        count, pos = uvarint(buf, pos)
        items = []
        for _ in range(count):
            item, pos = decode_idb_key(buf, pos)
            items.append(item)
        return items, pos
    if kind == KEY_BINARY:
        length, pos = uvarint(buf, pos)
        return buf[pos:pos + length], pos + length
    if kind == KEY_NULL:
        return None, pos
    if kind == KEY_MIN:
        return "<min>", pos
    return None, len(buf)


def scan_metadata(db):
    """
    Recorre las claves de metadata y arma el mapa de bases y object stores.

    Devuelve {database_id: {"name":..., "origin":..., "stores": {os_id: nombre}}}
    """
    databases = {}

    for key, value in db.items():
        decoded = decode_prefix(key)
        if decoded is None:
            continue
        db_id, os_id, idx_id, pos = decoded

        # nombres de bases: viven en la metadata global
        if db_id == 0 and os_id == 0 and idx_id == 0:
            if pos < len(key) and key[pos] == GLOBAL_DATABASE_NAME:
                try:
                    origin, npos = decode_string_with_length(key, pos + 1)
                    name, _ = decode_string_with_length(key, npos)
                    target, _ = uvarint(value, 0)
                    entry = databases.setdefault(
                        target, {"name": None, "origin": None, "stores": {}})
                    entry["name"] = name
                    entry["origin"] = origin
                except Exception:
                    pass
            continue

        # metadata de una base concreta
        if db_id > 0 and os_id == 0 and idx_id == 0 and pos < len(key):
            entry = databases.setdefault(
                db_id, {"name": None, "origin": None, "stores": {}})
            meta_type = key[pos]

            if meta_type == DB_DATABASE_NAME and entry["name"] is None:
                entry["name"] = decode_string(value)

            elif meta_type == DB_OBJECT_STORE_META:
                try:
                    store_id, spos = uvarint(key, pos + 1)
                    if spos < len(key) and key[spos] == OS_NAME:
                        entry["stores"][store_id] = decode_string(value)
                except Exception:
                    pass

    return databases


def iter_records(db, database_id, object_store_id):
    """Va largando (clave_del_registro, valor_crudo) de un object store."""
    for key, value in db.items():
        decoded = decode_prefix(key)
        if decoded is None:
            continue
        d_id, os_id, idx_id, pos = decoded
        if d_id != database_id or os_id != object_store_id:
            continue
        if idx_id != IDX_OBJECT_STORE_DATA:
            continue
        try:
            record_key, _ = decode_idb_key(key, pos)
        except Exception:
            record_key = None
        yield record_key, value


def strip_value_version(value):
    """
    El valor guardado es varint(version) + blob serializado por Blink.
    Devuelve solo el blob.
    """
    try:
        _, pos = uvarint(value, 0)
        return value[pos:]
    except Exception:
        return value
