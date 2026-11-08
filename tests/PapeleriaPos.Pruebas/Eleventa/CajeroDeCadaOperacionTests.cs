using PapeleriaPos.Eleventa;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// Cada operación de la caja 2 se anota en eleventa a nombre de quien la hizo (el piloto alfa, decisión del dueño del
/// 2026-10-25): la venta (<c>VENTATICKETS.CAJERO_ID</c> y el usuario de su salida de inventario), la devolución (su
/// <c>CAJERO</c>, con el <c>USUARIO</c> de quien devolvió, como lo anota eleventa, y el usuario de su entrada al inventario) y
/// el turno que abre el puente fuera del piloto. Sin cajero, el de la caja (<c>Eleventa:Cajero</c>). El turno de eleventa no
/// se toca: el puente dice de quién es, porque eleventa nunca vende a nombre de otro en un turno abierto.
/// </summary>
public class CajeroDeCadaOperacionTests
{
    /// <summary>La caja 2 de eleventa; su cajero de la configuración es Ana (7).</summary>
    private static readonly CajaEnEleventa Caja = new(CajaId: 4, CajeroId: 7, Ip: "10.0.0.20");

    /// <summary>En el piloto el turno lo abre eleventa: el puente no abre ninguno.</summary>
    private static readonly CajaEnEleventa Piloto = Caja with { AbreTurno = false };

    private static readonly DateTime Abierto = new(2026, 3, 9, 8, 0, 0);
    private static readonly TurnoParaEleventa Turno = new(Abierto, DineroInicial: 500);
    private const int Ana = 7;
    private const int Beto = 9;

