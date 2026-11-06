using PapeleriaPos.Impresiones;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Los trámites en la caja («Servicios», la CURP; el dueño, 2026-10-31; docs/tramites.md), sin la pantalla: a dónde puede ir la
/// página oficial, la carpeta del documento y el trámite de que se abre a que se termina, con el panel de Windows de mentiras
/// (<see cref="PanelDeTramitesFalso"/>). Lo que importa: el documento se atrapa con un nombre neutro, la primera copia va como
/// «T-0001 · CURP» y las demás aparte, se cobra una sola vez y solo si se entregó, al terminar se borra todo y el registro dice lo que
/// pasó sin nada del cliente. En Edge (el plan B), el PDF elegido se borra de Descargas solo al entregarlo y solo si está ahí.
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

    /// <summary>
    /// Un PDF de Descargas de solo lectura (el plan B de Edge): su copia con el folio no lo es, y la carpeta del trámite se borra. Antes,
    /// en Windows, File.Copy le pasaba «Solo lectura» y la CURP del cliente se quedaba en la PC (2026-11-03, el CI de Windows). Y lo que
    /// quedó de solo lectura en un trámite de antes también se borra al abrir la caja.
    /// </summary>
    [Fact]
    public void Un_pdf_de_solo_lectura_no_deja_la_curp_en_la_pc()
    {
        var archivos = Archivos;
        var descargas = Directory.CreateTempSubdirectory("descargas-").FullName;
        var descargado = Path.Combine(descargas, "CURP_PEPJ800101HPLRRN09.pdf");
        File.WriteAllBytes(descargado, PanelDeTramitesFalso.Pdf);
        File.SetAttributes(descargado, FileAttributes.ReadOnly);
        try
        {
            var documento = archivos.Adoptar("T-0042", descargado);
            var copia = archivos.ParaCompartir("T-0042", "CURP.pdf");

            Assert.False(File.GetAttributes(documento).HasFlag(FileAttributes.ReadOnly));
            Assert.False(File.GetAttributes(copia).HasFlag(FileAttributes.ReadOnly));
            Assert.True(archivos.Borrar("T-0042"));
            Assert.False(Directory.Exists(archivos.CarpetaDe("T-0042")));

            var quedo = archivos.NuevoDocumento("T-0043");
            File.WriteAllBytes(quedo, PanelDeTramitesFalso.Pdf);
            File.SetAttributes(quedo, FileAttributes.ReadOnly);
            var suelto = Path.Combine(_carpeta, "suelto.pdf");
            File.WriteAllBytes(suelto, PanelDeTramitesFalso.Pdf);
            File.SetAttributes(suelto, FileAttributes.ReadOnly);

            Assert.Equal(2, archivos.LimpiarAlArrancar());
            Assert.False(Directory.Exists(archivos.CarpetaDe("T-0043")));
            Assert.False(File.Exists(suelto));
        }
        finally
        {
            File.SetAttributes(descargado, FileAttributes.Normal);
            Directory.Delete(descargas, recursive: true);
        }
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
        // Dice en qué memoria (su nombre y su letra): con dos puestas, la que eligió Windows.
        Assert.Equal("Guardado en la memoria USB «KINGSTON (E:)» como «CURP.pdf».", tramite.Aviso);
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
    public async Task Si_el_panel_no_abre_se_dice_y_en_Edge_el_pdf_descargado_se_elige_aqui_se_imprime_con_su_folio_y_al_entregarlo_se_borra_de_descargas()
    {
        _panel.FallaAlAbrir = true;
        var descargas = Path.Combine(_carpeta, "Descargas");
        _panel.CarpetaDeDescargas = descargas;
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

        // Lo que bajó Edge, con el nombre de la página (la CURP de la persona): se elige y la caja guarda su copia. El original se queda
        // hasta entregarlo (segunda revisión: uno elegido por error se perdía), y la caja dice cuál va a borrar.
        Directory.CreateDirectory(descargas);
        var deEdge = Path.Combine(descargas, "CURP_PEPJ800101HPLRRN09.pdf");
        File.WriteAllBytes(deEdge, PanelDeTramitesFalso.Pdf);
        _panel.Descargado = deEdge;
        await tramite.ElegirPdfDescargadoAsync();

        Assert.Equal(EstadoTramite.Listo, tramite.Estado);
        Assert.True(File.Exists(deEdge));
        Assert.True(File.Exists(Path.Combine(_carpeta, "T-0042", "T-0042.pdf")));
        Assert.False(tramite.AvisoEsError);
        Assert.Equal("Revisa con el cliente que sea el suyo. Al imprimirlo o compartirlo, la caja borra de Descargas «CURP_PEPJ800101HPLRRN09.pdf».",
            tramite.Aviso);
        // Se imprime como en el panel: la primera copia es la incluida, con el folio, y queda anotada para el agente. Entregado: se borra.
        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);
        Assert.Equal([("T-0042 · CURP", 1, "EPSON WF-6590 Series")], _panel.Impresos);
        Assert.False(File.Exists(deEdge));
        Assert.Equal("Impreso en «EPSON WF-6590 Series». Se borró de Descargas: «CURP_PEPJ800101HPLRRN09.pdf».", tramite.Aviso);
        Assert.Equal(new TrabajoDeTramite("T-0042", false), trabajos.De("T-0042 · CURP", _reloj.Ahora.UtcDateTime.AddSeconds(2)));
        Assert.Equal(1, _cobros);
        await tramite.TerminarAsync();
        var anotado = LoQueSeAnoto();
        Assert.Contains("Trámite T-0042 · CURP · impreso (descargado en Edge) · 0 s", anotado);
        Assert.DoesNotContain("CURP_", anotado);
        // No abrió, pero su control ya estaba sobre el ticket: se cierra igual.
        Assert.Contains("cerrar", _panel.Pedido);
    }

    [Fact]
    public async Task En_Edge_lo_que_no_abre_como_pdf_no_se_cobra_ni_se_borra_de_descargas()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        tramite.AbrirEnElNavegador();
        var descargas = Path.Combine(_carpeta, "Descargas");
        _panel.CarpetaDeDescargas = descargas;
        Directory.CreateDirectory(descargas);
        // Se eligió otro archivo de Descargas (no el que bajó Edge): no es un PDF.
        var otro = Path.Combine(descargas, "factura.pdf");
        File.WriteAllText(otro, "no es un PDF");
        _panel.Descargado = otro;

        await tramite.ElegirPdfDescargadoAsync();

        Assert.Equal(EstadoTramite.NoAbre, tramite.Estado);
        Assert.True(File.Exists(otro));
        Assert.True(tramite.AvisoEsError);
        Assert.Contains("no se cobra ni se borra", tramite.Aviso);
        Assert.False(File.Exists(Path.Combine(_carpeta, "T-0042", "T-0042.pdf")));
        Assert.Equal(0, _cobros);

        // Se elige el bueno y se entrega (WhatsApp): ese sí se borra; el otro, no.
        var deEdge = Path.Combine(descargas, "CURP_PEPJ800101HPLRRN09.pdf");
        File.WriteAllBytes(deEdge, PanelDeTramitesFalso.Pdf);
        _panel.Descargado = deEdge;
        await tramite.ElegirPdfDescargadoAsync();
        Assert.Equal(EstadoTramite.Listo, tramite.Estado);
        Assert.True(File.Exists(deEdge));
        await tramite.CopiarParaWhatsAppAsync();
        Assert.False(File.Exists(deEdge));
        Assert.EndsWith("Se borró de Descargas: «CURP_PEPJ800101HPLRRN09.pdf».", tramite.Aviso);
        Assert.True(File.Exists(otro));
    }

    [Fact]
    public async Task En_Edge_un_pdf_elegido_de_otra_carpeta_nunca_se_borra_y_lo_dice()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        tramite.AbrirEnElNavegador();
        _panel.CarpetaDeDescargas = Path.Combine(_carpeta, "Descargas");
        // De Documentos (o de una subcarpeta de Descargas, o de una memoria): la caja no lo borra, nunca.
        var documentos = Path.Combine(_carpeta, "Documentos");
        Directory.CreateDirectory(documentos);
        var deOtraCarpeta = Path.Combine(documentos, "CURP.pdf");
        File.WriteAllBytes(deOtraCarpeta, PanelDeTramitesFalso.Pdf);
        _panel.Descargado = deOtraCarpeta;

        await tramite.ElegirPdfDescargadoAsync();

        Assert.Equal(EstadoTramite.Listo, tramite.Estado);
        Assert.Contains("Ese PDF no está en Descargas: la caja no lo borra", tramite.Aviso);
        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);
        await tramite.GuardarEnUsbAsync();
        Assert.True(File.Exists(deOtraCarpeta));
        Assert.DoesNotContain("Descargas", tramite.Aviso);
        await tramite.TerminarAsync();
        Assert.True(File.Exists(deOtraCarpeta));
        Assert.Contains("Trámite T-0042 · CURP · impreso y guardado en USB (descargado en Edge) · 0 s · el PDF elegido no estaba en Descargas: no se borró",
            LoQueSeAnoto());
    }

    [Fact]
    public void Solo_cuenta_como_de_descargas_lo_que_esta_en_esa_carpeta_misma()
    {
        var descargas = Path.Combine(_carpeta, "Descargas");
        Assert.True(ArchivosDeTramites.EstaEn(Path.Combine(descargas, "CURP_X.pdf"), descargas));
        Assert.True(ArchivosDeTramites.EstaEn(Path.Combine(descargas, "CURP_X.pdf"), descargas + Path.DirectorySeparatorChar));
        Assert.False(ArchivosDeTramites.EstaEn(Path.Combine(descargas, "WhatsApp", "CURP_X.pdf"), descargas));
        Assert.False(ArchivosDeTramites.EstaEn(Path.Combine(_carpeta, "Descargas viejas", "CURP_X.pdf"), descargas));
        Assert.False(ArchivosDeTramites.EstaEn(Path.Combine(descargas, "..", "CURP_X.pdf"), descargas));
        Assert.False(ArchivosDeTramites.EstaEn(Path.Combine(descargas, "CURP_X.pdf"), null));
    }

    [Fact]
    public async Task Si_no_se_puede_borrar_de_descargas_lo_dice_y_lo_anota_sin_su_nombre()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        tramite.AbrirEnElNavegador();
        var descargas = Path.Combine(_carpeta, "Descargas");
        _panel.CarpetaDeDescargas = descargas;
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
            Assert.False(tramite.AvisoEsError);

            await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);

            Assert.True(File.Exists(deEdge));
            Assert.True(tramite.AvisoEsError);
            Assert.Contains("No se pudo borrar «CURP_PEPJ800101HPLRRN09.pdf» de Descargas: bórralo a mano.", tramite.Aviso);
            Assert.Equal(1, _cobros);
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

    /// <summary>
    /// Segunda revisión: por su puerto, no solo por su nombre. Fuera lo que imprime en otra PC (un puerto local a la compartida de la principal,
    /// el escritorio remoto, AnyDesk, TeamViewer) y lo que no se sabe a dónde imprime (WSD, un puerto de otra clase). De las demás, solo USB
    /// (o paralelo o serie) y TCP/IP directo a la impresora; la del agente basta con que imprima aquí.
    /// </summary>
    [Fact]
    public void Para_un_tramite_solo_se_ofrecen_las_que_imprimen_aqui_por_usb_o_por_la_red_directo()
    {
        ColaDeLaPc[] colas =
        [
            new("EPSON WF-6590 Series", "IP_192.168.1.197", "EPSON WF-6590 Series", Tipo: TipoDePuerto.TcpIp),
            new("EPSON WF-6590 (de la principal)", @"\\PRINCIPAL\EPSON WF-6590 Series", "EPSON WF-6590 Series", Tipo: TipoDePuerto.Compartida),
            new("EPSON por un puerto local", @"\\PRINCIPAL\EPSON", "EPSON WF-6590 Series", Tipo: TipoDePuerto.Local),
            new("Brother HL-L2350DW", "USB001", "Brother HL-L2350DW series", Tipo: TipoDePuerto.Local),
            new("HP LaserJet de la bodega", "HP_LaserJet_Bodega", "HP Universal Printing PCL 6", Tipo: TipoDePuerto.TcpIp),
            new("Canon por WSD", "WSD-6a1b2c3d-0001", "Canon Generic Plus PCL6", Tipo: TipoDePuerto.Wsd),
            new("Epson por su propio monitor", "EP1234AB:", "EPSON L3250 Series", Tipo: TipoDePuerto.Otro),
            new("Brother (redirigida 2)", "TS001", "Remote Desktop Easy Print"),
            new("AnyDesk Printer", "AD_Port", "AnyDesk Printer"),
            new("TeamViewer Printer", "TVPrinterPort", "TeamViewer Printer Driver"),
            new("Lista vieja", "LPT1:", "Generic / Text Only", Tipo: TipoDePuerto.Local),
            new("A un archivo", @"C:\Users\caja\salida.prn", "Generic / Text Only", Tipo: TipoDePuerto.Otro),
            new("Sin registro por IP", "192.168.1.50_1", "Brother HL-L2350DW series"),
            new("Sin registro por WSD", "WSD-1234", "Brother HL-L2350DW series"),
        ];

        Assert.Equal(
            [
                new ImpresoraParaTramite("EPSON WF-6590 Series", true), new ImpresoraParaTramite("Brother HL-L2350DW", false),
                new ImpresoraParaTramite("HP LaserJet de la bodega", false), new ImpresoraParaTramite("Lista vieja", false),
                new ImpresoraParaTramite("Sin registro por IP", false),
            ],
            ImpresorasParaTramites.Elegir(colas, "EPSON WF-6590 Series"));
        // La del agente siempre va (primero), por WSD también: él la mide (tercera revisión: ni su nombre ni su driver la quitan). Solo
        // no si imprime en la compartida de otra PC (por su nombre o su puerto) o en el escritorio remoto: ahí la mediría Control Print.
        Assert.Equal(new ImpresoraParaTramite("Canon por WSD", true), ImpresorasParaTramites.Elegir(colas, "Canon por WSD")[0]);
        Assert.Equal(new ImpresoraParaTramite("AnyDesk Printer", true), ImpresorasParaTramites.Elegir(colas, "AnyDesk Printer")[0]);
        Assert.False(ImpresorasParaTramites.Elegir(colas, "EPSON por un puerto local")[0].LaMideElAgente);
        Assert.False(ImpresorasParaTramites.Elegir(colas, "Brother (redirigida 2)")[0].LaMideElAgente);
    }

    /// <summary>
    /// Tercera revisión: las palabras de lo que no imprime («XPS», «PDF»…) se buscan en el nombre de la cola, no en un pedazo del driver.
    /// Una láser de verdad con driver «…XPS» (Konica, Kyocera) sí se ofrece; los drivers virtuales de Windows, por su nombre exacto, no.
    /// </summary>
    [Fact]
    public void Una_impresora_de_verdad_con_un_driver_xps_se_ofrece_y_un_driver_virtual_de_windows_no()
    {
        ColaDeLaPc[] colas =
        [
            new("EPSON WF-6590 Series", "IP_192.168.1.197", "EPSON WF-6590 Series", Tipo: TipoDePuerto.TcpIp),
            new("Kyocera de la bodega", "IP_192.168.1.60", "Kyocera TASKalfa 3253ci XPS", Tipo: TipoDePuerto.TcpIp),
            new("Konica del mostrador", "USB002", "KONICA MINOLTA C364SeriesXPS", Tipo: TipoDePuerto.Local),
            new("Para clientes", "USB003", "Microsoft Print To PDF", Tipo: TipoDePuerto.Local),
            new("Impresora de la oficina", "USB004", "microsoft xps document writer v4", Tipo: TipoDePuerto.Local),
            new("Copias XPS", "USB005", "Brother HL-L2350DW series", Tipo: TipoDePuerto.Local),
        ];

        Assert.Equal(
            [
                new ImpresoraParaTramite("EPSON WF-6590 Series", true), new ImpresoraParaTramite("Kyocera de la bodega", false),
                new ImpresoraParaTramite("Konica del mostrador", false),
            ],
            ImpresorasParaTramites.Elegir(colas, "EPSON WF-6590 Series"));
    }

    [Fact]
    public async Task Lo_recordado_que_no_se_puede_leer_empieza_de_cero_lo_dice_y_lo_siguiente_lo_reemplaza()
    {
        var ruta = Path.Combine(_carpeta, "caja-local.db");
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var almacen = new AlmacenLocal(ruta);
        // Lo que dejaría otra versión (un objeto donde va la lista) y, aparte, una lista con un renglón vacío.
        await almacen.EscribirAsync(l => l.GuardarJson("tramites-impresos", "{\"version\":2}"));
        var contingencia = new Contingencia(almacen, new HttpClient(), conexion);
        var problemas = new List<string>();

        Assert.Empty(contingencia.TramitesImpresos(problemas.Add));
        Assert.Equal(["JsonException"], problemas);

        // Lo siguiente que se guarda lo reemplaza.
        TrabajoDeTramiteEnviado[] enviados = [new("T-0042 · CURP", new TrabajoDeTramite("T-0042", false), new DateTime(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc))];
        await contingencia.RecordarTramitesImpresosAsync(enviados).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(enviados, contingencia.TramitesImpresos(problemas.Add));
        Assert.Single(problemas);

        // Un renglón vacío o sin trámite no se carga (ni truena después).
        await almacen.EscribirAsync(l => l.GuardarJson("tramites-impresos",
            "[null, {\"nombre\":\"T-0043 · CURP\"}, {\"nombre\":\"T-0044 · CURP\",\"tramite\":{\"folio\":\"T-0044\",\"copiasExtra\":false},\"enviadoUtc\":\"2026-10-31T17:00:00Z\"}]"));
        Assert.Equal(["T-0044 · CURP"], contingencia.TramitesImpresos(problemas.Add).Select(e => e.Nombre));
        Assert.Single(problemas);
        contingencia.Cerrar();
    }

    /// <summary>
    /// Tercera revisión: si el trámite se cierra (Esc) mientras su copia todavía va a la impresora, al terminar de imprimirse no se cobra en
    /// el ticket que esté abierto en ese momento (sería el de otro cliente): se anota y la caja lo dice. Las de más no se mandan. Cuarta: el
    /// aviso lleva solo el folio, el trámite y la hora (se guarda en la base de la caja hasta «Entendido»), una vez.
    /// </summary>
    [Fact]
    public async Task Lo_que_termina_de_imprimirse_despues_de_cerrar_el_tramite_no_se_cobra_y_se_dice()
    {
        var tramite = Tramite(_panel);
        var avisos = new List<CopiaTardia>();
        tramite.ImpresoTrasCerrar += avisos.Add;
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();
        _panel.ImpresionLenta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var imprimiendo = tramite.ImprimirAsync("EPSON WF-6590 Series", 3);
        await tramite.TerminarAsync();
        _panel.ImpresionLenta.SetResult();
        await imprimiendo;

        Assert.Equal(0, _cobros);
        Assert.Equal([new CopiaTardia("T-0042", "CURP", _reloj.Ahora.UtcDateTime)], avisos);
        Assert.Equal([("T-0042 · CURP", 1, "EPSON WF-6590 Series")], _panel.Impresos);
        Assert.Contains("Trámite T-0042 · CURP: terminó de imprimirse después de cerrar el trámite; no se cobró.", LoQueSeAnoto());
    }

    /// <summary>Cuarta revisión: mientras la hoja va a la impresora, el trámite dice que imprime (la caja no lo deja con otra ventana).</summary>
    [Fact]
    public async Task Mientras_la_hoja_va_a_la_impresora_el_tramite_dice_que_imprime()
    {
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();
        _panel.ImpresionLenta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var imprimiendo = tramite.ImprimirAsync("EPSON WF-6590 Series", 1);
        Assert.True(tramite.Imprimiendo);
        Assert.True(tramite.Ocupado);
        _panel.ImpresionLenta.SetResult();
        await imprimiendo;

        Assert.False(tramite.Imprimiendo);
        Assert.Equal(1, _cobros);
    }

    /// <summary>
    /// Cuarta revisión: el aviso de una copia que salió tarde se guarda en la base de la caja hasta «Entendido», una vez cada copia, con solo
    /// el folio, el trámite y la hora. Lo que no se puede leer no impide nada.
    /// </summary>
    [Fact]
    public async Task El_aviso_de_una_copia_tardia_se_guarda_hasta_entendido_una_sola_vez()
    {
        var ruta = Path.Combine(_carpeta, "caja-local.db");
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var tardia = new CopiaTardia("T-0042", "CURP", new DateTime(2026, 11, 3, 17, 5, 0, DateTimeKind.Utc));
        var otra = new CopiaTardia("T-0043", "CURP", new DateTime(2026, 11, 3, 17, 9, 0, DateTimeKind.Utc));
        var antes = new Contingencia(new AlmacenLocal(ruta), new HttpClient(), conexion);
        Assert.Empty(antes.CopiasTardias());
        await antes.AnotarCopiaTardiaAsync(tardia).WaitAsync(TimeSpan.FromSeconds(10));
        await antes.AnotarCopiaTardiaAsync(tardia).WaitAsync(TimeSpan.FromSeconds(10));
        await antes.AnotarCopiaTardiaAsync(otra).WaitAsync(TimeSpan.FromSeconds(10));
        antes.Cerrar();

        // Al abrir la caja otra vez: las dos, una vez cada una.
        var despues = new Contingencia(new AlmacenLocal(ruta), new HttpClient(), conexion);
        Assert.Equal([tardia, otra], despues.CopiasTardias());
        await despues.QuitarCopiaTardiaAsync(tardia).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([otra], despues.CopiasTardias());
        despues.Cerrar();

        // Lo que no se puede leer: ninguna, y lo siguiente que se guarda lo reemplaza.
        var almacen = new AlmacenLocal(ruta);
        await almacen.EscribirAsync(l => l.GuardarJson("copias-tardias", "{\"version\":2}"));
        var danada = new Contingencia(almacen, new HttpClient(), conexion);
        Assert.Empty(danada.CopiasTardias());
        await danada.AnotarCopiaTardiaAsync(tardia).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([tardia], danada.CopiasTardias());
        danada.Cerrar();
    }

    /// <summary>Tercera revisión: la pista de la primera Esc no tapa lo que decía el panel, y se va sola.</summary>
    [Fact]
    public async Task La_pista_de_la_primera_esc_no_borra_lo_que_decia_el_panel_y_se_va_sola()
    {
        _porQueNoSeCobra = "no hay un producto con el código «CURP»";
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();
        await tramite.ImprimirAsync("EPSON WF-6590 Series", 1);
        var dicho = tramite.Aviso;
        Assert.StartsWith("No se agregó la CURP al ticket", dicho);

        Assert.False(_panel.Atrapa!.Tecla("Escape"));

        Assert.Equal(dicho, tramite.Aviso);
        Assert.True(tramite.AvisoEsError);
        Assert.Equal("Esc otra vez para terminar el trámite (se pierde lo escrito en la página).", tramite.Pista);
        _reloj.Ahora = _reloj.Ahora.AddSeconds(3);
        Assert.Null(tramite.Pista);
        Assert.Equal(dicho, tramite.Aviso);
    }

    [Fact]
    public async Task Lo_que_se_mando_a_imprimir_se_guarda_en_la_base_de_la_caja()
    {
        var ruta = Path.Combine(_carpeta, "caja-local.db");
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        TrabajoDeTramiteEnviado[] enviados = [new("T-0042 · CURP", new TrabajoDeTramite("T-0042", false), new DateTime(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc))];
        var antes = new Contingencia(new AlmacenLocal(ruta), new HttpClient(), conexion);
        Assert.Empty(antes.TramitesImpresos());

        await antes.RecordarTramitesImpresosAsync(enviados).WaitAsync(TimeSpan.FromSeconds(10));
        antes.Cerrar();

        // Al abrir la caja otra vez.
        var despues = new Contingencia(new AlmacenLocal(ruta), new HttpClient(), conexion);
        Assert.Equal(enviados, despues.TramitesImpresos());
        Assert.Equal(DateTimeKind.Utc, despues.TramitesImpresos()[0].EnviadoUtc.Kind);
        despues.Cerrar();
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
    public async Task En_la_pagina_oficial_F12_va_a_la_caja_y_Esc_solo_la_segunda_seguida()
    {
        var tramite = Tramite(_panel);
        var teclas = new List<string>();
        tramite.TeclaDeLaPagina += teclas.Add;
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();

        // F12 siempre es de la caja (la página no la recibe); Enter, nunca.
        Assert.True(_panel.Atrapa!.Tecla("F12"));
        Assert.False(_panel.Atrapa!.Tecla("Enter"));
        Assert.Equal(["F12"], teclas);

        // La primera Esc es de la página (cierra sus listas): la caja solo dice cómo terminar, en su propio renglón (la pista).
        Assert.False(_panel.Atrapa!.Tecla("Escape"));
        Assert.Equal(["F12"], teclas);
        Assert.Equal("Esc otra vez para terminar el trámite (se pierde lo escrito en la página).", tramite.Pista);
        // Otra enseguida (antes de 2 s): termina el trámite, como Esc en la caja.
        _reloj.Ahora = _reloj.Ahora.AddSeconds(1.5);
        Assert.True(_panel.Atrapa!.Tecla("Escape"));
        Assert.Equal(["F12", "Escape"], teclas);

        // Una después de más de 2 s vuelve a ser de la página.
        teclas.Clear();
        _reloj.Ahora = _reloj.Ahora.AddSeconds(5);
        Assert.False(_panel.Atrapa!.Tecla("Escape"));
        _reloj.Ahora = _reloj.Ahora.AddSeconds(2.5);
        Assert.False(_panel.Atrapa!.Tecla("Escape"));
        Assert.Empty(teclas);
        // Ya terminado (el panel ya lo soltó), nada.
        await tramite.TerminarAsync();
        Assert.False(((IAtrapaDocumentos)tramite).Tecla("F12"));
        Assert.Empty(teclas);
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
        public string? CarpetaDeDescargas => panel.CarpetaDeDescargas;
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

    /// <summary>
    /// Si el panel no abrió (el camino previsto, con el plan B de Edge), al terminar también se le pide cerrar: su control ya estaba puesto
    /// sobre el ticket y lo copiado para WhatsApp se queda en el portapapeles si no (auditoría 2026-11-04).
    /// </summary>
    [Fact]
    public async Task Si_el_panel_no_abrio_al_terminar_tambien_se_cierra_y_se_limpia_el_portapapeles()
    {
        _panel.FallaAlAbrir = true;
        var descargas = Path.Combine(_carpeta, "Descargas");
        Directory.CreateDirectory(descargas);
        _panel.CarpetaDeDescargas = descargas;
        var tramite = Tramite(_panel);
        await tramite.AbrirAsync();
        Assert.Equal(EstadoTramite.NoContesta, tramite.Estado);
        tramite.AbrirEnElNavegador();
        _panel.Descargado = Path.Combine(descargas, "CURP_PEPJ800101HPLRRN09.pdf");
        File.WriteAllBytes(_panel.Descargado, PanelDeTramitesFalso.Pdf);
        await tramite.ElegirPdfDescargadoAsync();
        await tramite.CopiarParaWhatsAppAsync();

        await tramite.TerminarAsync();

        Assert.Contains("cerrar", _panel.Pedido);
        Assert.True(_panel.PortapapelesLimpio);
        Assert.False(Directory.Exists(Archivos.CarpetaDe("T-0042")));
    }

    /// <summary>Sin caja de Windows (la del navegador) no hay panel que cerrar, y terminar sigue borrando lo suyo.</summary>
    [Fact]
    public async Task Sin_panel_terminar_no_truena()
    {
        var tramite = Tramite(panel: null);
        await tramite.AbrirAsync();
        await tramite.TerminarAsync();
        Assert.True(tramite.Terminado);
    }

    /// <summary>Deja la carpeta sin poderse borrar, como un PDF abierto por otro programa: en Linux un archivo abierto sí se borra.</summary>
    private static IDisposable SinPoderseBorrar(string carpeta, string documento)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(documento, FileMode.Open, FileAccess.Read, FileShare.None);
        File.SetUnixFileMode(carpeta, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new AlSoltar(() => File.SetUnixFileMode(carpeta, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
    }

    private sealed class AlSoltar(Action hacer) : IDisposable
    {
        public void Dispose() => hacer();
    }

    private async Task<TramiteEnCurso> ConDocumentoAsync(string folio)
    {
        var tramite = Tramite(_panel, folio: folio);
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();
        Assert.Equal(EstadoTramite.Listo, tramite.Estado);
        return tramite;
    }

    /// <summary>
    /// Lo que estaba abierto al terminar (WhatsApp recién pegado, el antivirus) se vuelve a intentar a los pocos segundos, sin esperar a que la
    /// caja se vuelva a abrir: la caja 2 pasa días abierta y es la CURP de alguien (auditoría 2026-11-04).
    /// </summary>
    [Fact]
    public async Task Lo_que_no_se_pudo_borrar_al_terminar_se_vuelve_a_intentar_a_los_pocos_segundos()
    {
        var tramite = await ConDocumentoAsync("T-0042");
        tramite.EsperasDelBorrado = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400)];
        var atorado = SinPoderseBorrar(Archivos.CarpetaDe("T-0042"), tramite.Documento);

        await tramite.TerminarAsync();

        Assert.True(Directory.Exists(Archivos.CarpetaDe("T-0042")));
        Assert.Contains("Trámite T-0042 · CURP · sin documento · 0 s · su archivo no se pudo borrar: se vuelve a intentar", LoQueSeAnoto());
        atorado.Dispose();
        await tramite.ReintentoDelBorrado.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(Directory.Exists(Archivos.CarpetaDe("T-0042")));
        Assert.Contains("Trámites: ya se borró lo que quedó de T-0042.", LoQueSeAnoto());
    }

    /// <summary>Si los reintentos no alcanzaron, lo borra el siguiente trámite al terminar: sin tocar nada que no se haya quedado atorado.</summary>
    [Fact]
    public async Task Lo_que_sigue_sin_borrarse_lo_borra_el_siguiente_tramite_al_terminar()
    {
        var archivos = Archivos;
        var tramite = new TramiteEnCurso(Tramites.Curp, "T-0042", _panel, archivos, () => Task.FromResult<string?>(null), registro: Registro, reloj: _reloj)
        {
            EsperasDelBorrado = [],
        };
        await tramite.AbrirAsync();
        _panel.Atrapa!.Cargo();
        await _panel.DescargarAsync();
        var atorado = SinPoderseBorrar(archivos.CarpetaDe("T-0042"), archivos.DocumentoDe("T-0042"));
        await tramite.TerminarAsync();
        Assert.True(Directory.Exists(archivos.CarpetaDe("T-0042")));
        atorado.Dispose();
        // Una carpeta que no se quedó atorada (la de otro trámite en curso) no se toca.
        var enCurso = archivos.NuevoDocumento("T-0099");
        File.WriteAllBytes(enCurso, PanelDeTramitesFalso.Pdf);

        var siguiente = new TramiteEnCurso(Tramites.Curp, "T-0043", _panel, archivos, () => Task.FromResult<string?>(null), registro: Registro, reloj: _reloj);
        await siguiente.AbrirAsync();
        await siguiente.TerminarAsync();

        Assert.False(Directory.Exists(archivos.CarpetaDe("T-0042")));
        Assert.True(File.Exists(enCurso));
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
