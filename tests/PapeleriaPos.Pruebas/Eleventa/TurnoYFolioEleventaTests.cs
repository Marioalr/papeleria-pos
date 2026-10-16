using PapeleriaPos.Eleventa;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// El piloto en la caja 2 (docs/migracion-eleventa.md, «Lo que falta para el piloto»): lo que la caja nueva manda va al
/// turno de eleventa de su hora (el que estaba abierto entonces, o el siguiente), nunca abre otro y, si eleventa ya cortó
/// ese turno, queda por revisar; y el folio que eleventa repite entre cajas. Corren contra un Firebird de prueba local.
/// </summary>
public class TurnoYFolioEleventaTests
{
    /// <summary>La caja 2 en el piloto: el turno lo abre eleventa, no el puente.</summary>
    private static readonly CajaEnEleventa Piloto = new(CajaId: 4, CajeroId: 7, AbreTurno: false);

    private static readonly TurnoParaEleventa TurnoNuevo = new(new DateTime(2026, 3, 9, 8, 0, 0), DineroInicial: 500);
    private static readonly ArticuloParaEleventa Cuaderno = new(10, "Cuaderno", 1, 30);

    private static async Task<EleventaDePrueba> TiendaAsync()
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (1, 'Principal', '10.0.0.10')");
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO) VALUES (7, 'Ana Ruiz', 'cajera2')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, @nombre)", ("nombre", EnvioEleventa.DepartamentoComunes));
        await e.ProductoAsync(10, "CUAD-PRO", "Cuaderno profesional", depto: 5, costo: 18, precio: 30, existencia: 12);
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN) VALUES (41, 1, 1, -1, 'f', @cuando)",
            ("cuando", new DateTime(2026, 3, 9, 7, 30, 0)));
        return e;
    }

    private static VentaParaEleventa Venta(DateTime cobrada, params ArticuloParaEleventa[] articulos) =>
        new(cobrada.AddMinutes(-1), cobrada, PagoCon: 100, TurnoNuevo, articulos);

    private static async Task<int> CuantosAsync(EleventaDePrueba e, string sql, params (string, object?)[] parametros) =>
        Convert.ToInt32(await e.ValorAsync(sql, parametros));

    [FactConFirebird]
    public async Task Lo_de_antes_del_corte_de_eleventa_queda_por_revisar_y_lo_de_despues_va_al_turno_siguiente()
    {
        await using var e = await TiendaAsync();
        var primero = await e.AbrirCajaAsync(4, 7, new DateTime(2026, 3, 9, 8, 0, 0), fondo: 300);
        var antes = await EnvioEleventa.MandarAsync(e.Conexion, Piloto, Venta(new DateTime(2026, 3, 9, 10, 0, 0), Cuaderno));
        Assert.Equal(new TurnoDeEleventa(primero, new DateTime(2026, 3, 9, 8, 0, 0)), antes.Turno);

        // Eleventa hace su corte a las 20:00. Una venta de las 19:30 que no alcanzó a llegar ya se contó allá.
        await e.CortarAsync(primero, new DateTime(2026, 3, 9, 20, 0, 0));
        var contada = await Assert.ThrowsAsync<ChoqueConEleventa>(() =>
            EnvioEleventa.MandarAsync(e.Conexion, Piloto, Venta(new DateTime(2026, 3, 9, 19, 30, 0), Cuaderno)));
        Assert.Equal(CorteEnElPiloto.YaSeContoEnEleventa(new DateTime(2026, 3, 9, 20, 0, 0)), contada.Message);
        // Una de las 20:30, después del corte: espera a que abran la caja en eleventa (no es un choque).
        var despues = Venta(new DateTime(2026, 3, 9, 20, 30, 0), Cuaderno);
        var espera = await Assert.ThrowsAsync<InvalidOperationException>(() => EnvioEleventa.MandarAsync(e.Conexion, Piloto, despues));
        Assert.Equal(CorteEnElPiloto.AbreLaCajaEnEleventa, espera.Message);
        // Lo mismo una salida de efectivo de antes del corte, y una devolución.
        await Assert.ThrowsAsync<ChoqueConEleventa>(() => EnvioEleventa.MoverEfectivoAsync(e.Conexion, Piloto,
            new MovimientoParaEleventa(false, 50, "Garrafón", new DateTime(2026, 3, 9, 19, 0, 0), TurnoNuevo)));
        await Assert.ThrowsAsync<ChoqueConEleventa>(() => EnvioEleventa.DevolverAsync(e.Conexion, Piloto,
            new DevolucionParaEleventa(antes.TicketId, false, new DateTime(2026, 3, 9, 19, 45, 0), TurnoNuevo, [new ArticuloDevuelto(10, "Cuaderno", 1, 30)])));

        // Al día siguiente eleventa abre la caja: lo de las 20:30 entra a ese turno, el siguiente.
        var segundo = await e.AbrirCajaAsync(4, 7, new DateTime(2026, 3, 10, 8, 0, 0), fondo: 300);
        var r = await EnvioEleventa.MandarAsync(e.Conexion, Piloto, despues);

        Assert.Equal(segundo, r.Turno!.Id);
        Assert.Equal(segundo, await CuantosAsync(e, "SELECT TURNO_ID FROM VENTATICKETS WHERE ID = @id", ("id", r.TicketId)));
        // El turno cortado no cambió: sigue con su venta de las 10:00 y nada más; el puente no abrió otro turno.
        Assert.Equal(30m, (decimal)(await e.ValorAsync("SELECT ACUMULADO_VENTAS FROM TURNOS WHERE ID = @id", ("id", primero)))!);
        Assert.Equal(0m, (decimal)(await e.ValorAsync("SELECT ACUMULADO_SALIDAS FROM TURNOS WHERE ID = @id", ("id", primero)))!);
        Assert.Equal(0, await CuantosAsync(e, "SELECT COUNT(*) FROM DEVOLUCIONES"));
        Assert.Equal(2, await CuantosAsync(e, "SELECT COUNT(*) FROM TURNOS"));
    }

    [FactConFirebird]
    public async Task Con_dos_turnos_abiertos_para_la_caja_no_escribe_nada()
    {
        await using var e = await TiendaAsync();
        // La base de eleventa lo impide (UNQ_TURNO_ABIERTO_CAJA no deja dos turnos de la misma caja sin TERMINO_EN). Por si
        // una base no lo trae: dos turnos abiertos de la caja 2.
        await e.EjecutarAsync("ALTER TABLE TURNOS DROP CONSTRAINT UNQ_TURNO_ABIERTO_CAJA");
        await e.AbrirCajaAsync(4, 7, new DateTime(2026, 3, 9, 8, 0, 0), fondo: 300);
        await e.EjecutarAsync("""
            INSERT INTO TURNOS (ID_CAJA, ID_OPERACION_INICIO, ID_CAJERO, INICIO_EN, DINERO_INICIAL, SOSPECHOSO, ACUMULADO_VENTAS)
            VALUES (4, 1, 7, @inicio, 0, 'F', 0)
            """, ("inicio", new DateTime(2026, 3, 9, 9, 0, 0)));

        var choque = await Assert.ThrowsAsync<ChoqueConEleventa>(() =>
            EnvioEleventa.MandarAsync(e.Conexion, Piloto, Venta(new DateTime(2026, 3, 9, 10, 0, 0), Cuaderno)));

        Assert.Equal(CorteEnElPiloto.DosTurnosAbiertos, choque.Message);
        Assert.Equal(0, await CuantosAsync(e, "SELECT COUNT(*) FROM VENTATICKETS WHERE CAJA_ID = 4 AND ESTA_ABIERTO = 'f'"));
        Assert.Equal(12, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);
    }

    [FactConFirebird]
    public async Task Un_folio_repetido_al_guardar_toma_el_siguiente_y_corrige_solo_su_ticket_y_su_kardex()
    {
        await using var e = await TiendaAsync();
        await e.AbrirCajaAsync(4, 7, new DateTime(2026, 3, 9, 8, 0, 0), fondo: 300);
        var venta = Venta(new DateTime(2026, 3, 9, 10, 0, 0), Cuaderno);
        var r = await EnvioEleventa.MandarAsync(e.Conexion, Piloto, venta);
        Assert.Equal((42, false, null), (r.Folio, r.FolioRepetido, r.FolioAnterior));
        // La caja principal cobró casi al mismo tiempo y tomó el mismo folio (eleventa lo calcula sin candado).
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN, TOTAL) VALUES (42, 1, 1, -1, 'f', @cuando, 45)",
            ("cuando", new DateTime(2026, 3, 9, 10, 0, 1)));
        var otro = Convert.ToInt32(await e.ValorAsync("SELECT ID FROM VENTATICKETS WHERE FOLIO = 42 AND CAJA_ID = 1"));

        // Al revisarlo justo después de guardar (aquí, al mandarla otra vez porque no se supo), su folio todavía no se entregó:
        // toma el siguiente.
        var otraVez = await EnvioEleventa.MandarAsync(e.Conexion, Piloto, venta);

        Assert.Equal((r.TicketId, 43, true, false, 42), (otraVez.TicketId, otraVez.Folio, otraVez.YaEstaba, otraVez.FolioRepetido, otraVez.FolioAnterior));
        Assert.Equal(43, await CuantosAsync(e, "SELECT FOLIO FROM VENTATICKETS WHERE ID = @id", ("id", r.TicketId)));
        Assert.Equal("Venta #43", (string)(await e.ValorAsync("SELECT DESCRIPCION FROM INVENTARIO_HISTORIAL WHERE VENTA_ID = @id", ("id", r.TicketId)))!);
        // El otro ticket no se toca.
        Assert.Equal(42, await CuantosAsync(e, "SELECT FOLIO FROM VENTATICKETS WHERE ID = @id", ("id", otro)));
        // Sin otro choque, la revisión lo deja como está.
        Assert.Equal(new FolioRevisado(43, false, null), await EnvioEleventa.RevisarFolioAsync(e.Conexion, r.TicketId, 43));
    }

    [FactConFirebird]
    public async Task La_revision_tardia_encuentra_el_otro_ticket_con_el_mismo_folio()
    {
        await using var e = await TiendaAsync();
        await e.AbrirCajaAsync(4, 7, new DateTime(2026, 3, 9, 8, 0, 0), fondo: 300);
        var r = await EnvioEleventa.MandarAsync(e.Conexion, Piloto, Venta(new DateTime(2026, 3, 9, 10, 0, 0), Cuaderno));
        Assert.Empty(await FoliosRepetidos.OtrosConEsosFoliosAsync(e.Conexion, [(r.Folio, r.TicketId)]));

        // Un instante después de la revisión al guardar, la principal guardó el mismo folio.
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN, TOTAL) VALUES (@folio, 1, 1, -1, 'f', @cuando, 45.5)",
            ("folio", r.Folio), ("cuando", new DateTime(2026, 3, 9, 10, 0, 2)));
        var otros = await FoliosRepetidos.OtrosConEsosFoliosAsync(e.Conexion, [(r.Folio, r.TicketId), (41, -1)]);

        // El de la caja 2 no cuenta; el 41 de antes, tampoco es de esta caja: sale como otro con ese folio.
        var otro = Assert.Single(otros, o => o.Folio == r.Folio);
        Assert.Equal(("Principal", new DateTime(2026, 3, 9, 10, 0, 2), 45.5m), (otro.Caja, otro.VendidoEn, otro.Total));
    }

    [FactConFirebird]
    public async Task La_lectura_del_piloto_trae_el_turno_abierto_de_la_caja_y_el_corte_de_los_adoptados()
    {
        await using var e = await TiendaAsync();
        var adoptado = await e.AbrirCajaAsync(4, 7, new DateTime(2026, 3, 9, 8, 0, 0), fondo: 300);
        // Eleventa lo cortó con un faltante de $12.50 y abrió otro.
        await e.CortarAsync(adoptado, new DateTime(2026, 3, 9, 20, 0, 0));
        await e.EjecutarAsync("UPDATE TURNOS SET EFECTIVO_AL_CIERRE = 787.5 WHERE ID = @id", ("id", adoptado));
        await e.EjecutarAsync("""
            INSERT INTO CORTE_MOVIMIENTOS (CUANDO_FUE, MONTO, DESCRIPCION, TIPO, ID_TURNO, ID_CAJA)
            VALUES (@cuando, 12.5, 'Efectivo faltante al cerrar turno de Ana Ruiz del 09/03/2026', 'Salida', @turno, 4)
            """, ("cuando", new DateTime(2026, 3, 9, 20, 0, 0)), ("turno", adoptado));
        var abierto = await e.AbrirCajaAsync(4, 7, new DateTime(2026, 3, 10, 8, 0, 0), fondo: 300);
        // El de la principal no es de la caja 2.
        await e.AbrirCajaAsync(1, 7, new DateTime(2026, 3, 10, 8, 5, 0), fondo: 500);

        var datos = await LectorEleventa.LeerCatalogoAsync(e.Conexion, new ConsultaDelPiloto(4, [adoptado]));

        var piloto = datos.Piloto!;
        Assert.Equal([adoptado, abierto], piloto.Turnos.Select(t => t.Id).Order());
        var cortado = piloto.Turnos.Single(t => t.Id == adoptado);
        Assert.Equal((new DateTime(2026, 3, 9, 20, 0, 0), 787.5m), (cortado.Termino, cortado.EfectivoAlCierre));
        Assert.Equal(new FilaCierreDeTurno(adoptado, Falto: true, 12.5m), Assert.Single(piloto.Cierres));
        Assert.Null(piloto.Turnos.Single(t => t.Id == abierto).Termino);
        // Lo de siempre viene en la misma foto.
        Assert.Single(datos.Productos);
    }
}