    private static async Task<EleventaDePrueba> TiendaAsync()
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO) VALUES (7, 'Ana Ruiz', 'cajera2', '1')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO) VALUES (9, 'Beto Gómez', 'beto', '1')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, @nombre)", ("nombre", EnvioEleventa.DepartamentoComunes));
        await e.ProductoAsync(10, "CUAD-PRO", "Cuaderno profesional", depto: 5, costo: 18, precio: 30, existencia: 12);
        return e;
    }

    private static VentaParaEleventa Venta(DateTime cobrada, int? cajero, decimal cuadernos = 2) =>
        new(cobrada.AddMinutes(-2), cobrada, PagoCon: 100, Turno, [new ArticuloParaEleventa(10, "Cuaderno", cuadernos, cuadernos * 30)], cajero);

    private static async Task<int> CajeroDelTicketAsync(EleventaDePrueba e, int ticket) =>
        Convert.ToInt32(await e.ValorAsync("SELECT CAJERO_ID FROM VENTATICKETS WHERE ID = @id", ("id", ticket)));

    private static async Task<List<int>> UsuariosDelKardexAsync(EleventaDePrueba e, string descripcion) =>
        (await e.FilasAsync("SELECT USUARIO_ID FROM INVENTARIO_HISTORIAL WHERE DESCRIPCION STARTING WITH @d ORDER BY ID", ("d", descripcion)))
            .Select(f => Convert.ToInt32(f["USUARIO_ID"])).ToList();

    [FactConFirebird]
    public async Task La_venta_va_a_nombre_de_quien_la_cobro_y_el_turno_de_eleventa_sigue_siendo_de_quien_lo_abrio()
    {
        await using var e = await TiendaAsync();
        // Eleventa abrió la caja 2 con Ana; vende Beto en la caja nueva.
        var turno = await e.AbrirCajaAsync(4, cajero: Ana, Abierto, fondo: 500);

        var r = await EnvioEleventa.MandarAsync(e.Conexion, Piloto, Venta(Abierto.AddHours(1), Beto));

        Assert.Equal(Beto, await CajeroDelTicketAsync(e, r.TicketId));
        Assert.Equal([Beto], await UsuariosDelKardexAsync(e, "Venta #"));
        // El turno es de Ana, y el puente lo dice para que quede en su registro.
        Assert.Equal(Ana, Convert.ToInt32(await e.ValorAsync("SELECT ID_CAJERO FROM TURNOS WHERE ID = @t", ("t", turno))));
        Assert.Equal(Ana, r.CajeroDelTurno);
    }

    [FactConFirebird]
    public async Task Sin_cajero_va_a_nombre_del_de_la_caja()
    {
        await using var e = await TiendaAsync();
        await e.AbrirCajaAsync(4, cajero: Ana, Abierto, fondo: 500);

        var r = await EnvioEleventa.MandarAsync(e.Conexion, Piloto, Venta(Abierto.AddHours(1), cajero: null));

        Assert.Equal(Ana, await CajeroDelTicketAsync(e, r.TicketId));
        Assert.Equal([Ana], await UsuariosDelKardexAsync(e, "Venta #"));
        Assert.Equal(Ana, r.CajeroDelTurno);
    }

    /// <summary>
    /// Fuera del piloto, sin turno abierto el puente lo abre como eleventa: a nombre de quien hizo lo primero (en eleventa lo abre
    /// quien entra a la caja), con su ticket abierto; también para una entrada de efectivo.
    /// </summary>
    [FactConFirebird]
    public async Task El_turno_que_abre_el_puente_es_de_quien_hizo_lo_primero()
    {
        await using var e = await TiendaAsync();

        var r = await EnvioEleventa.MandarAsync(e.Conexion, Caja, Venta(Abierto.AddHours(1), Beto));

        var turno = (await e.FilasAsync("SELECT ID, ID_CAJERO FROM TURNOS")).Single();
        Assert.Equal(Beto, Convert.ToInt32(turno["ID_CAJERO"]));
        Assert.Equal(Beto, Convert.ToInt32(await e.ValorAsync("SELECT CAJERO_ID FROM VENTATICKETS WHERE ESTA_ABIERTO = 't'")));
        Assert.Equal(Beto, r.CajeroDelTurno);

        // Se cierra; la entrada de efectivo de otro día abre el siguiente, a nombre de quien la hizo.
        await e.CortarAsync(Convert.ToInt32(turno["ID"]), Abierto.AddHours(10));
        var entrada = await EnvioEleventa.MoverEfectivoAsync(e.Conexion, Caja,
            new MovimientoParaEleventa(true, 200, "Cambio", Abierto.AddDays(1), Turno with { AbiertoLocal = Abierto.AddDays(1) }, Ana));
        Assert.Equal(Ana, Convert.ToInt32(await e.ValorAsync("SELECT ID_CAJERO FROM TURNOS WHERE TERMINO_EN IS NULL")));
        Assert.Equal(Ana, entrada.CajeroDelTurno);
    }

    [FactConFirebird]
    public async Task La_devolucion_lleva_el_usuario_de_quien_devolvio_como_lo_anota_eleventa()
    {
        await using var e = await TiendaAsync();
        await e.AbrirCajaAsync(4, cajero: Ana, Abierto, fondo: 500);
        var cobrada = Abierto.AddHours(1);
        var venta = await EnvioEleventa.MandarAsync(e.Conexion, Piloto, Venta(cobrada, Beto, cuadernos: 4));

        // Beto devuelve un cuaderno; después, alguien que no viene de eleventa (va a nombre del de la caja), otro.
        var deBeto = await EnvioEleventa.DevolverAsync(e.Conexion, Piloto, new DevolucionParaEleventa(venta.TicketId, false, cobrada.AddMinutes(10), Turno,
            [new ArticuloDevuelto(10, "Cuaderno", 1, 30)], Beto));
        await EnvioEleventa.DevolverAsync(e.Conexion, Piloto, new DevolucionParaEleventa(venta.TicketId, false, cobrada.AddMinutes(20), Turno,
            [new ArticuloDevuelto(10, "Cuaderno", 1, 30)]));

        // En DEVOLUCIONES, como texto: el USUARIO (con el que se entra a eleventa), no el nombre completo.
        Assert.Equal(["beto", "cajera2"], (await e.FilasAsync("SELECT CAJERO FROM DEVOLUCIONES ORDER BY ID")).Select(f => ((string)f["CAJERO"]!).Trim()));
        Assert.Equal([Beto, Ana], await UsuariosDelKardexAsync(e, "Devoluci"));
        Assert.Equal(Ana, deBeto.CajeroDelTurno);
    }
}
