using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using PapeleriaPos.Servicio;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El piloto alfa (decisión del dueño del 2026-10-25), de punta a punta: cada operación de la caja 2 va a eleventa a nombre de
/// quien la hizo, con el número de eleventa que tenía al ponerse en la bandeja; quien no viene de eleventa, a nombre del cajero de
/// la configuración, y queda en el registro del servicio. También queda ahí si el turno de eleventa es de otro cajero, y eso se ve
/// además en la pantalla de la caja 2 (el chip «Eleventa», revisión del PR #144): el registro nadie lo lee en la tienda.
/// </summary>
public class CajeroEnEleventaApiTests
{
    private const int Beto = 9;
    private const int Enrique = 11;
    private const int Otro = 12;

    /// <summary>El eleventa de prueba con Beto (9), Enrique (11) y otro usuario (12) además de Ana (7, la de la configuración).</summary>
    private static async Task<EleventaDePrueba> EleventaConUsuariosAsync(bool abierta)
    {
        var (e, _) = await EleventaAsync(abierta);
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO) VALUES (9, 'Beto Gómez', 'beto', '1')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO) VALUES (11, 'Enrique Díaz', 'enrique', '1')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO) VALUES (12, 'Otro', 'otro', '1')");
        return e;
    }

    /// <summary>El servicio del piloto, con el registro del puente a la vista.</summary>
    private static async Task<(ServicioDePrueba Servicio, RegistroDePrueba<EnvioVentasEleventa> Registro)> ServicioAsync(EleventaDePrueba e)
    {
        var registro = new RegistroDePrueba<EnvioVentasEleventa>();
        var servicio = await ServicioDePrueba.CrearAsync(s => s.AddSingleton<ILogger<EnvioVentasEleventa>>(registro), Ajustes(e.Servidor));
        return (servicio, registro);
    }

    /// <summary>A quién de eleventa corresponde un usuario de aquí (como lo deja la importación o la lectura).</summary>
    private static async Task LigarAsync(ServicioDePrueba servicio, string usuario, int? eleventa, string? nombre = null)
    {
        await using var db = servicio.Base();
        var suyo = await db.Usuarios.SingleAsync(u => u.NombreUsuario == usuario);
        suyo.EleventaId = eleventa;
        if (nombre is not null)
            suyo.Nombre = nombre;
        await db.SaveChangesAsync();
    }

    private static async Task<List<int>> CajerosDeLosTicketsAsync(EleventaDePrueba e) =>
        (await e.FilasAsync("SELECT CAJERO_ID FROM VENTATICKETS WHERE ESTA_ABIERTO = 'f' AND FOLIO > 41 ORDER BY FOLIO")).Select(f => Convert.ToInt32(f["CAJERO_ID"])).ToList();

    private static async Task<List<int>> UsuariosDelKardexAsync(EleventaDePrueba e, string descripcion) =>
        (await e.FilasAsync("SELECT USUARIO_ID FROM INVENTARIO_HISTORIAL WHERE DESCRIPCION STARTING WITH @d ORDER BY ID", ("d", descripcion)))
            .Select(f => Convert.ToInt32(f["USUARIO_ID"])).ToList();

    [FactConFirebird]
    public async Task Cada_operacion_va_a_eleventa_a_nombre_de_quien_la_hizo_y_sin_numero_de_eleventa_al_de_la_configuracion()
    {
        await using var e = await EleventaConUsuariosAsync(abierta: true);
        var (servicio, registro) = await ServicioAsync(e);
        await using var _ = servicio;
        var variante = await MapearAsync(servicio);
        // El cajero de prueba es Beto en eleventa, y el encargado, Enrique; el dueño se dio de alta aquí.
        await LigarAsync(servicio, "cajero", Beto);
        await LigarAsync(servicio, "encargado", Enrique);
        var c = await AbrirAsync(servicio, variante);
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));

        var deBeto = await CobrarAsync(c);
        var delDueno = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, dueno.UsuarioId,
            [new LineaSolicitada(c.Variante, null, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 100000)])));
        // Enrique cancela la venta de Beto.
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{deBeto.VentaId}/cancelacion",
            new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió")));
        var envios = await MandarAsync(c);

        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        // La venta de Beto a su nombre; la del dueño (que no viene de eleventa), a nombre de Ana, la de la configuración.
        Assert.Equal([Beto, 7], await CajerosDeLosTicketsAsync(e));
        Assert.Equal([Beto, 7], await UsuariosDelKardexAsync(e, "Venta #"));
        // La cancelación, a nombre de Enrique: su usuario de eleventa en DEVOLUCIONES y su número en la entrada al inventario.
        Assert.Equal("enrique", ((string)(await e.ValorAsync("SELECT CAJERO FROM DEVOLUCIONES"))!).Trim());
        Assert.Equal([Enrique], await UsuariosDelKardexAsync(e, "Devoluci"));
        // En la bandeja, a nombre de quién iba cada cosa al ponerse ahí.
        await using (var db = servicio.Base())
            Assert.Equal([(TipoEnvioEleventa.Venta, (int?)Beto), (TipoEnvioEleventa.Venta, null), (TipoEnvioEleventa.Cancelacion, Enrique)],
                (await db.EnviosAEleventa.AsNoTracking().OrderBy(x => x.CreadoUtc).ToListAsync()).Select(x => (x.Tipo, x.CajeroEleventa)));
        // Lo del dueño queda dicho en el registro; y lo de Beto y Enrique también, porque el turno de eleventa es de Ana.
        var avisos = registro.De(LogLevel.Warning).ToList();
        Assert.Single(avisos, a => a.Contains("a nombre del cajero de la configuración") && a.Contains("Dueño de prueba no viene de eleventa"));
        Assert.Contains(avisos, a => a.Contains("de Cajero de prueba quedó en eleventa a nombre del cajero 9 en un turno del cajero 7"));
        Assert.Contains(avisos, a => a.Contains("de Encargado de prueba quedó en eleventa a nombre del cajero 11 en un turno del cajero 7"));
        Assert.Equal(delDueno.FolioEleventa, Convert.ToInt32(await e.ValorAsync("SELECT MAX(FOLIO) FROM VENTATICKETS")));
        // En la caja 2, lo último que quedó en el turno de otro (la cancelación de Enrique; Ana no tiene usuario aquí).
        var pantalla = await Leer<PuenteEleventaDto>(await c.Http.GetAsync("api/eleventa/pantalla"));
        var turnoDeOtro = Assert.Single(pantalla.Avisos, a => a.Contains("en el turno"));
        Assert.Contains("lo que hizo Encargado de prueba en la caja 2 quedó en eleventa a su nombre, pero en el turno del usuario 7 de eleventa", turnoDeOtro);
    }

    [FactConFirebird]
    public async Task Lo_que_ya_esta_en_la_bandeja_no_cambia_de_cajero_aunque_cambie_el_usuario_despues()
    {
        // La caja 2 todavía no se abre en eleventa: lo cobrado espera en la bandeja.
        await using var e = await EleventaConUsuariosAsync(abierta: false);
        var (servicio, registro) = await ServicioAsync(e);
        await using var _ = servicio;
        var variante = await MapearAsync(servicio);
        await LigarAsync(servicio, "cajero", Beto);
        var c = await AbrirAsync(servicio, variante);

        var ticket = await CobrarAsync(c);
        Assert.Null(ticket.FolioEleventa);
        // Después, eleventa le cambia el nombre y su número de aquí cambia (otra importación, una corrección a mano).
        await LigarAsync(servicio, "cajero", Otro, nombre: "Roberto Gómez");
        var entrada = await c.Http.PostAsJsonAsync($"api/turnos/{c.Turno.Id}/movimientos",
            new SolicitudMovimiento(PapeleriaPos.Nucleo.Turnos.TipoMovimientoCaja.Entrada, 20000, "Cambio", c.Sesion.UsuarioId));
        Assert.True(entrada.IsSuccessStatusCode, await entrada.Content.ReadAsStringAsync());
        // Ahora sí se abre en eleventa, con Beto, y se manda lo que esperaba.
        await e.AbrirCajaAsync(CajaEleventa, cajero: Beto, HoraLocal(30), fondo: 300);
        var envios = await MandarAsync(c);

        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        // La venta, a nombre de quien la cobró cuando se cobró; la entrada (de después), con su número de ahora.
        Assert.Equal([Beto], await CajerosDeLosTicketsAsync(e));
        await using (var db = servicio.Base())
            Assert.Equal([(TipoEnvioEleventa.Venta, (int?)Beto), (TipoEnvioEleventa.MovimientoDeCaja, Otro)],
                (await db.EnviosAEleventa.AsNoTracking().OrderBy(x => x.CreadoUtc).ToListAsync()).Select(x => (x.Tipo, x.CajeroEleventa)));
        // Lo de Beto fue en un turno de Beto: no hay nada que decir, ni en el registro ni en la caja.
        Assert.DoesNotContain(registro.De(LogLevel.Warning), a => a.Contains("en un turno del cajero"));
        Assert.DoesNotContain((await Leer<PuenteEleventaDto>(await c.Http.GetAsync("api/eleventa/pantalla"))).Avisos, a => a.Contains("en el turno"));
    }
}
