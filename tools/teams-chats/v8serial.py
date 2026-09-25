"""
Deserializador del formato de serializacion estructurada de V8 / Blink.

Es lo que guarda Chromium adentro de cada valor de IndexedDB. El blob viene
envuelto asi:

    FF <version_blink>            envoltorio de Blink
    [FE <offset:8><size:4>]       trailer opcional (Chrome moderno)
    FF <version_v8>               arranca el payload de V8
    <valor>                       arbol de tags

Cada tag es un byte ASCII: 'o' abre objeto, '{' lo cierra, '"' string de un
byte, 'c' string UTF-16, 'N' double, 'I' int32 en zigzag, y asi.

Referencia: v8/src/objects/value-serializer.cc
"""

import struct

# tags del serializador de V8
T_VERSION = 0xFF
T_TRAILER = 0xFE
T_PADDING = 0x00
T_VERIFY_COUNT = ord("?")
T_THE_HOLE = ord("-")
T_UNDEFINED = ord("_")
T_NULL = ord("0")
T_TRUE = ord("T")
T_FALSE = ord("F")
T_INT32 = ord("I")
T_UINT32 = ord("U")
T_DOUBLE = ord("N")
T_BIGINT = ord("Z")
T_UTF8_STRING = ord("S")
T_ONE_BYTE_STRING = ord('"')
T_TWO_BYTE_STRING = ord("c")
T_OBJECT_REFERENCE = ord("^")
T_BEGIN_OBJECT = ord("o")
T_END_OBJECT = ord("{")
T_BEGIN_SPARSE_ARRAY = ord("a")
T_END_SPARSE_ARRAY = ord("@")
T_BEGIN_DENSE_ARRAY = ord("A")
T_END_DENSE_ARRAY = ord("$")
T_DATE = ord("D")
T_TRUE_OBJECT = ord("y")
T_FALSE_OBJECT = ord("x")
T_NUMBER_OBJECT = ord("n")
T_BIGINT_OBJECT = ord("z")
T_STRING_OBJECT = ord("s")
T_REGEXP = ord("R")
T_BEGIN_MAP = ord(";")
T_END_MAP = ord(":")
T_BEGIN_SET = ord("'")
T_END_SET = ord(",")
T_ARRAY_BUFFER = ord("B")
T_RESIZABLE_ARRAY_BUFFER = ord("~")
T_ARRAY_BUFFER_VIEW = ord("V")
T_SHARED_ARRAY_BUFFER = ord("u")
T_ERROR = ord("r")
T_HOST_OBJECT = ord("\\")


class Undefined:
    """Marcador para distinguir undefined de null."""

    _instance = None

    def __new__(cls):
        if cls._instance is None:
            cls._instance = super().__new__(cls)
        return cls._instance

    def __repr__(self):
        return "undefined"

    def __bool__(self):
        return False


UNDEFINED = Undefined()


