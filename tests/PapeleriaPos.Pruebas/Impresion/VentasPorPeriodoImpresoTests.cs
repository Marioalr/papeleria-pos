using System.Text.RegularExpressions;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Impresion;

/// <summary>
/// El resumen de «Ventas por período» impreso (entrega 9, parte 2): a 48 columnas, de qué días y de qué caja, lo de cada día (y
/// sus formas de pago si no fue todo en efectivo, y lo devuelto), las formas de pago y los totales; no cada ticket. Sale por la
/// cola de la impresora de tickets, como el corte del día.
/// </summary>
public sealed class VentasPorPeriodoImpresoTests
{
    private static readonly DateTime Impreso = new(2026, 10, 22, 14, 5, 0);

    private static FormaPagoReporteDto Forma(FormaPago forma, long centavos) => new(forma, centavos);

    /// <summary>Del lunes 19 al miércoles 21 de octubre: el martes con tarjeta y una devolución; el miércoles, un cancelado.</summary>
    private static VentasPorPeriodoDto Periodo(bool piloto = false)
    {
        var dias = new List<ResumenDeVentasDto>
        {
            new(new DateOnly(2026, 10, 19), 60, 431250, 0, 0, 0, 0, [Forma(FormaPago.Efectivo, 431250)]),
            new(new DateOnly(2026, 10, 20), 1234, 1250000, 2, 5000, 0, 0, [Forma(FormaPago.Efectivo, 1150000), Forma(FormaPago.Tarjeta, 100000)]),
            new(new DateOnly(2026, 10, 21), 55, 398000, 0, 0, 1, 4000, [Forma(FormaPago.Efectivo, 398000)]),
        };
        var totales = new ResumenDeVentasDto(null, 1349, 2079250, 2, 5000, 1, 4000, [Forma(FormaPago.Efectivo, 1979250), Forma(FormaPago.Tarjeta, 100000)]);
        return new VentasPorPeriodoDto(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 22), null, [], [], 1350, 0, 100,
            totales, dias, 0, 0, piloto);
    }

    private static (string Seguido, string[] Renglones) Imprimir(VentasPorPeriodoDto d, string? caja = null)
    {
        var documento = DisenoComprobante.VentasPorPeriodo(d, caja, "Dueño de prueba", Impreso);
        Assert.All(documento.OfType<Renglon>(), r => Assert.True(r.Texto.Length <= DisenoTicket.Columnas, $"«{r.Texto}» mide {r.Texto.Length}"));
        var renglones = TicketTexto.Generar(documento).Split(Environment.NewLine);
        return (string.Join(' ', renglones.Select(r => r.Trim()).Where(r => r.Length > 0)), renglones);
    }

    /// <summary>Un renglón con su concepto y sus montos en columnas (lo de cada día va con sangría).</summary>
    private static bool Columnas(string renglon, string concepto, params string[] montos) =>
        Regex.IsMatch(renglon.Trim(), $"^{Regex.Escape(concepto)}" + string.Concat(montos.Select(m => $@"\s+{Regex.Escape(m)}")) + "$");

    [Fact]
    public void Lleva_cada_dia_las_formas_de_pago_y_los_totales_sin_cada_ticket()
    {
        var (seguido, renglones) = Imprimir(Periodo());

        Assert.Contains("VENTAS POR PERÍODO", seguido);
        Assert.Contains("Del 19/10/2026 al 21/10/2026 Todas las cajas", seguido);
        Assert.Contains("Impreso: 22/10/2026  14:05 Lo imprimió: Dueño de prueba", seguido);
        // Cada día, con sus tickets y lo vendido; el martes, con sus formas (no fue todo en efectivo) y lo devuelto.
        Assert.Contains(renglones, r => Columnas(r, "Día", "Tickets", "Vendido"));
        Assert.Contains(renglones, r => Columnas(r, "lun 19/10", "60", "$4,312.50"));
        Assert.Contains(renglones, r => Columnas(r, "mar 20/10", "1,234", "$12,500.00"));
        Assert.Contains(renglones, r => Columnas(r, "Efectivo", "$11,500.00"));
        Assert.Contains(renglones, r => Columnas(r, "Tarjeta", "$1,000.00"));
        Assert.Contains(renglones, r => Columnas(r, "Devuelto (2)", "-$50.00"));
        Assert.Contains(renglones, r => Columnas(r, "mié 21/10", "55", "$3,980.00"));
        // Las formas de pago del periodo y los totales, como en la pantalla.
        var formas = Array.FindIndex(renglones, r => r.Trim() == "POR FORMA DE PAGO");
        Assert.True(Columnas(renglones[formas + 1], "Efectivo", "$19,792.50"), renglones[formas + 1]);
        Assert.True(Columnas(renglones[formas + 2], "Tarjeta", "$1,000.00"), renglones[formas + 2]);
        Assert.Contains("El efectivo, sin el redondeo al peso.", seguido);
        Assert.Contains(renglones, r => Columnas(r, "Tickets", "1,349"));
        Assert.Contains(renglones, r => Columnas(r, "Vendido", "$20,792.50"));
        Assert.Contains(renglones, r => Columnas(r, "Devoluciones (2)", "-$50.00"));
        Assert.Contains(renglones, r => Columnas(r, "Venta neta", "$20,742.50"));
        // La cancelación de esos días, como en el corte del día: el ticket ya no está en lo vendido.
        Assert.Contains(renglones, r => Columnas(r, "Cancelaciones (1)", "$40.00"));
        Assert.Contains("El ticket cancelado ya no cuenta en lo vendido.", seguido);
        // Ningún ticket suelto.
        Assert.DoesNotContain("A-0", seguido);
    }

    [Fact]
    public void Dice_de_que_caja_es_y_en_el_piloto_que_lo_demas_esta_en_eleventa()
    {
        Assert.Contains("Del 19/10/2026 al 21/10/2026 Caja 2", Imprimir(Periodo(), "Caja 2").Seguido);
        Assert.Contains("Solo esta caja (lo demás, en eleventa)", Imprimir(Periodo(piloto: true)).Seguido);
        // Un solo día, con su nombre; sin ventas, lo dice.
        var vacio = Periodo() with
        {
            Desde = new DateOnly(2026, 10, 22), Hasta = new DateOnly(2026, 10, 22), Dias = [],
            Totales = new ResumenDeVentasDto(null, 0, 0, 0, 0, 0, 0, []),
        };
        var (seguido, _) = Imprimir(vacio);
        Assert.Contains("jueves 22/10/2026", seguido);
        Assert.Contains("No hubo ventas en esos días.", seguido);
        Assert.Contains("Nada cobrado en esos días.", seguido);
    }

    /// <summary>De una caja no es lo del corte del día: lo devuelto y lo cancelado son de sus tickets (revisión del PR #134).</summary>
    [Fact]
    public void De_una_caja_dice_que_lo_devuelto_y_lo_cancelado_son_de_sus_tickets()
    {
        const string nota = "Lo devuelto y lo cancelado son de los tickets de esta caja, en esta caja o en otra.";
        Assert.Contains(nota, Imprimir(Periodo(), "Caja 2").Seguido);
        Assert.DoesNotContain(nota, Imprimir(Periodo()).Seguido);
        Assert.DoesNotContain(nota, Imprimir(Periodo(piloto: true), "Caja 2").Seguido);
    }

    /// <summary>Unos días seguidos, cada uno con dos tickets de $5 en efectivo.</summary>
    private static VentasPorPeriodoDto CadaDia(DateOnly desde, DateOnly hasta)
    {
        var dias = Enumerable.Range(0, hasta.DayNumber - desde.DayNumber + 1)
            .Select(n => new ResumenDeVentasDto(desde.AddDays(n), 2, 1000, 0, 0, 0, 0, [Forma(FormaPago.Efectivo, 1000)])).ToList();
        var totales = new ResumenDeVentasDto(null, dias.Sum(x => x.Tickets), dias.Sum(x => x.VendidoCentavos), 0, 0, 0, 0,
            [Forma(FormaPago.Efectivo, dias.Sum(x => x.VendidoCentavos))]);
        return new VentasPorPeriodoDto(desde, hasta, hasta, null, [], [], totales.Tickets, 0, 100, totales, dias, 0, 0, false);
    }

    /// <summary>
    /// Un año día por día eran metros de papel (revisión del PR #134): hasta 31 días va cada día; hasta tres meses, cada semana (de
    /// lunes a domingo, solo los días que se pidieron); más, cada mes.
    /// </summary>
    [Fact]
    public void Mas_de_un_mes_va_por_semana_y_mas_de_tres_meses_por_mes()
    {
        // 31 días: todavía día por día.
        var (unMes, _) = Imprimir(CadaDia(new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 21)));
        Assert.Contains("POR DÍA", unMes);

        // Del sábado 12 de septiembre al miércoles 21 de octubre (40 días): siete semanas, la primera y la última cortadas.
        var (porSemana, semanas) = Imprimir(CadaDia(new DateOnly(2026, 9, 12), new DateOnly(2026, 10, 21)));
        Assert.Contains("POR SEMANA", porSemana);
        Assert.DoesNotContain("POR DÍA", porSemana);
        Assert.Contains(semanas, r => Columnas(r, "Semana", "Tickets", "Vendido"));
        Assert.Contains(semanas, r => Columnas(r, "12/09 al 13/09", "4", "$20.00"));
        Assert.Contains(semanas, r => Columnas(r, "14/09 al 20/09", "14", "$70.00"));
        Assert.Contains(semanas, r => Columnas(r, "19/10 al 21/10", "6", "$30.00"));
        Assert.Equal(7, semanas.Count(r => Regex.IsMatch(r.Trim(), @"^\d\d/\d\d al \d\d/\d\d ")));
        Assert.Contains(semanas, r => Columnas(r, "Vendido", "$400.00"));

        // De enero al 21 de octubre: diez meses.
        var (porMes, meses) = Imprimir(CadaDia(new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 21)));
        Assert.Contains("POR MES", porMes);
        Assert.Contains(meses, r => Columnas(r, "Mes", "Tickets", "Vendido"));
        Assert.Contains(meses, r => Columnas(r, "ene 2026", "62", "$310.00"));
        Assert.Contains(meses, r => Columnas(r, "feb 2026", "56", "$280.00"));
        Assert.Contains(meses, r => Columnas(r, "oct 2026", "42", "$210.00"));
        Assert.Equal(10, meses.Count(r => Regex.IsMatch(r.Trim(), @"^[a-z]{3} 2026 ")));

        // Tres años (lo más que se puede pedir), por mes: unos 37 renglones de meses, no más de mil de días.
        var (_, tresAnios) = Imprimir(CadaDia(new DateOnly(2023, 10, 22), new DateOnly(2026, 10, 21)));
        Assert.Equal(37, tresAnios.Count(r => Regex.IsMatch(r.Trim(), @"^[a-z]{3} 20\d\d ")));
        Assert.True(tresAnios.Length < 80, $"Son {tresAnios.Length} renglones.");
    }

    [Fact]
    public async Task Sale_por_la_cola_de_la_impresora_de_tickets()
    {
        var impresora = new TicketeraDePrueba();
        var cola = new ColaDeImpresion(impresora, [TimeSpan.FromMilliseconds(1)]);

        var trabajo = cola.Encolar("Ventas del 19/10 al 21/10", DisenoComprobante.VentasPorPeriodo(Periodo(), null, "Dueño de prueba", Impreso), abrirCajon: false);

        Assert.True(await trabajo.Resultado.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(EstadoImpresion.Impreso, trabajo.Estado);
        var papel = Assert.Single(impresora.Salieron);
        Assert.Contains("Venta neta", papel);
        Assert.Contains("$20,742.50", papel);
    }
}
