using PapeleriaPos.Impresiones;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Los trámites en la caja («Servicios», la CURP; el dueño, 2026-10-31; docs/tramites.md), sin la pantalla: a dónde puede ir la
/// página oficial, la carpeta del documento y el trámite de que se abre a que se termina, con el panel de Windows de mentiras
/// (<see cref="PanelDeTramitesFalso"/>). Lo que importa: el documento se atrapa con un nombre neutro, la primera copia va como
/// «T-0001 · CURP» y las demás aparte, se cobra una sola vez y solo si se entregó, al terminar se borra todo y el registro dice lo que
/// pasó sin nada del cliente.
/// </summary>
public sealed class TramitesTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"tramites-{Guid.NewGuid():N}");
    private readonly RelojFijo _reloj = new(new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero));
    private readonly PanelDeTramitesFalso _panel = new();
    private int _cobros;
    private string? _porQueNoSeCobra;

    private ArchivosDeTramites Archivos => new(_carpeta);

    private RegistroDiario Registro => new(Path.Combine(_carpeta, "registros"), "caja", reloj: _reloj);

    private TramiteEnCurso Tramite(IPanelDeTramites? panel = null, bool conSoloCobrar = false, string folio = "T-0042", TrabajosDeTramites? trabajos = null) =>
        new(Tramites.Curp, folio, panel, Archivos, () =>
        {
            if (_porQueNoSeCobra is { } porQue)
                return Task.FromResult<string?>(porQue);
            _cobros++;
            return Task.FromResult<string?>(null);
        }, conSoloCobrar ? () => Task.FromResult(true) : null, Registro, _reloj, trabajos);

    private string LoQueSeAnoto() => File.Exists(Registro.ArchivoDeHoy) ? File.ReadAllText(Registro.ArchivoDeHoy) : "";

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    // ── El catálogo y la política de navegación ──

    [Fact]
    public void La_curp_es_el_unico_tramite_y_se_encuentra_por_su_comando_su_rapido_y_lo_que_se_busca()
    {
        Assert.Equal([Tramites.Curp], Tramites.Todos);
        Assert.Equal("/curp", Tramites.Curp.Comando);
        Assert.Equal(new Uri("https://www.gob.mx/curp/"), Tramites.Curp.Direccion);
        Assert.Equal("CURP", Tramites.Curp.CodigoProducto);
        foreach (var comando in new[] { "curp", "/curp", "CURP", " Curp " })
            Assert.Same(Tramites.Curp, Tramites.PorClave(comando));
        Assert.Null(Tramites.PorClave("servicios"));
        Assert.Null(Tramites.PorClave(""));
        // El rápido que se llama como el trámite lo abre (Alt+2), también con algo entre paréntesis.
        Assert.Same(Tramites.Curp, Tramites.DelRapido("CURP"));
        Assert.Same(Tramites.Curp, Tramites.DelRapido("CURP (10)"));
        Assert.Null(Tramites.DelRapido("Copia B/N"));
        // Arriba de la búsqueda: con cuatro letras o más del principio («cur», camino de «curitas», todavía no).
        Assert.Equal([Tramites.Curp], Tramites.Buscados("curp"));
        Assert.Equal([Tramites.Curp], Tramites.Buscados(" CURP "));
        Assert.Empty(Tramites.Buscados("cur"));
        Assert.Empty(Tramites.Buscados("cuaderno"));
        Assert.Empty(Tramites.Buscados("curp gratis"));
    }

    [Theory]
    [InlineData("https://www.gob.mx/curp/", false, true)]
    [InlineData("https://gob.mx/curp", false, true)]
    [InlineData("https://consultas.curp.gob.mx/CurpSP/pdfgobmx", false, true)]
    [InlineData("https://www.gob.mx/v1/renapoCURP/consulta", false, true)]
    [InlineData("http://www.gob.mx/curp/", false, false)]
    [InlineData("https://www.facebook.com/", false, false)]
    [InlineData("https://curp-gratis.com.mx/", false, false)]
    [InlineData("https://www.gob.mx.falso.com/curp/", false, false)]
    [InlineData("https://imss.gob.mx/", false, false)]
    [InlineData("https://www.google.com/recaptcha/enterprise/anchor?k=x", true, true)]
    [InlineData("https://www.google.com/recaptcha/enterprise/anchor?k=x", false, false)]
    [InlineData("https://www.google.com/search?q=curp", true, false)]
    [InlineData("https://www.recaptcha.net/recaptcha/api2/bframe", true, true)]
    [InlineData("about:blank", false, true)]
    [InlineData("blob:https://www.gob.mx/0f3b2c1e-1111-2222-3333-444455556666", false, true)]
    [InlineData("blob:https://otro.com/0f3b2c1e-1111-2222-3333-444455556666", false, false)]
    [InlineData("data:application/pdf;base64,JVBERi0=", false, false)]
    [InlineData("file:///C:/Users/caja/Documents/otro.pdf", false, false)]
    public void La_pagina_solo_va_a_los_sitios_del_tramite_y_sus_marcos_al_captcha(string direccion, bool esMarco, bool permitida) =>
        Assert.Equal(permitida, Tramites.Curp.Politica().Permite(new Uri(direccion), esMarco));

    [Fact]
    public void El_documento_atrapado_se_puede_ensenar_y_ningun_otro_archivo()
    {
        var politica = Tramites.Curp.Politica();
        var documento = Path.Combine(_carpeta, "T-0042", "T-0042.pdf");
        Assert.False(politica.Permite(new Uri(documento)));

        politica.PermitirArchivo(documento);

        Assert.True(politica.Permite(new Uri(documento)));
        Assert.False(politica.Permite(new Uri(documento), esMarco: true));
        Assert.False(politica.Permite(new Uri(Path.Combine(_carpeta, "T-0042", "CURP.pdf"))));
    }

    // ── La carpeta del trámite ──

    [Fact]
    public void El_documento_va_con_su_folio_se_comparte_con_otro_nombre_y_se_borra_con_su_carpeta()
    {
        var archivos = Archivos;
        var ruta = archivos.NuevoDocumento("T-0042");
        Assert.Equal(Path.Combine(_carpeta, "T-0042", "T-0042.pdf"), ruta);
        File.WriteAllBytes(ruta, PanelDeTramitesFalso.Pdf);
        Assert.True(ArchivosDeTramites.EsPdf(ruta));

        var copia = archivos.ParaCompartir("T-0042", "CURP.pdf");

        Assert.Equal(Path.Combine(_carpeta, "T-0042", "CURP.pdf"), copia);
        Assert.Equal(PanelDeTramitesFalso.Pdf, File.ReadAllBytes(copia));
        Assert.True(archivos.Borrar("T-0042"));
        Assert.False(Directory.Exists(Path.Combine(_carpeta, "T-0042")));
        Assert.True(archivos.Borrar("T-0042"));
    }

    [Fact]
    public void Al_abrir_la_caja_se_borra_lo_que_quedo_de_un_tramite()
    {
        var archivos = Archivos;
        File.WriteAllBytes(archivos.NuevoDocumento("T-0007"), PanelDeTramitesFalso.Pdf);
        File.WriteAllBytes(archivos.NuevoDocumento("T-0008"), PanelDeTramitesFalso.Pdf);
        File.WriteAllText(Path.Combine(_carpeta, "suelto.tmp"), "x");

        Assert.Equal(3, archivos.LimpiarAlArrancar());

        Assert.Empty(Directory.EnumerateFileSystemEntries(_carpeta));
        Assert.Equal(0, new ArchivosDeTramites(Path.Combine(_carpeta, "no-existe")).LimpiarAlArrancar());
    }

    [Fact]
    public void Lo_que_no_empieza_como_un_pdf_no_es_un_pdf()
    {
        Directory.CreateDirectory(_carpeta);
        var html = Path.Combine(_carpeta, "pagina.pdf");
        File.WriteAllText(html, "<html><body>El servicio no está disponible, por favor intenta más tarde.</body></html>");
        Assert.False(ArchivosDeTramites.EsPdf(html));
        var vacio = Path.Combine(_carpeta, "vacio.pdf");
        File.WriteAllBytes(vacio, []);
        Assert.False(ArchivosDeTramites.EsPdf(vacio));
        Assert.False(ArchivosDeTramites.EsPdf(Path.Combine(_carpeta, "no-existe.pdf")));
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(72, "1 min 12 s")]
    [InlineData(3780, "1 h 3 min")]
    public void La_duracion_se_dice_corta(int segundos, string texto) => Assert.Equal(texto, TramiteEnCurso.Duracion(TimeSpan.FromSeconds(segundos)));

    [Fact]
    public void El_folio_lleva_cuatro_numeros_y_crece() =>
        Assert.Equal(["T-0001", "T-0042", "T-9999", "T-10000"], new[] { 1, 42, 9999, 10000 }.Select(TramiteEnCurso.FolioDe));

    // ── El trámite, de principio a fin ──

    [Fact]
    public async Task Atrapa_el_pdf_con_su_nombre_neutro_lo_imprime_como_su_folio_y_cobra_una_vez()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        Assert.Equal(new Uri("https://www.gob.mx/curp/"), _panel.Abierta);
        Assert.Equal(EstadoTramite.Abriendo, tramite.Estado);
        _panel.Atrapa!.Cargo();
        Assert.Equal(EstadoTramite.EnLaPagina, tramite.Estado);

        // La página descarga «CURP_…pdf»: queda como T-0042.pdf en la carpeta del trámite, y se enseña.
        var atrapado = await _panel.DescargarAsync();

        Assert.Equal(Path.Combine(_carpeta, "T-0042", "T-0042.pdf"), atrapado);
        Assert.Equal(EstadoTramite.Listo, tramite.Estado);
        Assert.Equal(1, tramite.Paginas);
        Assert.Contains("enseñar T-0042.pdf", _panel.Pedido);
        // El panel ya puede enseñar ese archivo, y ningún otro.
        Assert.True(_panel.Politica!.Permite(new Uri(atrapado!)));
        Assert.Equal(0, _cobros);

        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);

        Assert.Equal([("T-0042 · CURP", 1, "EPSON WF-6590 Series")], _panel.Impresos);
        Assert.Equal(1, _cobros);
        Assert.True(tramite.Cobrado);
        Assert.True(tramite.Entregado);

        // Otra vez, tres más: son copias de más (en F2, al precio de la impresión); la CURP no se cobra otra vez.
        await tramite.ImprimirAsync("EPSON WF-6590 Series", 3);

        Assert.Equal(("T-0042 · CURP · copias extra", 3, "EPSON WF-6590 Series"), _panel.Impresos[^1]);
        Assert.Equal(1, _cobros);
        Assert.Equal(4, tramite.Copias);
        Assert.Contains("Las 3 copias de más salen en F2", tramite.Aviso);

        _reloj.Ahora = _reloj.Ahora.AddSeconds(72);
        await tramite.TerminarAsync();

        Assert.True(_panel.Cerrado);
        Assert.False(Directory.Exists(Path.Combine(_carpeta, "T-0042")));
        Assert.Contains("Trámite T-0042 · CURP · impreso (4 copias) · 1 min 12 s", LoQueSeAnoto());
    }

    [Fact]
    public async Task Con_varias_copias_desde_la_primera_vez_una_va_incluida_y_las_demas_aparte()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();

        await tramite.ImprimirAsync("EPSON WF-6590 Series", 3);

        Assert.Equal([("T-0042 · CURP", 1, "EPSON WF-6590 Series"), ("T-0042 · CURP · copias extra", 2, "EPSON WF-6590 Series")], _panel.Impresos);
        Assert.Equal(1, _cobros);
        // Las copias, entre 1 y 20.
        await tramite.ImprimirAsync("EPSON WF-6590 Series", 500);
        Assert.Equal(TramiteEnCurso.CopiasMaximas, _panel.Impresos[^1].Copias);
    }

    [Fact]
    public async Task Compartido_por_usb_y_para_whatsapp_se_cobra_una_vez_y_al_terminar_se_quita_todo()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();

        // Sin memoria USB conectada: no se guarda nada (nunca en una carpeta de la PC), no cuenta como entregado y no se cobra.
        _panel.HayMemoriaUsb = false;
        await tramite.GuardarEnUsbAsync();
        Assert.Equal(0, _cobros);
        Assert.False(tramite.Entregado);
        Assert.False(tramite.Compartido);
        Assert.True(tramite.AvisoEsError);
        Assert.Contains("Conecta la memoria USB", tramite.Aviso);
        Assert.Empty(_panel.EnUsb);

        _panel.HayMemoriaUsb = true;
        await tramite.GuardarEnUsbAsync();
        Assert.Equal("Guardado en la memoria USB como «CURP.pdf».", tramite.Aviso);
        await tramite.CopiarParaWhatsAppAsync();

        Assert.Equal(1, _cobros);
        Assert.Equal("CURP.pdf", Assert.Single(_panel.EnUsb).Nombre);
        // Lo que se pega en WhatsApp se llama CURP.pdf (no T-0042.pdf, ni el nombre con la CURP de la persona), en la carpeta del trámite.
        Assert.Equal(Path.Combine(_carpeta, "T-0042", "CURP.pdf"), _panel.Copiado);
        Assert.True(File.Exists(_panel.Copiado));

        await tramite.TerminarAsync();

        Assert.True(_panel.PortapapelesLimpio);
        Assert.False(File.Exists(_panel.Copiado));
        Assert.Contains("Trámite T-0042 · CURP · guardado en USB y copiado para WhatsApp · 0 s", LoQueSeAnoto());
    }

    [Fact]
    public async Task Lo_que_no_es_un_pdf_no_se_guarda_y_un_pdf_que_no_abre_no_se_cobra()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();

        // Un .exe: ni siquiera se guarda.
        Assert.Null(await _panel.DescargarAsync([0x4D, 0x5A], ".exe", "application/octet-stream"));
        Assert.Equal(EstadoTramite.EnLaPagina, tramite.Estado);
        Assert.Contains("no es un PDF", tramite.Aviso);

        // Dice ser un PDF, pero es la página de error.
        await _panel.DescargarAsync("<html>El servicio no está disponible</html>"u8.ToArray());
        Assert.Equal(EstadoTramite.NoAbre, tramite.Estado);
        Assert.False(File.Exists(Path.Combine(_carpeta, "T-0042", "T-0042.pdf")));

        // Empieza como PDF, pero Windows no lo abre (agosto de 2026).
        _panel.PaginasDelPdf = null;
        await _panel.DescargarAsync();
        Assert.Equal(EstadoTramite.NoAbre, tramite.Estado);

        // No hay qué imprimir: no se manda nada ni se cobra.
        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);
        Assert.Empty(_panel.Impresos);
        Assert.Equal(0, _cobros);
        await tramite.TerminarAsync();
        Assert.Contains("Trámite T-0042 · CURP · sin documento (el PDF no abrió) · 0 s", LoQueSeAnoto());
    }

    [Theory]
    [InlineData(FallaDeLaPagina.SinInternet, EstadoTramite.SinInternet, "sin documento (sin internet)")]
    [InlineData(FallaDeLaPagina.NoContesta, EstadoTramite.NoContesta, "sin documento (la página no contestó)")]
    public async Task Sin_internet_o_con_el_sitio_caido_lo_dice_y_no_hay_nada_que_cobrar(FallaDeLaPagina falla, EstadoTramite estado, string anotado)
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();

        _panel.Atrapa!.NoCargo(falla);

        Assert.Equal(estado, tramite.Estado);
        Assert.True(tramite.AvisoEsError);
        Assert.Contains("no hay nada que cobrar", tramite.Aviso);
        // «Reintentar»: la página oficial otra vez.
        await tramite.ReintentarAsync();
        Assert.Contains("ir a https://www.gob.mx/curp/", _panel.Pedido);
        Assert.Equal(EstadoTramite.Abriendo, tramite.Estado);
        _panel.Atrapa!.NoCargo(falla);
        await tramite.TerminarAsync();
        Assert.Equal(0, _cobros);
        Assert.Contains($"Trámite T-0042 · CURP · {anotado} · 0 s", LoQueSeAnoto());
    }

    [Fact]
    public async Task Si_el_panel_no_abre_se_dice_y_en_Edge_el_pdf_descargado_se_elige_aqui_se_imprime_con_su_folio_y_se_borra_de_descargas()
    {
        _panel.FallaAlAbrir = true;
        var trabajos = new TrabajosDeTramites(_reloj);
        var tramite = Tramite(_panel, trabajos: trabajos);

        await tramite.AbrirAsync();

        Assert.Equal(EstadoTramite.NoContesta, tramite.Estado);
        Assert.Contains("ábrela en el navegador", tramite.Aviso);
        tramite.AbrirEnElNavegador();
        Assert.Contains("abrir en el navegador https://www.gob.mx/curp/", _panel.Pedido);
        Assert.Equal(EstadoTramite.EnElNavegador, tramite.Estado);
        Assert.Contains("Elegir el PDF descargado", tramite.Aviso);
        Assert.Contains("lo borra de Descargas", tramite.Aviso);
        // En Edge no se cobra sin documento (el plan B): el PDF se elige aquí.
        await tramite.CobrarSinDocumentoAsync();
        Assert.Equal(0, _cobros);

        // Lo que bajó Edge, con el nombre de la página (la CURP de la persona): se elige, la caja guarda su copia y lo borra de Descargas.
        var descargas = Path.Combine(_carpeta, "Descargas");
        Directory.CreateDirectory(descargas);
        var deEdge = Path.Combine(descargas, "CURP_PEPJ800101HPLRRN09.pdf");
        File.WriteAllBytes(deEdge, PanelDeTramitesFalso.Pdf);
        _panel.Descargado = deEdge;
        await tramite.ElegirPdfDescargadoAsync();

        Assert.Equal(EstadoTramite.Listo, tramite.Estado);
        Assert.False(File.Exists(deEdge));
        Assert.True(File.Exists(Path.Combine(_carpeta, "T-0042", "T-0042.pdf")));
        Assert.Equal("Se borró de Descargas: la caja ya tiene su copia. Imprímelo o compártelo aquí.", tramite.Aviso);
        // Se imprime como en el panel: la primera copia es la incluida, con el folio, y queda anotada para el agente.
        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);
        Assert.Equal([("T-0042 · CURP", 1, "EPSON WF-6590 Series")], _panel.Impresos);
        Assert.Equal(new TrabajoDeTramite("T-0042", false), trabajos.De("T-0042 · CURP", _reloj.Ahora.UtcDateTime.AddSeconds(2)));
        Assert.Equal(1, _cobros);
        await tramite.TerminarAsync();
        var anotado = LoQueSeAnoto();
        Assert.Contains("Trámite T-0042 · CURP · impreso (descargado en Edge) · 0 s", anotado);
        Assert.DoesNotContain("CURP_", anotado);
        // No se abrió: no hay qué cerrar.
        Assert.DoesNotContain("cerrar", _panel.Pedido);
    }

    [Fact]
    public async Task En_Edge_lo_que_no_abre_como_pdf_no_se_cobra_ni_se_borra_de_descargas()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        tramite.AbrirEnElNavegador();
        var descargas = Path.Combine(_carpeta, "Descargas");
        Directory.CreateDirectory(descargas);
        // Se eligió otro archivo de Descargas (no el que bajó Edge): no es un PDF.
        var otro = Path.Combine(descargas, "factura.pdf");
        File.WriteAllText(otro, "no es un PDF");
        _panel.Descargado = otro;

        await tramite.ElegirPdfDescargadoAsync();

        Assert.Equal(EstadoTramite.NoAbre, tramite.Estado);
        Assert.True(File.Exists(otro));
        Assert.True(tramite.AvisoEsError);
        Assert.Contains("no se borró de Descargas", tramite.Aviso);
        Assert.False(File.Exists(Path.Combine(_carpeta, "T-0042", "T-0042.pdf")));
        Assert.Equal(0, _cobros);

        // Se elige el bueno: ese sí se borra.
        var deEdge = Path.Combine(descargas, "CURP_PEPJ800101HPLRRN09.pdf");
        File.WriteAllBytes(deEdge, PanelDeTramitesFalso.Pdf);
        _panel.Descargado = deEdge;
        await tramite.ElegirPdfDescargadoAsync();
        Assert.Equal(EstadoTramite.Listo, tramite.Estado);
        Assert.False(File.Exists(deEdge));
        Assert.True(File.Exists(otro));
    }

    [Fact]
    public async Task Si_no_se_puede_borrar_de_descargas_lo_dice_y_lo_anota_sin_su_nombre()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        tramite.AbrirEnElNavegador();
        var descargas = Path.Combine(_carpeta, "Descargas");
        Directory.CreateDirectory(descargas);
        var deEdge = Path.Combine(descargas, "CURP_PEPJ800101HPLRRN09.pdf");
        File.WriteAllBytes(deEdge, PanelDeTramitesFalso.Pdf);
        // Que no se deje borrar: de solo lectura (Windows) o su carpeta sin permiso de escribir (Linux).
        if (OperatingSystem.IsWindows())
            File.SetAttributes(deEdge, FileAttributes.ReadOnly);
        else
            File.SetUnixFileMode(descargas, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            _panel.Descargado = deEdge;
            await tramite.ElegirPdfDescargadoAsync();

            Assert.Equal(EstadoTramite.Listo, tramite.Estado);
            Assert.True(File.Exists(deEdge));
            Assert.True(tramite.AvisoEsError);
            Assert.Contains("No se pudo borrar de Descargas: bórralo a mano", tramite.Aviso);
            await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);
            await tramite.TerminarAsync();
            var anotado = LoQueSeAnoto();
            Assert.Contains("Trámite T-0042 · CURP · impreso (descargado en Edge) · 0 s · el PDF se quedó en Descargas", anotado);
            Assert.DoesNotContain("CURP_", anotado);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
                File.SetAttributes(deEdge, FileAttributes.Normal);
            else
                File.SetUnixFileMode(descargas, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Lo_que_manda_a_imprimir_queda_anotado_para_el_agente_de_esta_caja()
    {
        var trabajos = new TrabajosDeTramites(_reloj);
        var tramite = Tramite(_panel, trabajos: trabajos);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();

        await tramite.ImprimirAsync("EPSON WF-6590 Series", 3);

        var enLaCola = _reloj.Ahora.UtcDateTime.AddSeconds(5);
        Assert.Equal(new TrabajoDeTramite("T-0042", false), trabajos.De("T-0042 · CURP", enLaCola));
        Assert.Equal(new TrabajoDeTramite("T-0042", true), trabajos.De("T-0042 · CURP · copias extra", enLaCola));
        // Lo de un cliente que se llame parecido, no.
        Assert.Null(trabajos.De("T-0042.pdf", enLaCola));
    }

    [Fact]
    public void Para_un_tramite_se_ofrece_la_impresora_del_agente_primero_y_nunca_las_que_guardan_archivos_ni_las_de_otra_pc()
    {
        ColaDeLaPc[] colas =
        [
            new("Microsoft Print to PDF", "PORTPROMPT:", "Microsoft Print To PDF"),
            new("Microsoft XPS Document Writer", "PORTPROMPT:", "Microsoft XPS Document Writer v4"),
            new("OneNote (Desktop)", "nul:", "Send to Microsoft OneNote 16 Driver"),
            new("Fax", "SHRFAX:", "Microsoft Shared Fax Driver"),
            new("Brother HL-L2350DW", "USB001", "Brother HL-L2350DW series"),
            new(@"\\PRINCIPAL\EPSON WF-6590 Series", null, "EPSON WF-6590 Series", DeOtraPc: true),
            new("EPSON WF-6590 Series", "IP_192.168.1.197", "EPSON WF-6590 Series"),
        ];

        Assert.Equal([new ImpresoraParaTramite("EPSON WF-6590 Series", true), new ImpresoraParaTramite("Brother HL-L2350DW", false)],
            ImpresorasParaTramites.Elegir(colas, "epson wf-6590 series"));
        // Sin la del agente (la caja no vigila ninguna, o la que dice no está): las de esta PC, y ninguna cuenta las copias de más.
        Assert.Equal([new ImpresoraParaTramite("Brother HL-L2350DW", false), new ImpresoraParaTramite("EPSON WF-6590 Series", false)],
            ImpresorasParaTramites.Elegir(colas, null));
        Assert.Equal([new ImpresoraParaTramite("Brother HL-L2350DW", false), new ImpresoraParaTramite("EPSON WF-6590 Series", false)],
            ImpresorasParaTramites.Elegir(colas, "Otra que no existe"));
        // La compartida desde otra PC no, aunque sea la que dice la caja: ahí la mediría Control Print y saldría por cobrar otra vez.
        Assert.DoesNotContain(ImpresorasParaTramites.Elegir(colas, @"\\PRINCIPAL\EPSON WF-6590 Series"), i => i.Nombre.StartsWith(@"\\"));
    }

    [Fact]
    public async Task En_una_impresora_que_no_mide_el_agente_las_copias_de_mas_se_cobran_a_mano_y_la_que_no_imprime_no_se_usa()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();
        Assert.Equal(["EPSON WF-6590 Series", "Brother HL-L2350DW"], tramite.Impresoras().Select(i => i.Nombre));

        // «Guardar como PDF» no es una impresora para trámites: la CURP del cliente quedaría en la PC.
        await tramite.ImprimirAsync("Microsoft Print to PDF", 1);
        Assert.Empty(_panel.Impresos);
        Assert.Equal("«Microsoft Print to PDF» no es una impresora para trámites.", tramite.Aviso);

        await tramite.ImprimirAsync("Brother HL-L2350DW", 3);

        Assert.Equal([("T-0042 · CURP", 1, "Brother HL-L2350DW"), ("T-0042 · CURP · copias extra", 2, "Brother HL-L2350DW")], _panel.Impresos);
        Assert.Equal("Impreso en «Brother HL-L2350DW». En esta impresora las 2 copias de más no se cuentan solas: cóbralas a mano.", tramite.Aviso);
        Assert.DoesNotContain("F2", tramite.Aviso);
    }

    [Fact]
    public async Task Si_la_curp_no_entro_al_ticket_se_reintenta_sin_volver_a_imprimir()
    {
        _porQueNoSeCobra = "la caja principal no contestó.";
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();
        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);
        Assert.True(tramite.SinCobrar);
        Assert.False(tramite.Entregado);

        _porQueNoSeCobra = null;
        await tramite.ReintentarCobroAsync();

        Assert.True(tramite.Cobrado);
        Assert.True(tramite.Entregado);
        Assert.False(tramite.SinCobrar);
        Assert.Single(_panel.Impresos);
        Assert.Equal(1, _cobros);
        Assert.Equal("La CURP ya está en el ticket.", tramite.Aviso);
    }

    [Fact]
    public async Task Esc_y_F12_en_la_pagina_oficial_van_a_la_caja()
    {
        var tramite = Tramite(_panel);
        var teclas = new List<string>();
        tramite.TeclaDeLaPagina += teclas.Add;
        await tramite.AbrirAsync();

        _panel.Atrapa!.Tecla("Escape");
        _panel.Atrapa!.Tecla("F12");
        _panel.Atrapa!.Tecla("Enter");

        Assert.Equal(["Escape", "F12"], teclas);
    }

    [Fact]
    public async Task Un_documento_de_mas_paginas_de_las_que_se_imprimen_lo_dice()
    {
        _panel.PaginasDelPdf = 60;
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();

        await _panel.DescargarAsync();

        Assert.Equal(EstadoTramite.Listo, tramite.Estado);
        Assert.Equal($"El documento tiene 60 páginas: se imprimen las primeras {TramiteEnCurso.PaginasMaximas}.", tramite.Aviso);
    }

    [Fact]
    public void En_la_memoria_usb_no_se_pisa_lo_que_ya_tiene()
    {
        var hay = new HashSet<string>([Path.Combine("E:", "CURP.pdf"), Path.Combine("E:", "CURP (2).pdf")]);

        Assert.Equal(Path.Combine("E:", "CURP (3).pdf"), ArchivosDeTramites.NombreLibre("E:", "CURP.pdf", hay.Contains));
        Assert.Equal(Path.Combine("E:", "CURP.pdf"), ArchivosDeTramites.NombreLibre("E:", "CURP.pdf", _ => false));
    }

    [Fact]
    public async Task Si_se_termina_mientras_abre_el_panel_no_se_queda()
    {
        var abriendo = new TaskCompletionSource();
        var panel = new PanelQueTardaEnAbrir(_panel, abriendo.Task);
        var tramite = Tramite(panel);

        var abrir = tramite.AbrirAsync();
        await tramite.TerminarAsync();
        abriendo.SetResult();
        await abrir;

        Assert.True(_panel.Cerrado);
        Assert.Equal(EstadoTramite.Terminado, tramite.Estado);
    }

    /// <summary>El panel de mentiras, que tarda en abrir hasta que se le diga (el WebView2 arrancando).</summary>
    private sealed class PanelQueTardaEnAbrir(PanelDeTramitesFalso panel, Task listo) : IPanelDeTramites
    {
        public async Task AbrirAsync(Uri direccion, PoliticaDeNavegacion politica, IAtrapaDocumentos atrapa)
        {
            await listo;
            await panel.AbrirAsync(direccion, politica, atrapa);
        }

        public void Colocar(LugarEnPantalla lugar) => panel.Colocar(lugar);
        public Task IrAAsync(Uri direccion) => panel.IrAAsync(direccion);
        public Task<bool> GuardarPaginaComoPdfAsync(string destino) => panel.GuardarPaginaComoPdfAsync(destino);
        public Task<int?> PaginasAsync(string archivo) => panel.PaginasAsync(archivo);
        public Task MostrarDocumentoAsync(string archivo) => panel.MostrarDocumentoAsync(archivo);
        public string? ColaDelAgente => panel.ColaDelAgente;
        public IReadOnlyList<ColaDeLaPc> Colas() => panel.Colas();
        public Task ImprimirAsync(string archivo, string impresora, string nombreDelTrabajo, int copias) => panel.ImprimirAsync(archivo, impresora, nombreDelTrabajo, copias);
        public Task<GuardadoEnUsb> GuardarEnUsbAsync(string archivo, string nombre) => panel.GuardarEnUsbAsync(archivo, nombre);
        public Task<string?> ElegirPdfDescargadoAsync() => panel.ElegirPdfDescargadoAsync();
        public void CopiarArchivo(string archivo) => panel.CopiarArchivo(archivo);
        public void AbrirEnElNavegador(Uri direccion) => panel.AbrirEnElNavegador(direccion);
        public Task CerrarAsync() => panel.CerrarAsync();
    }

    [Fact]
    public async Task Ya_salio_guarda_la_pagina_como_pdf_con_su_nombre_neutro()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();

        await tramite.YaSalioAsync();

        Assert.Contains("guardar la página como T-0042.pdf", _panel.Pedido);
        Assert.Equal(EstadoTramite.Listo, tramite.Estado);
        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);
        Assert.Equal(1, _cobros);
    }

    [Fact]
    public async Task Sin_panel_la_caja_del_navegador_lo_abre_en_otra_pestana_y_cobra_una_vez()
    {
        var tramite = Tramite();

        await tramite.AbrirAsync();

        Assert.False(tramite.ConPanel);
        Assert.Equal(EstadoTramite.EnOtraPestana, tramite.Estado);
        Assert.False(tramite.Entregado);
        await tramite.CobrarSinDocumentoAsync();
        await tramite.CobrarSinDocumentoAsync();
        Assert.Equal(1, _cobros);
        Assert.True(tramite.Entregado);
        Assert.Contains("Borra el PDF de Descargas", tramite.Aviso);
        // Sin panel no se imprime ni se comparte nada desde aquí.
        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);
        await tramite.GuardarEnUsbAsync();
        await tramite.TerminarAsync();
        Assert.Contains("Trámite T-0042 · CURP · cobrado (en otra pestaña) · 0 s", LoQueSeAnoto());
    }

    [Fact]
    public async Task Si_no_hay_producto_lo_dice_y_lo_vuelve_a_intentar_al_entregarlo_otra_vez()
    {
        _porQueNoSeCobra = "no hay un producto con el código «CURP».";
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();

        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);

        Assert.False(tramite.Cobrado);
        Assert.True(tramite.AvisoEsError);
        Assert.Equal("No se agregó la CURP al ticket: no hay un producto con el código «CURP».", tramite.Aviso);

        // Ya lo dieron de alta: la siguiente entrega lo cobra (la segunda impresión ya es una copia de más).
        _porQueNoSeCobra = null;
        await tramite.CopiarParaWhatsAppAsync();
        Assert.True(tramite.Cobrado);
        Assert.Equal(1, _cobros);
    }

    [Fact]
    public async Task Imprimir_que_falla_lo_dice_y_no_cobra()
    {
        _panel.FallaAlImprimir = "La impresora no contesta.";
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();

        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);

        Assert.Equal("No salió en «EPSON WF-6590 Series»: La impresora no contesta.", tramite.Aviso);
        Assert.Equal(0, _cobros);
        Assert.False(tramite.Ocupado);
        // Sin impresora elegida, no se manda.
        _panel.FallaAlImprimir = null;
        await tramite.ImprimirAsync(" ", 1);
        Assert.Empty(_panel.Impresos);
    }

    [Fact]
    public async Task Solo_cobrar_se_ofrece_hasta_que_algo_se_cobra()
    {
        var tramite = Tramite(_panel, conSoloCobrar: true);
        await tramite.AbrirAsync();
        Assert.True(tramite.PuedeSoloCobrar);

        Assert.True(await tramite.SoloCobrarAsync());

        Assert.True(tramite.SoloCobrado);
        Assert.False(tramite.PuedeSoloCobrar);
        Assert.False(await tramite.SoloCobrarAsync());
        await tramite.TerminarAsync();
        Assert.Contains("Trámite T-0042 · CURP · solo cobrar · 0 s", LoQueSeAnoto());
        // Sin rápido, no hay «Solo cobrar».
        Assert.False(Tramite(_panel, folio: "T-0043").PuedeSoloCobrar);
    }

    [Fact]
    public async Task Un_enlace_fuera_de_la_pagina_oficial_se_dice_y_lo_que_llega_al_terminar_no_se_queda()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        var atrapa = _panel.Atrapa!;
        atrapa.Cargo();
        atrapa.Bloqueado();
        Assert.Equal("Ese enlace va fuera de gob.mx/curp: aquí no se abre.", tramite.Aviso);
        Assert.False(tramite.AvisoEsError);
        var destino = atrapa.DondeGuardar(".pdf", "application/pdf")!;
        await File.WriteAllBytesAsync(destino, PanelDeTramitesFalso.Pdf);

        // Se terminó antes de que acabara de bajar: el archivo no se queda.
        await tramite.TerminarAsync();
        await atrapa.DescargadoAsync(destino);

        Assert.False(File.Exists(destino));
        Assert.Null(atrapa.DondeGuardar(".pdf", "application/pdf"));
        Assert.Equal(0, _cobros);
        // Terminar dos veces no anota dos veces.
        await tramite.TerminarAsync();
        Assert.Single(LoQueSeAnoto().Split('\n'), l => l.Contains("Trámite T-0042"));
    }

    [Fact]
    public async Task El_registro_no_lleva_nada_del_cliente()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        // El nombre que trae la descarga de la página («CURP_» y la CURP) nunca llega al trámite: solo su extensión.
        await _panel.DescargarAsync();
        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);
        await tramite.CopiarParaWhatsAppAsync();
        await tramite.TerminarAsync();

        var anotado = LoQueSeAnoto();
        Assert.Contains("Trámite T-0042 · CURP · impreso y copiado para WhatsApp · 0 s", anotado);
        Assert.DoesNotContain("CURP_", anotado);
        Assert.DoesNotContain("gob.mx", anotado);
        Assert.DoesNotContain(_carpeta, anotado);
        Assert.Single(anotado.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}
