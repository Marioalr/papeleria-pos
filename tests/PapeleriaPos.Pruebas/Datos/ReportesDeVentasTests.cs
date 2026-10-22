using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// La tienda de las pruebas de los reportes de ventas: la matriz con la Caja 1; Ana y Beto (cajeros), Carmen (encargada) y
/// Luis (el dueño, con todos los permisos); los departamentos Escritura, Cuadernos y Servicios, y sus productos: la pluma ($10,
/// cuesta $4), la goma ($12.50, cuesta $5), el cuaderno ($40, cuesta $25) y el engargolado ($35, un servicio sin inventario ni
/// costo). Cada venta, devolución, corte y ticket del historial de eleventa se guarda con la hora (de la tienda) que se dice.
/// </summary>
internal sealed class TiendaDeReportes : IAsyncDisposable
{
    public required BaseEnMemoria Base { get; init; }
    public required Sucursal Matriz { get; init; }
    public required Caja Caja { get; init; }
    public required Usuario Ana { get; init; }
    public required Usuario Beto { get; init; }
    public required Usuario Carmen { get; init; }
    public required Usuario Luis { get; init; }
    public required ProductoDePrueba Pluma { get; init; }
    public required ProductoDePrueba Goma { get; init; }
    public required ProductoDePrueba Cuaderno { get; init; }
    public required ProductoDePrueba Engargolado { get; init; }

    private int _folio;
    private int _folioHistorial;

    /// <summary>Las otras cajas de la matriz (<see cref="OtraCajaAsync"/>), por su Id.</summary>
    private readonly Dictionary<Guid, Caja> _otrasCajas = [];

    /// <summary>Otra caja de la matriz (la caja 2), con su serie.</summary>
    public async Task<Caja> OtraCajaAsync(string nombre = "Caja 2", string serie = "B")
    {
        var caja = new Caja { SucursalId = Matriz.Id, Nombre = nombre, Serie = serie };
        await using var db = Contexto();
        db.Cajas.Add(caja);
        await db.SaveChangesAsync();
        _otrasCajas[caja.Id] = caja;
        return caja;
    }

    /// <summary>Un producto del catálogo de la prueba, con su precio y el costo que guarda cada pieza vendida.</summary>
    public sealed record ProductoDePrueba(Producto Producto, Variante Variante, Presentacion Pieza, Precio Precio, long CostoCentavos, bool LlevaInventario);

