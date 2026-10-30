using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PapeleriaPos.Impresiones.ControlPrint;

namespace PapeleriaPos.Impresiones.Pruebas.ControlPrint;

/// <summary>
/// El cliente de Control Print contra un Control Print falso con el mismo protocolo (<see cref="ServidorControlPrintFalso"/>):
/// el saludo y la prueba del código, el certificado fijado, los errores, las lecturas y que nunca escribe.
/// </summary>
public sealed class ClienteControlPrintTests : IAsyncLifetime
{
    /// <summary>Inventado, con el formato de Control Print.</summary>
    private const string Codigo = "K7QP-3MZX-9RTD-W2HB";
    private static readonly DateTime Desde = new(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Hasta = new(2026, 10, 24, 23, 0, 0, DateTimeKind.Utc);

    private ServidorControlPrintFalso _servidor = null!;

    public async Task InitializeAsync() => _servidor = await ServidorControlPrintFalso.IniciarAsync(Codigo, s =>
    {
        s.Trabajos.AddRange(DatosDeControlPrint.Trabajos(120, Desde, Hasta));
        s.Cotizaciones.AddRange(DatosDeControlPrint.Cotizaciones(Desde));
        s.Lecturas.AddRange(DatosDeControlPrint.Lecturas(Desde, Hasta));
    });

    public async Task DisposeAsync() => await _servidor.DisposeAsync();

    private OpcionesClienteControlPrint Opciones => new("127.0.0.1", _servidor.Puerto);

    private void SoloLeyo()
    {
        Assert.Empty(_servidor.Escrituras);
        Assert.All(_servidor.Peticiones, p => Assert.True(p.EsLectura, $"{p.Metodo} {p.Ruta}"));
    }

    // ── El protocolo ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void La_prueba_del_codigo_es_la_de_Control_Print()
    {
        // Calculada aparte (HMAC-SHA256 del reto y la huella con el código sin guiones ni espacios y en mayúsculas).
        const string huella = "7BD83926F886B22EB03599E8C4BA08855185E839A964AC4B00C8E63456D9A562";
        Assert.Equal("F3554ABC7DF8C4256C1819C13BDF001DBF902C52D551BED891585F0AE6AF79B3",
            ProtocoloControlPrint.Prueba("k7qp-3mzx 9rtd-w2hb", "00112233445566778899AABBCCDDEEFF", huella));
        Assert.Equal("7B:D8:39:26:F8:86:B2:2E:B0:35:99:E8:C4:BA:08:85:51:85:E8:39:A9:64:AC:4B:00:C8:E6:34:56:D9:A5:62",
            ProtocoloControlPrint.HuellaLegible(huella));
        Assert.Equal(huella, ProtocoloControlPrint.LeerHuella(ProtocoloControlPrint.HuellaLegible(huella).ToLowerInvariant()));
        Assert.Null(ProtocoloControlPrint.LeerHuella("7B:D8"));
    }

    [Fact]
    public async Task El_saludo_comprueba_el_codigo_sin_mandarlo_y_fija_el_certificado()
    {
        using var cliente = await ClienteControlPrint.SaludarAsync(Opciones);

        Assert.Equal("PRINCIPAL", cliente.NombreServidor);
        Assert.Equal("1.12.0", cliente.VersionServidor);
        Assert.Equal(_servidor.Huella, cliente.Huella);
        var saludo = Assert.Single(_servidor.Peticiones);
        Assert.Equal("/api/hello", saludo.Ruta);
        Assert.Null(saludo.Codigo);
        Assert.Null(saludo.Equipo);

        Assert.True(cliente.UsarCodigo("k7qp 3mzx-9rtd w2hb"));
        await cliente.SaludAsync();
        var lectura = _servidor.Peticiones.Last();
        Assert.Equal("K7QP3MZX9RTDW2HB", lectura.Codigo);
        Assert.Equal($"{Environment.MachineName} (POS)", lectura.Equipo);
        Assert.StartsWith("PapeleriaPos ", lectura.Version);
        Assert.Equal(2, _servidor.Peticiones.Count);
        SoloLeyo();
    }

    [Fact]
    public async Task Un_codigo_equivocado_no_coincide_y_nunca_se_manda()
    {
        using (var cliente = await ClienteControlPrint.SaludarAsync(Opciones))
        {
            Assert.False(cliente.UsarCodigo("AAAA-BBBB-CCCC-DDDD"));
            Assert.True(cliente.UsarCodigo(Codigo));
        }
        var error = await Assert.ThrowsAsync<ErrorControlPrint>(() => ClienteControlPrint.ConectarAsync(Opciones, "AAAA-BBBB-CCCC-DDDD"));

        Assert.Equal(FallaControlPrint.CodigoNoCoincide, error.Falla);
        Assert.Contains("Ajustes → Red", error.Message);
        Assert.DoesNotContain("AAAA", error.Message);
        Assert.All(_servidor.Peticiones, p => Assert.Equal("/api/hello", p.Ruta));
        Assert.All(_servidor.Peticiones, p => Assert.Null(p.Codigo));
    }

    [Fact]
    public async Task Un_codigo_incompleto_se_rechaza_sin_preguntar()
    {
        using var cliente = await ClienteControlPrint.SaludarAsync(Opciones);

        var error = Assert.Throws<ErrorControlPrint>(() => cliente.UsarCodigo("K7QP-3MZX-9RTD"));

        Assert.Equal(FallaControlPrint.CodigoIncompleto, error.Falla);
        Assert.DoesNotContain("K7QP", error.Message);
        Assert.Single(_servidor.Peticiones);
    }

    [Fact]
    public async Task Un_servidor_que_no_conoce_el_codigo_no_recibe_nada()
    {
        _servidor.NoConoceElCodigo = true;

        var error = await Assert.ThrowsAsync<ErrorControlPrint>(() => ClienteControlPrint.ConectarAsync(Opciones, Codigo));

        Assert.Equal(FallaControlPrint.CodigoNoCoincide, error.Falla);
        Assert.All(_servidor.Peticiones, p => Assert.Null(p.Codigo));
    }

    [Fact]
    public async Task Si_cambian_el_codigo_en_Control_Print_el_401_lo_dice()
    {
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);
        await cliente.SaludAsync();
        _servidor.Codigo = "N3W2-C0DE-4567-89AB";

        var error = await Assert.ThrowsAsync<ErrorControlPrint>(() => cliente.UsuariosAsync());

        Assert.Equal(FallaControlPrint.CodigoRechazado, error.Falla);
        Assert.Contains("Ajustes → Red", error.Message);
        Assert.DoesNotContain("K7QP", error.Message);
        Assert.DoesNotContain("N3W2", error.Message);
        SoloLeyo();
    }

