using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Datos;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>
/// La importación final (docs/migracion-eleventa.md, «La importación final»): el historial de eleventa entra a la base de la
/// tienda sin las ventas que mandó la caja 2, que ya están ahí. Sin Firebird: las filas de eleventa se arman con las marcas
/// que deja el puente (contra el Firebird de prueba, en ImportacionFinalConEleventaTests).
/// </summary>
public sealed class ImportacionFinalTests : IAsyncDisposable
{
    private BaseEnMemoria? _base;

    public async ValueTask DisposeAsync()
    {
        if (_base is not null)
            await _base.DisposeAsync();
    }

    /// <summary>Hora de la tienda de los tickets que ya estaban cuando se armó la base.</summary>
    private static readonly DateTime Antes = new(2026, 9, 20, 12, 0, 0);

    private static FilaProducto Producto(int id, string codigo, string nombre, double precio) => new(id, codigo, nombre, 'U', 1, 10, precio, 0, 2, false);

    private static FilaLinea Linea(int ticket, string codigo, double cantidad, double precio) =>
        new(ticket, codigo, "Algo", cantidad, cantidad * precio, precio, precio / 3, 2, false);

    /// <summary>
    /// Eleventa: la goma ($15), el lápiz ($6), los dos tickets de la principal que ya estaban cuando se armó la base de la tienda
    /// y lo que se pida (lo que escribió el puente, lo nuevo de eleventa y las devoluciones).
    /// </summary>
    private static DatosEleventa Eleventa(IEnumerable<FilaTicket>? tickets = null, IEnumerable<FilaLinea>? lineas = null,
        IEnumerable<DevolucionLeida>? devoluciones = null, IEnumerable<FilaProducto>? productos = null) => new()
    {
        Departamentos = [new(2, "Papelería", true)],
        Productos = [Producto(1, "A1", "Goma", 15), Producto(2, "B2", "Lápiz", 6), .. (productos ?? [])],
        Existencias = [new(1, 50), new(2, 80)],
        Tickets = [new(10, 100, Antes, 15, false), new(11, 101, Antes.AddHours(1), 12, false), .. (tickets ?? [])],
        Lineas = [Linea(10, "A1", 1, 15), Linea(11, "B2", 2, 6), .. (lineas ?? [])],
        Devoluciones = devoluciones?.ToList() ?? [],
    };

    private sealed record Mostrador(Caja Caja, Usuario Cajera, Usuario Encargado, Turno Turno);

    /// <summary>La base de la tienda como la deja importar-eleventa y, encima, la caja 2 del piloto con su turno abierto.</summary>
    private async Task<Mostrador> PilotoAsync()
    {
        _base = await BaseEnMemoria.CrearAsync();
        await using (var importada = _base.Contexto())
            await GuardadoLote.GuardarAsync(ImportadorEleventa.Importar(Eleventa()), importada);
        await using var db = _base.Contexto();
        var caja = new Caja { Nombre = "Caja 2", Serie = "A", SucursalId = (await db.Sucursales.SingleAsync()).Id };
        var cajera = new Usuario { Nombre = "Ana", NombreUsuario = "ana" };
        var encargado = new Usuario { Nombre = "Luis", NombreUsuario = "luis", Rol = Rol.Encargado };
        cajera.EstablecerPin("1111");
        encargado.EstablecerPin("2222");
        db.AddRange(caja, cajera, encargado);
        await db.SaveChangesAsync();
        await MigracionPermisos.AplicarAsync(db);
        return new Mostrador(caja, cajera, encargado, await new RegistroCaja(db).AbrirTurnoAsync(caja.Id, cajera.Id, 50000));
    }