    /// <param name="ahoraLocal">La hora de la tienda en el reloj de la base: «hoy» para los reportes.</param>
    public static async Task<TiendaDeReportes> CrearAsync(DateTime ahoraLocal)
    {
        var baseEnMemoria = await BaseEnMemoria.CrearAsync();
        baseEnMemoria.Reloj.Ahora = new DateTimeOffset(HoraMexico.AUtc(ahoraLocal), TimeSpan.Zero);
        var matriz = new Sucursal { Clave = "M", Nombre = "Matriz" };
        var escritura = new Departamento { Nombre = "Escritura" };
        var cuadernos = new Departamento { Nombre = "Cuadernos" };
        var servicios = new Departamento { Nombre = "Servicios" };
        ProductoDePrueba Producto(string nombre, Departamento departamento, long precio, long costo, bool llevaInventario = true)
        {
            var producto = new Producto
            {
                Nombre = nombre, DepartamentoId = departamento.Id, Tipo = llevaInventario ? TipoProducto.Mercancia : TipoProducto.Servicio,
            };
            var variante = new Variante { ProductoId = producto.Id };
            var pieza = new Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true };
            return new ProductoDePrueba(producto, variante, pieza, new Precio { VarianteId = variante.Id, PrecioCentavos = precio }, costo, llevaInventario);
        }
        var tienda = new TiendaDeReportes
        {
            Base = baseEnMemoria,
            Matriz = matriz,
            Caja = new Caja { SucursalId = matriz.Id, Nombre = "Caja 1", Serie = "A", EsPrincipal = true },
            Ana = Usuario("Ana", "ana", Rol.Cajero, matriz),
            Beto = Usuario("Beto", "beto", Rol.Cajero, matriz),
            Carmen = Usuario("Carmen", "carmen", Rol.Encargado, matriz),
            Luis = Usuario("Luis", "luis", Rol.Dueno, null),
            Pluma = Producto("Pluma punto fino", escritura, 1000, 400),
            Goma = Producto("Goma blanca", escritura, 1250, 500),
            Cuaderno = Producto("Cuaderno profesional", cuadernos, 4000, 2500),
            Engargolado = Producto("Engargolado", servicios, 3500, 0, llevaInventario: false),
        };
        await using var db = tienda.Contexto();
        db.AddRange(matriz, escritura, cuadernos, servicios, tienda.Caja, tienda.Ana, tienda.Beto, tienda.Carmen, tienda.Luis);
        foreach (var p in new[] { tienda.Pluma, tienda.Goma, tienda.Cuaderno, tienda.Engargolado })
            db.AddRange(p.Producto, p.Variante, p.Pieza, p.Precio);
        await db.SaveChangesAsync();
        await MigracionPermisos.AplicarAsync(db);
        return tienda;
    }

    private static Usuario Usuario(string nombre, string usuario, Rol rol, Sucursal? sucursal)
    {
        var u = new Usuario { Nombre = nombre, NombreUsuario = usuario, Rol = rol, SucursalId = sucursal?.Id };
        u.EstablecerPin("1234");
        return u;
    }

    public PosDbContext Contexto() => Base.Contexto();

    public ValueTask DisposeAsync() => Base.DisposeAsync();

    /// <summary>La hora de la tienda, en UTC.</summary>
    public static DateTime A(int anio, int mes, int dia, int hora, int minuto = 0, int segundo = 0) =>
        HoraMexico.AUtc(new DateTime(anio, mes, dia, hora, minuto, segundo));

    /// <summary>
    /// Un turno de <paramref name="cajero"/> en la Caja 1 (u otra): abierto y, si se dice, cerrado (con su corte Z, si se da la
    /// diferencia).
    /// </summary>
    public async Task<Turno> TurnoAsync(Usuario cajero, DateTime abiertoUtc, DateTime? cerradoUtc = null, long? diferencia = null, Usuario? corta = null,
        Caja? caja = null)
    {
        await using var db = Contexto();
        var turno = new Turno
        {
            SucursalId = Matriz.Id, CajaId = (caja ?? Caja).Id, CajeroId = cajero.Id, AbiertoUtc = abiertoUtc, CerradoUtc = cerradoUtc, FondoCentavos = 50000,
        };
        db.Turnos.Add(turno);
        if (cerradoUtc is { } cerrado && diferencia is { } dif)
            db.Cortes.Add(new Corte
            {
                TurnoId = turno.Id, Tipo = TipoCorte.Z, FechaUtc = cerrado, RealizadoPorId = (corta ?? Carmen).Id, EsperadoCentavos = 50000,
                ContadoCentavos = 50000 + dif, DiferenciaCentavos = dif,
            });
        await db.SaveChangesAsync();
        return turno;
    }

    /// <summary>
    /// Una venta cobrada a esa hora en ese turno: cuántas piezas de cada producto (cada pieza guarda su costo) y, con
    /// <paramref name="comun"/>, un artículo común por ese precio. Sin cobros, en efectivo exacto (redondeado al peso).
    /// </summary>
    public async Task<Venta> VenderAsync(Turno turno, DateTime pagadaUtc, IReadOnlyList<(ProductoDePrueba Producto, int Piezas)> productos, long? comun = null,
        params CobroSolicitado[] cobros)
    {
        var caja = _otrasCajas.GetValueOrDefault(turno.CajaId) ?? Caja;
        var venta = new Venta { SucursalId = Matriz.Id, CajaId = caja.Id, TurnoId = turno.Id, CajeroId = turno.CajeroId, Serie = caja.Serie, CreadaUtc = pagadaUtc };
        foreach (var (p, piezas) in productos)
        {
            var linea = venta.AgregarProducto(new ProductoEnVenta(p.Variante.Id, p.Producto.Nombre, Impuesto.Iva16, UnidadBase.Pieza, p.LlevaInventario), p.Pieza,
                Cantidad.Piezas(piezas), [p.Precio]);
            linea.CostoCentavos = p.CostoCentavos * piezas;
        }
        if (comun is { } precio)
            venta.AgregarArticuloComun("Recuerdo", precio, Cantidad.Piezas(1));
        venta.Pagar(++_folio, cobros.Length > 0 ? cobros : [new CobroSolicitado(FormaPago.Efectivo, Redondeo.AlPeso(venta.TotalCentavos) + 100_000)], pagadaUtc);
        await using var db = Contexto();
        db.Ventas.Add(venta);
        await db.SaveChangesAsync();
        return venta;
    }

    /// <summary>Se devuelven esas piezas de un producto de la venta, a esa hora y en ese turno.</summary>
    public async Task<Devolucion> DevolverAsync(Venta venta, ProductoDePrueba producto, int piezas, Turno turno, DateTime fechaUtc)
    {
        await using var db = Contexto();
        var guardada = await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).SingleAsync(v => v.Id == venta.Id);
        var anteriores = await db.Devoluciones.Include(d => d.Lineas).Where(d => d.VentaId == venta.Id).ToListAsync();
        var linea = guardada.Lineas.Single(l => l.VarianteId == producto.Variante.Id);
        var devolucion = CalculoDevolucion.Armar(guardada, [new LineaPorDevolver(linea.Id, Cantidad.Piezas(piezas))], turno.CajaId, turno.Id, turno.CajeroId,
            "No era", fechaUtc, anteriores);
        CalculoDevolucion.Aplicar(guardada, devolucion);
        db.Devoluciones.Add(devolucion);
        await db.SaveChangesAsync();
        return devolucion;
    }

    /// <summary>Se cancela la venta a esa hora, en su turno.</summary>
    public async Task<Devolucion> CancelarAsync(Venta venta, DateTime fechaUtc)
    {
        await using var db = Contexto();
        var guardada = await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).SingleAsync(v => v.Id == venta.Id);
        var cancelacion = CalculoDevolucion.ArmarCancelacion(guardada, guardada.TurnoId, guardada.CajeroId, "Se equivocó", fechaUtc);
        CalculoDevolucion.Aplicar(guardada, cancelacion);
        db.Devoluciones.Add(cancelacion);
        await db.SaveChangesAsync();
        return cancelacion;
    }

    /// <summary>Un ticket del historial de eleventa a esa hora: piezas de cada producto a su precio, con el costo que se dé.</summary>
    public async Task<VentaHistorica> HistorialAsync(DateTime fechaUtc, IReadOnlyList<(ProductoDePrueba Producto, int Piezas, long CostoPorPieza)> productos,
        bool cancelada = false)
    {
        var venta = new VentaHistorica
        {
            SucursalId = Matriz.Id, FechaUtc = fechaUtc, FolioOrigen = (++_folioHistorial).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Cancelada = cancelada,
        };
        foreach (var (p, piezas, costo) in productos)
            venta.Lineas.Add(new VentaHistoricaLinea
            {
                VentaHistoricaId = venta.Id, VarianteId = p.Variante.Id, Cantidad = Cantidad.Piezas(piezas), PrecioCentavos = p.Precio.PrecioCentavos,
                ImporteCentavos = p.Precio.PrecioCentavos * piezas, CostoCentavos = costo * piezas,
            });
        venta.TotalCentavos = venta.Lineas.Sum(l => l.ImporteCentavos);
        await using var db = Contexto();
        db.VentasHistoricas.Add(venta);
        await db.SaveChangesAsync();
        return venta;
    }

    /// <summary>Le pone ese nivel a un permiso de ese usuario.</summary>
    public async Task PermisoAsync(Usuario usuario, AccionControlada accion, NivelPermiso nivel)
    {
        await using var db = Contexto();
        var permiso = await db.PermisosUsuario.SingleAsync(p => p.UsuarioId == usuario.Id && p.Accion == accion);
        permiso.Nivel = nivel;
        await db.SaveChangesAsync();
    }

    public async Task<ReporteVentasDto> ReporteAsync(PeriodoReporte periodo, DateOnly? fecha = null, Usuario? quien = null, bool piloto = false)
    {
        await using var db = Contexto();
        return await new ReportesDeVentas(db).ArmarAsync(Caja.Id, (quien ?? Luis).Id, periodo, fecha, piloto);
    }
}

