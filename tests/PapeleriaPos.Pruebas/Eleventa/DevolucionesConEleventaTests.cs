using PapeleriaPos.Eleventa;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// Devoluciones de ida y vuelta con eleventa (docs/migracion-eleventa.md, «Devoluciones de ida y vuelta»), contra un Firebird
/// de prueba local: lo que se lee en vivo de un ticket, y cuándo escribir una devolución es un choque que queda por revisar.
/// </summary>
public class DevolucionesConEleventaTests
{
    private static readonly CajaEnEleventa Piloto = new(CajaId: 4, CajeroId: 7, AbreTurno: false);
    private static readonly TurnoParaEleventa TurnoNuevo = new(new DateTime(2026, 3, 9, 8, 0, 0), DineroInicial: 500);
    private static readonly DateTime Apertura = new(2026, 3, 9, 8, 0, 0);

    private static async Task<EleventaDePrueba> TiendaAsync()
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (1, 'Principal', '10.0.0.10')");
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO) VALUES (7, 'Ana Ruiz', 'cajera2')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, @nombre)", ("nombre", EnvioEleventa.DepartamentoComunes));
        await e.ProductoAsync(10, "CUAD-PRO", "Cuaderno profesional", depto: 5, costo: 18, precio: 30, existencia: 12);
        await e.ProductoAsync(11, "IMP-BN", "Impresiones", depto: 5, costo: 2, precio: 10, existencia: 500, tipoVenta: 'D');
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN) VALUES (41, 1, 1, -1, 'f', @cuando)",
            ("cuando", Apertura.AddMinutes(-30)));
        return e;
    }

    private static VentaParaEleventa Venta(DateTime cobrada, params ArticuloParaEleventa[] articulos) =>
        new(cobrada.AddMinutes(-1), cobrada, PagoCon: 100, TurnoNuevo, articulos);

    private static DevolucionParaEleventa Devolucion(int ticket, bool cancelacion, DateTime cuando, params ArticuloDevuelto[] articulos) =>
        new(ticket, cancelacion, cuando, TurnoNuevo, articulos);

    [FactConFirebird]
    public async Task La_devolucion_de_un_ticket_de_otro_turno_o_de_otra_caja_queda_por_revisar()
    {
        await using var e = await TiendaAsync();
        var primero = await e.AbrirCajaAsync(4, 7, Apertura, fondo: 300);
        var ayer = await EnvioEleventa.MandarAsync(e.Conexion, Piloto, Venta(Apertura.AddHours(2), new ArticuloParaEleventa(10, "Cuaderno", 2, 60)));
        await e.CortarAsync(primero, Apertura.AddHours(12));
        await e.AbrirCajaAsync(4, 7, Apertura.AddDays(1), fondo: 300);
        // La principal cobró uno el mismo día en su propio turno.
        await e.AbrirCajaAsync(1, 7, Apertura, fondo: 500);
        var principal = await EnvioEleventa.MandarAsync(e.Conexion, Piloto with { CajaId = 1 }, Venta(Apertura.AddHours(3), new ArticuloParaEleventa(10, "Cuaderno", 1, 30)));

        // Todavía no se ha visto cómo anota eleventa esas devoluciones: no se escriben, quedan por revisar.
        var deAyer = await Assert.ThrowsAsync<ChoqueConEleventa>(() => EnvioEleventa.DevolverAsync(e.Conexion, Piloto,
            Devolucion(ayer.TicketId, false, Apertura.AddDays(1).AddHours(1), new ArticuloDevuelto(10, "Cuaderno", 1, 30))));
        var deOtraCaja = await Assert.ThrowsAsync<ChoqueConEleventa>(() => EnvioEleventa.DevolverAsync(e.Conexion, Piloto,
            Devolucion(principal.TicketId, false, Apertura.AddDays(1).AddHours(1), new ArticuloDevuelto(10, "Cuaderno", 1, 30))));

        Assert.Equal((EnvioEleventa.DeOtroTurnoODeOtraCaja, EnvioEleventa.DeOtroTurnoODeOtraCaja), (deAyer.Message, deOtraCaja.Message));
        Assert.Equal(0, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM DEVOLUCIONES")));
        Assert.Equal(9, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);
    }

    [FactConFirebird]
    public async Task Lo_ya_cancelado_en_eleventa_es_un_choque_que_queda_por_revisar()
    {
        await using var e = await TiendaAsync();
        await e.AbrirCajaAsync(4, 7, Apertura, fondo: 300);
        var venta = await EnvioEleventa.MandarAsync(e.Conexion, Piloto, Venta(Apertura.AddHours(2), new ArticuloParaEleventa(10, "Cuaderno", 2, 60)));
        // Eleventa la canceló (aquí, con otra hora: es otra cancelación).
        await EnvioEleventa.DevolverAsync(e.Conexion, Piloto, Devolucion(venta.TicketId, true, Apertura.AddHours(3)));

        var choque = await Assert.ThrowsAsync<ChoqueConEleventa>(() => EnvioEleventa.DevolverAsync(e.Conexion, Piloto,
            Devolucion(venta.TicketId, false, Apertura.AddHours(4), new ArticuloDevuelto(10, "Cuaderno", 1, 30))));

        Assert.Equal($"En eleventa el ticket #{venta.Folio} ya está cancelado.", choque.Message);
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM DEVOLUCIONES")));
    }

    [FactConFirebird]
    public async Task Un_ticket_de_eleventa_se_lee_con_sus_articulos_y_sus_devoluciones_y_se_devuelve_por_su_renglon()
    {
        await using var e = await TiendaAsync();
        await e.AbrirCajaAsync(4, 7, Apertura, fondo: 300);
        // Se cobró en el eleventa de la caja 2, en el mismo turno: dos cuadernos, media hoja de impresiones y un común.
        var cobrado = await EnvioEleventa.MandarAsync(e.Conexion, Piloto, Venta(Apertura.AddHours(1),
            new ArticuloParaEleventa(10, "Cuaderno", 2, 60), new ArticuloParaEleventa(11, "Impresiones", 0.5m, 5), new ArticuloParaEleventa(null, "Recuerdo", 1, 15)));
        // Allá ya se devolvió un cuaderno.
        await EnvioEleventa.DevolverAsync(e.Conexion, Piloto, Devolucion(cobrado.TicketId, false, Apertura.AddHours(2), new ArticuloDevuelto(10, "Cuaderno", 1, 30)));

        var porFolio = await TicketsDeEleventa.PorFolioAsync(e.Conexion, cobrado.Folio);
        var ticket = (await TicketsDeEleventa.TicketAsync(e.Conexion, cobrado.TicketId))!;

        Assert.Equal([cobrado.TicketId], porFolio.Select(t => t.Id));
        Assert.Equal((cobrado.Folio, 4, "Caja 2", false, true, 50m, 80m), (ticket.Folio, ticket.CajaId, ticket.Caja, ticket.Cancelado, ticket.EnEfectivo,
            ticket.Total, ticket.Pagado));
        Assert.Equal(["Cuaderno profesional", "Impresiones", "Recuerdo"], ticket.Articulos.Select(a => a.Nombre));
        var cuaderno = ticket.Articulos[0];
        Assert.Equal((1m, 30m, 12m, 10), (cuaderno.Cantidad, cuaderno.Total, cuaderno.GananciaPorPieza, cuaderno.ProductoId));
        Assert.True(ticket.Articulos[1].AGranel);
        Assert.Null(ticket.Articulos[2].ProductoId);
        var devuelta = Assert.Single(ticket.Devoluciones);
        Assert.Equal((false, 30m, "cajera2", "Caja 2"), (devuelta.EsCancelacion, devuelta.Total, devuelta.Cajero, devuelta.Caja));
        Assert.Equal(new ArticuloDevueltoLeido("CUAD-PRO", "Cuaderno profesional", 1, 30, 10), Assert.Single(devuelta.Articulos));

        // La ida: el común por su renglón (aunque haya otro con el mismo nombre en otro ticket).
        var comun = ticket.Articulos[2];
        await EnvioEleventa.DevolverAsync(e.Conexion, Piloto, Devolucion(cobrado.TicketId, false, Apertura.AddHours(3),
            new ArticuloDevuelto(null, comun.Nombre, 1, 15, ArticuloEleventa: comun.Id)));

        var despues = (await TicketsDeEleventa.TicketAsync(e.Conexion, cobrado.TicketId))!;
        Assert.Equal(["Cuaderno profesional", "Impresiones"], despues.Articulos.Select(a => a.Nombre));
        Assert.Equal(2, despues.Devoluciones.Count);
    }
}
