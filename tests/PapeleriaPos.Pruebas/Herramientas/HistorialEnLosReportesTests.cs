using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Herramientas.Eleventa;
using PapeleriaPos.Herramientas.Importacion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Datos;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>
/// El historial de eleventa en los reportes de ventas (revisión del PR #131, 2026-10-22), importado como en la tienda: su
/// artículo común cuenta como el de aquí (aparte y fuera de la utilidad) y lo que el total de un ticket tiene de más o de menos
/// contra sus artículos sale «Sin desglose», así las ventas por departamento suman la venta neta. La importación avisa de esos
/// tickets.
/// </summary>
public sealed class HistorialEnLosReportesTests : IAsyncDisposable
{
    /// <summary>Hoy en las pruebas: el miércoles 21 de octubre de 2026 a la 1:30 de la tarde.</summary>
    private static readonly DateTime Ahora = new(2026, 10, 21, 13, 30, 0);

    /// <summary>El miércoles 22 de octubre de 2025 a las 10:00: 364 días antes de hoy, el mismo día de la semana.</summary>
    private static readonly DateTime HaceUnAnio = new(2025, 10, 22, 10, 0, 0);

    private BaseEnMemoria? _base;

    public async ValueTask DisposeAsync()
    {
        if (_base is not null)
            await _base.DisposeAsync();
    }

    private sealed record Tienda(Guid SucursalId, Caja Caja, Usuario Dueno);

    /// <summary>La base como la deja importar-eleventa con esos datos, con la Caja 1 y el dueño (todos los permisos).</summary>
    private async Task<(Tienda Tienda, ResultadoImportacion Importado)> ImportarAsync(DatosEleventa e)
    {
        _base = await BaseEnMemoria.CrearAsync();
        _base.Reloj.Ahora = new DateTimeOffset(HoraMexico.AUtc(Ahora), TimeSpan.Zero);
        var importado = ImportadorEleventa.Importar(e);
        await using var db = _base.Contexto();
        await GuardadoLote.GuardarAsync(importado, db);
        var sucursal = await db.Sucursales.SingleAsync();
        var caja = new Caja { Nombre = "Caja 1", Serie = "A", SucursalId = sucursal.Id, EsPrincipal = true };
        var dueno = new Usuario { Nombre = "Luis", NombreUsuario = "luis", Rol = Rol.Dueno };
        dueno.EstablecerPin("1234");
        db.AddRange(caja, dueno);
        await db.SaveChangesAsync();
        await MigracionPermisos.AplicarAsync(db);
        return (new Tienda(sucursal.Id, caja, dueno), importado);
    }

    private async Task<ReporteVentasDto> ReporteAsync(Tienda t, DateOnly? fecha = null)
    {
        await using var db = _base!.Contexto();
        return await new ReportesDeVentas(db).ArmarAsync(t.Caja.Id, t.Dueno.Id, PeriodoReporte.Hoy, fecha);
    }

