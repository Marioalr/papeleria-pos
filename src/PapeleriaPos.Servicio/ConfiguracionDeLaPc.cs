using System.Text.Json;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace PapeleriaPos.Servicio;

/// <summary>
/// La configuración de esta PC (<c>servicio.json</c>, en <c>C:\ProgramData\PapeleriaPos</c>; <see cref="RutasServicio.ConfiguracionDeLaPc"/>):
/// lo que cambia de una PC a otra, como el puente con eleventa del piloto en la caja 2. Va fuera de la carpeta del programa:
/// actualizar (<c>instalacion/actualizar.ps1</c>) reemplaza esa carpeta con todo y su appsettings.json, y este archivo no lo toca.
/// <para>
/// Manda sobre appsettings.json, y las variables de entorno y la línea de órdenes mandan sobre él: la clave de eleventa va solo en
/// la variable de entorno <c>Eleventa__Clave</c>, nunca en un archivo. Admite comentarios, como appsettings.json. Si no existe, el
/// servicio arranca con lo de siempre; si está mal escrito, no arranca y lo dice (en el Visor de eventos de Windows).
/// </para>
/// </summary>
public static class ConfiguracionDeLaPc
{
    /// <summary>
    /// Pone <paramref name="ruta"/> después de appsettings.json y antes de las variables de entorno y de la línea de órdenes. Nada
    /// si <paramref name="ruta"/> está vacía (la prueba con una persona real, las pruebas).
    /// </summary>
    /// <param name="argumentos">
    /// Los de la línea de órdenes: solo hacen falta si la configuración no trae las variables de entorno (no pasa con la del
    /// servicio): entonces se vuelven a poner, con ellas, después del archivo.
    /// </param>
    public static void Agregar(ConfigurationManager configuracion, string? ruta, string[]? argumentos = null)
    {
        if (string.IsNullOrEmpty(ruta))
            return;
        var archivo = new JsonConfigurationSource { Path = Path.GetFullPath(ruta), Optional = true, ReloadOnChange = false };
        // Para una ruta completa: lee desde su carpeta (o desde la más cercana que exista, si todavía no hay ninguna).
        archivo.ResolveFileProvider();
        IList<IConfigurationSource> fuentes = configuracion.Sources;
        try
        {
            // Las variables de entorno de la aplicación (sin prefijo) van después de appsettings.json, y la línea de órdenes
            // después de ellas: el archivo entra justo antes.
            var variables = -1;
            for (var i = 0; i < fuentes.Count && variables < 0; i++)
                if (fuentes[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
                    variables = i;
            if (variables >= 0)
            {
                fuentes.Insert(variables, archivo);
                return;
            }
            fuentes.Add(archivo);
            fuentes.Add(new EnvironmentVariablesConfigurationSource());
            if (argumentos is { Length: > 0 })
                fuentes.Add(new CommandLineConfigurationSource { Args = argumentos });
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or JsonException)
        {
            // Lo de adentro dice el renglón y la columna; lo de afuera, solo «no se pudo cargar el archivo».
            throw new InvalidOperationException(
                $"No se pudo leer la configuración de esta PC ({ruta}): {ex.GetBaseException().Message} Corrígela (se edita como "
                + "administrador) o quítala para arrancar sin ella.",
                ex);
        }
    }
}
