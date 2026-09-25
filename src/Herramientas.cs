using System;
using System.IO;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>
    /// Dónde están los programas de afuera (loopcap, los scripts de Python). Una sola regla para toda la app:
    /// lo que diga la configuración (con variables de entorno expandidas) o, si está vacío, `tools\` al lado del exe.
    /// Así el mismo exe sirve portable (con `tools\` adentro de su carpeta) y en una máquina de desarrollo (rutas en la config).
    /// </summary>
    internal static class Herramientas
    {
        /// <summary>`tools\` al lado del exe.</summary>
        public static string Base => Path.Combine(Path.GetDirectoryName(Application.ExecutablePath) ?? "", "tools");

        /// <summary>La ruta configurada, o `tools\{relativo}` si no hay ninguna. O(1).</summary>
        public static string Resolver(string configurado, string relativoATools)
        {
            if (!string.IsNullOrWhiteSpace(configurado)) return Environment.ExpandEnvironmentVariables(configurado.Trim());
            return Path.Combine(Base, relativoATools);
        }
    }
}