/// <summary>
/// Los reportes de ventas (entrega 9, parte 1, 2026-10-22): cada cifra contra datos hechos a mano. Cuentan como el corte del
/// día (la venta neta, los tickets, la utilidad y los departamentos), comparan contra el mismo periodo del año pasado (hoy y la
/// semana por día de la semana, el mes y el año por fecha, hasta el mismo momento), juntan lo de este POS con el historial de
/// eleventa, respetan los días de la tienda (la medianoche y el horario de verano de antes de 2022) y los permisos, y en el
/// piloto solo cuentan lo de la caja 2. Cada prueba con su base en memoria.
/// </summary>
public sealed class ReportesDeVentasTests
{
    /// <summary>Hoy en las pruebas: el miércoles 21 de octubre de 2026 a la 1:30 de la tarde.</summary>
    private static readonly DateTime Ahora = new(2026, 10, 21, 13, 30, 0);

    private static readonly DateOnly Hoy = DateOnly.FromDateTime(Ahora);

    private static DateTime A(int anio, int mes, int dia, int hora, int minuto = 0, int segundo = 0) => TiendaDeReportes.A(anio, mes, dia, hora, minuto, segundo);

    /// <summary>
    /// La semana del lunes 19 al miércoles 21 de octubre de 2026 en la Caja 1, y su año pasado. Hoy: Beto abre a las 8 y corta a
    /// las 10 (faltaron $20); Ana abre a las 10 y sigue. Lo que pasa:
    /// <list type="bullet">
    /// <item>9:10, Beto: una goma ($12.50; en efectivo, se cobran $13: 50 centavos de redondeo).</item>
    /// <item>10:15, Ana: dos plumas y un cuaderno ($60, en efectivo).</item>
    /// <item>11:40, Ana: un engargolado ($35, con tarjeta).</item>
    /// <item>12:05, Ana: un artículo común ($15).</item>
    /// <item>12:30, Ana: un cuaderno ($40) que se cancela a las 12:35: no cuenta.</item>
    /// <item>13:00, Ana devuelve una pluma ($10) del ticket de Beto de ayer.</item>
    /// <item>Ayer, martes 20, 17:00, Beto (su turno de 16:00 a 20:00): tres plumas ($30).</item>
    /// <item>El lunes 19, 10:00, Ana (su turno de 9:00 a 18:00; sobraron $5): un cuaderno ($40, por transferencia).</item>
    /// <item>El 10 de febrero de 2026, Ana: dos plumas ($20).</item>
    /// </list>
    /// El año pasado, del historial de eleventa (y una venta de este POS, como las del piloto):
    /// <list type="bullet">
    /// <item>El miércoles 22 de octubre de 2025 (364 días antes de hoy): 9:30, un cuaderno y una pluma ($50; cuestan $24 y $4);
    /// 11:00, un engargolado cobrado en este POS ($35); 12:00, dos plumas canceladas en eleventa ($20); 15:00, tres plumas
    /// ($30), después de la 1:30.</item>
    /// <item>El lunes 20 de octubre de 2025, 10:00: un cuaderno ($40; costó $20).</item>
    /// <item>El jueves 23 de octubre de 2025: siete plumas ($70), después del mismo momento de la semana.</item>
    /// <item>El 15 de marzo de 2025: cuatro cuadernos ($160; cuestan $15).</item>
    /// </list>
    /// </summary>
    private sealed record Semana(TiendaDeReportes Tienda, Turno DeBeto, Turno DeAna, Venta ConGoma, Venta DePlumas, Venta Cancelada, Venta DeAyer);

