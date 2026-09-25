"""
Lector de LevelDB en Python puro (cero dependencias).

Implementa lo minimo necesario para leer una base LevelDB de Chromium/Blink
sin la libreria nativa: descompresion snappy, parseo de SSTables (.ldb) y
del write-ahead log (.log).

Formato de referencia:
  - snappy raw block: google/snappy format_description.txt
  - sstable / block:  leveldb/table/format.cc, leveldb/table/block.cc
  - wal:              leveldb/db/log_format.h, leveldb/db/write_batch.cc
"""

import os
import struct

SSTABLE_MAGIC = 0xDB4775248B80FB57
BLOCK_TRAILER = 5           # 1 byte de tipo de compresion + 4 de crc32c
FOOTER_LEN = 48
LOG_BLOCK = 32768
LOG_HEADER = 7


# --------------------------------------------------------------------------
# varints
# --------------------------------------------------------------------------

def uvarint(buf, pos=0):
    """Lee un varint sin signo estilo protobuf. Devuelve (valor, pos_nueva)."""
    result = 0
    shift = 0
    while True:
        if pos >= len(buf):
            raise ValueError("varint truncado")
        byte = buf[pos]
        pos += 1
        result |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return result, pos
        shift += 7
        if shift > 63:
            raise ValueError("varint demasiado largo")


# --------------------------------------------------------------------------
# snappy
# --------------------------------------------------------------------------

def snappy_decompress(data):
    """Descomprime un bloque snappy crudo (sin framing)."""
    expected, pos = uvarint(data, 0)
    out = bytearray()
    end = len(data)

    while pos < end:
        tag = data[pos]
        pos += 1
        kind = tag & 0x03

        if kind == 0:
            # literal
            length = tag >> 2
            if length < 60:
                length += 1
            else:
                extra = length - 59
                length = int.from_bytes(data[pos:pos + extra], "little") + 1
                pos += extra
            out += data[pos:pos + length]
            pos += length
            continue

        if kind == 1:
            length = 4 + ((tag >> 2) & 0x07)
            offset = ((tag >> 5) << 8) | data[pos]
            pos += 1
        elif kind == 2:
            length = (tag >> 2) + 1
            offset = int.from_bytes(data[pos:pos + 2], "little")
            pos += 2
        else:
            length = (tag >> 2) + 1
            offset = int.from_bytes(data[pos:pos + 4], "little")
            pos += 4

        if offset == 0 or offset > len(out):
            raise ValueError("offset de copia invalido")

        # las copias pueden solaparse con lo ya escrito: ahi hay que ir de a bytes
        start = len(out) - offset
        if offset >= length:
            out += out[start:start + length]
        else:
            for i in range(length):
                out.append(out[start + i])

    if len(out) != expected:
        raise ValueError("largo descomprimido inesperado")
    return bytes(out)


# --------------------------------------------------------------------------
# bloques de sstable
# --------------------------------------------------------------------------

def parse_block(block):
    """Recorre un bloque de datos y va largando pares (clave, valor)."""
    if len(block) < 4:
        return
    num_restarts = struct.unpack_from("<I", block, len(block) - 4)[0]
    limit = len(block) - 4 - num_restarts * 4
    if limit < 0:
        return

    pos = 0
    key = b""
    while pos < limit:
        shared, pos = uvarint(block, pos)
        non_shared, pos = uvarint(block, pos)
        value_len, pos = uvarint(block, pos)
        if shared > len(key):
            return
        key = key[:shared] + block[pos:pos + non_shared]
        pos += non_shared
        value = block[pos:pos + value_len]
        pos += value_len
        yield key, value


def read_block(fh, offset, size):
    """Lee un bloque del archivo y lo descomprime si hace falta."""
    fh.seek(offset)
    raw = fh.read(size + BLOCK_TRAILER)
    if len(raw) < size + 1:
        return None
    body = raw[:size]
    compression = raw[size]
    if compression == 0:
        return body
    if compression == 1:
        try:
            return snappy_decompress(body)
        except Exception:
            return None
    return None   # zstd u otro: no lo soportamos


