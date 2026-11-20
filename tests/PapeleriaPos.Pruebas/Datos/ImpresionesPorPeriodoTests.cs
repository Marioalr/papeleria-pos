using PapeleriaPos.Datos;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// Reportes › Impresiones por período (2026-11-20), contra impresiones hechas a mano en la tienda de los reportes de ventas: los
/// totales, los grupos (tipo, quien atendió, PC, impresora y día), las no vendidas por motivo, las hojas que faltaron, el costo solo
/// con su permiso, los días de la tienda y lo que no se entiende.
/// </summary>
public sealed class ImpresionesPorPeriodoTests
{
    /// <summary>Hoy en las pruebas: el miércoles 21 de octubre de 2026 a la 1:30 de la tarde.</summary>
    private static readonly DateTime Ahora = new(2026, 10, 21, 13, 30, 0);

    private static readonly DateOnly Hoy = DateOnly.FromDateTime(Ahora);

    private static DateTime A(int anio, int mes, int dia, int hora, int minuto = 0) => TiendaDeReportes.A(anio, mes, dia, hora, minuto);

    /// <summary>
    /// Dos impresoras (la WF-6590 y la L5590) y, del martes 20 a hoy:
    /// <list type="bullet">
    /// <item>Martes 20, 23:50, Ana en CAJA1 (WF-6590): una tarea a color, 4 caras, $20 (cuesta $6), cobrada hoy: cuenta el martes.</item>
    /// <item>Hoy, 9:00, Ana en CAJA1 (WF-6590): 10 caras B/N, $15 (cuesta $3), cobrada.</item>
    /// <item>Hoy, 10:00, Beto en CAJA2 (L5590): 6 caras a color, $30 (cuesta $9), por cobrar; la impresora contó 4: faltaron hojas.</item>
    /// <item>Hoy, 11:00, Beto en CAJA2 (L5590): 2 caras, $5 (cuesta $1), no vendida porque salió mal.</item>
    /// <item>Hoy, 11:30, sin sesión, copias del panel de la WF-6590: 20 caras, $20 (cuesta $4), cobradas.</item>
    /// <item>Hoy, 12:00, la tarjeta del contador de la WF-6590: 8 caras, $8 (cuesta $2), por cobrar (contó 8: no es una falta).</item>
    /// <item>Hoy, 12:30, Ana en CAJA1: la copia de un trámite, 1 cara, sin precio (cuesta $0.50).</item>
    /// <item>El lunes 19 a las 9:00 y mañana no cuentan; tampoco lo de otra sucursal.</item>
    /// </list>
    /// </summary>
    private static async Task<TiendaDeReportes> TiendaAsync()
    {
        var t = await TiendaDeReportes.CrearAsync(Ahora);
        await using var db = t.Contexto();
        var wf = new Impresora { SucursalId = t.Matriz.Id, Numero = 1, Nombre = "WF-6590" };
        var l5590 = new Impresora { SucursalId = t.Matriz.Id, Numero = 2, Nombre = "L5590" };
        db.Set<Impresora>().AddRange(wf, l5590);
        var centro = new Sucursal { Clave = "C", Nombre = "Centro" };
        db.Sucursales.Add(centro);
        void Trabajo(DateTime cuando, Usuario? quien, string pc, Impresora impresora, int caras, long precio, long costo, EstadoCobroImpresion cobro,
            TipoTrabajoImpresion tipo = TipoTrabajoImpresion.Impresion, int color = 0, int? contadas = null, MotivoNoVendida? motivo = null,
            Guid? sucursal = null, string documento = "Tarea.pdf") =>
            db.Set<TrabajoImpresion>().Add(new TrabajoImpresion
            {
                SucursalId = sucursal ?? t.Matriz.Id, CajaId = t.Caja.Id, ImpresoraId = impresora.Id, Tipo = tipo, AtendioId = quien?.Id, Pc = pc, Cola = "Cola",
                Documento = documento, ImpresoUtc = cuando, Paginas = caras, Copias = 1, Caras = caras, CarasColor = color, Hojas = caras, Color = color > 0,
                Tamano = "Carta", TipoPapel = "Bond", Calidad = "Normal", DetalleJson = "{}", Miniatura = [1, 2, 3], CostoCentavos = costo,
                PrecioCentavos = precio, Cobro = cobro, CarasContadas = contadas, MotivoNoVendida = motivo,
            });
        Trabajo(A(2026, 10, 20, 23, 50), t.Ana, "CAJA1", wf, 4, 2000, 600, EstadoCobroImpresion.Cobrada, color: 4);
        Trabajo(A(2026, 10, 21, 9), t.Ana, "CAJA1", wf, 10, 1500, 300, EstadoCobroImpresion.Cobrada);
        Trabajo(A(2026, 10, 21, 10), t.Beto, "CAJA2", l5590, 6, 3000, 900, EstadoCobroImpresion.PorCobrar, color: 6, contadas: 4, documento: "Cartel.pdf");
        Trabajo(A(2026, 10, 21, 11), t.Beto, "CAJA2", l5590, 2, 500, 100, EstadoCobroImpresion.NoVendida, motivo: MotivoNoVendida.SalioMal);
        Trabajo(A(2026, 10, 21, 11, 30), null, "CAJA1", wf, 20, 2000, 400, EstadoCobroImpresion.Cobrada, TipoTrabajoImpresion.Copia);
        Trabajo(A(2026, 10, 21, 12), null, "CAJA1", wf, 8, 800, 200, EstadoCobroImpresion.PorCobrar, TipoTrabajoImpresion.Contador, contadas: 8);
        Trabajo(A(2026, 10, 21, 12, 30), t.Ana, "CAJA1", wf, 1, 0, 50, EstadoCobroImpresion.EnTramite);
        Trabajo(A(2026, 10, 19, 9), t.Ana, "CAJA1", wf, 3, 900, 100, EstadoCobroImpresion.Cobrada);
        Trabajo(A(2026, 10, 22, 9), t.Ana, "CAJA1", wf, 3, 900, 100, EstadoCobroImpresion.PorCobrar);
        Trabajo(A(2026, 10, 21, 9), t.Ana, "CAJA1", wf, 3, 900, 100, EstadoCobroImpresion.Cobrada, sucursal: centro.Id);
        await db.SaveChangesAsync();
        return t;
    }

