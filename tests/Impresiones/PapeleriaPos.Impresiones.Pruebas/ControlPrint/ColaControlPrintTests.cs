using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;
using static PapeleriaPos.Impresiones.Pruebas.ControlPrint.PorCobrarDePrueba;

namespace PapeleriaPos.Impresiones.Pruebas.ControlPrint;

/// <summary>
/// La cola «por cobrar» de Control Print vista desde la caja 2 (<see cref="ColaControlPrint"/>), contra el Control Print falso: lo
/// que lee, cómo se entera de los cambios (su espera larga, o cada tanto si no la tiene), lo que pasa si no contesta, sus costos, la
/// revisión antes de cobrar y las miniaturas. Solo lee: el falso hace fallar la prueba con cualquier escritura.
/// </summary>
public sealed class ColaControlPrintTests : IAsyncLifetime
{
    private ServidorControlPrintFalso _servidor = null!;
    private readonly CancellationTokenSource _alto = new();
    private readonly List<Task> _corriendo = [];
    private readonly List<ColaControlPrint> _colas = [];

    public async Task InitializeAsync() => _servidor = await ServidorControlPrintFalso.IniciarAsync(Codigo);

    public async Task DisposeAsync()
    {
        await _alto.CancelAsync();
        await Task.WhenAll(_corriendo);
        foreach (var cola in _colas)
            cola.Dispose();
        // Al terminar, el falso hace fallar la prueba si alguien le escribió.
        await _servidor.DisposeAsync();
    }

    private ColaControlPrint Cola(Func<OpcionesColaControlPrint, OpcionesColaControlPrint>? ajustar = null, bool correr = true)
    {
        var opciones = new OpcionesColaControlPrint("127.0.0.1")
        {
            Puerto = _servidor.Puerto, Codigo = Codigo, Reintento = TimeSpan.FromMilliseconds(200), ReintentoLargo = TimeSpan.FromMilliseconds(300),
            EsperaCambios = TimeSpan.FromSeconds(2), Juntar = TimeSpan.FromMilliseconds(20), Equipo = "CAJA2 (POS)",
        };
        var cola = new ColaControlPrint(ajustar is null ? opciones : ajustar(opciones));
        _colas.Add(cola);
        if (correr)
            _corriendo.Add(cola.CorrerAsync(_alto.Token));
        return cola;
    }

    private static async Task<ColaControlPrint> LeidaAsync(ColaControlPrint cola, int lecturas = 1)
    {
        await HastaAsync(() => cola.Estado.Lecturas >= lecturas, $"la lectura {lecturas} de la cola");
        return cola;
    }

    private void Agregar(params JobRecordCp[] trabajos) => _servidor.CambiarTrabajos(t => t.AddRange(trabajos));