def read_sstable(path):
    """Va largando (clave_interna, valor) de los bloques de datos de un .ldb."""
    size = os.path.getsize(path)
    if size < FOOTER_LEN:
        return

    with open(path, "rb") as fh:
        fh.seek(size - FOOTER_LEN)
        footer = fh.read(FOOTER_LEN)
        magic = struct.unpack_from("<Q", footer, FOOTER_LEN - 8)[0]
        if magic != SSTABLE_MAGIC:
            return

        # primero el handle del metaindex, despues el del indice
        pos = 0
        _, pos = uvarint(footer, pos)      # metaindex offset
        _, pos = uvarint(footer, pos)      # metaindex size
        index_offset, pos = uvarint(footer, pos)
        index_size, pos = uvarint(footer, pos)

        index_block = read_block(fh, index_offset, index_size)
        if index_block is None:
            return

        # el valor de cada entrada del indice es un BlockHandle
        for _, handle in parse_block(index_block):
            try:
                blk_off, hpos = uvarint(handle, 0)
                blk_size, _ = uvarint(handle, hpos)
            except ValueError:
                continue
            data_block = read_block(fh, blk_off, blk_size)
            if data_block is None:
                continue
            try:
                for key, value in parse_block(data_block):
                    yield key, value
            except ValueError:
                continue


# --------------------------------------------------------------------------
# write-ahead log
# --------------------------------------------------------------------------

def read_log(path):
    """Va largando (clave, valor, borrado) de un .log de leveldb."""
    with open(path, "rb") as fh:
        blob = fh.read()

    batch = bytearray()
    pos = 0
    while pos + LOG_HEADER <= len(blob):
        block_left = LOG_BLOCK - (pos % LOG_BLOCK)
        if block_left < LOG_HEADER:
            pos += block_left
            continue

        length = struct.unpack_from("<H", blob, pos + 4)[0]
        rec_type = blob[pos + 6]
        payload = blob[pos + LOG_HEADER:pos + LOG_HEADER + length]
        pos += LOG_HEADER + length

        if rec_type == 0:                      # relleno
            continue
        if rec_type in (1, 2):                 # FULL / FIRST
            batch = bytearray(payload)
        elif rec_type in (3, 4):               # MIDDLE / LAST
            batch += payload
        if rec_type in (1, 4):                 # el batch quedo completo
            yield from parse_write_batch(bytes(batch))
            batch = bytearray()


def parse_write_batch(batch):
    """Desarma un WriteBatch: cabecera de 12 bytes y despues las operaciones."""
    if len(batch) < 12:
        return
    count = struct.unpack_from("<I", batch, 8)[0]
    pos = 12
    for _ in range(count):
        if pos >= len(batch):
            return
        op = batch[pos]
        pos += 1
        try:
            klen, pos = uvarint(batch, pos)
            key = batch[pos:pos + klen]
            pos += klen
            if op == 1:                        # put
                vlen, pos = uvarint(batch, pos)
                value = batch[pos:pos + vlen]
                pos += vlen
                yield key, value, False
            else:                              # delete
                yield key, b"", True
        except ValueError:
            return


# --------------------------------------------------------------------------
# base completa
# --------------------------------------------------------------------------

def read_database(directory):
    """
    Junta todo lo que haya en el directorio (sstables + log) y devuelve
    un dict clave -> valor quedandose con la version mas nueva de cada clave.

    Las claves internas de sstable traen 8 bytes de cola con el numero de
    secuencia y el tipo; las del log ya vienen limpias.
    """
    latest = {}

    files = sorted(os.listdir(directory))
    for name in files:
        if not name.endswith(".ldb") and not name.endswith(".sst"):
            continue
        path = os.path.join(directory, name)
        try:
            for internal_key, value in read_sstable(path):
                if len(internal_key) < 8:
                    continue
                user_key = internal_key[:-8]
                trailer = int.from_bytes(internal_key[-8:], "little")
                seq = trailer >> 8
                kind = trailer & 0xFF
                prev = latest.get(user_key)
                if prev is None or seq >= prev[0]:
                    latest[user_key] = (seq, value if kind == 1 else None)
        except Exception:
            continue

    # el log es lo mas fresco: pisa cualquier sstable
    high = 1 << 62
    for name in files:
        if not name.endswith(".log"):
            continue
        path = os.path.join(directory, name)
        try:
            for key, value, deleted in read_log(path):
                high += 1
                latest[key] = (high, None if deleted else value)
        except Exception:
            continue

    return {k: v[1] for k, v in latest.items() if v[1] is not None}