    /// <summary>
    /// El mismo negocio los dos años: una pluma de $10 (cuesta $4) y un artículo común de $15. El año pasado, en eleventa (su
    /// común lleva el código COMUN-…, el departamento «Productos Comunes» y la ganancia igual al precio); hoy, en este POS. La
    /// venta, la utilidad y el artículo común salen iguales.
    /// </summary>
    [Fact]
    public async Task El_articulo_comun_de_eleventa_cuenta_como_el_de_aqui_aparte_y_sin_utilidad()
    {
        var (t, importado) = await ImportarAsync(new DatosEleventa
        {
            Departamentos = [new(1, "Escritura", true), new(99, EnvioEleventa.DepartamentoComunes, true)],
            Productos = [new FilaProducto(1, "P1", "Pluma", 'U', 1, 4, 10, 0, 1, false)],
            Existencias = [new(1, 50)],
            Tickets = [new FilaTicket(10, 100, HaceUnAnio, 25, false)],
            Lineas = [new FilaLinea(10, "P1", "Pluma", 1, 10, 10, 6, 1, false), new FilaLinea(10, "COMUN-4821-1", "Recuerdo", 1, 15, 15, 15, 99, false)],
        });
        // Importado: el común, marcado y sin costo; la pluma, no.
        var lineas = importado.Ventas.Single().Lineas;
        Assert.Equal([(1000L, 400L, false), (1500L, 0L, true)], lineas.Select(l => (l.ImporteCentavos, l.CostoCentavos, l.EsArticuloComun)));
        await using (var db = _base!.Contexto())
        {
            // Hoy, a las 10:00, lo mismo en este POS.
            var turno = new Turno { SucursalId = t.SucursalId, CajaId = t.Caja.Id, CajeroId = t.Dueno.Id, AbiertoUtc = TiendaDeReportes.A(2026, 10, 21, 8) };
            db.Turnos.Add(turno);
            var variante = await db.CodigosBarras.Where(c => c.Codigo == "P1").Select(c => c.Presentacion!.VarianteId).SingleAsync();
            var venta = new Venta { SucursalId = t.SucursalId, CajaId = t.Caja.Id, TurnoId = turno.Id, CajeroId = t.Dueno.Id, Serie = "A", CreadaUtc = TiendaDeReportes.A(2026, 10, 21, 10) };
            venta.AgregarProducto(new ProductoEnVenta(variante, "Pluma", Impuesto.Iva16, UnidadBase.Pieza, true), await db.Presentaciones.SingleAsync(p => p.VarianteId == variante && p.EsBase),
                Cantidad.Piezas(1), await db.Precios.Where(p => p.VarianteId == variante).ToListAsync()).CostoCentavos = 400;
            venta.AgregarArticuloComun("Recuerdo", 1500, Cantidad.Piezas(1));
            venta.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 2500)], TiendaDeReportes.A(2026, 10, 21, 10));
            db.Ventas.Add(venta);
            await db.SaveChangesAsync();
        }

        var r = await ReporteAsync(t);

        Assert.Equal((2500L, 2500L), (r.Actual.VentaCentavos, r.Anterior!.VentaCentavos));
        Assert.Equal(new UtilidadDelDiaDto(1000, 400, 0), r.Actual.Utilidad);
        Assert.Equal(r.Actual.Utilidad, r.Anterior.Utilidad);
        Assert.Equal(
            [("Escritura", ClaseVenta.Departamento, 1000L, (long?)1000), ("Artículo común", ClaseVenta.ArticuloComun, 1500L, 1500)],
            r.Departamentos.Select(d => (d.Nombre, d.Clase, d.VentaCentavos, d.AnteriorCentavos)));
        // El común de eleventa no es un producto de «lo que más se vende».
        var anioPasado = await ReporteAsync(t, DateOnly.FromDateTime(HaceUnAnio));
        Assert.Equal(["Pluma"], anioPasado.MasVendidos.Select(p => p.Nombre));
        Assert.Equal(1500L, anioPasado.Departamentos.Single(d => d.Clase == ClaseVenta.ArticuloComun).VentaCentavos);
    }

    /// <summary>
    /// Un artículo común es el de código COMUN-… o, de un código que no está en el catálogo de eleventa, el que va en
    /// «Productos Comunes». Un producto del catálogo en ese departamento sigue siendo un producto. Los comunes van todos al
    /// producto de los comunes de eleventa (2026-10-24: antes, uno por cada uno).
    /// </summary>
    [Fact]
    public async Task Es_comun_lo_de_codigo_COMUN_o_lo_que_no_es_del_catalogo_en_productos_comunes()
    {
        var (_, importado) = await ImportarAsync(new DatosEleventa
        {
            Departamentos = [new(1, "Escritura", true), new(99, " productos comunes ", true)],
            Productos = [new FilaProducto(1, "P1", "Pluma", 'U', 1, 4, 10, 0, 1, false), new FilaProducto(2, "BOLSA", "Bolsa de regalo", 'U', 1, 3, 8, 0, 99, false)],
            Tickets = [new FilaTicket(10, 100, HaceUnAnio, 46, false)],
            Lineas =
            [
                new FilaLinea(10, "P1", "Pluma", 1, 10, 10, 6, 1, false), new FilaLinea(10, "comun-7-2", "Recuerdo", 1, 15, 15, 15, null, false),
                new FilaLinea(10, "VIEJO-1", "Tarjeta", 1, 13, 13, 13, 99, false), new FilaLinea(10, "BOLSA", "Bolsa de regalo", 1, 8, 8, 5, 99, false),
            ],
        });

        Assert.Equal([("Pluma", false), (ImportadorEleventa.ArticuloComun, true), (ImportadorEleventa.ArticuloComun, true), ("Bolsa de regalo", false)],
            importado.Ventas.Single().Lineas.Select(l => (importado.Productos.Single(p => importado.Variantes.Single(v => v.Id == l.VarianteId).ProductoId == p.Id).Nombre,
                l.EsArticuloComun)));
    }

    /// <summary>
    /// Dos tickets de eleventa cuyo total no es la suma de sus artículos: uno de $25 con una sola pluma de $10 (le faltan $15) y
    /// otro de $10 con dos plumas y una línea de −1 (no se importa: sin ella le sobran $10). La importación los avisa; en el
    /// reporte, los $5 de diferencia salen «Sin desglose» y las ventas por departamento suman la venta neta.
    /// </summary>
    [Fact]
    public async Task Lo_que_un_ticket_de_eleventa_suma_sin_desglose_sale_aparte_y_la_importacion_lo_avisa()
    {
        var (t, importado) = await ImportarAsync(new DatosEleventa
        {
            Departamentos = [new(1, "Escritura", true)],
            Productos = [new FilaProducto(1, "P1", "Pluma", 'U', 1, 4, 10, 0, 1, false)],
            Tickets = [new FilaTicket(10, 100, HaceUnAnio, 25, false), new FilaTicket(11, 101, HaceUnAnio.AddMinutes(5), 10, false),
                new FilaTicket(12, 102, HaceUnAnio.AddMinutes(10), 99, true)],
            Lineas =
            [
                new FilaLinea(10, "P1", "Pluma", 1, 10, 10, 6, 1, false),
                new FilaLinea(11, "P1", "Pluma", 2, 20, 10, 6, 1, false), new FilaLinea(11, "P1", "Pluma", -1, -10, 10, 6, 1, false),
                // Uno cancelado no cuenta, cuadre o no.
                new FilaLinea(12, "P1", "Pluma", 1, 10, 10, 6, 1, false),
            ],
        });
        Assert.Equal(2, importado.Avisos[ImportadorEleventa.AvisoTotalSinDesglose]);
        Assert.Equal(1, importado.Avisos[ImportadorEleventa.AvisoLineaSinCantidad]);

        var hoy = await ReporteAsync(t);
        var anioPasado = await ReporteAsync(t, DateOnly.FromDateTime(HaceUnAnio));

        // El año pasado a estas alturas: $35 de venta neta, $30 de plumas y $5 sin desglose (sin costo ni utilidad).
        Assert.Equal(3500L, hoy.Anterior!.VentaCentavos);
        Assert.Equal([("Escritura", ClaseVenta.Departamento, 0L, (long?)3000), ("Sin desglose", ClaseVenta.SinDesglose, 0L, 500)],
            hoy.Departamentos.Select(d => (d.Nombre, d.Clase, d.VentaCentavos, d.AnteriorCentavos)));
        Assert.Equal(new UtilidadDelDiaDto(3000, 1200, 0), hoy.Anterior.Utilidad);
        // Ese día, como periodo: lo mismo, y suma la venta neta.
        Assert.Equal(3500L, anioPasado.Actual.VentaCentavos);
        Assert.Equal(anioPasado.Actual.VentaCentavos, anioPasado.Departamentos.Sum(d => d.VentaCentavos));
        Assert.Equal((500L, (long?)null), (anioPasado.Departamentos.Single(d => d.Clase == ClaseVenta.SinDesglose).VentaCentavos,
            anioPasado.Departamentos.Single(d => d.Clase == ClaseVenta.SinDesglose).CostoCentavos));
    }
}
