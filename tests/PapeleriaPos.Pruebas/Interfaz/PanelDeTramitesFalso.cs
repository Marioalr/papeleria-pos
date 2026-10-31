using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El panel de los trámites de la ventana de Windows, de mentiras (2026-10-31): anota lo que se le pide (abrir, imprimir con qué nombre
/// de trabajo, guardar, copiar, cerrar) y la prueba hace lo que haría la página oficial: cargar, no cargar, descargar un documento.
/// Así se prueba en Linux todo lo que decide el trámite (<see cref="TramiteEnCurso"/>): atrapar, nombrar, cobrar una vez y borrar.
/// </summary>
internal sealed class PanelDeTramitesFalso : IPanelDeTramites
{
    private readonly List<string> _pedido = [];

    public IAtrapaDocumentos? Atrapa { get; private set; }

    public PoliticaDeNavegacion? Politica { get; private set; }

    public Uri? Abierta { get; private set; }

    /// <summary>Lo que se le pidió, en orden («abrir https://…», «imprimir T-0001 · CURP ×1 en …», «cerrar»).</summary>
    public IReadOnlyList<string> Pedido
    {
        get
        {
            lock (_pedido)
                return [.. _pedido];
        }
    }

    /// <summary>Los trabajos que se mandaron a imprimir: su nombre y sus copias.</summary>
    public List<(string Nombre, int Copias, string Impresora)> Impresos { get; } = [];

    public List<LugarEnPantalla> Lugares { get; } = [];

    /// <summary>Cuántas páginas dice que tiene un PDF que abre; vacío: no abre.</summary>
    public int? PaginasDelPdf { get; set; } = 1;

    /// <summary>Si «Guardar en USB» se guarda (o se cancela el diálogo).</summary>
    public bool GuardaEnUsb { get; set; } = true;

    public List<(string Archivo, string Nombre)> EnUsb { get; } = [];

    public string? Copiado { get; private set; }

    /// <summary>Lo que estaba en el portapapeles al cerrar: se quita.</summary>
    public bool PortapapelesLimpio { get; private set; }

    public bool Cerrado { get; private set; }

    /// <summary>Que abrir falle (el WebView2 no arranca).</summary>
    public bool FallaAlAbrir { get; set; }

    /// <summary>Que imprimir falle con este mensaje.</summary>
    public string? FallaAlImprimir { get; set; }

    public IReadOnlyList<string> ImpresorasDeLaPc { get; set; } = ["EPSON WF-6590 Series", "Tickets"];

    private void Anotar(string que)
    {
        lock (_pedido)
            _pedido.Add(que);
    }

    public Task AbrirAsync(Uri direccion, PoliticaDeNavegacion politica, IAtrapaDocumentos atrapa)
    {
        Anotar($"abrir {direccion}");
        if (FallaAlAbrir)
            throw new InvalidOperationException("El WebView2 no arrancó.");
        (Abierta, Politica, Atrapa, Cerrado) = (direccion, politica, atrapa, false);
        return Task.CompletedTask;
    }

    public void Colocar(LugarEnPantalla lugar)
    {
        lock (Lugares)
            Lugares.Add(lugar);
    }

    public Task IrAAsync(Uri direccion)
    {
        Anotar($"ir a {direccion}");
        return Task.CompletedTask;
    }

    public Task<bool> GuardarPaginaComoPdfAsync(string destino)
    {
        Anotar($"guardar la página como {Path.GetFileName(destino)}");
        File.WriteAllBytes(destino, Pdf);
        return Task.FromResult(true);
    }

    public Task<int?> PaginasAsync(string archivo) => Task.FromResult(PaginasDelPdf);

    public Task MostrarDocumentoAsync(string archivo)
    {
        Anotar($"enseñar {Path.GetFileName(archivo)}");
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> Impresoras() => ImpresorasDeLaPc;

    public Task ImprimirAsync(string archivo, string impresora, string nombreDelTrabajo, int copias)
    {
        if (FallaAlImprimir is { } falla)
            throw new InvalidOperationException(falla);
        Anotar($"imprimir {nombreDelTrabajo} ×{copias} en {impresora} ({Path.GetFileName(archivo)})");
        lock (Impresos)
            Impresos.Add((nombreDelTrabajo, copias, impresora));
        return Task.CompletedTask;
    }

    public Task<bool> GuardarEnUsbAsync(string archivo, string nombre)
    {
        Anotar($"guardar en USB como {nombre}");
        if (GuardaEnUsb)
            EnUsb.Add((archivo, nombre));
        return Task.FromResult(GuardaEnUsb);
    }

    public void CopiarArchivo(string archivo)
    {
        Anotar($"copiar {Path.GetFileName(archivo)}");
        Copiado = archivo;
    }

    public void AbrirEnElNavegador(Uri direccion) => Anotar($"abrir en el navegador {direccion}");

    public Task CerrarAsync()
    {
        Anotar("cerrar");
        PortapapelesLimpio = Copiado is not null;
        (Cerrado, Atrapa, Politica) = (true, null, null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// La página descarga su documento, como lo haría la de la CURP («CURP_» y la CURP de la persona): al panel solo le llega la
    /// extensión y el tipo; el archivo va donde dijo el trámite, con <paramref name="contenido"/> (un PDF, si no se dice otra cosa).
    /// Devuelve dónde quedó, o vacío si el trámite no lo dejó.
    /// </summary>
    public async Task<string?> DescargarAsync(byte[]? contenido = null, string extension = ".pdf", string tipo = "application/pdf")
    {
        if (Atrapa is not { } atrapa || atrapa.DondeGuardar(extension, tipo) is not { } destino)
            return null;
        await File.WriteAllBytesAsync(destino, contenido ?? Pdf);
        await atrapa.DescargadoAsync(destino);
        return destino;
    }

    /// <summary>Un PDF chico de una página (lo que importa es que empiece como uno).</summary>
    public static byte[] Pdf { get; } = "%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n"u8.ToArray();
}
