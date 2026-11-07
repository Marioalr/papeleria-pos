using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Al arrancar se le da una vuelta al camino del cobro, para que el primero del día no pase del plazo de la caja. Toca el dinero, así
/// que lo que se revisa es que no quede nada: ni venta, ni folio, ni turno, ni kardex, ni bandeja hacia eleventa.
/// </summary>
public sealed class CalentarElCobroTests
{
    private static async Task<CalentarElCobro> TerminadoAsync(ServicioDePrueba servicio)
    {
        var calentar = servicio.Fabrica.Services.GetServices<IHostedService>().OfType<CalentarElCobro>().Single();
        await calentar.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(60));
        return calentar;
    }

    private static async Task<(int Ventas, int Turnos, int Movimientos, int Bitacora, int Envios, long Secuencias)> LoQueHayAsync(ServicioDePrueba servicio)
    {
        await using var db = servicio.Base();
        return (await db.Ventas.CountAsync(), await db.Turnos.CountAsync(), await db.MovimientosInventario.CountAsync(m => m.DocumentoTipo == nameof(Venta)), await db.Bitacora.CountAsync(),
            await db.EnviosAEleventa.CountAsync(), await db.Secuencias.SumAsync(s => s.Valor));
    }

    [Fact]
    public async Task Sin_turno_abierto_le_da_la_vuelta_al_cobro_y_no_queda_nada()
    {
        await using var caliente = await ServicioDePrueba.CrearAsync(("Cobro:Calentar", "true"));

        var calentar = await TerminadoAsync(caliente);
        Assert.True(calentar.Calento);
        var (ventas, turnos, movimientos, _, envios, _) = await LoQueHayAsync(caliente);
        Assert.Equal((0, 0, 0, 0), (ventas, turnos, movimientos, envios));
    }

    [Fact]
    public async Task El_primer_cobro_de_verdad_lleva_el_primer_folio_y_descuenta_una_sola_vez()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(("Cobro:Calentar", "true"));
        Assert.True((await TerminadoAsync(servicio)).Calento);
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var sesion = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var turno = (await (await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, sesion.UsuarioId, 50000))).Content.ReadFromJsonAsync<TurnoDto>())!;
        Guid variante;
        await using (var db = servicio.Base())
            variante = await db.Precios.Select(p => p.VarianteId).FirstAsync();

        var respuesta = await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, sesion.UsuarioId,
            [new LineaSolicitada(variante, null, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 1000000)]));

        respuesta.EnsureSuccessStatusCode();
        await using var lectura = servicio.Base();
        Assert.Equal(1, (await lectura.Ventas.SingleAsync()).Folio);
        Assert.Equal(1, await lectura.MovimientosInventario.CountAsync(m => m.DocumentoId == lectura.Ventas.Single().Id));
    }

    /// <summary>Con la caja ya con turno abierto (un reinicio a media jornada): el turno sigue igual y sin esa venta.</summary>
    [Fact]
    public async Task Con_turno_abierto_tampoco_queda_nada()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var sesion = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        (await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, sesion.UsuarioId, 50000))).EnsureSuccessStatusCode();
        var antes = await LoQueHayAsync(servicio);
        await using var alcance = servicio.Fabrica.Services.CreateAsyncScope();
        var s = alcance.ServiceProvider;

        var calento = await ApiCaja.CalentarCobroAsync(s.GetRequiredService<PapeleriaPos.Datos.PosDbContext>(), s.GetRequiredService<CatalogoEnMemoria>(),
            s.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpcionesTicket>>().Value, s.GetRequiredService<EnvioVentasEleventa>(), TimeProvider.System, default);

        Assert.True(calento);
        Assert.Equal(antes, await LoQueHayAsync(servicio));
    }

    /// <summary>En una PC con pocos recursos el recolector de memoria va a ser el de estación, y el servicio se publica ya compilado, como la caja.</summary>
    [Fact]
    public void El_servicio_usa_el_recolector_de_estacion_y_se_publica_ya_compilado()
    {
        var configuracion = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PapeleriaPos.Servicio.runtimeconfig.json")))!;
        Assert.False(configuracion["runtimeOptions"]!["configProperties"]!["System.GC.Server"]!.GetValue<bool>());
        var proyecto = File.ReadAllText(Path.Combine(PapeleriaPos.Pruebas.Instalacion.ServicioJsonTests.RaizDelRepositorio(), "src", "PapeleriaPos.Servicio", "PapeleriaPos.Servicio.csproj"));
        Assert.Contains("<PublishReadyToRun>true</PublishReadyToRun>", proyecto);
    }
}
