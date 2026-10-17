using FirebirdSql.Data.FirebirdClient;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// Ver que eleventa se restauró a un respaldo anterior (docs/migracion-eleventa.md, «Los respaldos de eleventa»), contra un
/// Firebird de prueba local: solo con lo que el puente sabe que escribió, así que lo que eleventa hace siempre no da
/// falsas alarmas, y lo que se iba a escribir sobre una base restaurada no se escribe.
/// </summary>
public class RestauracionDeEleventaTests
{
    private static readonly CajaEnEleventa Piloto = new(CajaId: 4, CajeroId: 7, AbreTurno: false);
    private static readonly DateTime Apertura = new(2026, 3, 9, 8, 0, 0);
    private static readonly TurnoParaEleventa TurnoNuevo = new(Apertura, DineroInicial: 500);

    private static async Task<EleventaDePrueba> TiendaAsync()
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (1, 'Principal', '10.0.0.10')");
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO) VALUES (7, 'Ana Ruiz', 'cajera2')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, @nombre)", ("nombre", EnvioEleventa.DepartamentoComunes));
        await e.ProductoAsync(10, "CUAD-PRO", "Cuaderno profesional", depto: 5, costo: 18, precio: 30, existencia: 12);
        return e;
    }

    private static VentaParaEleventa Venta(DateTime cobrada, int cuadernos = 1) =>
        new(cobrada.AddMinutes(-1), cobrada, PagoCon: 100, TurnoNuevo, [new ArticuloParaEleventa(10, "Cuaderno", cuadernos, cuadernos * 30)]);

    /// <summary>Lo que la caja 2 anotó de lo que mandó: los tickets con su folio y su marca, el turno que adoptó y los contadores.</summary>
    private static HuellaDelPuente Huella(int turno, params (VentaEnEleventa Venta, VentaParaEleventa Mandada, DateTime EnviadaUtc)[] tickets) =>
        new(tickets.Select(t => new TicketDelPuente(t.Venta.TicketId, t.Venta.Folio, t.Mandada.CreadaLocal, t.EnviadaUtc)).ToList(),
            [new TurnoDeEleventa(turno, Apertura)],
            new Dictionary<string, int> { ["GEN_VENTATICKETS_ID"] = tickets.Max(t => t.Venta.TicketId), ["GEN_TURNOS_ID"] = turno });

    private static async Task<Restauracion?> RevisarAsync(EleventaDePrueba e, HuellaDelPuente huella)
    {
        await using var tx = await e.Conexion.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        });
        var r = await RestauracionDeEleventa.RevisarAsync(e.Conexion, tx, huella);
        await tx.CommitAsync();
        return r;
    }

    [FactConFirebird]
    public async Task Lo_que_eleventa_hace_siempre_no_es_una_restauracion()
    {
        await using var e = await TiendaAsync();
        var turno = await e.AbrirCajaAsync(4, 7, Apertura, fondo: 300);
        var primera = Venta(Apertura.AddHours(1), cuadernos: 2);
        var segunda = Venta(Apertura.AddHours(2));
        var huella = Huella(turno,
            (await EnvioEleventa.MandarAsync(e.Conexion, Piloto, primera), primera, DateTime.UtcNow.AddHours(-2)),
            (await EnvioEleventa.MandarAsync(e.Conexion, Piloto, segunda), segunda, DateTime.UtcNow.AddHours(-1)));

        // Eleventa cancela una, devuelve todo lo de la otra (su renglón se borra), cobra en la principal y corta el turno
        // (borra su ticket abierto).
        await EnvioEleventa.DevolverAsync(e.Conexion, Piloto, new DevolucionParaEleventa(huella.Tickets[0].Id, true, Apertura.AddHours(3), TurnoNuevo, []));
        await EnvioEleventa.DevolverAsync(e.Conexion, Piloto, new DevolucionParaEleventa(huella.Tickets[1].Id, false, Apertura.AddHours(3).AddMinutes(1), TurnoNuevo,
            [new ArticuloDevuelto(10, "Cuaderno", 1, 30)]));
        await e.AbrirCajaAsync(1, 7, Apertura, fondo: 500);
        await EnvioEleventa.MandarAsync(e.Conexion, Piloto with { CajaId = 1 }, Venta(Apertura.AddHours(4)));
        await e.CortarAsync(turno, Apertura.AddHours(10));

        Assert.Null(await RevisarAsync(e, huella));
        // Y se sigue escribiendo: la huella pasa en la misma transacción.
        await e.AbrirCajaAsync(4, 7, Apertura.AddHours(11), fondo: 300);
        var otra = await EnvioEleventa.MandarAsync(e.Conexion, Piloto with { Huella = huella }, Venta(Apertura.AddHours(12)));
        Assert.False(otra.YaEstaba);
    }

    [FactConFirebird]
    public async Task Si_ya_no_esta_lo_que_mando_o_los_numeros_regresaron_es_una_restauracion_y_no_se_escribe()
    {
        await using var e = await TiendaAsync();
        var turno = await e.AbrirCajaAsync(4, 7, Apertura, fondo: 300);
        var primera = Venta(Apertura.AddHours(1));
        var segunda = Venta(Apertura.AddHours(2));
        var enviadaUtc = HoraMexico.AUtc(Apertura.AddHours(2));
        var mandadas = new[]
        {
            (await EnvioEleventa.MandarAsync(e.Conexion, Piloto, primera), primera, HoraMexico.AUtc(Apertura.AddHours(1))),
            (await EnvioEleventa.MandarAsync(e.Conexion, Piloto, segunda), segunda, enviadaUtc),
        };
        var huella = Huella(turno, mandadas);
        var (ticket, folio) = (mandadas[1].Item1.TicketId, mandadas[1].Item1.Folio);

        // Eleventa regresa a un respaldo de entre las dos: la segunda ya no está y su contador regresó.
        await e.EjecutarAsync("DELETE FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t", ("t", ticket));
        await e.EjecutarAsync("DELETE FROM VENTATICKETS WHERE ID = @t", ("t", ticket));
        await e.EjecutarAsync($"SET GENERATOR GEN_VENTATICKETS_ID TO {ticket - 1}");
        var restaurada = await RevisarAsync(e, huella);

        Assert.NotNull(restaurada);
        Assert.Equal([$"ya no está el ticket #{folio} que mandó el 09/03 10:00",
            $"los números de los tickets regresaron: eleventa va en el {ticket - 1} y ya le había dado el {ticket} a la caja 2"], restaurada.Pruebas);
        Assert.Equal(enviadaUtc, restaurada.DesdeUtc);

        // Lo siguiente no se escribe: eleventa ya no cuadra con lo que mandó la caja 2.
        var antes = Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS"));
        var choque = await Assert.ThrowsAsync<EleventaRestaurada>(() => EnvioEleventa.MandarAsync(e.Conexion, Piloto with { Huella = huella }, Venta(Apertura.AddHours(3))));
        Assert.Equal(RestauracionDeEleventa.Texto(restaurada), choque.Message);
        Assert.Equal(antes, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS")));

        // Después eleventa cobra en la principal con el mismo número: ese ticket ya es otro.
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN, CREADO_EN) VALUES (@f, 1, 1, -1, 'f', @c, @c)",
            ("f", folio + 1), ("c", Apertura.AddHours(4)));
        var reusado = await RevisarAsync(e, huella);
        Assert.Equal([$"el ticket {ticket} de eleventa ya no es el #{folio} que mandó el 09/03 10:00 (ahora es otro, con el folio {folio + 1})"], reusado!.Pruebas);
    }

    [FactConFirebird]
    public async Task Un_turno_adoptado_que_ya_no_es_el_mismo_tambien_es_una_restauracion()
    {
        await using var e = await TiendaAsync();
        var turno = await e.AbrirCajaAsync(4, 7, Apertura, fondo: 300);
        var venta = Venta(Apertura.AddHours(1));
        var huella = Huella(turno, (await EnvioEleventa.MandarAsync(e.Conexion, Piloto, venta), venta, DateTime.UtcNow));

        await e.EjecutarAsync("UPDATE TURNOS SET INICIO_EN = @inicio WHERE ID = @t", ("inicio", Apertura.AddDays(-1)), ("t", turno));
        await e.EjecutarAsync($"SET GENERATOR GEN_TURNOS_ID TO {turno - 1}");

        Assert.Equal([$"el turno {turno} de eleventa ya no es el que empezó el 09/03 08:00",
            $"los números de los turnos regresaron: eleventa va en el {turno - 1} y ya le había dado el {turno} a la caja 2"], (await RevisarAsync(e, huella))!.Pruebas);
    }
}
