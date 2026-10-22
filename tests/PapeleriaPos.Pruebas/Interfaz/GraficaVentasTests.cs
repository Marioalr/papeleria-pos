using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Reportes;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La gráfica de los reportes de ventas (entrega 9), sola: las barras, la línea del año pasado (cortada donde ese día no
/// existe), lo que todavía no llega y un periodo sin ninguna venta, que también se dibuja.
/// </summary>
public class GraficaVentasTests
{
    private static readonly CifrasReporteDto Nada = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static ReporteVentasDto Reporte(PeriodoReporte periodo, DateOnly dia, DateTime ahora, Func<int, PuntoReporteDto, PuntoReporteDto> punto)
    {
        var p = PeriodoDeReporte.De(periodo, dia, ahora);
        var serie = p.Cubetas.Select((c, i) => punto(i, new PuntoReporteDto(p.Inicio(c)!.Value, p.Inicio(c, anterior: true), 0, 0))).ToList();
        return new ReporteVentasDto(periodo, p.Desde, p.Hasta.AddDays(-1), DateOnly.FromDateTime(ahora), p.Ahora, p.DesdeAnterior, p.HastaAnterior.AddDays(-1),
            p.CorteAnterior, Nada, Nada, serie, [], [], [], [], ConUtilidad: false, Piloto: false);
    }

    private static async Task<List<ElementoDibujado>> DibujarAsync(ReporteVentasDto reporte, bool tabla = false)
    {
        var p = new PantallaSinNavegador(new ServiceCollection().BuildServiceProvider());
        await using var _ = p;
        await p.AbrirAsync<GraficaVentas>(new() { [nameof(GraficaVentas.Reporte)] = reporte, [nameof(GraficaVentas.Tabla)] = tabla });
        Assert.Empty(p.Errores);
        return (await p.LeerAsync()).Elementos;
    }

    private static IEnumerable<string> Valores(IEnumerable<ElementoDibujado> elementos) =>
        elementos.SelectMany(e => e.Atributos.Values).OfType<string>();

    [Fact]
    public async Task Un_periodo_sin_ninguna_venta_tambien_se_dibuja()
    {
        // Un domingo que la tienda no abrió, sin nada el año pasado.
        var vacio = Reporte(PeriodoReporte.Hoy, new DateOnly(2026, 10, 18), new DateTime(2026, 10, 21, 13, 30, 0),
            (_, p) => p with { InicioAnteriorLocal = null, AnteriorCentavos = null });

        var elementos = await DibujarAsync(vacio);

        Assert.DoesNotContain(Valores(elementos), v => v.Contains("NaN") || v.Contains("Infinity") || v.Contains('∞'));
        Assert.DoesNotContain(elementos, e => e.Etiqueta is "path" or "polyline");
        // La escala de abajo, de $0 en adelante.
        Assert.Contains(elementos, e => e.Etiqueta == "text" && e.Texto == "$0");
    }

    [Fact]
    public async Task Las_barras_de_lo_que_ya_paso_y_la_linea_del_anio_pasado_cortada_donde_no_hay_dia()
    {
        // Febrero de 2028 (29 días) a la fecha, el 10: barras hasta el 10 (el 10, en curso); el año pasado no tiene 29.
        var febrero = Reporte(PeriodoReporte.Mes, new DateOnly(2028, 2, 10), new DateTime(2028, 2, 10, 12, 0, 0),
            (i, p) => p with
            {
                ActualCentavos = i < 10 ? 10_000 * (i + 1) : null, EnCurso = i == 9,
                AnteriorCentavos = p.InicioAnteriorLocal is null ? null : 50_000, InicioAnteriorLocal = p.InicioAnteriorLocal,
            });

        var elementos = await DibujarAsync(febrero);

        var barras = elementos.Where(e => e.Etiqueta == "path").ToList();
        Assert.Equal(10, barras.Count);
        Assert.Equal(["barra", "barra en-curso"], barras.Select(b => b.Clase).Distinct());
        Assert.Equal("barra en-curso", barras[^1].Clase);
        // La línea: del 1 al 28, en un tramo.
        var linea = Assert.Single(elementos, e => e.Etiqueta == "polyline");
        Assert.Equal(28, linea.Atributos["points"]!.ToString()!.Split(' ').Length);
        Assert.DoesNotContain(Valores(elementos), v => v.Contains("NaN"));
        // Cada cuadro con el día del año pasado que le toca y cuánto cambió; el del 10 va en curso (sin cambio) y el del 29 no tiene
        // año pasado.
        var cuadros = elementos.Where(e => e.Clase == "vn-rep-tip").Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        Assert.Equal(29, cuadros.Count);
        Assert.Equal("mié 9 feb Este año: $900.00 Año pasado (mar 9 feb 2027): $500.00 · ▲ 80 %", cuadros[8]);
        Assert.Equal("jue 10 feb Este año: $1,000.00 (va en curso) Año pasado (mié 10 feb 2027): $500.00", cuadros[9]);
        Assert.Equal("mar 29 feb Este año: —", cuadros[28]);
    }

    [Fact]
    public async Task Como_tabla_lo_mismo_en_renglones()
    {
        var semana = Reporte(PeriodoReporte.Semana, new DateOnly(2026, 10, 21), new DateTime(2026, 10, 21, 13, 30, 0),
            (i, p) => p with { ActualCentavos = i <= 2 ? 1000 : null, EnCurso = i == 2, AnteriorCentavos = 800 });

        var elementos = await DibujarAsync(semana, tabla: true);

        var renglones = elementos.Where(e => e.Etiqueta == "tr").Skip(1).Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        Assert.Equal(["lunes 19 oct $10.00 $8.00 +25 %", "martes 20 oct $10.00 $8.00 +25 %", "miércoles 21 oct (va en curso) $10.00 $8.00 —",
            "jueves 22 oct — $8.00 —"], renglones.Take(4));
        Assert.Equal(7, renglones.Count);
        Assert.DoesNotContain(elementos, e => e.Etiqueta == "svg");
    }
}
