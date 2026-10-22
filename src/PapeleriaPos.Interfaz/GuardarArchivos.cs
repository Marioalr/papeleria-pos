using Microsoft.JSInterop;

namespace PapeleriaPos.Interfaz;

/// <summary>Un archivo que la caja guarda (el Excel de un reporte): cómo se llama, qué es y lo que lleva.</summary>
/// <param name="Nombre">El que propone la caja principal: «Ventas 2026-10.xlsx».</param>
/// <param name="Tipo">El tipo (MIME) del archivo.</param>
public sealed record ArchivoPorGuardar(string Nombre, string Tipo, byte[] Datos)
{
    /// <summary>«.xlsx».</summary>
    public string Extension => Path.GetExtension(Nombre);
}

/// <summary>Dónde quedó un archivo que se guardó.</summary>
/// <param name="Nombre">Con el que quedó.</param>
/// <param name="Ruta">En esta PC (la ventana de Windows); vacía si lo bajó el navegador, que no dice dónde.</param>
public sealed record ArchivoGuardado(string Nombre, string? Ruta)
{
    /// <summary>Lo que dice la pantalla: «Se guardó en C:\…\Ventas 2026-10.xlsx.» o «Se descargó «…»: está en las descargas del navegador.»</summary>
    public string Texto => Ruta is { } ruta ? $"Se guardó en {ruta}." : $"Se descargó «{Nombre}»: está en las descargas del navegador.";

    /// <summary>Lo que dice la pantalla si quien lo guardaba canceló.</summary>
    public const string Cancelado = "No se guardó: se canceló.";
}

/// <summary>
/// La única forma de guardar un archivo desde la caja (entrega 9, parte 2, 2026-10-22; antes la caja no guardaba archivos). Cada
/// anfitrión pone la suya: la ventana de Windows, su «Guardar como» (<see cref="GuardadoEnLaPc"/>); la caja del navegador, una
/// descarga (<see cref="DescargaDelNavegador"/>, la que se pone si el anfitrión no puso otra). Las pruebas ponen una de mentiras.
/// </summary>
public interface IGuardarArchivo
{
    /// <returns>Dónde quedó; vacío si quien lo guardaba lo canceló.</returns>
    /// <exception cref="ErrorCaja">No se pudo guardar (abierto en otro programa, Windows no deja escribir ahí), en palabras de la tienda.</exception>
    Task<ArchivoGuardado?> GuardarAsync(ArchivoPorGuardar archivo);
}

/// <summary>Lo que pregunta «Guardar como»: en qué carpeta empieza, con qué nombre, y el tipo de archivo («Libro de Excel (*.xlsx)|*.xlsx»).</summary>
public sealed record PreguntaGuardarComo(string Carpeta, string Nombre, string Filtro, string Extension);

/// <summary>
/// Dónde empiezan «Guardar como» y «Abrir» en la ventana de Windows: la primera vez en Documentos, y después en la carpeta del
/// último archivo que se guardó o se abrió, mientras la caja siga abierta. Así el Excel de los productos que se exportó se vuelve
/// a encontrar al importarlo.
/// </summary>
/// <param name="inicial">Dónde empieza la primera vez; sin ella, Documentos.</param>
public sealed class CarpetaDeArchivos(string? inicial = null)
{
    public string Ruta { get; private set; } = inicial ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>Se guardó o se abrió ese archivo: la siguiente vez se empieza en su carpeta.</summary>
    public void Usado(string archivo)
    {
        if (Path.GetDirectoryName(archivo) is { Length: > 0 } carpeta)
            Ruta = carpeta;
    }
}