    // ── Lo que lee ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lee_los_por_cobrar_de_hoy_y_ayer_los_mas_nuevos_primero_y_nada_mas()
    {
        var ayer = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow)).AddDays(-1);
        var deAyer = HoraMexico.AUtc(ayer.ToDateTime(new TimeOnly(10, 30)));
        Agregar(
            Trabajo(1, deAyer.AddDays(-2)),                                   // de antier: ya no se ve
            Trabajo(2, deAyer, documento: "Acta.pdf"),                         // de ayer: sí
            Trabajo(3, Hace(30), cobro: PaymentState.Paid),                    // ya se cobró en Control Print
            Trabajo(4, Hace(25), cobro: PaymentState.Unsold),                  // no vendida
            Trabajo(5, Hace(20), estado: JobState.Cancelled),                  // cancelada: no se cobra
            Trabajo(6, Hace(15), estado: JobState.Detected),                   // todavía se imprime, sin precio
            Trabajo(7, Hace(10), estado: JobState.Analyzed, precio: 6),        // analizada: ya tiene precio
            Trabajo(8, Hace(5), precio: 12));

        var cola = await LeidaAsync(Cola());

        var estado = cola.Estado;
        Assert.True(estado.Contesta);
        Assert.Null(estado.Problema);
        Assert.Equal("PRINCIPAL", estado.Nombre);
        Assert.Equal("1.12.0", estado.VersionServidor);
        Assert.Equal([8L, 7, 2], estado.PorCobrar.Select(t => t.Id));
        Assert.Equal(3, estado.Total);
        // Lo que pidió: los por cobrar con precio desde el principio de ayer, en hora de la tienda.
        var busqueda = Assert.Single(_servidor.Busquedas);
        Assert.Equal(PaymentState.Pending, busqueda.Payment);
        Assert.True(busqueda.OnlyChargeable);
        Assert.Equal(HoraMexico.AUtc(ayer.ToDateTime(TimeOnly.MinValue)), busqueda.FromUtc!.Value.ToUniversalTime());
        Assert.Equal(ColaControlPrint.Limite, busqueda.Limit);
        // Se presenta como el POS de la caja 2, con el código en cada lectura.
        Assert.All(_servidor.Peticiones.Where(p => p.Ruta != "/api/hello"), p => Assert.Equal(("K7QP3MZX9RTDW2HB", "CAJA2 (POS)"), (p.Codigo, p.Equipo)));
        Assert.Empty(_servidor.Escrituras);
    }

    [Fact]
    public async Task Un_trabajo_nuevo_o_cobrado_en_la_caja_principal_llega_con_su_espera_de_cambios()
    {
        Agregar(Trabajo(10, Hace(8)));
        var cola = await LeidaAsync(Cola(o => o with { EsperaCambios = TimeSpan.FromSeconds(20) }));
        // Ya espera cambios (su espera larga, de hasta 20 s aquí).
        await HastaAsync(() => _servidor.Pedidas("/api/changes") >= 2, "la espera de cambios");
        var antes = DateTime.UtcNow;

        Agregar(Trabajo(11, Hace(1), documento: "Nuevo.pdf"));
        await HastaAsync(() => cola.Estado.PorCobrar.Any(t => t.Id == 11), "el trabajo nuevo");

        // Llegó por el aviso, no por volver a leer cada tanto: mucho antes de los 20 s de la espera.
        Assert.True(DateTime.UtcNow - antes < TimeSpan.FromSeconds(10));
        Assert.Equal([11L, 10], cola.Estado.PorCobrar.Select(t => t.Id));

        // En la caja principal cobran el 10: deja de verse.
        _servidor.CambiarTrabajos(t => t.Single(x => x.Id == 10).Payment = PaymentState.Paid);
        await HastaAsync(() => cola.Estado.PorCobrar.All(t => t.Id != 10), "que el cobrado allá se quite");
        Assert.Equal([11L], cola.Estado.PorCobrar.Select(t => t.Id));
        Assert.Empty(_servidor.Escrituras);
    }

    [Fact]
    public async Task Si_Control_Print_no_avisa_sus_cambios_se_lee_cada_tanto()
    {
        // Un Control Print sin «api/changes»: contesta que no existe.
        _servidor.Antes = async (ctx, p) =>
        {
            if (p.Ruta != "/api/changes")
                return false;
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { kind = "missing", message = "No existe." });
            return true;
        };
        Agregar(Trabajo(20, Hace(5)));
        var cola = await LeidaAsync(Cola());

        Agregar(Trabajo(21, Hace(1)));
        await HastaAsync(() => cola.Estado.PorCobrar.Any(t => t.Id == 21), "el trabajo nuevo, leyendo cada tanto");
        Assert.True(cola.Estado.Contesta);
        Assert.True(_servidor.Busquedas.Count >= 2);
    }

    [Fact]
    public async Task Si_Control_Print_no_contesta_lo_dice_y_vuelve_solo_cuando_contesta()
    {
        Agregar(Trabajo(30, Hace(5)));
        var cola = await LeidaAsync(Cola());

        // La caja principal se apaga: cada conexión se corta.
        _servidor.Antes = (ctx, _) =>
        {
            ctx.Abort();
            return Task.FromResult(true);
        };
        _servidor.Avisar(0);
        await HastaAsync(() => cola.Estado.Contesta == false, "que diga que no contesta");
        Assert.Equal(FallaControlPrint.SinConexion, cola.Estado.Falla);
        Assert.Contains("No se pudo hablar con Control Print en 127.0.0.1", cola.Estado.Problema);
        Assert.DoesNotContain("K7QP", cola.Estado.Problema);

        // Vuelve: lo vuelve a leer solo.
        var lecturas = cola.Estado.Lecturas;
        _servidor.Antes = null;
        await HastaAsync(() => cola.Estado.Contesta == true && cola.Estado.Lecturas > lecturas, "que vuelva a contestar");
        Assert.Null(cola.Estado.Problema);
        Assert.Equal([30L], cola.Estado.PorCobrar.Select(t => t.Id));
    }

    [Fact]
    public async Task Sin_el_codigo_no_se_conecta_y_lo_dice()
    {
        var cola = Cola(o => o with { Codigo = null });
        await Task.Delay(300);

        Assert.True(cola.Configurado);
        Assert.False(cola.Estado.TieneCodigo);
        Assert.False(cola.Estado.Contesta);
        Assert.Contains("poner-codigo-controlprint.ps1", cola.Estado.Problema);
        Assert.Empty(_servidor.Peticiones);
        // Sin configurar: ni eso.
        var sinConfigurar = new ColaControlPrint(null);
        Assert.False(sinConfigurar.Configurado);
        Assert.Null(sinConfigurar.Estado.Problema);
        await sinConfigurar.CorrerAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Un_codigo_que_no_coincide_no_se_manda_y_lo_dice()
    {
        var cola = Cola(o => o with { Codigo = "AAAA-BBBB-CCCC-DDDD" });

        await HastaAsync(() => cola.Estado.Falla == FallaControlPrint.CodigoNoCoincide, "que diga que el código no coincide");
        Assert.Contains("Ajustes → Red", cola.Estado.Problema);
        Assert.All(_servidor.Peticiones, p => Assert.Equal(("/api/hello", null), (p.Ruta, p.Codigo)));
    }

    [Fact]
    public async Task La_huella_se_fija_en_el_primer_saludo_y_otro_certificado_ya_no_recibe_nada()
    {
        var archivo = Path.Combine(Path.GetTempPath(), $"cp-huella-{Guid.NewGuid():N}.controlprint");
        try
        {
            // La primera vez (el servicio arranca): se fija en el saludo y se guarda junto a la base. Luego se detiene.
            using (var detener = new CancellationTokenSource())
            using (var primera = new ColaControlPrint(new OpcionesColaControlPrint("127.0.0.1")
                   {
                       Puerto = _servidor.Puerto, Codigo = Codigo, ArchivoHuella = archivo, EsperaCambios = TimeSpan.FromSeconds(1),
                   }))
            {
                var corriendo = primera.CorrerAsync(detener.Token);
                await LeidaAsync(primera);
                Assert.Equal(_servidor.Huella, primera.Estado.Huella);
                await detener.CancelAsync();
                await corriendo;
            }
            Assert.True(File.Exists(archivo));
            var guardada = JsonDocument.Parse(await File.ReadAllTextAsync(archivo)).RootElement;
            Assert.Equal(ProtocoloControlPrint.HuellaLegible(_servidor.Huella), guardada.GetProperty("Huella").GetString());
            Assert.Equal("PRINCIPAL", guardada.GetProperty("Servidor").GetString());
            Assert.DoesNotContain(Codigo.Replace("-", ""), await File.ReadAllTextAsync(archivo));

            // Se reinicia el servicio y en esa dirección contesta otro, con otro certificado: no se le manda nada.
            _servidor.Certificado = ServidorControlPrintFalso.OtroCertificado;
            _servidor.CerrarConexiones = true;
            var antes = _servidor.Peticiones.Count;
            var otra = Cola(o => o with { ArchivoHuella = archivo });

            await HastaAsync(() => otra.Estado.Falla == FallaControlPrint.CertificadoDistinto, "que rechace el otro certificado");
            Assert.Contains("otra huella", otra.Estado.Problema);
            Assert.Equal(antes, _servidor.Peticiones.Count);
        }
        finally
        {
            File.Delete(archivo);
        }
    }

    [Fact]
    public async Task Cada_quien_ve_sus_trabajos_se_avisa()
    {
        _servidor.Visibilidad = "OwnOnly";
        var cola = await LeidaAsync(Cola());

        Assert.True(cola.Estado.SoloInvitado);
    }

    // ── Los costos ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sus_costos_llegan_del_servidor_y_avisan_cuando_cambian()
    {
        var avisados = new List<CostSettings>();
        var cola = Cola(correr: false);
        cola.AlCambiarCostos = (costos, _) =>
        {
            lock (avisados)
                avisados.Add(costos);
            return Task.CompletedTask;
        };
        _corriendo.Add(cola.CorrerAsync(_alto.Token));
        await LeidaAsync(cola);
        await HastaAsync(() => avisados.Count == 1, "los costos de Control Print");

        Assert.Equal((85m, 3m, 0.5m), (avisados[0].MarginPercent, avisados[0].MinimumPrice, avisados[0].RoundTo));
        var costos = await cola.CostosAsync();
        Assert.False(costos!.DeAntes);
        Assert.Equal(3m, costos.Costos.MinimumPrice);

        // El dueño cambia el precio mínimo en Ajustes → Costos de Control Print.
        _servidor.CambiarCostos(JsonSerializer.Serialize(new CostSettings { MarginPercent = 85, MinimumPrice = 5, RoundTo = 1 }, CostSettings.Json));
        await HastaAsync(() => avisados.Count == 2, "los costos nuevos");
        Assert.Equal(5m, avisados[1].MinimumPrice);
        Assert.Equal(5m, (await cola.CostosAsync())!.Costos.MinimumPrice);

        // Sin conexión, los de antes, diciendo que son de antes.
        _servidor.Antes = (ctx, _) =>
        {
            ctx.Abort();
            return Task.FromResult(true);
        };
        _servidor.Avisar(0);
        await HastaAsync(() => cola.Estado.Contesta == false, "que no conteste");
        var deAntes = await cola.CostosAsync();
        Assert.Equal(5m, deAntes!.Costos.MinimumPrice);
    }

    // ── Antes de cobrar y las miniaturas ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Antes_de_cobrar_se_revisa_cada_uno_en_Control_Print()
    {
        Agregar(Trabajo(40, Hace(9), precio: 4), Trabajo(41, Hace(8), precio: 6), Trabajo(42, Hace(7), precio: 10), Trabajo(43, Hace(6), precio: 2));
        var cola = await LeidaAsync(Cola());
        // Mientras tanto, en la caja principal: cobran el 41, le ponen precio especial al 42 y marcan no vendido el 43.
        _servidor.CambiarTrabajos(t =>
        {
            var cobrado = t.Single(x => x.Id == 41);
            (cobrado.Payment, cobrado.PaymentBy) = (PaymentState.Paid, "Ana");
            var especial = t.Single(x => x.Id == 42);
            (especial.PriceOverride, especial.PriceKind, especial.SpecialName) = (8, PriceKind.Special, "Escuela");
            t.Single(x => x.Id == 43).Payment = PaymentState.Unsold;
        });

        var revision = await cola.RevisarAsync([(40, 400), (41, 600), (42, 1000), (43, 200), (99, 500)]);

        Assert.Null(revision.NoContesta);
        Assert.False(revision.TodoBien);
        Assert.Equal([40L], revision.Siguen.Select(t => t.Id));
        Assert.Equal([(42L, 800L)], revision.OtroPrecio);
        Assert.Equal([41L, 43, 99], revision.YaNo.Select(y => y.Id));
        Assert.Contains("#41 ya se cobró en Control Print (Ana)", revision.YaNo[0].Porque);
        Assert.Contains("no vendida", revision.YaNo[1].Porque);
        Assert.Contains("ya no está en Control Print", revision.YaNo[2].Porque);

        // Sin conexión no se sabe: no se cobra.
        _servidor.Antes = (ctx, _) =>
        {
            ctx.Abort();
            return Task.FromResult(true);
        };
        var sinConexion = await cola.RevisarAsync([(40, 400)]);
        Assert.Empty(sinConexion.Siguen);
        Assert.Contains("No se pudo hablar con Control Print", sinConexion.NoContesta);
    }

    [Fact]
    public async Task La_miniatura_se_pide_una_vez_y_solo_de_lo_que_esta_en_la_cola()
    {
        var conMiniatura = Trabajo(50, Hace(3));
        conMiniatura.Thumbnail = DatosDeControlPrint.Png;
        Agregar(conMiniatura, Trabajo(51, Hace(2)));
        var cola = await LeidaAsync(Cola());

        Assert.Equal(DatosDeControlPrint.Png, await cola.MiniaturaAsync(50));
        Assert.Equal(DatosDeControlPrint.Png, await cola.MiniaturaAsync(50));
        Assert.Null(await cola.MiniaturaAsync(51));
        Assert.Null(await cola.MiniaturaAsync(51));
        Assert.Null(await cola.MiniaturaAsync(12345));

        Assert.Equal(1, _servidor.Pedidas("/api/jobs/50/thumbnail"));
        // La que todavía no tiene se vuelve a pedir, pero no enseguida.
        Assert.Equal(1, _servidor.Pedidas("/api/jobs/51/thumbnail"));
        Assert.Equal(0, _servidor.Pedidas("/api/jobs/12345/thumbnail"));
    }
}
