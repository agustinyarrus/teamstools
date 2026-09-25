using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TeamsTools
{
    internal enum Nivel { Debug, Info, Ok, Aviso, Alerta, Error }

    internal sealed class LineaLog
    {
        public DateTime Hora;
        public Nivel Nivel;
        public string Texto;
        public override string ToString() => $"{Hora:HH:mm:ss} {Etiqueta(Nivel),-6} {Texto}";
        public static string Etiqueta(Nivel n)
        {
            switch (n)
            {
                case Nivel.Debug: return "debug";
                case Nivel.Ok: return "ok";
                case Nivel.Aviso: return "aviso";
                case Nivel.Alerta: return "ALERTA";
                case Nivel.Error: return "ERROR";
                default: return "info";
            }
        }
    }

    /// <summary>Log a archivo diario + anillo en memoria para el panel. Seguro entre hilos.</summary>
    internal sealed class Logger
    {
        readonly object candado = new object();
        readonly Queue<LineaLog> anillo = new Queue<LineaLog>();
        readonly string carpeta;
        public int Capacidad = 600;
        public bool IncluirDebug = false;
        public event Action<LineaLog> LineaNueva;

        public Logger(string carpetaLogs)
        {
            carpeta = carpetaLogs;
            try { Directory.CreateDirectory(carpeta); } catch { }
            PodarViejos(30);
        }

        public string ArchivoDeHoy => Path.Combine(carpeta, $"TeamsTools-{DateTime.Now:yyyy-MM-dd}.log");

        public void Debug(string t) => Escribir(Nivel.Debug, t);
        public void Info(string t) => Escribir(Nivel.Info, t);
        public void Ok(string t) => Escribir(Nivel.Ok, t);
        public void Aviso(string t) => Escribir(Nivel.Aviso, t);
        public void Alerta(string t) => Escribir(Nivel.Alerta, t);
        public void Error(string t) => Escribir(Nivel.Error, t);

        public void Escribir(Nivel n, string texto)
        {
            var l = new LineaLog { Hora = DateTime.Now, Nivel = n, Texto = texto ?? "" };
            lock (candado)
            {
                if (n != Nivel.Debug || IncluirDebug)
                {
                    anillo.Enqueue(l);
                    while (anillo.Count > Capacidad) anillo.Dequeue();
                }
                Disco.Agregar(ArchivoDeHoy, l + Environment.NewLine);   // de fondo y en orden: loguear jamás espera al disco
            }
            if (n != Nivel.Debug || IncluirDebug) { try { LineaNueva?.Invoke(l); } catch { } }
        }

        public LineaLog[] Ultimas() { lock (candado) return anillo.ToArray(); }

        void PodarViejos(int dias)
        {
            try
            {
                foreach (var patron in new[] { "TeamsTools-*.log", "teams-autoleave-*.log" })
                    foreach (var f in Directory.GetFiles(carpeta, patron))
                        if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-dias)) File.Delete(f);
            }
            catch { }
        }
    }
}
