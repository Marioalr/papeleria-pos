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

    /// <summary>Si hay una memoria USB conectada (sin ella, «Guardar en USB» no guarda nada).</summary>
    public bool HayMemoriaUsb { get; set; } = true;

    public List<(string Archivo, string Nombre)> EnUsb { get; } = [];

    /// <summary>Lo que se elige con «Elegir el PDF descargado» (el plan B, con Edge); vacío: se cancela el diálogo.</summary>
    public string? Descargado { get; set; }

    /// <summary>La carpeta Descargas del usuario de Windows (de ahí, y solo de ahí, se borra el PDF elegido al entregarlo); vacía: no se sabe.</summary>
    public string? CarpetaDeDescargas { get; set; }

    /// <summary>Mientras está puesta, cerrar (borrar lo del perfil) espera a que se suelte, como un WebView2 que tarda en limpiarse.</summary>
    public TaskCompletionSource? CierreLento { get; set; }

    public string? Copiado { get; private set; }

    /// <summary>Lo que estaba en el portapapeles al cerrar: se quita.</summary>
    public bool PortapapelesLimpio { get; private set; }

    public bool Cerrado { get; private set; }

    /// <summary>Que abrir falle (el WebView2 no arranca).</summary>
    public bool FallaAlAbrir { get; set; }

    /// <summary>Que imprimir falle con este mensaje.</summary>
    public string? FallaAlImprimir { get; set; }

    /// <summary>Mientras está puesta, imprimir espera a que se suelte (la hoja todavía va a la cola), como un documento largo.</summary>
    public TaskCompletionSource? ImpresionLenta { get; set; }

    /// <summary>
    /// Las colas de la PC: la del agente (la WF-6590 por su IP), una que guarda PDF, la compartida desde la caja principal y una láser de la
    /// misma PC. Para un trámite se ofrecen la del agente primero y la láser (<see cref="ImpresorasParaTramites"/>).
    /// </summary>
    public IReadOnlyList<ColaDeLaPc> ColasDeLaPc { get; set; } =
    [
        new("Microsoft Print to PDF", "PORTPROMPT:", "Microsoft Print To PDF"),
        new("EPSON WF-6590 Series", "IP_192.168.1.197", "EPSON WF-6590 Series"),
        new(@"\\PRINCIPAL\EPSON WF-6590 Series", null, "EPSON WF-6590 Series", DeOtraPc: true),
        new("Brother HL-L2350DW", "USB001", "Brother HL-L2350DW series"),
    ];

    public string? ColaDelAgente { get; set; } = "EPSON WF-6590 Series";

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

    public IReadOnlyList<ColaDeLaPc> Colas() => ColasDeLaPc;

    public async Task ImprimirAsync(string archivo, string impresora, string nombreDelTrabajo, int copias)
    {
        if (FallaAlImprimir is { } falla)
            throw new InvalidOperationException(falla);
        if (ImpresionLenta is { } lenta)
            await lenta.Task;
        Anotar($"imprimir {nombreDelTrabajo} ×{copias} en {impresora} ({Path.GetFileName(archivo)})");
        lock (Impresos)
            Impresos.Add((nombreDelTrabajo, copias, impresora));
    }

    public Task<GuardadoEnUsb> GuardarEnUsbAsync(string archivo, string nombre)
    {
        Anotar($"guardar en USB como {nombre}");
        if (!HayMemoriaUsb)
            return Task.FromResult(GuardadoEnUsb.SinMemoria);
        EnUsb.Add((archivo, nombre));
        return Task.FromResult(new GuardadoEnUsb(Path.Combine("E:", nombre), Unidad: "KINGSTON (E:)"));
    }

    public Task<string?> ElegirPdfDescargadoAsync()
    {
        Anotar("elegir el PDF descargado");
        return Task.FromResult(Descargado);
    }

    public void CopiarArchivo(string archivo)
    {
        Anotar($"copiar {Path.GetFileName(archivo)}");
        Copiado = archivo;
    }

    public void AbrirEnElNavegador(Uri direccion) => Anotar($"abrir en el navegador {direccion}");

    public async Task CerrarAsync()
    {
        Anotar("cerrar");
        PortapapelesLimpio = Copiado is not null;
        (Atrapa, Politica) = (null, null);
        if (CierreLento is { } lento)
            await lento.Task;
        Cerrado = true;
        Anotar("cerrado");
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
