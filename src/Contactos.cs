using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TeamsTools
{
    internal sealed class Contacto
    {
        public string Apodo = "";
        public string Nombre = "";      // como lo muestra Teams: "Apellido, Nombre"
        public string Correo = "";
        public string Rol = "";
        public override string ToString() => Nombre + (Apodo.Length > 0 ? $" ({Apodo})" : "");
        public string NombrePila
        {
            get
            {
                int c = Nombre.IndexOf(',');
                string n = c >= 0 ? Nombre.Substring(c + 1).Trim() : Nombre.Trim();
                int sp = n.IndexOf(' ');
                return sp > 0 ? n.Substring(0, sp) : n;
            }
        }
    }

    /// <summary>Agenda de compañeros: apodo, nombre tal cual Teams, correo. Arranca solo con «yo»; el resto se carga desde la pestaña personalizados o editando contactos.json.</summary>
    internal sealed class Contactos
    {
        public List<Contacto> Lista = new List<Contacto>();
        string ruta;

        public static Contactos Cargar(string ruta)
        {
            var c = new Contactos { ruta = ruta };
            var d = Json.LeerObjeto(ruta);
            if (d != null)
            {
                foreach (var o in Json.Lista(d, "contactos"))
                    c.Lista.Add(new Contacto { Apodo = Json.S(o, "apodo"), Nombre = Json.S(o, "nombre"), Correo = Json.S(o, "correo"), Rol = Json.S(o, "rol") });
            }
            if (c.Lista.Count == 0) { c.Lista = Semilla(); c.Guardar(); }
            return c;
        }

        public void Guardar()
        {
            try
            {
                Json.Escribir(ruta, new Dictionary<string, object>
                {
                    ["_ayuda"] = "Contactos para el autocontestador y los recordatorios. 'nombre' es como lo muestra Teams (Apellido, Nombre); 'apodo' es lo que escribís vos. El contacto 'yo' sos vos: si su nombre queda vacío, la app lo aprende de tu ficha en la reunión.",
                    ["contactos"] = Lista.Select(x => new Dictionary<string, object> { ["apodo"] = x.Apodo, ["nombre"] = x.Nombre, ["correo"] = x.Correo, ["rol"] = x.Rol }).ToList()
                });
            }
            catch { }
        }

        public static string Normalizar(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (var ch in s.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString().Trim();
        }

        /// <summary>Busca por apodo, nombre, apellido o correo; devuelve las coincidencias ordenadas (exactas primero).</summary>
        public List<Contacto> Buscar(string texto)
        {
            string q = Normalizar(texto);
            if (q.Length == 0) return new List<Contacto>();
            return Lista.Select(c => new { c, p = Puntaje(c, q) }).Where(x => x.p > 0).OrderByDescending(x => x.p).ThenBy(x => x.c.Nombre).Select(x => x.c).ToList();
        }

        static int Puntaje(Contacto c, string q)
        {
            string apodo = Normalizar(c.Apodo), nombre = Normalizar(c.Nombre), correo = Normalizar(c.Correo);
            if (apodo == q || nombre == q || correo == q) return 100;
            if (apodo.Split('/', ',').Select(a => a.Trim()).Any(a => a == q)) return 95;
            if (nombre.StartsWith(q)) return 80;
            if (correo.StartsWith(q)) return 75;
            if (nombre.Contains(q)) return 60;
            if (apodo.Contains(q)) return 55;
            if (correo.Contains(q)) return 40;
            return 0;
        }

        public Contacto Mejor(string texto) => Buscar(texto).FirstOrDefault();

        public Contacto PorNombreTeams(string nombreTeams)
        {
            string n = Normalizar(nombreTeams);
            return Lista.FirstOrDefault(c => Normalizar(c.Nombre) == n) ?? Lista.FirstOrDefault(c => n.StartsWith(Normalizar(c.Nombre)));
        }

        /// <summary>
        /// La agenda arranca con una sola entrada, «yo», sin nombre: la app aprende tu nombre de tu ficha en la primera
        /// reunión (o lo tomás de config.json → miNombre) y la gente se carga desde la pestaña personalizados.
        /// </summary>
        static List<Contacto> Semilla() => new List<Contacto>
        {
            new Contacto { Apodo = "yo", Nombre = "", Correo = "", Rol = "vos" },
        };
    }
}
