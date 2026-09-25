"""
consola.py — el lenguaje visual de las herramientas de voz: fondo negro puro, pasteles (Catppuccin / Tokyo Night),
tarjetas anchas y barras de progreso finas. Todo script de esta carpeta pinta con esto: ninguno escupe texto sin forma.
"""
import re
import sys

PALETA = {
    "cian": (143, 214, 204), "verde": (181, 223, 168), "rosa": (243, 185, 210), "durazno": (246, 192, 160),
    "malva": (196, 181, 253), "crema": (238, 223, 184), "apagado": (88, 93, 110), "texto": (211, 214, 223),
    "cielo": (137, 180, 250),
}
_ANSI = re.compile(r"\x1b\[[0-9;]*m")
FIN = "\x1b[0m"


def color(nombre: str, texto) -> str:
    r, g, b = PALETA[nombre]
    return f"\x1b[38;2;{r};{g};{b}m{texto}{FIN}"


def visible(s: str) -> int:
    """Ancho en pantalla: sin las secuencias de color."""
    return len(_ANSI.sub("", s))


def ajustar(s: str, ancho: int, derecha: bool = False) -> str:
    relleno = " " * max(0, ancho - visible(s))
    return relleno + s if derecha else s + relleno


def barra(fraccion: float, ancho: int = 28) -> str:
    """Barra fina de progreso con medios bloques: ▰▱ en pastel."""
    f = min(1.0, max(0.0, fraccion))
    llenos = int(round(f * ancho))
    return color("cian", "▰" * llenos) + color("apagado", "▱" * (ancho - llenos))


def titulo(texto: str, detalle: str = "") -> None:
    print(f"\n  {color('cian', texto)}" + (f" {color('apagado', '· ' + detalle)}" if detalle else ""), flush=True)


def paso(texto: str) -> None:
    print(f"  {color('malva', '▸')} {texto}", flush=True)


def ok(texto: str) -> None:
    print(f"    {color('verde', '✓')} {texto}", flush=True)


def aviso(texto: str) -> None:
    print(f"    {color('durazno', '!')} {texto}", flush=True)


def error(texto: str) -> None:
    print(f"    {color('rosa', '✗')} {texto}", flush=True)


def progreso(etiqueta: str, fraccion: float, extra: str = "") -> None:
    """Una sola línea que se reescribe (\r): no llena la pantalla de renglones."""
    sys.stdout.write(f"\r    {barra(fraccion)} {color('texto', f'{fraccion:6.1%}')} {color('apagado', etiqueta)} {extra}   ")
    sys.stdout.flush()
    if fraccion >= 1.0:
        sys.stdout.write("\n")


def tarjeta(titulo_: str, cabecera: list[str], filas: list[list[str]], derecha: set[int] | None = None,
            pie: str = "") -> None:
    """Tarjeta ancha con bordes redondeados. O(celdas): calcula anchos una vez y pinta."""
    derecha = derecha or set()
    anchos = [max([visible(cabecera[i])] + [visible(f[i]) for f in filas]) for i in range(len(cabecera))]
    interior = sum(anchos) + 3 * (len(anchos) - 1) + 2
    borde = lambda s: color("cian", s)
    print(f"\n  {borde('╭─ ')}{color('crema', titulo_)} {borde('─' * max(0, interior - visible(titulo_) - 3) + '╮')}")
    fila_txt = lambda celdas, estilo: " " + "   ".join(
        ajustar(estilo(c), anchos[i], i in derecha) for i, c in enumerate(celdas)) + " "
    print(f"  {borde('│')}{fila_txt(cabecera, lambda c: color('apagado', c))}{borde('│')}")
    print(f"  {borde('├' + '─' * interior + '┤')}")
    for f in filas:
        print(f"  {borde('│')}{fila_txt(f, lambda c: c)}{borde('│')}")
    print(f"  {borde('╰' + '─' * interior + '╯')}")
    if pie:
        print(f"  {color('apagado', pie)}")
    sys.stdout.flush()