    private static async Task<ImpresionesPorPeriodoDto> ReporteAsync(TiendaDeReportes t, DateOnly? desde, DateOnly? hasta, Usuario? quien = null,
        bool conTrabajos = false, bool piloto = false)
    {
        await using var db = t.Contexto();
        return await new ReporteImpresionesPorPeriodo(db).ArmarAsync(t.Caja.Id, (quien ?? t.Luis).Id, desde, hasta, piloto, conTrabajos);
    }

    [Fact]
    public async Task Los_totales_de_esos_dias_cuentan_por_el_dia_en_que_se_imprimio_con_su_cobro_y_su_costo()
    {
        await using var t = await TiendaAsync();

        var d = await ReporteAsync(t, new DateOnly(2026, 10, 20), Hoy);

        Assert.Equal((new DateOnly(2026, 10, 20), Hoy, Hoy, true, false), (d.Desde, d.Hasta, d.Hoy, d.VeCosto, d.Piloto));
        var x = d.Totales;
        Assert.Equal((7, 51, 10, 51), (x.Trabajos, x.Caras, x.CarasColor, x.Hojas));
        // Cobradas: la del martes, la de hoy y las copias ($20 + $15 + $20); por cobrar: la de la L5590 y la tarjeta del contador.
        Assert.Equal((3, 5500L, 2, 3800L, 1, 500L, 1), (x.Cobradas, x.CobradoCentavos, x.PorCobrar, x.PorCobrarCentavos, x.NoVendidas, x.NoVendidasCentavos, x.EnTramite));
        Assert.Equal((2550L, 1300L, 100L, 4200L), (x.CostoCentavos, x.CostoCobradoCentavos, x.PerdidaCentavos, x.UtilidadCentavos));
        // Cada día: el martes solo la de las 23:50 (la de medianoche es del día en que salió).
        Assert.Equal([(new DateOnly(2026, 10, 20), 1, 2000L), (Hoy, 6, 3500L)], d.Dias.Select(r => (r.Fecha!.Value, r.Trabajos, r.CobradoCentavos)));
        Assert.Empty(d.Trabajos);
    }

    [Fact]
    public async Task Por_tipo_por_quien_atendio_por_pc_y_por_impresora_del_que_mas_cobro_al_que_menos()
    {
        await using var t = await TiendaAsync();

        var d = await ReporteAsync(t, new DateOnly(2026, 10, 20), Hoy);

        Assert.Equal([("Desde las PC", 5, 3500L), ("Copias del panel", 1, 2000L), ("Copias o celular (por el contador)", 1, 0L)],
            d.PorTipo.Select(r => (r.Nombre!, r.Trabajos, r.CobradoCentavos)));
        Assert.Equal([("Ana", 3, 3500L), (ReporteImpresionesPorPeriodo.SinSesion, 2, 2000L), ("Beto", 2, 0L)],
            d.PorAtendio.Select(r => (r.Nombre!, r.Trabajos, r.CobradoCentavos)));
        Assert.Equal([("CAJA1", 5, 5500L), ("CAJA2", 2, 0L)], d.PorPc.Select(r => (r.Nombre!, r.Trabajos, r.CobradoCentavos)));
        Assert.Equal([("WF-6590", 5, 5500L, 1550L), ("L5590", 2, 0L, 1000L)],
            d.PorImpresora.Select(r => (r.Nombre!, r.Trabajos, r.CobradoCentavos, r.CostoCentavos!.Value)));
    }

