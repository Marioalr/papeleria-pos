using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Restaurar un respaldo de la base en la caja 2 del piloto, contra el Firebird de prueba (2026-10-23). Restaurar no toca eleventa:
/// la bandeja del puente regresa como estaba en el respaldo, y lo que ya había llegado a eleventa vuelve a estar pendiente. No se
/// manda dos veces: cada intento lo busca allá por su marca. Y como al restaurar todo lo pendiente queda como «ya se intentó»,
/// cancelar aquí una de esas ventas antes de que se vuelva a mandar también la busca allá: su cancelación llega a eleventa, en lugar
/// de descartarla y dejarla allá como vendida.
/// </summary>
public sealed class RestaurarEnElPilotoTests
{
    private static async Task<int> TicketsDeLaCaja2Async(EleventaDePrueba e) =>
        Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE CAJA_ID = 4 AND ESTA_ABIERTO = 'f'"));

    private static async Task<Dictionary<Guid, EnvioAEleventa>> VentasEnLaBandejaAsync(ServicioDePrueba servicio)
    {
        await using var db = servicio.Base();
        return await db.EnviosAEleventa.AsNoTracking().Where(x => x.Tipo == TipoEnvioEleventa.Venta).ToDictionaryAsync(x => x.VentaId!.Value);
    }

    [FactConFirebird]
    public async Task Lo_que_ya_estaba_en_eleventa_y_regresa_pendiente_con_el_respaldo_no_se_manda_dos_veces()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var p = await PrincipalQueSeRestaura.CrearAsync(true, Ajustes(e.Servidor));
        var c = await AbrirAsync(p.Arrancar(), await MapearAsync(p.Servicio));
        var (s0, s1) = (await CobrarAsync(c), await CobrarAsync(c, plumas: 2));
        await MandarAsync(c);
        var enviadas = await VentasEnLaBandejaAsync(p.Servicio);
        var (ticket0, ticket1) = (enviadas[s0.VentaId!.Value].TicketEleventa!.Value, enviadas[s1.VentaId!.Value].TicketEleventa!.Value);