    /// <summary>
    /// Una venta en efectivo de la caja 2, que entra a la bandeja hacia eleventa con el cobro. Se empezó 40 segundos antes de
    /// cobrarla: esa hora, con sus diezmilésimas de segundo, es su marca en eleventa.
    /// </summary>
    private async Task<Venta> VenderAsync(Mostrador m, string codigo, int piezas)
    {
        _base!.Reloj.Ahora = _base.Reloj.Ahora.AddMinutes(7);
        await using var db = _base.Contexto();
        var variante = await db.Variantes.Include(v => v.Producto)
            .SingleAsync(v => v.Id == db.CodigosBarras.Where(c => c.Codigo == codigo).Select(c => c.Presentacion!.VarianteId).Single());
        var venta = new Venta
        {
            SucursalId = m.Caja.SucursalId, CajaId = m.Caja.Id, TurnoId = m.Turno.Id, CajeroId = m.Cajera.Id, Serie = "A",
            CreadaUtc = _base.Reloj.Ahora.UtcDateTime.AddSeconds(-40).AddTicks(1_234_567),
        };
        venta.AgregarProducto(new ProductoEnVenta(variante.Id, variante.Producto!.Nombre, Impuesto.Iva16, UnidadBase.Pieza, true),
            await db.Presentaciones.SingleAsync(p => p.VarianteId == variante.Id && p.EsBase), Cantidad.Piezas(piezas),
            await db.Precios.Where(p => p.VarianteId == variante.Id).ToListAsync());
        db.EnviosAEleventa.Add(new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = venta.Id, CreadoUtc = _base.Reloj.Ahora.UtcDateTime });
        await new RegistroCaja(db).CobrarAsync(venta, [new CobroSolicitado(FormaPago.Efectivo, venta.TotalCentavos)]);
        return venta;
    }

    /// <summary>Lo que hizo la bandeja con la venta: entró a eleventa con ese número y ese folio, o se quedó como se diga.</summary>
    private async Task EnvioAsync(Venta v, EstadoEnvioEleventa estado, int? ticket = null, int? folio = null, MotivoPorRevisar? porque = null)
    {
        await using var db = _base!.Contexto();
        var envio = await db.EnviosAEleventa.SingleAsync(e => e.Tipo == TipoEnvioEleventa.Venta && e.VentaId == v.Id);
        (envio.Estado, envio.Intentos, envio.TicketEleventa, envio.FolioEleventa, envio.PorRevisarPorque) = (estado, 1, ticket, folio, porque);
        if (estado == EstadoEnvioEleventa.Enviada)
            envio.EnviadaUtc = _base.Reloj.Ahora.UtcDateTime;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// La ida: en la caja 2 se devolvió algo de un ticket que se cobró en el eleventa de la caja 2, en el turno de ahora (lo único de
    /// eleventa que la caja 2 devuelve), y su envío a eleventa quedó como se diga.
    /// </summary>
    private async Task<DevolucionDeEleventa> IdaAsync(Mostrador m, int ticket, int folio, DateTime vendido, long centavos, EstadoEnvioEleventa estado,
        string? error = null)
    {
        _base!.Reloj.Ahora = _base.Reloj.Ahora.AddMinutes(3);
        await using var db = _base.Contexto();
        var ida = new DevolucionDeEleventa
        {
            SucursalId = m.Caja.SucursalId, CajaId = m.Caja.Id, TurnoId = m.Turno.Id, UsuarioId = m.Cajera.Id, Motivo = "No era",
            FechaUtc = _base.Reloj.Ahora.UtcDateTime.AddTicks(4_321), TicketEleventa = ticket, Folio = folio, CajaEleventa = 4, CajaNombre = "Caja 2",
            VendidoUtc = HoraMexico.AUtc(vendido), TicketTotalCentavos = 2 * centavos, TotalCentavos = centavos, ReembolsoEfectivoCentavos = centavos,
        };
        db.Add(ida);
        db.EnviosAEleventa.Add(new EnvioAEleventa
        {
            Tipo = TipoEnvioEleventa.DevolucionDeEleventa, DevolucionDeEleventaId = ida.Id, Estado = estado, Error = error, CreadoUtc = ida.FechaUtc,
        });
        await db.SaveChangesAsync();
        return ida;
    }

    /// <summary>El ticket que escribió el puente con esa venta: su número, su folio y su marca (cuándo se empezó y se cobró aquí).</summary>
    private static FilaTicket TicketDe(Venta v, int id, int folio, double? total = null, bool cancelado = false) =>
        new(id, folio, AlSegundo(v.PagadaUtc!.Value), total ?? v.TotalCobradoCentavos / 100.0, cancelado, RestauracionDeEleventa.Marca(v.CreadaUtc));

    private static DateTime AlSegundo(DateTime utc)
    {
        var local = HoraMexico.ALocal(utc);
        return new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerSecond);
    }

    /// <summary>Una hora de la tienda después de lo último que pasó en la caja 2: para lo que se hace en eleventa.</summary>
    private DateTime Luego(int minutos) => AlSegundo(_base!.Reloj.Ahora.UtcDateTime).AddMinutes(minutos);

    private async Task<ResultadoImportacionFinal> ImportarAsync(DatosEleventa e)
    {
        await using var db = _base!.Contexto();
        return await ImportacionFinal.AplicarAsync(db, e);
    }

    /// <summary>El historial de eleventa: cada ticket (su número en eleventa) con su venta.</summary>
    private async Task<Dictionary<int, VentaHistorica>> HistorialAsync()
    {
        await using var db = _base!.Contexto();
        var mapeos = await db.MapeosImportacion.Where(m => m.Origen == ImportadorEleventa.Origen && m.Entidad == nameof(VentaHistorica)).ToListAsync();
        var ventas = await db.VentasHistoricas.Include(v => v.Lineas).ToDictionaryAsync(v => v.Id);
        return mapeos.ToDictionary(m => int.Parse(m.IdOrigen), m => ventas[m.IdNuevo]);
    }

    /// <summary>Cuántas ventas, líneas, mapeos y productos hay: correr otra vez no debe cambiar ninguno.</summary>
    private async Task<(int, int, int, int)> ConteosAsync()
    {
        await using var db = _base!.Contexto();
        return (await db.VentasHistoricas.CountAsync(), await db.VentasHistoricasLineas.CountAsync(), await db.MapeosImportacion.CountAsync(),
            await db.Productos.CountAsync());
    }

    [Fact]
    public async Task Las_ventas_que_mando_la_caja_2_no_se_traen_y_lo_de_eleventa_si()
    {
        var m = await PilotoAsync();
        // La caja 2 vende tres veces. La primera entra a eleventa como el ticket 20; la segunda como el 21 y se cancela aquí (la
        // cancelación también llega); de la tercera aquí no se supo su número (se cortó la red justo cuando eleventa la guardó).
        var vendida = await VenderAsync(m, "A1", 2);
        await EnvioAsync(vendida, EstadoEnvioEleventa.Enviada, 20, 102);
        var cancelada = await VenderAsync(m, "B2", 1);
        await EnvioAsync(cancelada, EstadoEnvioEleventa.Enviada, 21, 103);
        Devolucion cancelacion;
        await using (var db = _base!.Contexto())
            cancelacion = await new RegistroDevoluciones(db).CancelarAsync(cancelada.Id, m.Encargado.Id, m.Caja.Id, "Se arrepintió", null);
        var sinNumero = await VenderAsync(m, "A1", 3);
        await EnvioAsync(sinNumero, EstadoEnvioEleventa.Pendiente);
        // En eleventa le devuelven una goma de la primera: la vuelta la pasa a su venta, sin efectivo.
        var vuelta = new DevolucionLeida(902, 20, false, Luego(5), 15, "cajera2", "Caja 2", null, [new ArticuloDevueltoLeido("A1", "Goma", 1, 15, 1)]);
        await using (var db = _base.Contexto())
        {
            Assert.Equal(1, (await DevolucionesConEleventa.AplicarVueltaAsync(db, [vuelta], _base.Reloj.Ahora.UtcDateTime)).Pasadas);
            await db.SaveChangesAsync();
        }
        // Lo de eleventa: la principal vende con un folio que eleventa repitió (el 102, el de la primera) y le devuelven un lápiz de
        // otro ticket suyo.
        var e = Eleventa(
            tickets: [TicketDe(vendida, 20, 102, total: 15), TicketDe(cancelada, 21, 103, cancelado: true), TicketDe(sinNumero, 22, 104),
                new(23, 102, Luego(1), 15, false, Luego(0)), new(24, 105, Luego(3), 6, false, Luego(2))],
            lineas: [Linea(20, "A1", 1, 15), Linea(21, "B2", 1, 6), Linea(22, "A1", 3, 15), Linea(23, "A1", 1, 15), Linea(24, "B2", 1, 6)],
            devoluciones: [new(900, 21, true, RestauracionDeEleventa.Marca(cancelacion.FechaUtc), 6, "cajera2", "Caja 2", null, []), vuelta,
                new(901, 24, false, Luego(4), 6, "admin", "Principal", null, [])]);

        var r = await ImportarAsync(e);

        // El historial: lo que ya estaba y lo nuevo de eleventa, con lo que eleventa le bajó al devolver.
        var historial = await HistorialAsync();
        Assert.Equal([10, 11, 23, 24], historial.Keys.Order());
        Assert.Equal(600, historial[24].TotalCentavos);
        Assert.Equal((2, 2, 0, 0), (r.Nuevos, r.Iguales, r.Actualizados, r.Quitados));
        // Lo de la caja 2 no se trajo, ni con él su cancelación de aquí ni la devolución que se hizo en eleventa (aquí ya están).
        var p = r.DelPuente;
        Assert.Equal([20, 21, 22], p.Tickets.Keys.Order());
        Assert.Equal((1, 1, 2, 1, 1), (p.PorSuMarca, p.Cancelados, p.ConDevoluciones, p.DevolucionesDeAqui, p.DevolucionesDeEleventa));
        // Cuadra con eleventa: lo que suma allá (sin la cancelada) es el historial más lo de la caja 2.
        Assert.Equal((1500L + 1200 + 1500 + 600, 1500L + 4500), (r.TotalCentavos, p.TotalCentavos));
        // La tercera sigue pendiente aquí, pero eleventa sí la tiene: se dice.
        var pendiente = Assert.Single(p.PorRevisar);
        Assert.Contains($"La venta {sinNumero.FolioTexto} de la caja 2", pendiente);
        Assert.Contains("quedó pendiente en la bandeja, pero eleventa sí la tiene (#104", pendiente);
        // Qué ticket de eleventa es cada venta de la caja 2 queda en los mapeos.
        await using var lectura = _base.Contexto();
        Assert.Equal([(20, vendida.Id), (21, cancelada.Id), (22, sinNumero.Id)],
            (await lectura.MapeosImportacion.Where(x => x.Entidad == LoQueMandoElPuente.EntidadMapeo).ToListAsync())
                .Select(x => (int.Parse(x.IdOrigen), x.IdNuevo)).Order());
        var texto = string.Join('\n', ImportacionFinal.Describir(r));
        Assert.Contains("No se trajeron: son ventas de la caja 2", texto);
        Assert.Contains("En eleventa, en total: $108.00 sin los cancelados = historial $48.00 + caja 2 $60.00", texto);
    }

    /// <summary>
    /// Los reportes de ventas (entrega 9) juntan el historial de eleventa con las ventas de este POS. Después de la importación
    /// final nada cuenta dos veces: la venta neta es la de eleventa sin los cancelados (el historial más lo de la caja 2, con lo
    /// que se le devolvió en eleventa) y los tickets, los de eleventa sin los cancelados.
    /// </summary>
    [Fact]
    public async Task Los_reportes_juntan_el_historial_y_lo_de_la_caja_2_sin_contar_nada_dos_veces()
    {
        var m = await PilotoAsync();
        // Como en la prueba de arriba: la caja 2 vende tres veces (una se cancela aquí, otra no supo su número) y en eleventa le
        // devuelven una goma; la principal vende dos (y le devuelven un lápiz).
        var vendida = await VenderAsync(m, "A1", 2);
        await EnvioAsync(vendida, EstadoEnvioEleventa.Enviada, 20, 102);
        var cancelada = await VenderAsync(m, "B2", 1);
        await EnvioAsync(cancelada, EstadoEnvioEleventa.Enviada, 21, 103);
        Devolucion cancelacion;
        await using (var db = _base!.Contexto())
            cancelacion = await new RegistroDevoluciones(db).CancelarAsync(cancelada.Id, m.Encargado.Id, m.Caja.Id, "Se arrepintió", null);
        var sinNumero = await VenderAsync(m, "A1", 3);
        await EnvioAsync(sinNumero, EstadoEnvioEleventa.Pendiente);
        var vuelta = new DevolucionLeida(902, 20, false, Luego(5), 15, "cajera2", "Caja 2", null, [new ArticuloDevueltoLeido("A1", "Goma", 1, 15, 1)]);
        await using (var db = _base.Contexto())
        {
            await DevolucionesConEleventa.AplicarVueltaAsync(db, [vuelta], _base.Reloj.Ahora.UtcDateTime);
            await db.SaveChangesAsync();
        }
        var e = Eleventa(
            tickets: [TicketDe(vendida, 20, 102, total: 15), TicketDe(cancelada, 21, 103, cancelado: true), TicketDe(sinNumero, 22, 104),
                new(23, 102, Luego(1), 15, false, Luego(0)), new(24, 105, Luego(3), 6, false, Luego(2))],
            lineas: [Linea(20, "A1", 1, 15), Linea(21, "B2", 1, 6), Linea(22, "A1", 3, 15), Linea(23, "A1", 1, 15), Linea(24, "B2", 1, 6)],
            devoluciones: [new(900, 21, true, RestauracionDeEleventa.Marca(cancelacion.FechaUtc), 6, "cajera2", "Caja 2", null, []), vuelta,
                new(901, 24, false, Luego(4), 6, "admin", "Principal", null, [])]);
        await ImportarAsync(e);
        // Quien ve los reportes: el encargado, con el permiso (al migrar, solo el dueño lo tiene).
        await using (var db = _base.Contexto())
        {
            (await db.PermisosUsuario.SingleAsync(p => p.UsuarioId == m.Encargado.Id && p.Accion == AccionControlada.VerReportes)).Nivel = NivelPermiso.Permitido;
            await db.SaveChangesAsync();
        }

        await using var lectura = _base.Contexto();
        var anio = await new ReportesDeVentas(lectura).ArmarAsync(m.Caja.Id, m.Encargado.Id, PeriodoReporte.Anio);

        // Lo que suma eleventa sin los cancelados ($108): el historial ($48) más lo de la caja 2 ($60: $30 − la goma devuelta + $45).
        Assert.Equal((10800L, 4800L), (anio.Actual.VentaCentavos, anio.Actual.DelHistorialCentavos));
        Assert.Equal((10800L - 4800, 1500L), (anio.Actual.VendidoCentavos - anio.Actual.DelHistorialCentavos - anio.Actual.DevueltoCentavos,
            anio.Actual.DevueltoCentavos));
        // Los tickets de eleventa sin los cancelados: 10, 11, 20, 22, 23 y 24 (el 20 y el 22, como ventas de la caja 2; el 21 se canceló).
        Assert.Equal((6, 4, 1), (anio.Actual.Tickets, anio.Actual.DelHistorialTickets, anio.Actual.Cancelados));
        // Las gomas y los lápices, una sola vez: lo vendido por producto suma lo mismo.
        Assert.Equal(10800L, anio.MasVendidos.Sum(p => p.VentaCentavos));
    }

    [Fact]
    public async Task Correrla_otra_vez_no_duplica_nada_y_trae_lo_que_cambio_en_eleventa()
    {
        var m = await PilotoAsync();
        var vendida = await VenderAsync(m, "A1", 2);
        await EnvioAsync(vendida, EstadoEnvioEleventa.Enviada, 20, 102);
        var e = Eleventa(tickets: [TicketDe(vendida, 20, 102), new(23, 103, Luego(1), 30, false, Luego(0))],
            lineas: [Linea(20, "A1", 2, 15), Linea(23, "A1", 2, 15)]);

        // Simulada (quien la corre deshace la transacción) no deja nada.
        await using (var db = _base!.Contexto())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            Assert.Equal(1, (await ImportacionFinal.AplicarAsync(db, e)).Nuevos);
            await tx.RollbackAsync();
        }
        Assert.Equal([10, 11], (await HistorialAsync()).Keys.Order());
        var primera = await ImportarAsync(e);
        var despuesDeLaPrimera = await ConteosAsync();
        var segunda = await ImportarAsync(e);

        Assert.Equal((1, 2), (primera.Nuevos, primera.Iguales));
        Assert.Equal((0, 0, 0, 3), (segunda.Nuevos, segunda.Actualizados, segunda.Quitados, segunda.Iguales));
        Assert.Equal(despuesDeLaPrimera, await ConteosAsync());

        // Después, en eleventa le devuelven una goma al 23 (lo baja a una) y la principal vende otra vez.
        var id23 = (await HistorialAsync())[23].Id;
        var despues = Eleventa(tickets: [TicketDe(vendida, 20, 102), new(23, 103, Luego(1), 15, false, Luego(0)), new(25, 104, Luego(5), 6, false, Luego(4))],
            lineas: [Linea(20, "A1", 2, 15), Linea(23, "A1", 1, 15), Linea(25, "B2", 1, 6)],
            devoluciones: [new(901, 23, false, Luego(2), 15, "admin", "Principal", null, [])]);
        var tercera = await ImportarAsync(despues);
        var cuarta = await ImportarAsync(despues);

        Assert.Equal((1, 1, 2), (tercera.Nuevos, tercera.Actualizados, tercera.Iguales));
        Assert.Equal((0, 0, 4), (cuarta.Nuevos, cuarta.Actualizados, cuarta.Iguales));
        var historial = await HistorialAsync();
        Assert.Equal([10, 11, 23, 25], historial.Keys.Order());
        // El mismo renglón del historial (y el mismo mapeo), ahora como está en eleventa.
        Assert.Equal((id23, 1500L, 1000L), (historial[23].Id, historial[23].TotalCentavos, Assert.Single(historial[23].Lineas).Cantidad.Milesimas));
        await using var db2 = _base.Contexto();
        Assert.Equal(1, await db2.MapeosImportacion.CountAsync(x => x.Entidad == nameof(VentaHistorica) && x.IdOrigen == "23"));
        Assert.Equal(1, await db2.MapeosImportacion.CountAsync(x => x.Entidad == LoQueMandoElPuente.EntidadMapeo));
    }

    [Fact]
    public async Task Lo_que_no_se_puede_decidir_solo_queda_para_revisar_a_mano()
    {
        var m = await PilotoAsync();
        // Una venta que no pudo pasar sola y se anotó a mano en eleventa: allá es el ticket 30, sin la marca de la caja 2.
        var aMano = await VenderAsync(m, "A1", 2);
        await EnvioAsync(aMano, EstadoEnvioEleventa.AMano);
        // Una que se mandó como el 31, pero eleventa ya no la tiene así (se restauró un respaldo y ese número es de otro ticket).
        var perdida = await VenderAsync(m, "B2", 1);
        await EnvioAsync(perdida, EstadoEnvioEleventa.Enviada, 31, 104);
        // Una que se mandó como el 32 y en eleventa le devolvieron algo que la lectura todavía no ha traído.
        var devuelta = await VenderAsync(m, "B2", 2);
        await EnvioAsync(devuelta, EstadoEnvioEleventa.Enviada, 32, 105);
        // Una que se canceló aquí antes de llegar, pero el intento que se cortó sí alcanzó a guardarla en eleventa (el 33).
        var descartada = await VenderAsync(m, "A1", 1);
        await using (var db = _base!.Contexto())
            await new RegistroDevoluciones(db).CancelarAsync(descartada.Id, m.Encargado.Id, m.Caja.Id, "Se equivocó de producto", null);
        await EnvioAsync(descartada, EstadoEnvioEleventa.Descartada);
        // Una que no ha llegado: no hay nada que decir de ella. Otra no pudo pasar sola (su turno ya se había cortado en eleventa)
        // y quedó para anotarse a mano allá: quizá se anotó.
        var pendiente = await VenderAsync(m, "B2", 3);
        var paraAnotar = await VenderAsync(m, "B2", 4);
        await EnvioAsync(paraAnotar, EstadoEnvioEleventa.PorRevisar, porque: MotivoPorRevisar.AnotarAMano);
        // La ida: en el eleventa de la caja 2 se vendieron dos gomas (el 34) y dos lápices (el 35), y en la caja 2 se devolvió uno de
        // cada uno. El del 34 llegó a eleventa; el del 35 no alcanzó: eleventa cortó ese turno antes de que se mandara y quedó para
        // anotarse a mano allá.
        var vendidos = Luego(-1);
        var llego = await IdaAsync(m, 34, 109, vendidos, 1500, EstadoEnvioEleventa.Enviada);
        await IdaAsync(m, 35, 110, vendidos, 600, EstadoEnvioEleventa.PorRevisar, EnvioEleventa.DeOtroTurnoODeOtraCaja);

        var e = Eleventa(
            tickets: [new(30, 106, Luego(10), 30, false, Luego(9)), new(31, 107, Luego(11), 99, false, Luego(10)), TicketDe(devuelta, 32, 105, total: 6),
                TicketDe(descartada, 33, 108), new(34, 109, vendidos, 15, false, vendidos.AddMinutes(-1)), new(35, 110, vendidos, 12, false, vendidos.AddMinutes(-1))],
            lineas: [Linea(30, "A1", 2, 15), Linea(31, "B2", 1, 99), Linea(32, "B2", 1, 6), Linea(33, "A1", 1, 15), Linea(34, "A1", 1, 15), Linea(35, "B2", 2, 6)],
            devoluciones: [new(950, 32, false, Luego(12), 6, "admin", "Principal", null, []),
                new(951, 34, false, RestauracionDeEleventa.Marca(llego.FechaUtc), 15, "cajera2", "Caja 2", null, [])]);

        var r = await ImportarAsync(e);

        var revisar = r.DelPuente.PorRevisar;
        Assert.Equal(6, revisar.Count);
        Assert.Contains(revisar, x => x.Contains(aMano.FolioTexto!) && x.Contains("se anotó a mano en eleventa") && x.Contains("#106"));
        Assert.Contains(revisar, x => x.Contains(perdida.FolioTexto!) && x.Contains("eleventa ya no la tiene") && x.Contains("#107"));
        Assert.Contains(revisar, x => x.StartsWith("La devolución de $6.00") && x.Contains(devuelta.FolioTexto!) && x.Contains("no la trajo"));
        Assert.Contains(revisar, x => x.Contains(descartada.FolioTexto!) && x.Contains("se canceló aquí antes de llegar"));
        Assert.Contains(revisar, x => x.Contains("#110") && x.EndsWith("cuenta $6.00 de más."));
        Assert.Contains(revisar, x => x.Contains(paraAnotar.FolioTexto!) && x.Contains("quedó para anotarse a mano en eleventa"));
        Assert.DoesNotContain(revisar, x => x.Contains(pendiente.FolioTexto!));
        Assert.Equal((2, 1), (r.DelPuente.NoLlegaron, r.DelPuente.Idas));
        // El que se anotó a mano y el que ocupó el número se trajeron como de eleventa; los de la caja 2, no. El 34 llega con lo que
        // se devolvió en la caja 2; el 35, sin eso (no llegó).
        var historial = await HistorialAsync();
        Assert.Equal([10, 11, 30, 31, 34, 35], historial.Keys.Order());
        Assert.Equal((1500L, 1200L, 0, 4), (historial[34].TotalCentavos, historial[35].TotalCentavos, r.Actualizados, r.Nuevos));
    }

    [Fact]
    public async Task La_anotacion_a_mano_de_una_devolucion_de_aqui_no_cuenta_como_otra_de_eleventa()
    {
        var m = await PilotoAsync();
        // Dos ventas que entraron a eleventa (el 20 y el 21). A cada una se le devolvió aquí una goma que no pasó sola a eleventa (su
        // turno allá ya se había cortado): quedaron para anotarse a mano.
        var ligada = await VenderAsync(m, "A1", 2);
        await EnvioAsync(ligada, EstadoEnvioEleventa.Enviada, 20, 102);
        var esperando = await VenderAsync(m, "A1", 3);
        await EnvioAsync(esperando, EstadoEnvioEleventa.Enviada, 21, 103);
        foreach (var venta in new[] { ligada, esperando })
        {
            await using var db = _base!.Contexto();
            var devolucion = await new RegistroDevoluciones(db).DevolverAsync(venta.Id, [new LineaPorDevolver(venta.Lineas.Single().Id, Cantidad.Piezas(1))],
                m.Cajera.Id, m.Caja.Id, "No era", null);
            var envio = await db.EnviosAEleventa.SingleAsync(x => x.DevolucionId == devolucion.Id);
            (envio.Estado, envio.Error, envio.PorRevisarPorque, envio.PorRevisarUtc) =
                (EstadoEnvioEleventa.PorRevisar, "Anótala a mano en eleventa.", MotivoPorRevisar.AnotarAMano, _base.Reloj.Ahora.UtcDateTime);
            await db.SaveChangesAsync();
        }
        // Las dos se anotaron a mano en eleventa. La de la primera ya se eligió como su anotación («Ya lo anoté en eleventa»); la de la
        // segunda todavía no: la lectura la retiene.
        var anotada = new DevolucionLeida(960, 20, false, Luego(5), 15, "cajera2", "Caja 2", null, [new ArticuloDevueltoLeido("A1", "Goma", 1, 15, 1)]);
        await using (var db = _base!.Contexto())
        {
            var envio = await db.EnviosAEleventa.SingleAsync(x => x.Tipo == TipoEnvioEleventa.Devolucion && x.VentaId == ligada.Id);
            DevolucionesConEleventa.Ligar(db, (await DevolucionesConEleventa.PorAnotarAsync(db, envio))!, [anotada], _base.Reloj.Ahora.UtcDateTime);
            envio.Estado = EstadoEnvioEleventa.AMano;
            await db.SaveChangesAsync();
        }
        var e = Eleventa(tickets: [TicketDe(ligada, 20, 102, total: 15), TicketDe(esperando, 21, 103, total: 30)],
            lineas: [Linea(20, "A1", 1, 15), Linea(21, "A1", 2, 15)],
            devoluciones: [anotada, new(961, 21, false, Luego(6), 15, "cajera2", "Caja 2", null, [])]);

        var r = await ImportarAsync(e);

        // La elegida es la de aquí; la otra puede serlo, y se dice. Ninguno de los dos tickets se trae.
        var p = r.DelPuente;
        Assert.Equal((2, 1, 0, 1), (p.ConDevoluciones, p.DevolucionesDeAqui, p.DevolucionesDeEleventa, p.DevolucionesSinPasar));
        var revisar = Assert.Single(p.PorRevisar);
        Assert.Contains($"la venta {esperando.FolioTexto} de la caja 2, puede ser la anotación a mano de la devolución de aquí", revisar);
        Assert.Contains("«Ya lo anoté en eleventa»", revisar);
        Assert.Equal([10, 11], (await HistorialAsync()).Keys.Order());
    }

    [Fact]
    public async Task Algo_de_la_caja_2_que_ya_estaba_en_el_historial_se_quita()
    {
        var m = await PilotoAsync();
        var vendida = await VenderAsync(m, "A1", 2);
        await EnvioAsync(vendida, EstadoEnvioEleventa.Enviada, 20, 102);
        // Una importación anterior lo trajo como de eleventa (de una copia en la que su ticket no traía la marca).
        Assert.Equal(1, (await ImportarAsync(Eleventa(tickets: [TicketDe(vendida, 20, 102) with { CreadoEn = null }], lineas: [Linea(20, "A1", 2, 15)]))).Nuevos);

        var r = await ImportarAsync(Eleventa(tickets: [TicketDe(vendida, 20, 102)], lineas: [Linea(20, "A1", 2, 15)]));

        Assert.Equal((1, 0, 2), (r.Quitados, r.Nuevos, r.Iguales));
        Assert.Equal([10, 11], (await HistorialAsync()).Keys.Order());
        await using var db = _base!.Contexto();
        Assert.False(await db.VentasHistoricasLineas.AnyAsync(l => !db.VentasHistoricas.Any(v => v.Id == l.VentaHistoricaId)));
    }

    [Fact]
    public async Task Ya_sin_el_puente_vale_lo_que_reconocio_la_importacion_anterior()
    {
        var m = await PilotoAsync();
        var vendida = await VenderAsync(m, "A1", 2);
        await EnvioAsync(vendida, EstadoEnvioEleventa.Enviada, 20, 102);
        var e = Eleventa(tickets: [TicketDe(vendida, 20, 102)], lineas: [Linea(20, "A1", 2, 15)]);
        await ImportarAsync(e);
        // Al dejar eleventa se quita el puente: su bandeja ya no está.
        await using (var db = _base!.Contexto())
            await db.EnviosAEleventa.ExecuteDeleteAsync();

        var r = await ImportarAsync(e);

        Assert.Equal((0, 0, 1, 3000L), (r.Nuevos, r.Quitados, r.DelPuente.YaReconocidos, r.DelPuente.TotalCentavos));
        Assert.Equal([10, 11], (await HistorialAsync()).Keys.Order());
        Assert.Contains(ImportacionFinal.Describir(r), l => l.Contains("los reconoció la importación anterior"));
    }

    [Fact]
    public async Task Solo_va_a_la_base_de_la_tienda_y_con_una_copia_al_dia()
    {
        // Una base que no se armó con importar-eleventa.
        await using (var otra = await BaseEnMemoria.CrearAsync())
        {
            await using (var db = otra.Contexto())
            {
                new CatalogoMinimo().Agregar(db);
                await db.SaveChangesAsync();
            }
            await using var lectura = otra.Contexto();
            var noEs = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportacionFinal.AplicarAsync(lectura, Eleventa()));
            Assert.Equal(ImportacionFinal.NoEsDeEleventa, noEs.Message);
        }

        // Una copia de eleventa de antes de lo que ya se importó (todavía no tiene el último ticket).
        await PilotoAsync();
        var vieja = Eleventa();
        vieja.Tickets.RemoveAll(t => t.Id == 11);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportarAsync(vieja));

        Assert.StartsWith("La copia de eleventa es de antes de lo que ya se importó", error.Message);
        Assert.Equal([10, 11], (await HistorialAsync()).Keys.Order());
    }

    [Fact]
    public async Task Una_copia_de_antes_de_lo_ultimo_que_mando_la_caja_2_se_dice_primero()
    {
        var m = await PilotoAsync();
        var vendida = await VenderAsync(m, "A1", 2);
        await EnvioAsync(vendida, EstadoEnvioEleventa.Enviada, 20, 102);

        // La copia se sacó antes de que llegara esa venta (tiene solo lo de la principal).
        var r = await ImportarAsync(Eleventa(tickets: [new(23, 101, AlSegundo(vendida.CreadaUtc).AddMinutes(-30), 15, false, Antes)], lineas: [Linea(23, "A1", 1, 15)]));

        Assert.StartsWith("La copia de eleventa es de antes de lo último que mandó la caja 2", r.DelPuente.PorRevisar[0]);
        Assert.Contains(r.DelPuente.PorRevisar, x => x.Contains(vendida.FolioTexto!) && x.Contains("eleventa ya no la tiene"));
    }

    [Fact]
    public async Task Lo_vendido_con_un_producto_que_la_base_no_tiene_se_da_de_alta_inactivo_una_sola_vez()
    {
        await PilotoAsync();
        // Un folder que se dio de alta en eleventa después de la última lectura, y un código que ya no es de ningún producto.
        var e = Eleventa(tickets: [new(23, 102, Luego(1), 25, false, Luego(0))], lineas: [Linea(23, "C3", 1, 10), Linea(23, "VIEJO", 1, 15)],
            productos: [Producto(3, "C3", "Folder", 10)]);

        var primera = await ImportarAsync(e);
        var conteos = await ConteosAsync();
        var segunda = await ImportarAsync(e);

        Assert.Equal((1, 1), (primera.Avisos[ImportacionFinal.AvisoProductoSinLeer], primera.Avisos[ImportadorEleventa.AvisoCodigoInexistente]));
        Assert.Empty(segunda.Avisos);
        Assert.Equal(conteos, await ConteosAsync());
        await using var db = _base!.Contexto();
        var folder = await db.Productos.SingleAsync(p => p.Nombre == "Folder");
        Assert.False(folder.Activo);
        // Con su Id de eleventa: si la caja vuelve a leer eleventa, lo adopta.
        var variante = await db.MapeosImportacion.SingleAsync(x => x.Entidad == nameof(Variante) && x.IdOrigen == "3");
        Assert.Equal(folder.Id, (await db.Variantes.SingleAsync(v => v.Id == variante.IdNuevo)).ProductoId);
        Assert.Equal(2, (await HistorialAsync())[23].Lineas.Count);
    }

    /// <summary>
    /// El mínimo y el máximo de inventario de eleventa (decidido el 2026-10-24, por revisar): la importación final se los pone a
    /// los productos de eleventa que aquí no tienen ninguno (una base que se armó antes de que la importación los trajera, sin la
    /// lectura del piloto); lo que ya tienen, de esa lectura o de F3, no se pisa. Repetirla no cambia nada.
    /// </summary>
    [Fact]
    public async Task La_importacion_final_pone_el_minimo_de_eleventa_solo_a_lo_que_no_tiene_ninguno()
    {
        await PilotoAsync();
        Guid VarianteDe(PosDbContext db, string codigo) => db.CodigosBarras.Where(c => c.Codigo == codigo).Select(c => c.Presentacion!.VarianteId).Single();
        await using (var db = _base!.Contexto())
        {
            // El lápiz ya tiene el suyo aquí.
            var id = VarianteDe(db, "B2");
            var lapiz = await db.Variantes.SingleAsync(v => v.Id == id);
            lapiz.InventarioMinimo = Cantidad.Piezas(4);
            await db.SaveChangesAsync();
        }
        var e = Eleventa();
        var conMinimos = new DatosEleventa
        {
            Departamentos = e.Departamentos, Existencias = e.Existencias, Tickets = e.Tickets, Lineas = e.Lineas,
            Productos = [.. e.Productos.Select(p => p with { InventarioMinimo = 10, InventarioMaximo = 10 })],
        };

        var primera = await ImportarAsync(conMinimos);
        var segunda = await ImportarAsync(conMinimos);

        Assert.Equal((1, 0), (primera.MinimosYMaximos, segunda.MinimosYMaximos));
        await using var lectura = _base.Contexto();
        var (gomaId, lapizId) = (VarianteDe(lectura, "A1"), VarianteDe(lectura, "B2"));
        var goma = await lectura.Variantes.SingleAsync(v => v.Id == gomaId);
        var lapizDespues = await lectura.Variantes.SingleAsync(v => v.Id == lapizId);
        Assert.Equal(((long?)10_000, (long?)10_000), (goma.InventarioMinimo?.Milesimas, goma.InventarioMaximo?.Milesimas));
        Assert.Equal(((long?)4_000, (long?)null), (lapizDespues.InventarioMinimo?.Milesimas, lapizDespues.InventarioMaximo?.Milesimas));
        Assert.Contains(ImportacionFinal.Describir(primera), l => l.StartsWith("Mínimo y máximo de inventario de eleventa, en los productos que aquí no tenían ninguno: 1"));
        Assert.Contains(ImportacionFinal.Describir(segunda), l => l.StartsWith("Mínimo y máximo de inventario de eleventa: nada que poner"));
    }

    /// <summary>
    /// El artículo común de eleventa entra marcado (los reportes lo cuentan como el de aquí; revisión del PR #131), y uno que ya
    /// estaba sin marcar (importado antes de marcarlos) se vuelve a escribir marcado.
    /// </summary>
    [Fact]
    public async Task El_articulo_comun_de_eleventa_entra_marcado_y_el_que_ya_estaba_se_marca()
    {
        await PilotoAsync();
        var e = Eleventa(tickets: [new(23, 102, Luego(1), 20, false, Luego(0))],
            lineas: [Linea(23, "A1", 1, 15), new FilaLinea(23, "COMUN-77-3", "Recuerdo", 1, 5, 5, 5, 2, false)]);

        var primera = await ImportarAsync(e);
        var comun = (await HistorialAsync())[23].Lineas.Single(l => l.ImporteCentavos == 500);
        await using (var db = _base!.Contexto())
            await db.VentasHistoricasLineas.Where(l => l.Id == comun.Id).ExecuteUpdateAsync(x => x.SetProperty(l => l.EsArticuloComun, false));
        var otra = await ImportarAsync(e);

        Assert.True(comun.EsArticuloComun);
        Assert.Equal((1, 0), (primera.Nuevos, primera.Actualizados));
        Assert.Equal((0, 1), (otra.Nuevos, otra.Actualizados));
        Assert.Equal([(500L, true), (1500L, false)], (await HistorialAsync())[23].Lineas.OrderBy(l => l.ImporteCentavos).Select(l => (l.ImporteCentavos, l.EsArticuloComun)));
    }
}
