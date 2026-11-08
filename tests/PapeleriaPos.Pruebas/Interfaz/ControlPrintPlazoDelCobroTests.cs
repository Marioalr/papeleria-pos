using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Pruebas.ControlPrint;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;
using static PapeleriaPos.Impresiones.Pruebas.ControlPrint.PorCobrarDePrueba;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La revisión en Control Print justo antes de cobrar, vista desde la caja de verdad (cliente, contingencia y su copia) con el plazo de
/// escritura de la caja de la tienda (<see cref="Transporte.PlazoEscritura"/>, 2.5 s). Lo encontró la revisión del PR #146
/// (2026-10-29): la revisión no tenía tope y, si la caja principal se caía al cobrar, el ticket quedaba «cobro sin confirmar» y la red,
/// lenta. Ahora tiene su tope (medio segundo, que se le quita a la espera del folio de eleventa) y, sin respuesta, la tarjeta se cobra con
/// lo último que se leyó de ella.
/// </summary>
public sealed class ControlPrintPlazoDelCobroTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-cp-plazo-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    [Fact]
    public async Task Un_Control_Print_que_se_cae_al_revisar_no_deja_el_cobro_sin_confirmar_ni_la_red_lenta()
    {
        await using var controlPrint = await ServidorControlPrintFalso.IniciarAsync(Codigo);
        controlPrint.CambiarTrabajos(t => t.Add(Trabajo(1234, Hace(4), precio: 15)));
        await using var servicio = await ServicioDePrueba.CrearAsync(ControlPrintEnLaCajaTests.Ajustes(controlPrint));
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var http = servicio.Fabrica.CreateClient();
        var hasta = DateTime.UtcNow.AddSeconds(30);
        while ((await http.GetFromJsonAsync<ColaControlPrintDto>(
                   $"api/sucursales/{v.Caja.SucursalId}/impresiones/control-print?usuarioId={v.Cajero.UsuarioId}"))!.Trabajos.Count != 1)
        {
            Assert.True(DateTime.UtcNow < hasta, "No llegó la tarjeta #1234 a la cola del servicio.");
            await Task.Delay(50);
        }
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        Assert.Equal(EstadoRed.Buena, v.Contingencia.EstadoDeLaRed);

        // La caja principal se apaga justo cuando el cajero de la caja 2 cobra: lo que se le pide a Control Print se queda colgado y se
        // corta a los 3.2 s. La caja, como en la tienda, espera el cobro 2.5 s.
        controlPrint.Antes = async (ctx, p) =>
        {
            if (p.Ruta != "/api/jobs/search")
                return false;
            await Task.Delay(TimeSpan.FromSeconds(3.2));
            ctx.Abort();
            return true;
        };
        v.Cliente.PlazoEscritura = Transporte.PlazoEscritura;
        var ventaId = Guid.NewGuid();
        var reloj = Stopwatch.StartNew();

        var ticket = await v.Cliente.CobrarAsync(new SolicitudCobro(v.Caja.Id, turno.Id, v.Cajero.UsuarioId, [], [new CobroSolicitado(FormaPago.Efectivo, 2000)],
            ventaId, ImpresionesControlPrint: [new ImpresionDeControlPrintSolicitada(1234, 1500)]));
        var tardo = reloj.Elapsed;

        // A tiempo, en línea y sin dejar la red lenta: Control Print no contestó en su medio segundo y la tarjeta se cobró con lo último que
        // se leyó de ella (hace un momento), «sin revisar»: se revisa cuando vuelva a contestar.
        Assert.True(tardo < Transporte.PlazoEscritura, $"La caja tardó {tardo.TotalSeconds:0.0} s.");
        Assert.Equal(1500, ticket.TotalCentavos);
        Assert.Equal(EstadoRed.Buena, v.Contingencia.EstadoDeLaRed);
        Assert.False(v.Contingencia.CobroSinConfirmar(ventaId));
        await using var db = servicio.Base();
        Assert.True(await db.Ventas.AnyAsync(x => x.Id == ventaId && !x.EnContingencia));
        Assert.True((await db.Set<TrabajoControlPrintCobrado>().SingleAsync(x => x.TrabajoId == 1234)).SinRevisar);
        Assert.Empty(controlPrint.Escrituras);
    }
}