/// <summary>
/// Guardar en esta PC (la ventana de Windows): «Guardar como» de Windows pregunta dónde, la primera vez en Documentos y después en
/// la carpeta del último que se guardó o se abrió (mientras la caja siga abierta, <see cref="CarpetaDeArchivos"/>), con el nombre
/// que propone la caja principal; y aquí se escribe. Primero va a un archivo aparte y luego se cambia de un jalón: si algo falla,
/// no queda un archivo a medias con ese nombre. «Guardar como» lo pone el anfitrión (en la ventana, en su hilo): así esto se prueba
/// sin Windows.
/// </summary>
public sealed class GuardadoEnLaPc : IGuardarArchivo
{
    private readonly Func<PreguntaGuardarComo, Task<string?>> _guardarComo;
    private readonly CarpetaDeArchivos _carpeta;

    /// <param name="guardarComo">Pregunta dónde guardarlo; vacío si se canceló.</param>
    /// <param name="carpeta">Dónde empieza: la misma que la de «Abrir» (<see cref="AbiertoEnLaPc"/>).</param>
    public GuardadoEnLaPc(Func<PreguntaGuardarComo, Task<string?>> guardarComo, CarpetaDeArchivos carpeta) => (_guardarComo, _carpeta) = (guardarComo, carpeta);

    /// <param name="guardarComo">Pregunta dónde guardarlo; vacío si se canceló.</param>
    /// <param name="carpetaInicial">Dónde empieza la primera vez; sin ella, Documentos.</param>
    public GuardadoEnLaPc(Func<PreguntaGuardarComo, Task<string?>> guardarComo, string? carpetaInicial = null)
        : this(guardarComo, new CarpetaDeArchivos(carpetaInicial))
    {
    }

    /// <summary>Donde empieza «Guardar como»: Documentos, o la carpeta del último que se guardó (o se abrió).</summary>
    public string Carpeta => _carpeta.Ruta;

    public async Task<ArchivoGuardado?> GuardarAsync(ArchivoPorGuardar archivo)
    {
        var ruta = await _guardarComo(new PreguntaGuardarComo(_carpeta.Ruta, archivo.Nombre, Filtro(archivo.Extension), archivo.Extension));
        if (string.IsNullOrWhiteSpace(ruta))
            return null;
        var aparte = ruta + ".guardando";
        try
        {
            await File.WriteAllBytesAsync(aparte, archivo.Datos);
            File.Move(aparte, ruta, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(aparte);
            }
            catch (Exception borrar) when (borrar is IOException or UnauthorizedAccessException)
            {
                // Si ni se pudo crear, no hay qué borrar.
            }
            throw new ErrorCaja(ex is UnauthorizedAccessException
                ? $"No se pudo guardar en {ruta}: Windows no deja escribir ahí. Elige otra carpeta, como Documentos."
                : $"No se pudo guardar en {ruta}: si está abierto en Excel, ciérralo o guárdalo con otro nombre. ({ex.Message})");
        }
        _carpeta.Usado(ruta);
        return new ArchivoGuardado(Path.GetFileName(ruta), ruta);
    }

    /// <summary>Lo que se ofrece en «Tipo»: un Excel, o lo que sea por su extensión.</summary>
    public static string Filtro(string extension) => extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
        ? "Libro de Excel (*.xlsx)|*.xlsx"
        : $"Archivo {extension.TrimStart('.').ToUpperInvariant()} (*{extension})|*{extension}";
}

/// <summary>
/// Guardar en la caja del navegador (la que sirve el servicio en desarrollo y en las pruebas): una descarga como cualquier otra,
/// con caja.js; queda en la carpeta de descargas del navegador, que no dice cuál es.
/// </summary>
public sealed class DescargaDelNavegador(ModuloCaja js) : IGuardarArchivo
{
    public async Task<ArchivoGuardado?> GuardarAsync(ArchivoPorGuardar archivo)
    {
        try
        {
            await js.DescargarAsync(archivo.Nombre, archivo.Tipo, archivo.Datos);
        }
        catch (JSException ex)
        {
            throw new ErrorCaja($"El navegador no pudo descargar «{archivo.Nombre}»: {ex.Message}");
        }
        return new ArchivoGuardado(archivo.Nombre, null);
    }
}