    [Fact]
    public async Task La_version_del_protocolo_tiene_que_ser_la_1()
    {
        _servidor.VersionProtocolo = 2;

        var error = await Assert.ThrowsAsync<ErrorControlPrint>(() => ClienteControlPrint.ConectarAsync(Opciones, Codigo));

        Assert.Equal(FallaControlPrint.VersionDistinta, error.Falla);
    }

    /// <summary>
    /// Un servidor que parece el código de conexión (puesto ahí por error, escrito como sea) se rechaza antes de buscarlo en la red
    /// y sin repetirlo (revisión del PR #143). Y el cliente sabe decir si un texto lleva su código, escrito como sea, sin darlo:
    /// así recopilar-control-print no lo guarda aunque Control Print lo mande en algún campo.
    /// </summary>
    [Fact]
    public async Task Un_servidor_que_parece_el_codigo_se_rechaza_y_el_codigo_no_sale_del_cliente()
    {
        foreach (var escrito in new[] { "k7qp.3mzx.9rtd.w2hb", "K7QP3MZX9RTDW2HB", "K7QP_3MZX_9RTD_W2HB" })
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => ClienteControlPrint.SaludarAsync(new OpcionesClienteControlPrint(escrito)));
            Assert.DoesNotContain("K7QP", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty(_servidor.Peticiones);

        using var cliente = await ClienteControlPrint.SaludarAsync(Opciones);
        Assert.False(cliente.LlevaElCodigo(Codigo));
        Assert.True(cliente.UsarCodigo(Codigo));
        Assert.True(cliente.LlevaElCodigo("Para conectar la caja 2: k7qp 3mzx 9rtd w2hb"));
        Assert.True(cliente.LlevaElCodigo("K7QP3MZX9RTDW2HB"));
        Assert.False(cliente.LlevaElCodigo("K7QP-3MZX-9RTD"));
        Assert.False(cliente.LlevaElCodigo("AAAA-BBBB-CCCC-DDDD"));
    }

