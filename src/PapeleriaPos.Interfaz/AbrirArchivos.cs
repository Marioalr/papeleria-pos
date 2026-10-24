using Microsoft.AspNetCore.Components.Forms;

namespace PapeleriaPos.Interfaz;

/// <summary>Un archivo que se eligió para abrirlo (el Excel de los productos que se importa): cómo se llama y lo que lleva.</summary>
/// <param name="Ruta">En esta PC (la ventana de Windows); vacía si lo dio el navegador, que no dice dónde estaba.</param>
public sealed record ArchivoAbierto(string Nombre, byte[] Datos, string? Ruta = null);

/// <summary>Qué archivo se pide: lo que dice arriba el diálogo, de qué tipo y cuánto puede pesar.</summary>
/// <param name="Titulo">«Elegir el Excel de los productos».</param>
/// <param name="Extension">«.xlsx».</param>
public sealed record ArchivoPorAbrir(string Titulo, string Extension, long BytesMaximos);

/// <summary>Lo que pregunta «Abrir» de Windows: en qué carpeta empieza, su título y el tipo de archivo («Libro de Excel (*.xlsx)|*.xlsx»).</summary>
public sealed record PreguntaAbrir(string Carpeta, string Titulo, string Filtro);

/// <summary>
/// Elegir un archivo para abrirlo desde la caja (2026-10-22, para importar el Excel de los productos; antes la caja solo elegía
/// imágenes, con el selector de la página), junto a <see cref="IGuardarArchivo"/>: en la ventana de Windows, «Abrir» de Windows
/// (<see cref="AbiertoEnLaPc"/>), que pone su anfitrión. En la caja del navegador no hay: ahí la página pone el selector de
/// archivos del navegador (<c>&lt;input type=file&gt;</c>, <see cref="ArchivoDelNavegador"/>), que solo se abre con un clic o una tecla
/// de quien la usa. Las pruebas ponen uno de mentiras.
/// </summary>
public interface IAbrirArchivo
{
    /// <returns>El archivo elegido, ya leído; vacío si quien lo elegía canceló.</returns>
    /// <exception cref="ErrorCaja">No se pudo leer (pesa demasiado, se quitó la memoria), en palabras de la tienda.</exception>
    Task<ArchivoAbierto?> AbrirAsync(ArchivoPorAbrir archivo);
}

/// <summary>
/// Abrir un archivo de esta PC (la ventana de Windows): «Abrir» de Windows pregunta cuál, empezando donde se guardó o se abrió el
/// último (<see cref="CarpetaDeArchivos"/>: el Excel que se acaba de exportar está ahí), y aquí se lee. Se lee aunque esté
/// abierto en Excel (Excel deja leerlo mientras lo tiene abierto): se lee lo último que se guardó. «Abrir» lo pone el anfitrión
/// (en la ventana, en su hilo): así esto se prueba sin Windows.
/// </summary>
/// <param name="abrir">Pregunta cuál abrir; vacío si se canceló.</param>
public sealed class AbiertoEnLaPc(Func<PreguntaAbrir, Task<string?>> abrir, CarpetaDeArchivos carpeta) : IAbrirArchivo
{
    /// <summary>Donde empieza «Abrir»: la carpeta del último que se guardó o se abrió (la primera vez, Documentos).</summary>
    public string Carpeta => carpeta.Ruta;

    public async Task<ArchivoAbierto?> AbrirAsync(ArchivoPorAbrir archivo)
    {
        var ruta = await abrir(new PreguntaAbrir(carpeta.Ruta, archivo.Titulo, GuardadoEnLaPc.Filtro(archivo.Extension)));
        if (string.IsNullOrWhiteSpace(ruta))
            return null;
        byte[] datos;
        try
        {
            // Excel tiene abierto el archivo mientras se edita y deja leerlo así: sin compartirlo para escribir, no se podría.
            await using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (flujo.Length > archivo.BytesMaximos)
                throw new ErrorCaja(DemasiadoGrande(Path.GetFileName(ruta), flujo.Length, archivo.BytesMaximos));
            datos = new byte[flujo.Length];
            await flujo.ReadExactlyAsync(datos);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ErrorCaja(ex is UnauthorizedAccessException
                ? $"No se pudo leer {ruta}: Windows no deja abrirlo. Cópialo a Documentos y elígelo de ahí."
                : $"No se pudo leer {ruta}: si se quitó la memoria USB o se movió, búscalo otra vez. ({ex.Message})");
        }
        carpeta.Usado(ruta);
        return new ArchivoAbierto(Path.GetFileName(ruta), datos, ruta);
    }

    /// <summary>«Productos.xlsx» pesa 9.5 MB: se importan archivos de hasta 8 MB.</summary>
    public static string DemasiadoGrande(string nombre, long bytes, long maximo) =>
        $"«{nombre}» pesa {Megas(bytes)}: se importan archivos de hasta {Megas(maximo)}. Pártelo en dos, o exporta un departamento a la vez.";

    private static string Megas(long bytes) => $"{(bytes / 1024m / 1024m).ToString("0.#", Formato.Mexico)} MB";
}

/// <summary>
/// El archivo que dio el selector del navegador (<c>&lt;input type=file&gt;</c>, en la caja del navegador): se lee de una vez, hasta
/// lo que puede pesar.
/// </summary>
public static class ArchivoDelNavegador
{
    public static async Task<ArchivoAbierto> LeerAsync(IBrowserFile archivo, long bytesMaximos)
    {
        if (archivo.Size > bytesMaximos)
            throw new ErrorCaja(AbiertoEnLaPc.DemasiadoGrande(archivo.Name, archivo.Size, bytesMaximos));
        try
        {
            await using var flujo = archivo.OpenReadStream(maxAllowedSize: bytesMaximos);
            using var memoria = new MemoryStream();
            await flujo.CopyToAsync(memoria);
            return new ArchivoAbierto(archivo.Name, memoria.ToArray());
        }
        catch (IOException ex)
        {
            throw new ErrorCaja($"No se pudo leer «{archivo.Name}»: elígelo otra vez. ({ex.Message})");
        }
    }
}