    [Fact]
    public async Task Las_no_vendidas_por_motivo_y_las_hojas_que_faltaron_solo_de_lo_que_salio_de_una_pc()
    {
        await using var t = await TiendaAsync();

        var d = await ReporteAsync(t, Hoy, Hoy);

        var noVendida = Assert.Single(d.NoVendidas);
        Assert.Equal((MotivoNoVendida.SalioMal, 1, 2, 500L, (long?)100), (noVendida.Motivo, noVendida.Cuantas, noVendida.Caras, noVendida.PrecioCentavos, noVendida.CostoCentavos));
        // La tarjeta del contador también tiene caras contadas, pero son las suyas: no es una falta.
        var falta = Assert.Single(d.Faltaron);
        Assert.Equal(("Cartel.pdf", "CAJA2", "L5590", "Beto", 6, 4, EstadoCobroImpresion.PorCobrar, false, 3000L),
            (falta.Documento, falta.Pc, falta.Impresora, falta.Atendio, falta.Caras, falta.CarasContadas, falta.Cobro, falta.SoloLoQueSalio, falta.PrecioCentavos));
        Assert.Equal(new DateTime(2026, 10, 21, 10, 0, 0), falta.FechaLocal);
    }

    [Fact]
    public async Task Sin_ver_costos_no_llevan_costo_utilidad_ni_perdida_y_para_el_excel_va_cada_impresion()
    {
        await using var t = await TiendaAsync();
        await t.PermisoAsync(t.Carmen, AccionControlada.VerReportes, NivelPermiso.Permitido);
        await t.PermisoAsync(t.Carmen, AccionControlada.VerUtilidad, NivelPermiso.NoPermitido);

        var d = await ReporteAsync(t, Hoy, Hoy, t.Carmen, conTrabajos: true, piloto: true);

        Assert.False(d.VeCosto);
        Assert.True(d.Piloto);
        Assert.All(new[] { d.Totales }.Concat(d.Dias).Concat(d.PorTipo).Concat(d.PorAtendio).Concat(d.PorPc).Concat(d.PorImpresora),
            r => Assert.Equal(((long?)null, (long?)null, (long?)null, (long?)null), (r.CostoCentavos, r.CostoCobradoCentavos, r.PerdidaCentavos, r.UtilidadCentavos)));
        Assert.All(d.NoVendidas, n => Assert.Null(n.CostoCentavos));
        // Cada impresión de hoy, en el orden en que salió, sin costo.
        Assert.Equal(6, d.Trabajos.Count);
        Assert.Equal(d.Trabajos.OrderBy(x => x.FechaLocal).Select(x => x.Id), d.Trabajos.Select(x => x.Id));
        Assert.All(d.Trabajos, x => Assert.Null(x.CostoCentavos));
        Assert.Equal(("Copias del panel", ReporteImpresionesPorPeriodo.SinSesion),
            (TextosDeImpresionesPorPeriodo.Tipo(d.Trabajos[3].Tipo), d.Trabajos[3].Atendio));
    }

    [Fact]
    public async Task Sin_fechas_es_hoy_y_sin_impresiones_da_ceros()
    {
        await using var t = await TiendaAsync();

        var hoy = await ReporteAsync(t, null, null);
        Assert.Equal((Hoy, Hoy, 6), (hoy.Desde, hoy.Hasta, hoy.Totales.Trabajos));

        var vacio = await ReporteAsync(t, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10));
        Assert.Equal((0, 0L), (vacio.Totales.Trabajos, vacio.Totales.CobradoCentavos));
        Assert.Empty(vacio.Dias);
        Assert.Empty(vacio.PorPc);
        Assert.Empty(vacio.Faltaron);
    }

    [Fact]
    public async Task Sin_ver_reportes_no_se_ve_y_los_dias_se_revisan_como_en_las_ventas_por_periodo()
    {
        await using var t = await TiendaAsync();

        var sinPermiso = await Assert.ThrowsAsync<PermisoRequerido>(() => ReporteAsync(t, Hoy, Hoy, t.Ana));
        Assert.Contains("«Ver reportes de ventas»", sinPermiso.Message);
        await t.PermisoAsync(t.Carmen, AccionControlada.VerReportes, NivelPermiso.RequiereAutorizacion);
        await Assert.ThrowsAsync<PermisoRequerido>(() => ReporteAsync(t, Hoy, Hoy, t.Carmen));

        Assert.Equal(ReporteVentasPorPeriodo.TodaviaNoLlega, (await Assert.ThrowsAsync<ArgumentException>(() => ReporteAsync(t, Hoy.AddDays(1), null))).Message);
        Assert.Equal(ReporteVentasPorPeriodo.AlReves, (await Assert.ThrowsAsync<ArgumentException>(() => ReporteAsync(t, Hoy, Hoy.AddDays(-1)))).Message);
        Assert.Equal(ReporteVentasPorPeriodo.AntesDelPrimerDia,
            (await Assert.ThrowsAsync<ArgumentException>(() => ReporteAsync(t, new DateOnly(1999, 12, 31), Hoy))).Message);
        Assert.Equal(ReporteVentasPorPeriodo.MasDeTresAnios,
            (await Assert.ThrowsAsync<ArgumentException>(() => ReporteAsync(t, new DateOnly(2020, 1, 1), Hoy))).Message);
        // El último día, si pasa de hoy, llega hasta hoy.
        Assert.Equal(Hoy, (await ReporteAsync(t, Hoy, Hoy.AddDays(5))).Hasta);
    }
}