    // ── El certificado ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Con_la_huella_esperada_otro_certificado_se_rechaza_desde_el_saludo()
    {
        var otra = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ServidorControlPrintFalso.OtroCertificado.RawData));

        var error = await Assert.ThrowsAsync<ErrorControlPrint>(() =>
            ClienteControlPrint.SaludarAsync(Opciones with { Huella = ProtocoloControlPrint.HuellaLegible(otra) }));

        Assert.Equal(FallaControlPrint.CertificadoDistinto, error.Falla);
        Assert.Empty(_servidor.Peticiones);

        using var bueno = await ClienteControlPrint.ConectarAsync(Opciones with { Huella = _servidor.Huella.ToLowerInvariant() }, Codigo);
        Assert.Equal(_servidor.Huella, bueno.Huella);
    }

    [Fact]
    public async Task Si_el_certificado_cambia_despues_del_saludo_ya_no_se_manda_nada()
    {
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);
        await cliente.SaludAsync();
        // Alguien más contesta en la misma dirección con otro certificado: la conexión de ahora se cierra y la siguiente es nueva.
        _servidor.Certificado = ServidorControlPrintFalso.OtroCertificado;
        _servidor.CerrarConexiones = true;
        await cliente.SaludAsync();
        var antes = _servidor.Peticiones.Count;

        var error = await Assert.ThrowsAsync<ErrorControlPrint>(() => cliente.UsuariosAsync());

        Assert.Equal(FallaControlPrint.CertificadoDistinto, error.Falla);
        Assert.Equal(antes, _servidor.Peticiones.Count);
        SoloLeyo();
    }

    /// <summary>
    /// Alguien en medio presenta su propio certificado y le reenvía el saludo al Control Print de verdad: la prueba que devuelve es
    /// la buena, pero ligada a la huella del certificado de verdad, no a la del que presentó. El código no coincide y no se manda
    /// nada, como en PairAsync de Control Print (la revisión del PR #143 lo comprobó).
    /// </summary>
    [Fact]
    public async Task La_prueba_del_servidor_de_verdad_no_sirve_con_otro_certificado()
    {
        var huellaDeVerdad = _servidor.Huella;
        _servidor.Certificado = ServidorControlPrintFalso.OtroCertificado;
        _servidor.Antes = async (ctx, p) =>
        {
            if (p.Ruta != "/api/hello")
                return false;
            var reto = ctx.Request.Query["nonce"].ToString();
            await ctx.Response.WriteAsJsonAsync(new
            {
                name = "PRINCIPAL",
                appVersion = "1.12.0",
                protocolVersion = 1,
                proof = ServidorControlPrintFalso.Prueba(Codigo, reto, huellaDeVerdad),
            });
            return true;
        };

        using var cliente = await ClienteControlPrint.SaludarAsync(Opciones);

        Assert.NotEqual(huellaDeVerdad, cliente.Huella);
        Assert.False(cliente.UsarCodigo(Codigo));
        Assert.All(_servidor.Peticiones, p => Assert.Null(p.Codigo));
        Assert.Equal(FallaControlPrint.CodigoNoCoincide,
            (await Assert.ThrowsAsync<ErrorControlPrint>(() => ClienteControlPrint.ConectarAsync(Opciones, Codigo))).Falla);
        Assert.All(_servidor.Peticiones, p => Assert.Equal("/api/hello", p.Ruta));
    }

    // ── Sin conexión ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sin_Control_Print_falla_rapido()
    {
        var libre = new TcpListener(IPAddress.Loopback, 0);
        libre.Start();
        var puerto = ((IPEndPoint)libre.LocalEndpoint).Port;
        libre.Stop();
        var reloj = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<ErrorControlPrint>(() => ClienteControlPrint.ConectarAsync(new OpcionesClienteControlPrint("127.0.0.1", puerto), Codigo));

        Assert.Equal(FallaControlPrint.SinConexion, error.Falla);
        Assert.Contains($"127.0.0.1:{puerto}", error.Message);
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(4), $"Tardó {reloj.Elapsed}.");
    }

    [Fact]
    public async Task Despues_de_una_falla_de_conexion_espera_la_pausa_y_se_puede_reintentar_ya()
    {
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);
        var cortar = true;
        _servidor.Antes = (ctx, p) =>
        {
            if (!cortar || p.Ruta != "/api/health")
                return Task.FromResult(false);
            ctx.Abort();
            return Task.FromResult(true);
        };

        Assert.Equal(FallaControlPrint.SinConexion, (await Assert.ThrowsAsync<ErrorControlPrint>(() => cliente.SaludAsync())).Falla);
        Assert.False(cliente.EnLinea);
        var pedidas = _servidor.Pedidas("/api/health");
        Assert.Equal(FallaControlPrint.SinConexion, (await Assert.ThrowsAsync<ErrorControlPrint>(() => cliente.SaludAsync())).Falla);
        Assert.Equal(pedidas, _servidor.Pedidas("/api/health"));

        cortar = false;
        cliente.ReintentarYa();
        var salud = await cliente.SaludAsync();

        Assert.True(salud.ImpresoraBien);
        Assert.True(cliente.EnLinea);
        SoloLeyo();
    }

    // ── Lo que se lee ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task La_busqueda_pagina_como_Control_Print()
    {
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);

        var paginas = new List<PaginaTrabajosControlPrint>();
        for (var saltar = 0; saltar < 120; saltar += 50)
            paginas.Add(await cliente.BuscarTrabajosAsync(new BusquedaTrabajosControlPrint { Limite = 50, Saltar = saltar }));

        Assert.All(paginas, p => Assert.Equal(120, p.Total));
        Assert.Equal(new[] { 50, 50, 20 }, paginas.Select(p => p.Trabajos.Count));
        var ids = paginas.SelectMany(p => p.Trabajos).Select(t => t.Id).ToList();
        Assert.Equal(Enumerable.Range(1, 120).Reverse().Select(i => (long)i), ids);
        Assert.All(paginas.SelectMany(p => p.Trabajos), t => Assert.Null(t.Miniatura));

        // Por fecha, como [desde, hasta) sobre la hora en que se detectó; una fecha sin zona se toma como UTC.
        var desde = new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Unspecified);
        var hasta = new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc);
        var septiembre = await cliente.BuscarTrabajosAsync(new BusquedaTrabajosControlPrint { DesdeUtc = desde, HastaUtc = hasta, Limite = 0 });
        var esperados = _servidor.Trabajos.Where(t => t.DetectedUtc >= DateTime.SpecifyKind(desde, DateTimeKind.Utc) && t.DetectedUtc < hasta).ToList();
        Assert.Equal(esperados.Count, septiembre.Total);
        Assert.Equal(esperados.Select(t => t.Id).OrderDescending(), septiembre.Trabajos.Select(t => t.Id));
        Assert.True(esperados.Count is > 20 and < 100);

        var porCobrar = await cliente.BuscarTrabajosAsync(new BusquedaTrabajosControlPrint { Cobro = CobroControlPrint.PorCobrar, SoloCobrables = true, Limite = 0 });
        Assert.Equal(_servidor.Trabajos.Count(t => t.Payment == PaymentState.Pending && t.IsChargeable), porCobrar.Total);
        Assert.All(porCobrar.Trabajos, t => Assert.Equal(CobroControlPrint.PorCobrar, t.Cobro));
        SoloLeyo();
    }

    [Fact]
    public async Task Cada_campo_del_trabajo_llega_con_su_nombre()
    {
        var original = _servidor.Trabajos.First(t => t.Payment == PaymentState.Unsold && t.UnsoldReason == UnsoldReason.Other && t.Thumbnail is not null);
        original.Extra = new() { ["printerSerial"] = JsonSerializer.SerializeToElement("X3ZT001234") };
        var copia = _servidor.Trabajos.First(t => t.Kind == JobKind.PanelCopy);
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);

        var trabajo = await cliente.TrabajoAsync(original.Id);

        Assert.NotNull(trabajo);
        Assert.Equal(original.Id, trabajo.Id);
        Assert.Equal(original.SourcePc, trabajo.PcOrigen);
        Assert.Equal(TipoTrabajoControlPrint.Impresion, trabajo.Tipo);
        Assert.Equal(original.Printer, trabajo.Impresora);
        Assert.Equal(original.SpoolJobId, trabajo.NumeroEnLaCola);
        Assert.Equal(original.SubmittedUtc, trabajo.EnviadoUtc);
        Assert.Equal(DateTimeKind.Utc, trabajo.DetectadoUtc.Kind);
        Assert.Equal(original.DetectedUtc, trabajo.DetectadoUtc);
        Assert.Equal(original.Document, trabajo.Documento);
        Assert.Equal(original.UserName, trabajo.UsuarioWindows);
        Assert.Equal(original.Machine, trabajo.Equipo);
        Assert.Equal(original.Datatype, trabajo.TipoDeDatos);
        Assert.Equal(original.AnalyzedUtc, trabajo.AnalizadoUtc);
        Assert.Equal(original.FinishedUtc, trabajo.TerminadoUtc);
        Assert.Equal(Estado(original.State), trabajo.Estado);
        Assert.Equal((original.Pages, original.Copies, original.Sides, original.ColorSides, original.Sheets), (trabajo.Paginas, trabajo.Copias, trabajo.Caras, trabajo.CarasColor, trabajo.Hojas));
        Assert.Equal((original.Color, original.Quality, original.MediaType, original.MediaName, original.PaperSize, original.Duplex, original.PagesPerSheet),
            (trabajo.Color, trabajo.Calidad, trabajo.TipoPapel, trabajo.NombrePapel, trabajo.TamanoPapel, trabajo.DobleCara, trabajo.PaginasPorHoja));
        Assert.Equal((original.InkC, original.InkM, original.InkY, original.InkK), (trabajo.TintaC, trabajo.TintaM, trabajo.TintaY, trabajo.TintaK));
        Assert.Equal((original.CostInk, original.CostPaper, original.CostMaintenance, original.CostPower, original.CostExtras, original.CostDepreciation, original.CostTotal),
            (trabajo.CostoTinta, trabajo.CostoPapel, trabajo.CostoMantenimiento, trabajo.CostoLuz, trabajo.CostoExtras, trabajo.CostoDepreciacion, trabajo.CostoTotal));
        Assert.Equal((original.MarginPercent, original.PaperMarginPercent, original.Price, original.PriceOverride),
            (trabajo.MargenPorcentaje, trabajo.MargenPapelPorcentaje, trabajo.Precio, trabajo.PrecioPuesto));
        Assert.Equal(CobroControlPrint.NoVendido, trabajo.Cobro);
        Assert.Equal(MotivoNoVendidaControlPrint.Otro, trabajo.MotivoNoVendida);
        Assert.Equal("Se manchó", trabajo.NotaNoVendida);
        Assert.Equal((original.PaymentBy, original.PaymentUtc), (trabajo.CobroPor, trabajo.CobroUtc));
        Assert.Equal((original.Label, original.Notes, original.Note, original.DetailJson), (trabajo.Etiqueta, trabajo.Notas, trabajo.NotaAnalisis, trabajo.DesgloseJson));
        Assert.Equal(original.Level == AnalysisLevel.Measured ? NivelAnalisisControlPrint.Medido : NivelAnalisisControlPrint.Estimado, trabajo.NivelAnalisis);
        Assert.Equal((original.OwnerId, original.OwnerName), (trabajo.AtendioId, trabajo.AtendioNombre));
        Assert.Equal(original.CountedSides, trabajo.CarasContadas);
        Assert.Equal((original.IsChargeable, original.HasPrintShortfall, original.EffectivePrice, original.DisplayName),
            (trabajo.Cobrable, trabajo.FaltaronCaras, trabajo.PrecioACobrar, trabajo.NombreQueSeVe));
        Assert.Equal(DatosDeControlPrint.Png, trabajo.Miniatura);
        // Lo que esta versión no conoce no se pierde.
        Assert.NotNull(trabajo.Otros);
        Assert.Equal("X3ZT001234", Assert.Single(trabajo.Otros).Value.GetString());

        var delPanel = await cliente.TrabajoAsync(copia.Id);
        Assert.Equal(TipoTrabajoControlPrint.CopiaEnElPanel, delPanel!.Tipo);
        Assert.Equal(TipoPrecioControlPrint.Copias, delPanel.TipoPrecio);
        Assert.Equal(copia.PriceOverride, delPanel.PrecioACobrar);
        Assert.Null(delPanel.Otros);
        Assert.Null(await cliente.TrabajoAsync(99_999));
        SoloLeyo();
    }

    [Fact]
    public async Task Un_valor_que_no_conoce_queda_como_desconocido_sin_romper_la_respuesta()
    {
        var raro = _servidor.Trabajos[10];
        raro.EstadoQueNoConoce = "Reprinted";
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);

        var pagina = await cliente.BuscarTrabajosAsync(new BusquedaTrabajosControlPrint { Limite = 0 });

        Assert.Equal(120, pagina.Trabajos.Count);
        Assert.Equal(EstadoTrabajoControlPrint.Desconocido, pagina.Trabajos.Single(t => t.Id == raro.Id).Estado);
    }

    [Fact]
    public async Task La_miniatura_el_resumen_las_caras_y_las_cotizaciones()
    {
        var conMiniatura = _servidor.Trabajos.First(t => t.Thumbnail is not null);
        var sinMiniatura = _servidor.Trabajos.First(t => t.Thumbnail is null);
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);

        Assert.Equal(DatosDeControlPrint.Png, await cliente.MiniaturaAsync(conMiniatura.Id));
        Assert.Null(await cliente.MiniaturaAsync(sinMiniatura.Id));
        Assert.Null(await cliente.MiniaturaAsync(99_999));

        var dia = DatosDeControlPrint.DiaLocal(conMiniatura.DetectedUtc);
        var resumen = await cliente.ResumenDelDiaAsync(dia);
        var delDia = _servidor.Trabajos.Where(t => DatosDeControlPrint.DiaLocal(t.DetectedUtc) == dia && t.IsChargeable).ToList();
        Assert.Equal(delDia.Count, resumen.Trabajos);
        Assert.Equal(delDia.Sum(t => t.CostTotal), resumen.Costo);
        Assert.Equal(resumen.Precio - resumen.Cobrado, resumen.PorCobrar);

        var caras = await cliente.CarasAsync(Desde, Hasta);
        Assert.Equal(_servidor.Trabajos.Where(t => t.IsChargeable && t.Kind == JobKind.Print).Sum(t => t.CountedSides ?? t.Sides), caras.CarasImpresas);
        Assert.Equal(_servidor.Trabajos.Where(t => t.IsChargeable && t.Kind == JobKind.PanelCopy).Sum(t => t.Sides), caras.CarasCopias);

        var cotizaciones = await cliente.CotizacionesAsync(Desde.AddDays(5));
        Assert.Equal(new[] { 3L, 2L }, cotizaciones.Select(c => c.Id));
        var especial = cotizaciones.Single(c => c.Id == 2);
        Assert.True(especial.Especial);
        Assert.Equal((120m, 100m, "Escuela de prueba"), (especial.Precio, especial.Cotizado, especial.Cliente));
        Assert.Equal("Carta", especial.Opciones.GetProperty("size").GetString());
        Assert.Equal(5, (await cliente.CotizacionAsync(1))!.TrabajoId);
        Assert.Null(await cliente.CotizacionAsync(77));
        Assert.Equal(1, Assert.Single(await cliente.CotizacionesDelTrabajoAsync(5)).Id);
        SoloLeyo();
    }

    [Fact]
    public async Task La_impresora_el_estado_y_la_calibracion()
    {
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);

        var todas = await cliente.LecturasAsync();
        var desde = await cliente.LecturasAsync(Desde.AddDays(30));
        var ultima = await cliente.UltimaLecturaAsync();

        Assert.Equal(_servidor.Lecturas.Count, todas.Count);
        Assert.Equal(_servidor.Lecturas.Count(l => l.TakenUtc >= Desde.AddDays(30)), desde.Count);
        Assert.Equal(_servidor.Lecturas[^1].TakenUtc, ultima!.TomadaUtc);
        Assert.Equal(_servidor.Lecturas[^1].LifeCount, ultima.ContadorTotal);
        Assert.Equal(new[] { "Atasco de papel" }, todas[3].Errores!);
        Assert.Contains("\"PrinterPages\": 3200", await cliente.CalibracionJsonAsync());
        _servidor.CalibracionJson = null;
        Assert.Null(await cliente.CalibracionJsonAsync());
        Assert.True((await cliente.SaludAsync()).MonitorBien);
        SoloLeyo();
    }

    [Fact]
    public async Task Los_usuarios_llegan_sin_contrasenas_ni_sesiones()
    {
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);

        var usuarios = await cliente.UsuariosAsync();
        var activos = await cliente.UsuariosAsync(tambienInactivos: false);

        Assert.Equal(
            new[]
            {
                new UsuarioControlPrint(1, "Dueño de prueba", RolControlPrint.Administrador, true),
                new UsuarioControlPrint(2, "Cajera de prueba", RolControlPrint.Empleado, true),
                new UsuarioControlPrint(3, "Sin sesión", RolControlPrint.Invitado, true),
                new UsuarioControlPrint(4, "Cajero que se fue", RolControlPrint.Empleado, false),
            },
            usuarios);
        Assert.Equal(3, activos.Count);
        // El tipo no tiene dónde guardar lo demás que manda el servidor.
        Assert.Equal(new[] { "Id", "Nombre", "Rol", "Activo" }, typeof(UsuarioControlPrint).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract"));
        var json = JsonSerializer.Serialize(usuarios);
        Assert.DoesNotContain("SECRETO", json);
        Assert.DoesNotContain("pbkdf2", json);
        SoloLeyo();
    }

    [Fact]
    public async Task Los_ajustes_y_la_configuracion_tal_cual()
    {
        _servidor.Visibilidad = "OwnOnly";
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);

        var ajustes = await cliente.AjustesAsync();
        var costos = await cliente.ConfiguracionJsonAsync(ConfiguracionControlPrint.Costos);

        Assert.Equal(new AjustesControlPrint { Impresora = "192.0.2.50", MinutosEntreLecturas = 30, VisibilidadTrabajos = VisibilidadTrabajosControlPrint.CadaQuienLosSuyos }, ajustes);
        Assert.Equal(_servidor.CostosJson, costos);
        // Con «cada quien ve los suyos», sin sesión el servidor solo da los del invitado.
        var pagina = await cliente.BuscarTrabajosAsync(new BusquedaTrabajosControlPrint { Limite = 0 });
        Assert.All(pagina.Trabajos, t => Assert.Equal(DatosDeControlPrint.Invitado, t.AtendioId));
        var deOtro = _servidor.Trabajos.First(t => t.OwnerId == DatosDeControlPrint.Dueno);
        Assert.Equal(FallaControlPrint.SinPermiso, (await Assert.ThrowsAsync<ErrorControlPrint>(() => cliente.TrabajoAsync(deOtro.Id))).Falla);
        SoloLeyo();
    }

    // ── Los costos, tipados y en memoria ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Los_costos_llegan_tipados_y_se_guardan_en_memoria()
    {
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);

        var primeros = await cliente.CostosAsync();
        primeros.Costos.MarginPercent = 1;
        var segundos = await cliente.CostosAsync();

        Assert.Equal(1, _servidor.Pedidas("/api/settings/cost"));
        Assert.False(primeros.DeAntes);
        Assert.Equal(85, segundos.Costos.MarginPercent);
        Assert.Equal(3, segundos.Costos.MinimumPrice);
        Assert.Equal(0.5m, segundos.Costos.RoundTo);
        Assert.Equal(Medicion.ScalingMode.BySheetCost, segundos.Costos.Scaling);
        Assert.Equal("T748XXL120-AL", segundos.Costos.Black.Sku);
        Assert.NotSame(primeros.Costos, segundos.Costos);

        await cliente.CostosAsync(forzar: true);
        Assert.Equal(2, _servidor.Pedidas("/api/settings/cost"));
        SoloLeyo();
    }

    [Fact]
    public async Task Los_costos_se_vuelven_a_leer_cuando_Control_Print_avisa_que_cambiaron()
    {
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);
        await cliente.CostosAsync();
        var versiones = await cliente.CambiosAsync(VersionesControlPrint.Ninguna, TimeSpan.Zero);

        var espera = cliente.CambiosAsync(versiones, TimeSpan.FromSeconds(10));
        _servidor.CambiarCostos(JsonSerializer.Serialize(new Medicion.CostSettings { MarginPercent = 60 }, Medicion.CostSettings.Json));
        var cambiaron = await espera;
        var nuevos = await cliente.CostosAsync();

        Assert.Equal(versiones.Configuracion + 1, cambiaron.Configuracion);
        Assert.Equal(versiones.Trabajos, cambiaron.Trabajos);
        Assert.Equal("1.12.0", cambiaron.VersionApp);
        Assert.Equal(60, nuevos.Costos.MarginPercent);
        Assert.Equal(2, _servidor.Pedidas("/api/settings/cost"));
        SoloLeyo();
    }

    [Fact]
    public async Task Los_costos_vencen_y_sin_conexion_se_dan_los_de_antes()
    {
        var reloj = new RelojDePrueba(new DateTimeOffset(2026, 10, 25, 16, 0, 0, TimeSpan.Zero));
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones with { Reloj = reloj }, Codigo);
        using var nuevo = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);
        var leidos = await cliente.CostosAsync();

        reloj.Ahora += ClienteControlPrint.VigenciaCostos - TimeSpan.FromSeconds(1);
        await cliente.CostosAsync();
        Assert.Equal(1, _servidor.Pedidas("/api/settings/cost"));
        reloj.Ahora += TimeSpan.FromSeconds(2);
        await cliente.CostosAsync();
        Assert.Equal(2, _servidor.Pedidas("/api/settings/cost"));

        _servidor.Antes = (ctx, _) =>
        {
            ctx.Abort();
            return Task.FromResult(true);
        };
        var deAntes = await cliente.CostosAsync(forzar: true);

        Assert.True(deAntes.DeAntes);
        Assert.Equal(85, deAntes.Costos.MarginPercent);
        Assert.True(deAntes.LeidosUtc > leidos.LeidosUtc);
        // Sin haberlos leído nunca, el error.
        Assert.Equal(FallaControlPrint.SinConexion, (await Assert.ThrowsAsync<ErrorControlPrint>(() => nuevo.CostosAsync())).Falla);
    }

    // ── Solo lee ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Solo_salen_GET_y_la_busqueda()
    {
        Assert.True(ClienteControlPrint.EsSoloLectura(HttpMethod.Get, "api/jobs/5"));
        Assert.True(ClienteControlPrint.EsSoloLectura(HttpMethod.Get, "api/settings/cost"));
        Assert.True(ClienteControlPrint.EsSoloLectura(HttpMethod.Post, "api/jobs/search"));
        Assert.False(ClienteControlPrint.EsSoloLectura(HttpMethod.Post, "api/jobs/5/action"));
        Assert.False(ClienteControlPrint.EsSoloLectura(HttpMethod.Post, "api/jobs/pay-batch"));
        Assert.False(ClienteControlPrint.EsSoloLectura(HttpMethod.Post, "api/login"));
        Assert.False(ClienteControlPrint.EsSoloLectura(HttpMethod.Post, "api/copies"));
        Assert.False(ClienteControlPrint.EsSoloLectura(HttpMethod.Post, "api/jobs/search?x=1"));
        Assert.False(ClienteControlPrint.EsSoloLectura(HttpMethod.Put, "api/settings/cost"));
        Assert.False(ClienteControlPrint.EsSoloLectura(HttpMethod.Delete, "api/jobs/5"));
        Assert.False(ClienteControlPrint.EsSoloLectura(HttpMethod.Patch, "api/settings/app"));
    }

    [Fact]
    public async Task Ninguna_lectura_del_cliente_escribe()
    {
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);
        var trabajo = _servidor.Trabajos[5];

        await cliente.BuscarTrabajosAsync(new BusquedaTrabajosControlPrint());
        await cliente.TrabajoAsync(trabajo.Id);
        await cliente.MiniaturaAsync(trabajo.Id);
        await cliente.CotizacionesDelTrabajoAsync(trabajo.Id);
        await cliente.ResumenDelDiaAsync(DatosDeControlPrint.DiaLocal(trabajo.DetectedUtc), DatosDeControlPrint.Cajera);
        await cliente.CarasAsync(Desde, Hasta);
        await cliente.LecturasAsync();
        await cliente.UltimaLecturaAsync();
        await cliente.CalibracionJsonAsync();
        await cliente.SaludAsync();
        await cliente.CotizacionesAsync(Desde, "Tesis", 10);
        await cliente.CotizacionAsync(1);
        await cliente.UsuariosAsync();
        await cliente.ConfiguracionJsonAsync(ConfiguracionControlPrint.Ajustes);
        await cliente.ConfiguracionJsonAsync(ConfiguracionControlPrint.Costos);
        await cliente.AjustesAsync();
        await cliente.CostosAsync(forzar: true);
        await cliente.CambiosAsync(VersionesControlPrint.Ninguna, TimeSpan.Zero);

        // Cada método público de lectura se probó arriba: si se agrega uno, que se agregue aquí.
        var metodos = typeof(ClienteControlPrint).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.Name.EndsWith("Async", StringComparison.Ordinal)).Select(m => m.Name).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(
            new[]
            {
                "AjustesAsync", "BuscarTrabajosAsync", "CalibracionJsonAsync", "CambiosAsync", "CarasAsync", "ConfiguracionJsonAsync", "CostosAsync",
                "CotizacionAsync", "CotizacionesAsync", "CotizacionesDelTrabajoAsync", "LecturasAsync", "MiniaturaAsync", "ResumenDelDiaAsync",
                "SaludAsync", "TrabajoAsync", "UltimaLecturaAsync", "UsuariosAsync",
            },
            metodos);
        Assert.All(_servidor.Peticiones.Where(p => p.Ruta != "/api/hello"), p => Assert.Equal("K7QP3MZX9RTDW2HB", p.Codigo));
        SoloLeyo();
    }

    /// <summary>
    /// Un 307 o un 308 repiten el método y el cuerpo: si el cliente los siguiera, la búsqueda llegaría como POST a una acción o al
    /// cobro en lote. No los sigue (AllowAutoRedirect = false): ni el saludo, ni la búsqueda, ni un GET (la revisión del PR #143 lo
    /// comprobó).
    /// </summary>
    [Fact]
    public async Task Una_redireccion_no_convierte_una_lectura_en_una_escritura()
    {
        using var cliente = await ClienteControlPrint.ConectarAsync(Opciones, Codigo);
        _servidor.Antes = (ctx, p) =>
        {
            var destino = p.Ruta switch
            {
                "/api/hello" => "/api/jobs/1/action",
                "/api/jobs/search" => "/api/jobs/1/action",
                "/api/health" => "/api/jobs/pay-batch",
                _ => null,
            };
            if (destino is null)
                return Task.FromResult(false);
            ctx.Response.StatusCode = p.Ruta == "/api/health" ? StatusCodes.Status308PermanentRedirect : StatusCodes.Status307TemporaryRedirect;
            ctx.Response.Headers.Location = destino;
            return Task.FromResult(true);
        };

        await Assert.ThrowsAsync<ErrorControlPrint>(() => cliente.BuscarTrabajosAsync(new BusquedaTrabajosControlPrint()));
        await Assert.ThrowsAsync<ErrorControlPrint>(() => cliente.SaludAsync());
        await Assert.ThrowsAsync<ErrorControlPrint>(() => ClienteControlPrint.SaludarAsync(Opciones));

        Assert.Empty(_servidor.Escrituras);
        Assert.DoesNotContain(_servidor.Peticiones, p => p.Ruta is "/api/jobs/1/action" or "/api/jobs/pay-batch");
    }

    [Fact]
    public async Task El_servidor_falso_si_nota_una_escritura()
    {
        // Para confiar en SoloLeyo: un POST que no es la búsqueda queda anotado como escritura (y haría fallar la prueba al terminar).
        _servidor.PermitirEscrituras = true;
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        var respuesta = await http.PostAsync($"https://127.0.0.1:{_servidor.Puerto}/api/jobs/pay-batch", new StringContent("{}"));

        Assert.Equal((HttpStatusCode)418, respuesta.StatusCode);
        Assert.Equal("/api/jobs/pay-batch", Assert.Single(_servidor.Escrituras).Ruta);
    }

    private static EstadoTrabajoControlPrint Estado(JobState estado) => estado switch
    {
        JobState.Detected => EstadoTrabajoControlPrint.Detectado,
        JobState.Analyzed => EstadoTrabajoControlPrint.Analizado,
        JobState.Printed => EstadoTrabajoControlPrint.Impreso,
        JobState.Cancelled => EstadoTrabajoControlPrint.Cancelado,
        _ => EstadoTrabajoControlPrint.SinAnalizar,
    };
}

/// <summary>Un reloj que se mueve a mano.</summary>
public sealed class RelojDePrueba(DateTimeOffset ahora) : TimeProvider
{
    public DateTimeOffset Ahora { get; set; } = ahora;

    public override DateTimeOffset GetUtcNow() => Ahora;
}