        // El respaldo se hizo justo después de guardar las dos y antes de que eleventa las confirmara: ahí siguen pendientes, sin intentos.
        var archivo = p.Respaldar(HoraMexico.ALocal(DateTime.UtcNow).AddMinutes(-1), "ahora-1010");
        await using (var respaldo = new PosDbContext(BaseDeDatos.Opciones($"Data Source={Path.Combine(p.CarpetaRespaldos, archivo)};Pooling=False")))
        {
            Assert.Equal(2, await respaldo.EnviosAEleventa.Where(x => x.Tipo == TipoEnvioEleventa.Venta).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Estado, EstadoEnvioEleventa.Pendiente).SetProperty(x => x.Intentos, 0).SetProperty(x => x.EnviadaUtc, (DateTime?)null)
                .SetProperty(x => x.TicketEleventa, (int?)null).SetProperty(x => x.FolioEleventa, (int?)null)));
        }
        // Y después del respaldo, otra venta que llega a eleventa (en la caja 2 se pierde al restaurar).
        var s2 = await CobrarAsync(c);
        await MandarAsync(c);
        Assert.Equal(3, await TicketsDeLaCaja2Async(e));

        // El dueño restaura el respaldo; la caja principal se reinicia.
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var lista = await Leer<RestaurablesDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/configuracion/respaldos/lista?usuarioId={dueno.UsuarioId}"));
        Assert.True(lista.PilotoEleventa);
        await Leer<RespaldosDto>(await c.Http.PostAsJsonAsync($"api/cajas/{c.Datos.Id}/configuracion/respaldos/restaurar",
            new SolicitudRestaurar(dueno.UsuarioId, "3333", archivo)));
        await p.Reinicio.Pedido.WaitAsync(TimeSpan.FromSeconds(20));
        await p.DetenerAsync();
        // Vuelve a arrancar, ya con el respaldo, y el puente todavía no puede escribir (le falta el respaldo del día de eleventa, por
        // ejemplo): lo pendiente se queda como regresó, sin otro intento. Es el rato en que la regla del piloto importa.
        c = c with { Http = p.Arrancar(("Eleventa:RespaldoObligatorio", "true")) };

        // Eleventa no se tocó. Las dos regresaron pendientes, ya como «ya se intentó»; la de después no está en la caja 2.
        Assert.Equal(3, await TicketsDeLaCaja2Async(e));
        var regresaron = await VentasEnLaBandejaAsync(p.Servicio);
        Assert.Equal((EstadoEnvioEleventa.Pendiente, 1), (regresaron[s0.VentaId.Value].Estado, regresaron[s0.VentaId.Value].Intentos));
        Assert.Equal((EstadoEnvioEleventa.Pendiente, 1), (regresaron[s1.VentaId.Value].Estado, regresaron[s1.VentaId.Value].Intentos));
        Assert.False(regresaron.ContainsKey(s2.VentaId!.Value));

        // Mientras, la segunda se cancela aquí. Cuando el puente vuelve a escribir, la busca en eleventa (está) antes de descartarla, y
        // su cancelación va para allá. (Sin la regla, con cero intentos, se descartaba sin buscarla y eleventa la seguía teniendo vendida.)
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{s1.VentaId}/cancelacion",
            new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió")));
        await p.DetenerAsync();
        c = c with { Http = p.Arrancar() };
        await MandarAsync(c);

        // Nada dos veces: los mismos tres tickets, cada venta otra vez con el suyo, y la cancelada con su devolución en eleventa.
        Assert.Equal(3, await TicketsDeLaCaja2Async(e));
        var despues = await VentasEnLaBandejaAsync(p.Servicio);
        Assert.Equal((EstadoEnvioEleventa.Enviada, (int?)ticket0), (despues[s0.VentaId.Value].Estado, despues[s0.VentaId.Value].TicketEleventa));
        Assert.Equal((EstadoEnvioEleventa.Enviada, (int?)ticket1), (despues[s1.VentaId.Value].Estado, despues[s1.VentaId.Value].TicketEleventa));
        await using (var db = p.Servicio.Base())
            Assert.Equal(EstadoEnvioEleventa.Enviada,
                (await db.EnviosAEleventa.AsNoTracking().SingleAsync(x => x.Tipo == TipoEnvioEleventa.Cancelacion && x.VentaId == s1.VentaId)).Estado);
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM DEVOLUCIONES WHERE TICKET_ID = @t", ("t", ticket1))));
        Assert.Equal(0, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM DEVOLUCIONES WHERE TICKET_ID = @t", ("t", ticket0))));
    }

    /// <summary>
    /// Revisión del PR #136. Una venta de la caja 2 llegó a eleventa con su folio (ya impreso en su ticket), y el respaldo la tiene
    /// pendiente. Otra caja de eleventa cobró después con el mismo folio. Al restaurar, el puente la vuelve a mandar y la encuentra por
    /// su marca: antes le cambiaba el folio en eleventa (el ticket impreso decía otro); ahora se queda con el suyo y queda como repetido,
    /// para revisarlo como los de la revisión tardía.
    /// </summary>
    [FactConFirebird]
    public async Task Despues_de_restaurar_el_reenvio_no_cambia_en_eleventa_el_folio_ya_impreso()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var p = await PrincipalQueSeRestaura.CrearAsync(true, Ajustes(e.Servidor));
        var c = await AbrirAsync(p.Arrancar(), await MapearAsync(p.Servicio));
        var s0 = await CobrarAsync(c);
        await MandarAsync(c);
        int ticket, folio;
        await using (var db = p.Servicio.Base())
        {
            var envio = await db.EnviosAEleventa.AsNoTracking().SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == s0.VentaId);
            (ticket, folio) = (envio.TicketEleventa!.Value, envio.FolioEleventa!.Value);
        }
        var archivo = p.Respaldar(HoraMexico.ALocal(DateTime.UtcNow).AddMinutes(-1), "ahora-1010");
        await using (var respaldo = new PosDbContext(BaseDeDatos.Opciones($"Data Source={Path.Combine(p.CarpetaRespaldos, archivo)};Pooling=False")))
            await respaldo.EnviosAEleventa.Where(x => x.Tipo == TipoEnvioEleventa.Venta).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Estado, EstadoEnvioEleventa.Pendiente).SetProperty(x => x.Intentos, 0).SetProperty(x => x.EnviadaUtc, (DateTime?)null)
                .SetProperty(x => x.TicketEleventa, (int?)null).SetProperty(x => x.FolioEleventa, (int?)null));
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN, TOTAL) VALUES (@folio, 1, 1, -1, 'f', @cuando, 45.5)",
            ("folio", folio), ("cuando", HoraLocal(0)));

        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        await Leer<RespaldosDto>(await c.Http.PostAsJsonAsync($"api/cajas/{c.Datos.Id}/configuracion/respaldos/restaurar",
            new SolicitudRestaurar(dueno.UsuarioId, "3333", archivo)));
        await p.Reinicio.Pedido.WaitAsync(TimeSpan.FromSeconds(20));
        await p.DetenerAsync();
        c = c with { Http = p.Arrancar() };
        await MandarAsync(c);

        Assert.Equal(folio, Convert.ToInt32(await e.ValorAsync("SELECT FOLIO FROM VENTATICKETS WHERE ID = @t", ("t", ticket))));
        Assert.Equal(2, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE FOLIO = @f", ("f", folio))));
        Assert.Equal(1, await TicketsDeLaCaja2Async(e));
        await using (var db = p.Servicio.Base())
        {
            var envio = await db.EnviosAEleventa.AsNoTracking().SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == s0.VentaId);
            Assert.Equal((EstadoEnvioEleventa.Enviada, (int?)ticket, (int?)folio, true), (envio.Estado, envio.TicketEleventa, envio.FolioEleventa, envio.FolioRepetido));
        }
    }
}