    private static async Task<Semana> SemanaAsync()
    {
        var t = await TiendaDeReportes.CrearAsync(Ahora);
        var (pluma, goma, cuaderno, engargolado) = (t.Pluma, t.Goma, t.Cuaderno, t.Engargolado);
        var deBeto = await t.TurnoAsync(t.Beto, A(2026, 10, 21, 8), A(2026, 10, 21, 10), diferencia: -2000);
        var deAna = await t.TurnoAsync(t.Ana, A(2026, 10, 21, 10));
        var conGoma = await t.VenderAsync(deBeto, A(2026, 10, 21, 9, 10), [(goma, 1)]);
        var dePlumas = await t.VenderAsync(deAna, A(2026, 10, 21, 10, 15), [(pluma, 2), (cuaderno, 1)]);
        await t.VenderAsync(deAna, A(2026, 10, 21, 11, 40), [(engargolado, 1)], cobros: new CobroSolicitado(FormaPago.Tarjeta, 3500, "Aut 7"));
        await t.VenderAsync(deAna, A(2026, 10, 21, 12, 5), [], comun: 1500);
        var cancelada = await t.VenderAsync(deAna, A(2026, 10, 21, 12, 30), [(cuaderno, 1)]);
        await t.CancelarAsync(cancelada, A(2026, 10, 21, 12, 35));
        var deAyerDeBeto = await t.TurnoAsync(t.Beto, A(2026, 10, 20, 16), A(2026, 10, 20, 20));
        var deAyer = await t.VenderAsync(deAyerDeBeto, A(2026, 10, 20, 17), [(pluma, 3)]);
        await t.DevolverAsync(deAyer, pluma, 1, deAna, A(2026, 10, 21, 13));
        var delLunes = await t.TurnoAsync(t.Ana, A(2026, 10, 19, 9), A(2026, 10, 19, 18), diferencia: 500);
        await t.VenderAsync(delLunes, A(2026, 10, 19, 10), [(cuaderno, 1)], cobros: new CobroSolicitado(FormaPago.Transferencia, 4000, "SPEI 1"));
        var deFebrero = await t.TurnoAsync(t.Ana, A(2026, 2, 10, 9), A(2026, 2, 10, 18));
        await t.VenderAsync(deFebrero, A(2026, 2, 10, 12), [(pluma, 2)]);

        await t.HistorialAsync(A(2025, 10, 22, 9, 30), [(cuaderno, 1, 2400), (pluma, 1, 400)]);
        var delPilotoDeAntes = await t.TurnoAsync(t.Ana, A(2025, 10, 22, 9), A(2025, 10, 22, 18));
        await t.VenderAsync(delPilotoDeAntes, A(2025, 10, 22, 11), [(engargolado, 1)]);
        await t.HistorialAsync(A(2025, 10, 22, 12), [(pluma, 2, 400)], cancelada: true);
        await t.HistorialAsync(A(2025, 10, 22, 15), [(pluma, 3, 400)]);
        await t.HistorialAsync(A(2025, 10, 20, 10), [(cuaderno, 1, 2000)]);
        await t.HistorialAsync(A(2025, 10, 23, 12), [(pluma, 7, 400)]);
        await t.HistorialAsync(A(2025, 3, 15, 12), [(cuaderno, 4, 1500)]);
        return new Semana(t, deBeto, deAna, conGoma, dePlumas, cancelada, deAyer);
    }

    [Fact]
    public async Task Hoy_cada_cifra_como_el_corte_del_dia()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;

        var r = await s.Tienda.ReporteAsync(PeriodoReporte.Hoy);

        Assert.Equal((Hoy, Hoy, Hoy, true, Ahora), (r.Desde, r.Hasta, r.Hoy, r.ALaFecha, r.AhoraLocal!.Value));
        // La venta neta: lo cobrado ($12.50 + $60 + $35 + $15) menos lo devuelto hoy ($10, de un ticket de ayer). El cuaderno
        // cancelado no cuenta; el redondeo al peso tampoco (como en el ticket).
        var a = r.Actual;
        Assert.Equal((11250L, 4, 12250L, 1000L, 1), (a.VentaCentavos, a.Tickets, a.VendidoCentavos, a.DevueltoCentavos, a.Devoluciones));
        Assert.Equal((1, 4000L), (a.Cancelados, a.CanceladosCentavos));
        Assert.Equal((0L, 0), (a.DelHistorialCentavos, a.DelHistorialTickets));
        // $112.50 entre 4 tickets: $28.125, al centavo.
        Assert.Equal(2813, a.TicketPromedioCentavos);
        // La utilidad: lo vendido con costo (sin el artículo común) menos su costo; lo devuelto se resta con el suyo. Escritura:
        // $20 + $12.50 − $10 (costos $8 + $5 − $4); cuadernos, $40 ($25); el engargolado, $35 sin costo.
        Assert.Equal(new UtilidadDelDiaDto(9750, 3400, 3500), a.Utilidad);
        Assert.Equal(6350, a.Utilidad!.UtilidadCentavos);
        Assert.Equal(6513, a.Utilidad.MargenCentesimas);
        Assert.True(r.ConUtilidad);
        Assert.False(r.Piloto);

        // Igual que el corte del día de hoy.
        await using (var db = s.Tienda.Contexto())
        {
            var corte = await new RegistroCorteDelDia(db).ArmarAsync(s.Tienda.Matriz.Id, Hoy, new Dictionary<Guid, PresenciaDeCaja>(), porDepartamento: true,
                conUtilidad: true);
            Assert.Equal(corte.VendidoCentavos - corte.DevolucionesCentavos, a.VentaCentavos);
            Assert.Equal(corte.PorDepartamento!.TotalCentavos, a.VentaCentavos);
            Assert.Equal((corte.Tickets, corte.VendidoCentavos), (a.Tickets, a.VendidoCentavos));
            Assert.Equal(corte.Utilidad, a.Utilidad);
            Assert.Equal(corte.PorDepartamento.Departamentos.Select(d => (d.Departamento, d.VentaCentavos, d.CostoCentavos)),
                r.Departamentos.Where(d => d.Clase == ClaseVenta.Departamento).Select(d => (d.Nombre, d.VentaCentavos, d.CostoCentavos)));
        }