class Deserializer:
    """Recorre el blob una sola vez, manteniendo la tabla de referencias."""

    def __init__(self, data):
        self.data = data
        self.pos = 0
        self.objects = []          # id -> objeto ya creado
        self.version = 0

    # -- primitivas ------------------------------------------------------

    def byte(self):
        if self.pos >= len(self.data):
            raise EOFError("fin de datos")
        value = self.data[self.pos]
        self.pos += 1
        return value

    def peek(self):
        if self.pos >= len(self.data):
            return None
        return self.data[self.pos]

    def varint(self):
        result = 0
        shift = 0
        while True:
            byte = self.byte()
            result |= (byte & 0x7F) << shift
            if not byte & 0x80:
                return result
            shift += 7
            if shift > 70:
                raise ValueError("varint desbocado")

    def zigzag(self):
        raw = self.varint()
        return (raw >> 1) ^ -(raw & 1)

    def double(self):
        value = struct.unpack_from("<d", self.data, self.pos)[0]
        self.pos += 8
        return value

    def raw(self, length):
        chunk = self.data[self.pos:self.pos + length]
        self.pos += length
        return chunk

    # -- registro de objetos --------------------------------------------

    def register(self, obj):
        """Cada objeto recibe un id en orden de creacion, para el tag '^'."""
        self.objects.append(obj)
        return obj

    # -- envoltorio ------------------------------------------------------

    def read_header(self):
        """Consume el envoltorio de Blink y la version de V8."""
        if self.peek() == T_VERSION:
            self.pos += 1
            blink_version = self.varint()
        else:
            blink_version = 0

        # trailer opcional: 0xFE + offset de 8 bytes + tamano de 4
        if self.peek() == T_TRAILER:
            self.pos += 1
            self.pos += 12

        if self.peek() == T_VERSION:
            self.pos += 1
            self.version = self.varint()
        return blink_version

    # -- valores ---------------------------------------------------------

    def read(self):
        tag = self.byte()

        # el padding se ignora
        while tag == T_PADDING:
            tag = self.byte()

        if tag == T_UNDEFINED or tag == T_THE_HOLE:
            return UNDEFINED
        if tag == T_NULL:
            return None
        if tag == T_TRUE:
            return True
        if tag == T_FALSE:
            return False
        if tag == T_INT32:
            return self.zigzag()
        if tag == T_UINT32:
            return self.varint()
        if tag == T_DOUBLE:
            return self.double()
        if tag == T_BIGINT:
            return self.read_bigint()
        if tag == T_UTF8_STRING:
            return self.raw(self.varint()).decode("utf-8", errors="replace")
        if tag == T_ONE_BYTE_STRING:
            return self.raw(self.varint()).decode("latin-1")
        if tag == T_TWO_BYTE_STRING:
            return self.raw(self.varint()).decode("utf-16-le", errors="replace")
        if tag == T_OBJECT_REFERENCE:
            index = self.varint()
            if index < len(self.objects):
                return self.objects[index]
            return None
        if tag == T_BEGIN_OBJECT:
            return self.read_object()
        if tag == T_BEGIN_DENSE_ARRAY:
            return self.read_dense_array()
        if tag == T_BEGIN_SPARSE_ARRAY:
            return self.read_sparse_array()
        if tag == T_BEGIN_MAP:
            return self.read_map()
        if tag == T_BEGIN_SET:
            return self.read_set()
        if tag == T_DATE:
            return self.register(("Date", self.double()))
        if tag == T_TRUE_OBJECT:
            return self.register(True)
        if tag == T_FALSE_OBJECT:
            return self.register(False)
        if tag == T_NUMBER_OBJECT:
            return self.register(self.double())
        if tag == T_BIGINT_OBJECT:
            return self.register(self.read_bigint())
        if tag == T_STRING_OBJECT:
            return self.register(self.read())
        if tag == T_REGEXP:
            pattern = self.read()
            flags = self.varint()
            return self.register(("RegExp", pattern, flags))
        if tag == T_ARRAY_BUFFER:
            return self.tal_vez_vista(self.register(self.raw(self.varint())))
        if tag == T_RESIZABLE_ARRAY_BUFFER:
            length = self.varint()
            self.varint()                      # maximo
            return self.tal_vez_vista(self.register(self.raw(length)))
        if tag == T_SHARED_ARRAY_BUFFER:
            self.varint()
            return self.tal_vez_vista(self.register(b""))
        if tag == T_ARRAY_BUFFER_VIEW:
            return self.read_array_buffer_view()
        if tag == T_ERROR:
            return self.read_error()
        if tag == T_HOST_OBJECT:
            # objeto propio de Blink: no lo sabemos leer, cortamos limpio
            raise ValueError("host object de Blink")

        raise ValueError("tag desconocido 0x%02x en %d" % (tag, self.pos - 1))

    def read_bigint(self):
        bitfield = self.varint()
        negative = bool(bitfield & 1)
        length = bitfield >> 1
        digits = self.raw(length)
        value = int.from_bytes(digits, "little")
        return -value if negative else value

    def read_object(self):
        obj = {}
        self.register(obj)
        while True:
            if self.peek() == T_END_OBJECT:
                self.pos += 1
                self.varint()                  # cantidad de propiedades
                return obj
            key = self.read()
            value = self.read()
            if isinstance(key, (str, int, float)):
                obj[key] = value
        return obj

    def read_dense_array(self):
        length = self.varint()
        array = []
        self.register(array)
        for _ in range(length):
            array.append(self.read())
        # despues del cuerpo pueden venir propiedades sueltas
        extras = {}
        while self.peek() != T_END_DENSE_ARRAY:
            key = self.read()
            value = self.read()
            extras[key] = value
        self.pos += 1
        self.varint()                          # cantidad de propiedades
        self.varint()                          # largo
        return array

    def read_sparse_array(self):
        self.varint()                          # largo declarado
        obj = {}
        self.register(obj)
        while self.peek() != T_END_SPARSE_ARRAY:
            key = self.read()
            value = self.read()
            obj[key] = value
        self.pos += 1
        self.varint()
        self.varint()
        # si las claves son 0..n-1 lo devolvemos como lista
        try:
            indexes = sorted(int(k) for k in obj)
            if indexes == list(range(len(indexes))):
                return [obj[k] for k in sorted(obj, key=lambda x: int(x))]
        except (TypeError, ValueError):
            pass
        return obj

    def read_map(self):
        result = {}
        self.register(result)
        while self.peek() != T_END_MAP:
            key = self.read()
            value = self.read()
            if isinstance(key, (str, int, float, bool)) or key is None:
                result[key] = value
        self.pos += 1
        self.varint()
        return result

    def read_set(self):
        items = []
        self.register(items)
        while self.peek() != T_END_SET:
            items.append(self.read())
        self.pos += 1
        self.varint()
        return items

    def tal_vez_vista(self, buffer):
        """Un ArrayBuffer puede venir seguido del tag 'V' de la vista que lo
        envuelve (Uint8Array y companiia). V8 los serializa como un solo valor:
        primero el buffer, despues la vista. Si no se consume el 'V' aca, el
        parser lo toma como un valor suelto y se desincroniza todo lo que sigue.
        """
        if self.peek() == T_ARRAY_BUFFER_VIEW:
            self.pos += 1
            self.byte()                        # subtipo de vista
            self.varint()                      # offset
            self.varint()                      # largo
            if self.version >= 14:
                self.varint()                  # flags
            self.register(buffer)
        return buffer

    def read_array_buffer_view(self):
        self.byte()                            # subtipo de vista
        self.varint()                          # offset
        length = self.varint()
        if self.version >= 14:
            self.varint()                      # flags
        return self.register(("ArrayBufferView", length))

    def read_error(self):
        message = None
        while True:
            sub = self.byte()
            if sub == ord("."):                # fin
                break
            if sub == ord("m"):
                message = self.read()
            elif sub == ord("s"):
                self.read()                    # stack
            elif sub == ord("c"):
                self.read()                    # cause
            else:
                pass
        return self.register(("Error", message))


def loads(blob):
    """Deserializa un valor completo de IndexedDB."""
    reader = Deserializer(blob)
    reader.read_header()
    return reader.read()


def to_plain(value):
    """Convierte el resultado a algo que json.dumps pueda escupir."""
    if isinstance(value, Undefined):
        return None
    if isinstance(value, dict):
        return {str(k): to_plain(v) for k, v in value.items()}
    if isinstance(value, list):
        return [to_plain(v) for v in value]
    if isinstance(value, tuple):
        return [to_plain(v) for v in value]
    if isinstance(value, bytes):
        return "<%d bytes>" % len(value)
    return value
