using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Los respaldos de eleventa en la caja 2, contra el Firebird de prueba (docs/migracion-eleventa.md, «Los respaldos de
/// eleventa»): si eleventa se restaura a un respaldo anterior, el puente deja de escribir y de aplicar la lectura, la
/// pantalla lo dice, y al reanudarlo a mano lo que se perdió allá queda por revisar. Y el respaldo propio del día va antes
/// de lo primero que llega a eleventa (con un gbak de mentiras).
/// </summary>
public sealed class RestauracionDelPilotoTests
{
    private static async Task<PuenteEleventaDto> PantallaAsync(Caja c) => await Leer<PuenteEleventaDto>(await c.Http.GetAsync("api/eleventa/pantalla"));

    private static async Task<int> TicketEnEleventaAsync(ServicioDePrueba servicio, TicketDto ticket)
    {
        await using var db = servicio.Base();
        return (await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == ticket.VentaId)).TicketEleventa!.Value;
    }

    private static async Task<int> TicketsDeLaCaja2Async(EleventaDePrueba e) =>
        Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE CAJA_ID = 4 AND ESTA_ABIERTO = 'f'"));

    /// <summary>Eleventa regresa a un respaldo de antes de ese ticket: ya no está y su contador regresó.</summary>
    private static async Task RestaurarAntesDeAsync(EleventaDePrueba e, int ticket)
    {
        await e.EjecutarAsync("DELETE FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t", ("t", ticket));
        await e.EjecutarAsync("DELETE FROM VENTATICKETS WHERE ID = @t", ("t", ticket));
        await e.EjecutarAsync($"SET GENERATOR GEN_VENTATICKETS_ID TO {ticket - 1}");
    }

    private static async Task<EstadoEleventaDto> LecturaAsync(Caja c)
    {
        var antes = (await c.Http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!.Lecturas;
        await c.Http.PostAsync("api/eleventa/leer", null);
        return await EsperarAsync(async () => (await c.Http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!, x => x.Lecturas > antes);
    }

    [FactConFirebird]
    public async Task Si_eleventa_se_restaura_la_caja_2_deja_de_escribir_y_al_reanudar_lo_perdido_queda_por_revisar()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var primera = await CobrarAsync(c);
        await MandarAsync(c);
        var segunda = await CobrarAsync(c);
        await MandarAsync(c);
        var (ticket, folio) = (await TicketEnEleventaAsync(servicio, segunda), await FolioAsync(servicio, segunda));

        // Eleventa regresa a un respaldo de entre las dos y después cobra en la principal con los mismos números.
        await RestaurarAntesDeAsync(e, ticket);
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN, CREADO_EN) VALUES (@f, 1, 1, -1, 'f', @c, @c)",
            ("f", folio), ("c", HoraLocal(0)));
        // Lo siguiente lo ve al ir a escribir (al cobrar, o en la vuelta si el cobro no la esperó).
        var tercera = await CobrarAsync(c);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        var pantalla = await EsperarAsync(() => PantallaAsync(c), p => p.Detenido is not null);

        // No se escribió: el ticket sale con el folio de la caja, la venta espera y la pantalla de la caja 2 lo dice.
        Assert.Null(tercera.FolioEleventa);
        Assert.Equal(1, await TicketsDeLaCaja2Async(e));
        Assert.Contains($"el ticket {ticket} de eleventa ya no es el #{folio} que mandó el", pantalla.Detenido);
        var esperando = (await EnviosAsync(c)).PorAtender.Single();
        Assert.Equal(EstadoEnvioEleventa.Pendiente, esperando.Estado);
        Assert.StartsWith("Eleventa ya no tiene lo que le mandó la caja 2 (¿se restauró un respaldo?)", esperando.Error);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        Assert.Equal(1, await TicketsDeLaCaja2Async(e));

        // El dueño revisó eleventa y lo reanuda: lo que se perdió allá queda por revisar, y lo demás vuelve a pasar.
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var reanudado = await Leer<PuenteEleventaDto>(await c.Http.PostAsJsonAsync("api/eleventa/reanudar",
            new SolicitudReanudarPuente(dueno.UsuarioId, c.Datos.Id, "Eleventa se restauró sola en la mañana")));
        Assert.Null(reanudado.Detenido);
        var perdida = Assert.Single(reanudado.PorRevisar);
        Assert.StartsWith("Eleventa ya no lo tiene: se perdió al restaurar un respaldo (se había mandado el", perdida.Motivo);
        await MandarAsync(c);

        Assert.Equal(2, await TicketsDeLaCaja2Async(e));
        await using var db = servicio.Base();
        var envios = await db.EnviosAEleventa.AsNoTracking().Where(x => x.Tipo == TipoEnvioEleventa.Venta).ToDictionaryAsync(x => x.VentaId!.Value);
        Assert.Equal(EstadoEnvioEleventa.Enviada, envios[primera.VentaId!.Value].Estado);
        // La perdida conserva el folio que salió impreso; su número de ticket en eleventa ya es de otro.
        Assert.Equal((EstadoEnvioEleventa.PorRevisar, (int?)null, folio), (envios[segunda.VentaId!.Value].Estado,
            envios[segunda.VentaId!.Value].TicketEleventa, envios[segunda.VentaId!.Value].FolioEleventa));
        Assert.Equal(EstadoEnvioEleventa.Enviada, envios[tercera.VentaId!.Value].Estado);
        var detencion = await db.Set<DetencionDelPuente>().SingleAsync();
        Assert.Equal((1, "Eleventa se restauró sola en la mañana", dueno.UsuarioId), (detencion.Perdidos, detencion.Nota, detencion.ReanudoId));
        var bitacora = await db.Bitacora.SingleAsync(b => b.Accion == AccionControlada.ConfigurarTienda && b.Detalle.StartsWith("Reanudó el puente con eleventa"));
        Assert.Contains("1 cosa que había mandado la caja 2 quedó por revisar", bitacora.Detalle);
    }

    [FactConFirebird]
    public async Task La_lectura_ve_la_restauracion_no_aplica_nada_y_al_reanudar_vuelve_a_aplicar()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await EsperarPrimeraLecturaAsync(c);
        var venta = await CobrarAsync(c);
        await MandarAsync(c);
        var folio = await FolioAsync(servicio, venta);

        // Eleventa regresa a un respaldo de antes de la venta, y ahí la pluma cuesta otra cosa.
        await RestaurarAntesDeAsync(e, await TicketEnEleventaAsync(servicio, venta));
        await e.EjecutarAsync("UPDATE PRODUCTOS SET PFINAL = 11 WHERE ID = 10");
        var leida = await LecturaAsync(c);

        Assert.False(leida.Bien);
        Assert.EndsWith("No se aplicó la lectura y el puente se detuvo.", leida.Error);
        Assert.Contains($"ya no está el ticket #{folio} que mandó el", (await PantallaAsync(c)).Detenido);
        Assert.Equal(900, await PrecioAsync(servicio, c.Variante));
        // Detenido, la siguiente tampoco aplica.
        Assert.StartsWith("El puente con eleventa está detenido desde el", (await LecturaAsync(c)).Error);
        Assert.Equal(900, await PrecioAsync(servicio, c.Variante));

        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        await Leer<PuenteEleventaDto>(await c.Http.PostAsJsonAsync("api/eleventa/reanudar", new SolicitudReanudarPuente(dueno.UsuarioId, c.Datos.Id, null)));
        await LeerAsync(c);

        Assert.Equal(1100, await PrecioAsync(servicio, c.Variante));
        Assert.Null((await PantallaAsync(c)).Detenido);
    }

    [FactConFirebird]
    public async Task El_respaldo_del_dia_va_antes_de_la_primera_venta_que_llega_a_eleventa()
    {
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        using var gbak = new GbakDePrueba();
        int? enEleventaAlRespaldar = null;
        gbak.AlRespaldar = async () => enEleventaAlRespaldar = await TicketsDeLaCaja2Async(e);
        await using var servicio = await ServicioDePrueba.CrearAsync(s => s.AddSingleton<IEjecutorGbak>(gbak),
            [.. Ajustes(e.Servidor), ("Eleventa:Gbak", gbak.Ruta), ("Eleventa:CarpetaRespaldos", gbak.Respaldos), ("Eleventa:RespaldoObligatorio", "true")]);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        // La primera del día no espera a gbak (sale con el folio de la caja); la bandeja respalda y después la manda. Lo
        // que sigue en el día ya no respalda.
        var primera = await CobrarAsync(c);
        await MandarAsync(c);
        await CobrarAsync(c);
        await MandarAsync(c);

        Assert.Equal(0, enEleventaAlRespaldar);
        Assert.Equal(2, await TicketsDeLaCaja2Async(e));
        Assert.Null(primera.FolioEleventa);
        var llamada = Assert.Single(gbak.Llamadas);
        Assert.Equal(RespaldoDeEleventa.Argumentos(e.Servidor, Path.Combine(gbak.Respaldos, $"eleventa-{HoraLocal(0):yyyy-MM-dd}.fbk.tmp")), llamada.Argumentos);
        Assert.Equal(e.Servidor.Clave, llamada.Entorno["ISC_PASSWORD"]);
    }

    private static async Task<int?> FolioAsync(ServicioDePrueba servicio, TicketDto ticket)
    {
        await using var db = servicio.Base();
        return (await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Venta && x.VentaId == ticket.VentaId)).FolioEleventa;
    }

    /// <summary>El precio de menudeo de la variante (el que la lectura pone al día).</summary>
    private static async Task<long> PrecioAsync(ServicioDePrueba servicio, Guid variante)
    {
        await using var db = servicio.Base();
        return (await db.Precios.AsNoTracking().Where(p => p.VarianteId == variante && p.PresentacionId == null && p.SucursalId == null).ToListAsync())
            .Single(p => p.CantidadMinima == Cantidad.Unidades(1)).PrecioCentavos;
    }
}
