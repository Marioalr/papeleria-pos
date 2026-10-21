using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Configuración › Acerca de › Red (la red de la tienda en el POS): las últimas caídas de la bitácora de red con desde, hasta y
/// cuánto duraron, dónde están sus archivos, y lo configurado por nombre o con WSD.
/// </summary>
public sealed class AcercaDeLaRedTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();

    public void Dispose() => _carpeta.Dispose();

    private sealed class ConWsd : IPuertosDeImpresoras
    {
        public PuertoDeImpresora? DeLaImpresora(string nombre) => new("WSD-4f1c9e2a", TipoDePuerto.Wsd);
    }

    /// <summary>
    /// La impresora de tickets con un puerto WSD (lo lee la caja de Windows; aquí, uno de mentira): el chip de los tickets de arriba
    /// y la entrada dicen «configúrala por IP», donde ya se ve si la impresora tiene un problema. Sin problema, el chip de siempre.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_estado_de_arriba_y_la_entrada_avisan_si_la_impresora_esta_con_wsd(bool conWsd)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var sesion = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        // Una impresora de tickets de verdad (no se le manda nada: solo para que el chip no diga «Ticket en pantalla»).
        var impresora = new ImpresoraRed("192.0.2.20");
        await using var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IImpresora>(impresora)
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RedDeLaCaja(conexion, avisoImpresora: AvisosDeRed.DeLaImpresora("POS-8360", conWsd ? new ConWsd() : null)))
            .BuildServiceProvider();

        await using (var arriba = new PantallaSinNavegador(servicios))
        {
            await arriba.AbrirAsync<EstadoCaja>(new() { [nameof(EstadoCaja.Caja)] = caja, [nameof(EstadoCaja.Sesion)] = sesion });
            var chip = await arriba.ElementoAsync(e => e.Clase?.StartsWith("vn-chip-estado", StringComparison.Ordinal) == true
                                                       && e.Texto.Contains("Tickets", StringComparison.Ordinal), "el chip de los tickets");
            Assert.Equal(conWsd ? "Tickets · configúrala por IP" : "Tickets", PantallaSinNavegador.Limpio(chip.Texto));
            Assert.Equal(conWsd ? "vn-chip-estado aviso" : "vn-chip-estado", chip.Clase);
            if (conWsd)
                Assert.StartsWith("La impresora de tickets «POS-8360» usa un puerto WSD", chip.Atributos["title"]?.ToString());
        }
        await using (var entrada = new PantallaSinNavegador(servicios))
        {
            await entrada.AbrirAsync<LadoEntrada>(new() { [nameof(LadoEntrada.Caja)] = caja });
            Assert.Equal(conWsd, (await entrada.TextoAsync()).Contains("Impresora de tickets: configúrala por IP", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Acerca_de_dice_las_ultimas_caidas_de_la_red_y_lo_configurado_por_nombre()
    {
        File.WriteAllLines(Path.Combine(_carpeta.Ruta, "red-caja-2026-10-17.log"),
        [
            "2026-10-17 09:00:00  Empieza la bitácora de red de esta caja.",
            "2026-10-17 12:00:00  Se perdió «internet»: lo dice la caja principal.",
            "2026-10-17 12:00:05  Se perdió «la caja principal»: caja1:5080 no contesta.",
            "2026-10-17 12:17:33  Volvió «la caja principal» tras 17 min 28 s (se perdió a las 12:00:05): contesta en 9 ms.",
            "2026-10-17 13:00:00  Volvió «internet» tras 1 h (se perdió a las 12:00:00): lo dice la caja principal.",
        ]);
        var bitacora = new BitacoraDeRed(_carpeta.Ruta, "red-caja", RelojDeLaRed.Nuevo());
        bitacora.Empezar("esta caja");
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var sesion = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var conexion = new ConexionCaja { Servicio = new Uri("http://caja1:5080/"), CajaId = caja.Id };
        await using var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .AddSingleton(new RedDeLaCaja(conexion, bitacora, avisoImpresora: AvisosDeRed.DeLaImpresora("POS-8360", new ConWsd())))
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        await using var _ = pantalla;
        await pantalla.AbrirAsync<Configuracion>(new()
        {
            [nameof(Configuracion.Caja)] = caja,
            [nameof(Configuracion.Sesion)] = sesion,
            [nameof(Configuracion.AlCambiar)] = new EventCallback(null, (Action)(() => { })),
            [nameof(Configuracion.AlNoGuardar)] = new EventCallback<string>(null, (Action<string>)(_ => { })),
        });
        await pantalla.EsperarAsync(t => !t.Contains("Cargando…"), "lo guardado");

        await pantalla.OprimirAsync(await pantalla.PorIdAsync("cfg-acerca"));
        await pantalla.EsperarAsync(t => t.Contains("Se perdió", StringComparison.Ordinal), "las caídas");

        var texto = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        Assert.Contains("Red Cómo está ahora y sus últimas caídas, de la bitácora de red, que se anota sola.", texto);
        // Los títulos de la tabla son fijos (van juntos, sin espacios, en lo que lee la prueba).
        Assert.Contains("Se perdióDesdeHastaDuróLo anotó", texto);
        Assert.Contains("la caja principal sáb 17/10 12:00 12:17 17 min 28 s esta caja", texto);
        Assert.Contains("internet sáb 17/10 12:00 13:00 1 h esta caja", texto);
        Assert.Contains("Esta caja busca a la caja principal por su nombre («caja1»)", texto);
        Assert.Contains("La impresora de tickets «POS-8360» usa un puerto WSD", texto);
        Assert.Contains($"La bitácora de red de esta caja está en {_carpeta.Ruta} (red-caja-AAAA-MM-DD.log, una por día; se guardan 10 días).", texto);
    }
}
