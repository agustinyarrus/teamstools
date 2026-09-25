#!/usr/bin/env python3
"""
png2ico - Convierte una imagen a .ico multi-resolucion (hasta 512 px).

Genera todas las resoluciones estandar de Windows y ademas un frame de 512,
util en pantallas HiDPI: la vista "iconos extra grandes" (256 logico) se pinta
a 384 px en 4K@150% (o 512 px en @200%), asi que un frame de 512 se ve nitido
en lugar de agrandar el de 256.

Nota tecnica: el directorio del .ico guarda el tamano en 1 byte (0 = 256), asi
que un frame de 512 se declara como "256"; Windows 11 igual lee el PNG embebido
por sus dimensiones reales. Por eso el .ico se arma a mano (Pillow descarta > 256).

Cada frame se reescala con LANCZOS y va en 32-bit RGBA (conserva transparencia).
Nunca se agranda por encima de la fuente: los tamanos mayores se descartan.

Uso:
    python png2ico.py entrada.png [salida.ico] [--sizes 16,24,32,48,64,128,256,512]

Requiere: Pillow  ->  python -m pip install Pillow
"""
import argparse
import io
import os
import struct
import sys

from PIL import Image

DEFAULT_SIZES = [16, 24, 32, 48, 64, 128, 256, 512]


def build_ico(src, out=None, sizes=None):
    sizes = sorted(set(sizes or DEFAULT_SIZES))
    if out is None:
        out = os.path.splitext(src)[0] + ".ico"

    base = Image.open(src).convert("RGBA")
    src_max = min(base.size)  # no agrandar por encima de la fuente
    used = [s for s in sizes if s <= src_max] or [min(sizes)]

    frames = []
    for s in used:
        buf = io.BytesIO()
        base.resize((s, s), Image.LANCZOS).save(buf, format="PNG")
        frames.append((s, buf.getvalue()))

    count = len(frames)
    header = struct.pack("<HHH", 0, 1, count)  # reserved, type=1, count
    entries = b""
    blob = b""
    offset = 6 + count * 16
    for s, png in frames:
        # w/h en 1 byte: 0 == 256; 512 tambien cae en 0 (se lee por el PNG real)
        wb = 0 if s >= 256 else s
        hb = 0 if s >= 256 else s
        # BBBBHHII: w, h, colores, reservado, planes, bitcount, bytes, offset
        entries += struct.pack("<BBBBHHII", wb, hb, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
        blob += png

    with open(out, "wb") as f:
        f.write(header + entries + blob)

    return out, [s for s, _ in frames]


def main(argv=None):
    p = argparse.ArgumentParser(description="Convierte una imagen a .ico multi-resolucion (hasta 512).")
    p.add_argument("input", help="Imagen de entrada (png/jpg/etc.)")
    p.add_argument("output", nargs="?", help="Salida .ico (opcional)")
    p.add_argument(
        "--sizes",
        default=",".join(map(str, DEFAULT_SIZES)),
        help="Resoluciones separadas por coma (default: %(default)s)",
    )
    args = p.parse_args(argv)

    if not os.path.isfile(args.input):
        p.error(f"no existe el archivo: {args.input}")

    sizes = [int(x) for x in args.sizes.split(",") if x.strip()]
    out, used = build_ico(args.input, args.output, sizes)
    print(f"OK -> {out}  ({len(used)} resoluciones: {', '.join(map(str, used))})")


if __name__ == "__main__":
    sys.exit(main())
