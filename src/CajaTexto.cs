using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>
    /// Un cuadro de texto de solo lectura que NO usa la barra de desplazamiento de Windows.
    ///
    /// 🚨 Un `TextBox` con `ScrollBars.Vertical` dibuja la barra nativa del sistema, que es BLANCA y no se
    /// puede tematizar: sobre un fondo negro queda una franja luminosa al costado que arruina la pantalla.
    /// Acá la barra nativa se apaga, la rueda se maneja a mano con `EM_LINESCROLL`, y el que lo contiene
    /// dibuja una barrita fina del color del tema usando `Arriba` y `Lineas`.
    /// </summary>
    internal sealed class CajaTexto : TextBox
    {
        const int EM_LINESCROLL = 0x00B6;
        const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        const int EM_GETLINECOUNT = 0x00BA;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public CajaTexto()
        {
            Multiline = true;
            ReadOnly = true;
            WordWrap = true;
            BorderStyle = BorderStyle.None;
            ScrollBars = ScrollBars.None;      // la del sistema no entra acá
            TabStop = false;
            Cursor = Cursors.Arrow;
        }

        /// <summary>
        /// 🚨 25-sep-2026 · el control de edición de Windows solo corta renglón con \r\n: con \n suelto (lo que escriben
        /// Python y el modelo local) todo sale PEGADO en un solo bloque. Todo texto que entra se normaliza acá.
        /// </summary>
        public override string Text
        {
            get => base.Text;
            set => base.Text = Saltos(value);
        }

        /// <summary>Cualquier combinación de saltos (\n, \r, \r\n) → \r\n, el único que entienden los TextBox y el portapapeles de Windows. O(n).</summary>
        public static string Saltos(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return s;
            var sb = new System.Text.StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\r') { sb.Append("\r\n"); if (i + 1 < s.Length && s[i + 1] == '\n') i++; }
                else if (c == '\n') sb.Append("\r\n");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Primera línea visible (ya envuelta), para poder dibujar la barrita afuera.</summary>
        public int Arriba => IsHandleCreated ? (int)SendMessage(Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero) : 0;

        /// <summary>Total de líneas una vez envuelto el texto.</summary>
        public int Lineas => IsHandleCreated ? Math.Max(1, (int)SendMessage(Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero)) : 1;

        /// <summary>Cuántas líneas entran a la vista. Aproximado por el alto de fuente, alcanza para la barra.</summary>
        public int Visibles => Math.Max(1, ClientSize.Height / Math.Max(1, FontHeight));

        public event EventHandler Desplazado;

        public void Desplazar(int lineas)
        {
            if (!IsHandleCreated || lineas == 0) return;
            SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)lineas);
            Desplazado?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // 🚨 sin barra nativa, el TextBox NO se desplaza solo con la rueda: hay que empujarlo a mano
            Desplazar(-Math.Sign(e.Delta) * 3);
            // no se llama a base: evita que la rueda se propague y mueva otra cosa de la pantalla
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.PageDown) { Desplazar(Visibles - 1); e.Handled = true; return; }
            if (e.KeyCode == Keys.PageUp) { Desplazar(-(Visibles - 1)); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        /// <summary>
        /// Dibuja una barrita fina del color del tema al borde derecho del rectángulo dado, si hace falta.
        /// La pinta el contenedor, porque un TextBox no deja pintar encima de sí mismo.
        /// </summary>
        public void PintarBarra(Graphics g, Rectangle marco, float esc)
        {
            int total = Lineas, ven = Visibles;
            if (total <= ven) return;
            int S(int px) => Dpi.S(esc, px);
            float frac = ven / (float)total;
            float alto = Math.Max(S(20), marco.Height * frac);
            float y = marco.Y + (Arriba / (float)Math.Max(1, total - ven)) * (marco.Height - alto);
            using (var b = new SolidBrush(Tema.Alpha(Tema.Apagado, 110)))
            using (var p = Tema.Redondeado(new RectangleF(marco.Right - S(4), y, S(3), alto), S(2)))
                g.FillPath(b, p);
        }
    }
}
