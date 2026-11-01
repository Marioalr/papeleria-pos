namespace PapeleriaPos.Interfaz;

/// <summary>
/// Donde se guarda el documento de un trámite mientras dura (docs/tramites.md, «Atrapar el documento»; 2026-10-31): una carpeta
/// por folio, con el documento con su nombre neutro («T-0042\T-0042.pdf»: el de la página trae la CURP de la persona) y, si se
/// comparte, la copia con el nombre para el cliente («T-0042\CURP.pdf»). Se borra la carpeta al terminar el trámite y, al abrir la
/// caja, todo lo que haya quedado (se cerró a la mitad de uno). Nunca se lee ni se anota lo que dice el documento.
/// </summary>
public sealed class ArchivosDeTramites(string carpeta)
{
    /// <summary>En la caja de Windows: %LocalAppData%\PapeleriaPos\tramites, junto al registro de la caja (nada que se sincronice).</summary>
    public static string CarpetaDeLaPc => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PapeleriaPos", "tramites");

    public string Carpeta { get; } = Path.GetFullPath(carpeta);

    public string CarpetaDe(string folio) => Path.Combine(Carpeta, folio);

    /// <summary>El documento del trámite, con su nombre neutro: «…\T-0042\T-0042.pdf».</summary>
    public string DocumentoDe(string folio) => Path.Combine(CarpetaDe(folio), $"{folio}.pdf");

    /// <summary>Lo deja listo para guardar ahí un documento nuevo (la carpeta, sin el de antes): «Descargar pdf» otra vez lo reemplaza.</summary>
    public string NuevoDocumento(string folio)
    {
        Directory.CreateDirectory(CarpetaDe(folio));
        var ruta = DocumentoDe(folio);
        File.Delete(ruta);
        return ruta;
    }

    /// <summary>
    /// El PDF que se descargó en Edge (el plan B, «Elegir el PDF descargado»), copiado como el documento del trámite, con su nombre neutro.
    /// Devuelve dónde quedó.
    /// </summary>
    public string Adoptar(string folio, string descargado)
    {
        var ruta = NuevoDocumento(folio);
        File.Copy(descargado, ruta, overwrite: true);
        return ruta;
    }

    /// <summary>
    /// Si <paramref name="archivo"/> está en <paramref name="carpeta"/> misma (no en una de sus subcarpetas): el PDF elegido en Edge solo se borra
    /// si está en Descargas (segunda revisión de servicios/curp). Sin carpeta, no.
    /// </summary>
    public static bool EstaEn(string archivo, string? carpeta)
    {
        if (string.IsNullOrWhiteSpace(carpeta))
            return false;
        try
        {
            var donde = Path.GetDirectoryName(Path.GetFullPath(archivo));
            var esa = Path.TrimEndingDirectorySeparator(Path.GetFullPath(carpeta));
            return string.Equals(donde, esa, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Borra de Descargas el PDF elegido en Edge, ya entregado (la caja lo dijo al elegirlo). Sin la Papelera: ahí se quedaría la CURP de la
    /// persona. Devuelve si se pudo; si no (abierto en Edge, sin permiso), la caja dice que se borre a mano.
    /// </summary>
    public static bool BorrarDescargado(string ruta)
    {
        try
        {
            File.Delete(ruta);
            return !File.Exists(ruta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Un nombre que no pise lo que ya hay en <paramref name="carpeta"/> (la memoria USB del cliente): «CURP.pdf», y si ya está, «CURP
    /// (2).pdf», «CURP (3).pdf»…
    /// </summary>
    public static string NombreLibre(string carpeta, string nombre, Func<string, bool>? existe = null)
    {
        existe ??= File.Exists;
        var ruta = Path.Combine(carpeta, nombre);
        var (base_, extension) = (Path.GetFileNameWithoutExtension(nombre), Path.GetExtension(nombre));
        for (var n = 2; existe(ruta) && n < 1000; n++)
            ruta = Path.Combine(carpeta, $"{base_} ({n}){extension}");
        return ruta;
    }

    /// <summary>Una copia del documento con el nombre para el cliente («CURP.pdf»), en la misma carpeta: se borra con ella.</summary>
    public string ParaCompartir(string folio, string nombre)
    {
        var copia = Path.Combine(CarpetaDe(folio), Path.GetFileName(nombre));
        File.Copy(DocumentoDe(folio), copia, overwrite: true);
        return copia;
    }

    /// <summary>Borra la carpeta del trámite con lo que tenga. Si algo la tiene abierta, se queda para la próxima vez que abra la caja.</summary>
    public bool Borrar(string folio) => BorrarCarpeta(CarpetaDe(folio));

    /// <summary>
    /// Al abrir la caja: lo que haya quedado de un trámite (la caja se cerró a la mitad de uno, se fue la luz). Devuelve cuántos
    /// trámites se borraron.
    /// </summary>
    public int LimpiarAlArrancar()
    {
        if (!Directory.Exists(Carpeta))
            return 0;
        var borrados = 0;
        foreach (var dentro in Directory.EnumerateDirectories(Carpeta))
            if (BorrarCarpeta(dentro))
                borrados++;
        foreach (var archivo in Directory.EnumerateFiles(Carpeta))
        {
            try
            {
                File.Delete(archivo);
                borrados++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Abierto por otro programa: la próxima vez.
            }
        }
        return borrados;
    }

    /// <summary>
    /// Si es un PDF: empieza con «%PDF-» (en su primer kilobyte, como lo revisa el medidor de PDF). Que abra lo dice la ventana de
    /// Windows (<see cref="IPanelDeTramites.PaginasAsync"/>): en agosto de 2026 la página de la CURP dio PDF que no abrían.
    /// </summary>
    public static bool EsPdf(string ruta)
    {
        try
        {
            using var archivo = File.OpenRead(ruta);
            var inicio = new byte[1024];
            var leidos = archivo.ReadAtLeast(inicio, inicio.Length, throwOnEndOfStream: false);
            return inicio.AsSpan(0, leidos).IndexOf("%PDF-"u8) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool BorrarCarpeta(string ruta)
    {
        try
        {
            if (Directory.Exists(ruta))
                Directory.Delete(ruta, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