        // Por departamento, de lo que más se vendió a lo que menos, y el artículo común al final, contra el miércoles del año pasado
        // hasta la 1:30 (el cuaderno y la pluma de eleventa y el engargolado de este POS).
        Assert.Equal(
            [
                ("Cuadernos", ClaseVenta.Departamento, 4000L, (long?)4000, (long?)2500), ("Servicios", ClaseVenta.Departamento, 3500L, 3500, 0),
                ("Escritura", ClaseVenta.Departamento, 2250L, 1000, 900), ("Artículo común", ClaseVenta.ArticuloComun, 1500L, 0, null),
            ],
            r.Departamentos.Select(d => (d.Nombre, d.Clase, d.VentaCentavos, d.AnteriorCentavos, d.CostoCentavos)));
        Assert.Equal(3750, r.Departamentos[0].MargenCentesimas);
        // La forma de pago suma la venta neta: el efectivo sin el redondeo ($88 − $0.50) y menos lo devuelto en efectivo ($10).
        Assert.Equal([new FormaPagoReporteDto(FormaPago.Efectivo, 7750), new FormaPagoReporteDto(FormaPago.Tarjeta, 3500)], r.FormasDePago);
        Assert.Equal(a.VentaCentavos, r.FormasDePago.Sum(f => f.Centavos));
        // Por cajero: Ana cobró tres; Beto, la goma, menos la pluma que se le devolvió hoy de su ticket de ayer, y su corte de hoy.
        Assert.Equal(
            [("Ana", 3, 11000L, 0, 0L, 0L, 0L), ("Beto", 1, 250L, 1, -2000L, 2000L, 0L)],
            r.Cajeros.Select(c => (c.Nombre, c.Tickets, c.VentaCentavos, c.Cortes, c.DiferenciaCentavos, c.FaltanteCentavos, c.SobranteCentavos)));
        // Lo que más se vende, por lo vendido: la pluma, dos menos la devuelta.
        Assert.Equal(
            [("Cuaderno profesional", 1000L, 4000L), ("Engargolado", 1000L, 3500L), ("Goma blanca", 1000L, 1250L), ("Pluma punto fino", 1000L, 1000L)],
            r.MasVendidos.Select(p => (p.Nombre, p.CantidadMilesimas, p.VentaCentavos)));
    }

    [Fact]
    public async Task Hoy_contra_el_mismo_dia_de_la_semana_del_anio_pasado_hasta_la_misma_hora()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;

        var r = await s.Tienda.ReporteAsync(PeriodoReporte.Hoy);

        // El miércoles 22 de octubre de 2025: 364 días antes, también miércoles. Hasta la 1:30: el ticket de eleventa de las 9:30
        // y el engargolado de este POS de las 11:00; el cancelado de las 12:00 no cuenta y el de las 3:00 todavía no.
        var desde = new DateOnly(2025, 10, 22);
        Assert.Equal((desde, desde, new DateTime(2025, 10, 22, 13, 30, 0)), (r.DesdeAnterior, r.HastaAnterior, r.CorteAnteriorLocal!.Value));
        var antes = r.Anterior!;
        Assert.Equal((8500L, 2, 8500L, 0L, 0), (antes.VentaCentavos, antes.Tickets, antes.VendidoCentavos, antes.DevueltoCentavos, antes.Cancelados));
        Assert.Equal((5000L, 1), (antes.DelHistorialCentavos, antes.DelHistorialTickets));
        // La utilidad según eleventa (su costo) y la del engargolado (sin costo).
        Assert.Equal(new UtilidadDelDiaDto(8500, 2800, 3500), antes.Utilidad);

        // La gráfica, por hora: las de la tienda (8:00 a 21:00, por omisión). Este año, hasta la de ahora (la 1, que va en
        // curso); el año pasado, todo el día.
        Assert.Equal(Enumerable.Range(8, 13).Select(h => Hoy.ToDateTime(new TimeOnly(h, 0))), r.Serie.Select(p => p.InicioLocal));
        Assert.Equal([0L, 1250, 6000, 3500, 1500, -1000, null, null, null, null, null, null, null], r.Serie.Select(p => p.ActualCentavos));
        Assert.Equal([0L, 5000, 0, 3500, 0, 0, 0, 3000, 0, 0, 0, 0, 0], r.Serie.Select(p => p.AnteriorCentavos));
        Assert.Equal([13], r.Serie.Where(p => p.EnCurso).Select(p => p.InicioLocal.Hour));
        Assert.Equal(new DateTime(2025, 10, 22, 9, 0, 0), r.Serie[1].InicioAnteriorLocal);
    }

    [Fact]
    public async Task La_semana_va_de_lunes_a_domingo_contra_la_de_hace_52_semanas()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;

        var r = await s.Tienda.ReporteAsync(PeriodoReporte.Semana);

        Assert.Equal((new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25)), (r.Desde, r.Hasta));
        Assert.Equal((new DateOnly(2025, 10, 20), new DateOnly(2025, 10, 26)), (r.DesdeAnterior, r.HastaAnterior));
        // Lo de hoy, más las plumas de ayer y el cuaderno del lunes.
        Assert.Equal((18250L, 6, 1000L), (r.Actual.VentaCentavos, r.Actual.Tickets, r.Actual.DevueltoCentavos));
        // El año pasado hasta el miércoles a la 1:30: el cuaderno del lunes y lo de esa mañana; las plumas del jueves no.
        Assert.Equal((12500L, 3), (r.Anterior!.VentaCentavos, r.Anterior.Tickets));
        // Por día, de lunes a domingo: este año, hasta hoy (en curso); el año pasado, la semana completa.
        Assert.Equal(Enumerable.Range(0, 7).Select(i => new DateOnly(2026, 10, 19).AddDays(i).ToDateTime(TimeOnly.MinValue)), r.Serie.Select(p => p.InicioLocal));
        Assert.Equal([4000L, 3000, 11250, null, null, null, null], r.Serie.Select(p => p.ActualCentavos));
        Assert.Equal([4000L, 0, 11500, 7000, 0, 0, 0], r.Serie.Select(p => p.AnteriorCentavos));
        Assert.Equal([false, false, true, false, false, false, false], r.Serie.Select(p => p.EnCurso));
        Assert.All(r.Serie, p => Assert.Equal(p.InicioLocal.DayOfWeek, p.InicioAnteriorLocal!.Value.DayOfWeek));
        // Las diferencias de corte de la semana: el sobrante de Ana el lunes y el faltante de Beto hoy.
        Assert.Equal(
            [("Ana", 4, 15000L, 1, 500L), ("Beto", 2, 3250L, 1, -2000L)],
            r.Cajeros.Select(c => (c.Nombre, c.Tickets, c.VentaCentavos, c.Cortes, c.DiferenciaCentavos)));
        Assert.Equal(
            [new FormaPagoReporteDto(FormaPago.Efectivo, 10750), new FormaPagoReporteDto(FormaPago.Tarjeta, 3500),
                new FormaPagoReporteDto(FormaPago.Transferencia, 4000)],
            r.FormasDePago);
    }

    [Fact]
    public async Task El_mes_y_el_anio_contra_las_mismas_fechas_del_anio_pasado()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;

        var mes = await s.Tienda.ReporteAsync(PeriodoReporte.Mes);
        var anio = await s.Tienda.ReporteAsync(PeriodoReporte.Anio);

        // Octubre: del 1 al 21 a la 1:30, contra lo mismo de 2025, por fecha: el cuaderno del lunes 20 de octubre de 2025 sí; lo
        // del miércoles 22 de 2025 ya no (la semana, por día de la semana, sí lo tenía).
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), (mes.Desde, mes.Hasta));
        Assert.Equal((new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31), new DateTime(2025, 10, 21, 13, 30, 0)),
            (mes.DesdeAnterior, mes.HastaAnterior, mes.CorteAnteriorLocal!.Value));
        Assert.Equal((18250L, 6), (mes.Actual.VentaCentavos, mes.Actual.Tickets));
        Assert.Equal((4000L, 1, 4000L), (mes.Anterior!.VentaCentavos, mes.Anterior.Tickets, mes.Anterior.DelHistorialCentavos));
        // Por día del mes: los 31, y el año pasado el mes completo.
        Assert.Equal(31, mes.Serie.Count);
        Assert.Equal((4000L, 3000L, 11250L, (long?)null), (mes.Serie[18].ActualCentavos!.Value, mes.Serie[19].ActualCentavos!.Value, mes.Serie[20].ActualCentavos!.Value,
            mes.Serie[21].ActualCentavos));
        Assert.Equal((4000L, 11500L, 7000L), (mes.Serie[19].AnteriorCentavos!.Value, mes.Serie[21].AnteriorCentavos!.Value, mes.Serie[22].AnteriorCentavos!.Value));
        Assert.Equal(new DateTime(2025, 10, 22, 0, 0, 0), mes.Serie[21].InicioAnteriorLocal);

        // El año: de enero al 21 de octubre, con las plumas de febrero; el año pasado, con los cuadernos de marzo.
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)), (anio.Desde, anio.Hasta));
        Assert.Equal((20250L, 7), (anio.Actual.VentaCentavos, anio.Actual.Tickets));
        Assert.Equal((20000L, 2), (anio.Anterior!.VentaCentavos, anio.Anterior.Tickets));
        Assert.Equal(12, anio.Serie.Count);
        Assert.Equal([0L, 2000, 0, 0, 0, 0, 0, 0, 0, 18250, null, null], anio.Serie.Select(p => p.ActualCentavos));
        Assert.Equal([0L, 0, 16000, 0, 0, 0, 0, 0, 0, 22500, 0, 0], anio.Serie.Select(p => p.AnteriorCentavos));
        Assert.True(anio.Serie[9].EnCurso);
        // Los departamentos del año pasado, también hasta el mismo día.
        Assert.Equal(
            [("Cuadernos", 8000L, (long?)20000), ("Escritura", 7250L, 0), ("Servicios", 3500L, 0)],
            anio.Departamentos.Where(d => d.Clase == ClaseVenta.Departamento).Select(d => (d.Nombre, d.VentaCentavos, d.AnteriorCentavos)));
    }

    [Fact]
    public async Task Un_periodo_que_ya_paso_se_compara_completo_y_uno_que_no_ha_llegado_no_se_pide()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;

        var ayer = await s.Tienda.ReporteAsync(PeriodoReporte.Hoy, new DateOnly(2026, 10, 20));
        var septiembre = await s.Tienda.ReporteAsync(PeriodoReporte.Mes, new DateOnly(2026, 9, 5));
        var manana = await Assert.ThrowsAsync<ArgumentException>(() => s.Tienda.ReporteAsync(PeriodoReporte.Hoy, Hoy.AddDays(1)));

        // Ayer: las plumas de Beto (la que se devolvió hoy se resta hoy, no ayer), contra el martes 21 de octubre de 2025, completo.
        Assert.Equal((false, (DateTime?)null, (DateTime?)null), (ayer.ALaFecha, ayer.AhoraLocal, ayer.CorteAnteriorLocal));
        Assert.Equal((3000L, 1, 0L), (ayer.Actual.VentaCentavos, ayer.Actual.Tickets, ayer.Actual.DevueltoCentavos));
        Assert.Equal(new DateOnly(2025, 10, 21), ayer.DesdeAnterior);
        // Ese martes no hubo nada: no hay con qué comparar.
        Assert.Null(ayer.Anterior);
        Assert.All(ayer.Serie, p => Assert.Null(p.AnteriorCentavos));
        Assert.All(ayer.Serie, p => Assert.False(p.EnCurso));
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 30), (septiembre.Desde, septiembre.Hasta, septiembre.Serie.Count));
        Assert.Equal(ReportesDeVentas.TodaviaNoLlega, manana.Message);
    }

    [Fact]
    public async Task Los_dias_son_de_la_tienda_de_medianoche_a_medianoche()
    {
        await using var t = await TiendaDeReportes.CrearAsync(Ahora);
        var turno = await t.TurnoAsync(t.Ana, A(2026, 10, 20, 9), A(2026, 10, 21, 10));
        // Un segundo antes de la medianoche es de ayer; a la medianoche, de hoy (en UTC, 05:59:59 y 06:00 del 21).
        await t.VenderAsync(turno, A(2026, 10, 20, 23, 59, 59), [(t.Pluma, 1)]);
        await t.VenderAsync(turno, A(2026, 10, 21, 0, 0, 0), [(t.Cuaderno, 1)]);
        Assert.Equal(new DateTime(2026, 10, 21, 6, 0, 0, DateTimeKind.Utc), A(2026, 10, 21, 0));

        var hoy = await t.ReporteAsync(PeriodoReporte.Hoy);
        var ayer = await t.ReporteAsync(PeriodoReporte.Hoy, Hoy.AddDays(-1));

        Assert.Equal((4000L, 1), (hoy.Actual.VentaCentavos, hoy.Actual.Tickets));
        Assert.Equal((1000L, 1), (ayer.Actual.VentaCentavos, ayer.Actual.Tickets));
        // Cada una en su hora: la de la medianoche abre la gráfica de hoy (antes de las 8, la hora de abrir) y la otra cierra la de
        // ayer.
        Assert.Equal((0, 4000L), (hoy.Serie[0].InicioLocal.Hour, hoy.Serie[0].ActualCentavos!.Value));
        Assert.Equal((23, 1000L), (ayer.Serie[^1].InicioLocal.Hour, ayer.Serie[^1].ActualCentavos!.Value));
    }

    /// <summary>
    /// México dejó el horario de verano en octubre de 2022 (<see cref="HoraMexico"/> lo trae de la zona del sistema): un día de
    /// julio de 2022 empezaba a las 05:00 UTC, no a las 06:00. El viernes 14 de julio de 2023 se compara con el viernes 15 de
    /// julio de 2022, con sus horas de entonces.
    /// </summary>
    [Fact]
    public async Task El_horario_de_verano_de_antes_de_2022_cuenta_en_el_anio_pasado()
    {
        await using var t = await TiendaDeReportes.CrearAsync(Ahora);
        Assert.Equal(new DateTime(2022, 7, 15, 5, 0, 0, DateTimeKind.Utc), A(2022, 7, 15, 0));
        Assert.Equal(new DateTime(2023, 7, 14, 6, 0, 0, DateTimeKind.Utc), A(2023, 7, 14, 0));
        DateTime Utc(int dia, int hora, int minuto) => new(2022, 7, dia, hora, minuto, 0, DateTimeKind.Utc);
        // El 14 a las 23:30 (04:30 UTC del 15) no es del 15; el 15 a las 00:30 (05:30 UTC) y a las 23:59 (04:59 UTC del 16) sí; el 16 a
        // las 00:00 (05:00 UTC) ya no. Con la hora de invierno (UTC−6) habrían caído en otro día.
        await t.HistorialAsync(Utc(15, 4, 30), [(t.Pluma, 1, 400)]);
        await t.HistorialAsync(Utc(15, 5, 30), [(t.Cuaderno, 1, 2500)]);
        await t.HistorialAsync(Utc(16, 4, 59), [(t.Goma, 2, 500)]);
        await t.HistorialAsync(Utc(16, 5, 0), [(t.Pluma, 5, 400)]);
        // El 14 de julio de 2023, sin horario de verano: de las 06:00 UTC a las 06:00 UTC.
        await t.HistorialAsync(new DateTime(2023, 7, 14, 5, 59, 0, DateTimeKind.Utc), [(t.Pluma, 9, 400)]);
        await t.HistorialAsync(new DateTime(2023, 7, 14, 6, 0, 0, DateTimeKind.Utc), [(t.Engargolado, 1, 0)]);

        var r = await t.ReporteAsync(PeriodoReporte.Hoy, new DateOnly(2023, 7, 14));

        Assert.Equal((new DateOnly(2022, 7, 15), DayOfWeek.Friday, DayOfWeek.Friday), (r.DesdeAnterior, r.Desde.DayOfWeek, r.DesdeAnterior.DayOfWeek));
        Assert.Equal((3500L, 1), (r.Actual.VentaCentavos, r.Actual.Tickets));
        Assert.Equal((6500L, 2), (r.Anterior!.VentaCentavos, r.Anterior.Tickets));
        // Cada una en su hora de entonces: el cuaderno a las 00:30 y las gomas a las 23:59.
        Assert.Equal((0, 4000L), (r.Serie[0].InicioLocal.Hour, r.Serie[0].AnteriorCentavos!.Value));
        Assert.Equal((23, 2500L), (r.Serie[^1].InicioLocal.Hour, r.Serie[^1].AnteriorCentavos!.Value));
        Assert.Equal(3500L, r.Serie.Single(p => p.InicioLocal.Hour == 0).ActualCentavos);
    }

    [Fact]
    public async Task Sin_ver_reportes_no_se_ve_y_sin_ver_la_utilidad_no_viene_ningun_costo()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;
        var t = s.Tienda;

        // Carmen, la encargada: sin «Ver reportes de ventas» (al migrar, solo el dueño), nada; con él en «Con autorización»,
        // tampoco: consultar no se autoriza.
        var sinPermiso = await Assert.ThrowsAsync<PermisoRequerido>(() => t.ReporteAsync(PeriodoReporte.Hoy, quien: t.Carmen));
        await t.PermisoAsync(t.Carmen, AccionControlada.VerReportes, NivelPermiso.RequiereAutorizacion);
        var conAutorizacion = await Assert.ThrowsAsync<PermisoRequerido>(() => t.ReporteAsync(PeriodoReporte.Hoy, quien: t.Carmen));
        await t.PermisoAsync(t.Carmen, AccionControlada.VerReportes, NivelPermiso.Permitido);
        var r = await t.ReporteAsync(PeriodoReporte.Hoy, quien: t.Carmen);

        Assert.Equal(AccionControlada.VerReportes, sinPermiso.Accion);
        Assert.Equal("Esto lo hace quien tiene el permiso «Ver reportes de ventas»: Luis.", sinPermiso.Message);
        Assert.Equal(AccionControlada.VerReportes, conAutorizacion.Accion);
        // Con los reportes y sin «Ver costos, utilidad y margen»: lo mismo, sin utilidad ni costos.
        Assert.False(r.ConUtilidad);
        Assert.Equal(11250, r.Actual.VentaCentavos);
        Assert.Null(r.Actual.Utilidad);
        Assert.Null(r.Anterior!.Utilidad);
        Assert.All(r.Departamentos, d => Assert.Null(d.CostoCentavos));
        Assert.All(r.Departamentos, d => Assert.Null(d.MargenCentesimas));
        var json = System.Text.Json.JsonSerializer.Serialize(r, System.Text.Json.JsonSerializerOptions.Web);
        Assert.DoesNotContain("costo", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"utilidad\":", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// El piloto con eleventa: la base de la caja 2 tiene lo que vendió ella y el historial de toda la tienda de cuando se armó.
    /// Juntarlos mezclaría lo de una caja con lo de las dos: el reporte es solo de lo suyo, sin el año pasado.
    /// </summary>
    [Fact]
    public async Task En_el_piloto_solo_cuenta_lo_de_la_caja_2_y_no_se_compara()
    {
        var s = await SemanaAsync();
        await using var _ = s.Tienda;
        // Lo que la importación de eleventa trajo de este mismo año (de toda la tienda).
        await s.Tienda.HistorialAsync(A(2026, 10, 21, 9), [(s.Tienda.Cuaderno, 2, 2500)]);

        var fueraDelPiloto = await s.Tienda.ReporteAsync(PeriodoReporte.Hoy);
        var piloto = await s.Tienda.ReporteAsync(PeriodoReporte.Hoy, piloto: true);

        Assert.Equal((19250L, 8000L), (fueraDelPiloto.Actual.VentaCentavos, fueraDelPiloto.Actual.DelHistorialCentavos));
        Assert.True(piloto.Piloto);
        Assert.Equal((11250L, 0L, 4), (piloto.Actual.VentaCentavos, piloto.Actual.DelHistorialCentavos, piloto.Actual.Tickets));
        Assert.Null(piloto.Anterior);
        Assert.All(piloto.Serie, p => Assert.Null(p.AnteriorCentavos));
        Assert.All(piloto.Serie, p => Assert.Null(p.InicioAnteriorLocal));
        Assert.All(piloto.Departamentos, d => Assert.Null(d.AnteriorCentavos));
    }

    [Fact]
    public async Task Lo_mas_vendido_junta_lo_que_se_capturo_dos_veces_y_sin_las_impresiones_ni_el_comun()
    {
        await using var t = await TiendaDeReportes.CrearAsync(Ahora);
        var turno = await t.TurnoAsync(t.Ana, A(2026, 10, 21, 9));
        await t.VenderAsync(turno, A(2026, 10, 21, 10), [(t.Pluma, 3), (t.Goma, 2)], comun: 99_900);
        await t.VenderAsync(turno, A(2026, 10, 21, 11), [(t.Goma, 1)]);
        // La goma estaba capturada dos veces: la pluma se juntó en ella (la historia de la pluma se queda en la suya).
        await using (var db = t.Contexto())
        {
            var pluma = await db.Variantes.SingleAsync(v => v.Id == t.Pluma.Variante.Id);
            (pluma.Activa, pluma.JuntadaEnId) = (false, t.Goma.Variante.Id);
            await db.SaveChangesAsync();
        }

        var r = await t.ReporteAsync(PeriodoReporte.Hoy);

        Assert.Equal([("Goma blanca", 6000L, 6750L)], r.MasVendidos.Select(p => (p.Nombre, p.CantidadMilesimas, p.VentaCentavos)));
        Assert.Equal(UnidadBase.Pieza, r.MasVendidos[0].Unidad);
        // En los departamentos cada uno sigue en el suyo, como en el corte del día.
        Assert.Equal(6750L, r.Departamentos.Single(d => d.Nombre == "Escritura").VentaCentavos);
    }
}
