using System.Text.RegularExpressions;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Impresion;

/// <summary>
/// El corte del día impreso (2026-10-17), como el de eleventa y con el acomodo del corte del turno: a 48 columnas, el día y
/// si se cerró, lo vendido y sus formas de pago, cada turno con lo que se contó, los movimientos, las ventas por departamento
/// y, solo si viene (para quien ve la utilidad), la utilidad, también la de cada departamento.
/// </summary>
public class CorteDelDiaImpresoTests
{
    private static readonly DateTime Impreso = new(2026, 10, 17, 9, 30, 0);

    /// <summary>Lo vendido por departamento; con <paramref name="conCosto"/>, con su costo (para quien ve la utilidad).</summary>
    private static VentasPorDepartamentoDto PorDepartamento(bool conCosto) => new(
        [
            new DepartamentoDelDiaDto("Papelería, oficina y regalos de temporada", 200000, conCosto ? 120000 : null),
            new DepartamentoDelDiaDto("Escolar", 100000, conCosto ? 70000 : null),
        ],
        ImpresionesCentavos: 35600, ImpresionesCostoCentavos: conCosto ? 9000 : null, ArticuloComunCentavos: 15000);

    /// <summary>La utilidad que va con el costo de <see cref="PorDepartamento"/>: 2,000 + 1,000 + 356 de venta y 1,200 + 700 + 90 de costo.</summary>
    private static UtilidadDelDiaDto Utilidad() => new(VentaCentavos: 335600, CostoCentavos: 199000, ServiciosSinCostoCentavos: 20000);

    private static CorteDelDiaDto Dia(UtilidadDelDiaDto? utilidad = null, bool deOtroDia = true, bool piloto = false, CierreDelDiaDto? cierre = null,
        string? enEleventa = null, VentasPorDepartamentoDto? porDepartamento = null) => new(
        new DateOnly(2026, 10, 16),
        [
            new TurnoDelDiaDto(Guid.NewGuid(), Guid.NewGuid(), "Caja 1", "Ana", new DateTime(2026, 10, 16, 9, 0, 0), new DateTime(2026, 10, 16, 15, 0, 0), 12,
                350000, 50000, 0, 287500, enEleventa is null ? 287000 : null, enEleventa is null ? -500 : null, false, enEleventa),
            new TurnoDelDiaDto(Guid.NewGuid(), Guid.NewGuid(), "Caja 2", "Beto", new DateTime(2026, 10, 15, 20, 0, 0), null, 3, 40000, 0, 0, 120000,
                null, null, true),
        ],
        VendidoCentavos: 390000, Tickets: 15, EfectivoCentavos: 340000, TarjetaCentavos: 50000, TransferenciaCentavos: 0, FiadoCentavos: 0,
        TransferenciasPorVerificar: 1, Salidas: [new SalidaDelDiaDto("Pago al proveedor de los refrescos y las botanas del mes", 2, 40000)],
        EntradasCentavos: 10000, Devoluciones: 1, DevolucionesCentavos: 3800, Cancelaciones: 1, CancelacionesCentavos: 1500, Abonos: 2,
        AbonosCentavos: 30000, ImpresionesCentavos: 35600, PorQueNoSeCierra: [], Avisos: [], Piloto: piloto, Cierre: cierre, Utilidad: utilidad,
        DeOtroDia: deOtroDia, PorDepartamento: porDepartamento ?? PorDepartamento(conCosto: utilidad is not null));

    /// <summary>
    /// El texto como sale, sus renglones y, para leer lo que baja de renglón, todo seguido. Antes, que ningún renglón pase de
    /// 48: la vista previa y la impresora los recortan, y lo que sobra se perdería sin que se notara.
    /// </summary>
    private static (string Seguido, string[] Renglones) Imprimir(CorteDelDiaDto dia)
    {
        var documento = DisenoComprobante.CorteDelDia(dia, "Luis", Impreso);
        Assert.All(documento.OfType<Renglon>(), r => Assert.True(r.Texto.Length <= DisenoTicket.Columnas, $"«{r.Texto}» mide {r.Texto.Length}"));
        var renglones = TicketTexto.Generar(documento).Split(Environment.NewLine);
        return (string.Join(' ', renglones.Select(r => r.Trim()).Where(r => r.Length > 0)), renglones);
    }

    /// <summary>Un renglón con su concepto y sus montos en columnas.</summary>
    private static bool Columnas(string renglon, string concepto, params string[] montos) =>
        Regex.IsMatch(renglon, $"^{Regex.Escape(concepto)}" + string.Concat(montos.Select(m => $@"\s+{Regex.Escape(m)}")) + "$");

    [Fact]
    public void Lleva_el_dia_lo_vendido_los_turnos_y_los_movimientos_a_48_columnas()
    {
        var (texto, renglones) = Imprimir(Dia(cierre: new CierreDelDiaDto(new DateTime(2026, 10, 16, 21, 5, 0), "Luis")));

        Assert.Contains("CORTE DEL DÍA", texto);
        Assert.Contains("Todas las cajas · viernes 16/10/2026", texto);
        Assert.Contains("Impreso: 17/10/2026  09:30", renglones);
        Assert.Contains("Lo imprimió: Luis", renglones);
        Assert.Contains("Día cerrado a las 21:05 por Luis.", texto);
        Assert.Contains(renglones, r => r.StartsWith("Vendido") && r.EndsWith("$3,900.00"));
        Assert.Contains("Tickets: 15 en 2 turnos", texto);
        Assert.Contains(renglones, r => r.StartsWith("Efectivo") && r.EndsWith("$3,400.00"));
        Assert.Contains(renglones, r => r.StartsWith("Tarjeta") && r.EndsWith("$500.00"));
        Assert.Contains("Transferencias por verificar: 1", texto);
        // Cada turno: su horario, lo vendido y lo que se contó; el que viene de otro día y sigue abierto, con su fecha.
        Assert.Contains("Caja 1 · Ana", renglones);
        Assert.Contains(renglones, r => Columnas(r, "  09:00-15:00 · 12 tickets", "$3,500.00"));
        Assert.Contains("  Esperado $2,875.00 · contado $2,870.00", renglones);
        Assert.Contains(renglones, r => Columnas(r, "  Faltante", "$5.00"));
        Assert.Contains("Caja 2 · Beto (sin conexión)", renglones);
        Assert.Contains(renglones, r => Columnas(r, "  15/10 20:00-abierto · 3 tickets", "$400.00"));
        Assert.Contains("  Turno abierto · esperado $1,200.00", renglones);
        Assert.Contains(renglones, r => Columnas(r, "Diferencias de corte", "-$5.00"));
        Assert.Contains("De un turno que viene de otro día, solo lo de este día", texto);
        // Un motivo largo baja de renglón y el monto va en el último.
        var salida = Array.FindIndex(renglones, r => r.StartsWith("Salidas · Pago al proveedor"));
        var monto = Array.FindIndex(renglones, r => Regex.IsMatch(r, @"^\S.* \(2\)\s+-\$400\.00$"));
        Assert.True(salida >= 0 && monto > salida, texto);
        Assert.Contains("Salidas · Pago al proveedor de los refrescos y las botanas del mes (2)", texto);
        Assert.Contains(renglones, r => r.StartsWith("Entradas de efectivo") && r.EndsWith("$100.00"));
        Assert.Contains(renglones, r => r.StartsWith("Devoluciones (1)") && r.EndsWith("-$38.00"));
        Assert.Contains(renglones, r => r.StartsWith("Cancelaciones (1)") && r.EndsWith("-$15.00"));
        Assert.Contains(renglones, r => r.StartsWith("Abonos recibidos (2)") && r.EndsWith("$300.00"));
        Assert.Contains(renglones, r => r.StartsWith("Impresiones cobradas") && r.EndsWith("$356.00"));
        // Las ventas por departamento, como en eleventa, para todos los que ven el corte: la venta de cada uno, las impresiones,
        // el artículo común y el total, menos lo devuelto.
        Assert.Contains("VENTAS POR DEPARTAMENTO", renglones);
        Assert.Contains(renglones, r => Columnas(r, "Departamento", "Venta"));
        Assert.Contains(renglones, r => Columnas(r, "Papelería, oficina y regalos de", "$2,000.00"));
        Assert.Contains(renglones, r => Columnas(r, "Escolar", "$1,000.00"));
        Assert.Contains(renglones, r => Columnas(r, "Impresiones", "$356.00"));
        Assert.Contains(renglones, r => Columnas(r, "Artículo común", "$150.00"));
        Assert.Contains(renglones, r => Columnas(r, "Total", "$3,506.00"));
        Assert.Contains("Lo devuelto ese día ya se restó de su departamento.", texto);
        // Sin la utilidad (quien no la ve no la recibe), nada de la utilidad: ni arriba, ni por departamento, ni su sección.
        Assert.DoesNotContain("UTILIDAD", texto);
        Assert.DoesNotContain("Utilidad", texto);
        Assert.DoesNotContain("sin costo", texto);
        Assert.DoesNotContain("Margen", texto);
    }

    [Fact]
    public void Con_la_utilidad_lleva_la_utilidad_y_las_ventas_por_departamento()
    {
        var (texto, renglones) = Imprimir(Dia(Utilidad()));

        // Arriba, junto a lo vendido; y abajo, su detalle: 2,000 + 1,000 + 356 de venta con costo, 1,200 + 700 + 90 de costo.
        Assert.Contains(renglones, r => r.StartsWith("Utilidad bruta") && r.EndsWith("$1,366.00"));
        Assert.Contains("UTILIDAD", renglones);
        Assert.Contains(renglones, r => r.StartsWith("Venta con costo") && r.EndsWith("$3,356.00"));
        Assert.Contains(renglones, r => r.StartsWith("Costo") && r.EndsWith("-$1,990.00"));
        Assert.Contains(renglones, r => r.StartsWith("Margen") && r.EndsWith("40.7 %"));
        Assert.Contains("Sin el artículo común ($150.00): no tiene costo.", texto);
        Assert.Contains("aún no tienen costo: cuentan completos.", texto);
        // Por departamento: la venta y la utilidad; un nombre largo baja de renglón; las impresiones y el artículo común aparte.
        Assert.Contains("VENTAS POR DEPARTAMENTO", renglones);
        Assert.Contains(renglones, r => Columnas(r, "Departamento", "Venta", "Utilidad"));
        var papeleria = Array.FindIndex(renglones, r => Columnas(r, "Papelería, oficina y", "$2,000.00", "$800.00"));
        Assert.True(papeleria >= 0, texto);
        Assert.Equal("  regalos de temporada", renglones[papeleria + 1]);
        Assert.Contains(renglones, r => Columnas(r, "Escolar", "$1,000.00", "$300.00"));
        Assert.Contains(renglones, r => Columnas(r, "Impresiones", "$356.00", "$266.00"));
        Assert.Contains(renglones, r => Columnas(r, "Artículo común", "$150.00", "sin costo"));
        // El total es todo lo vendido, menos lo devuelto; la utilidad, sin el artículo común.
        Assert.Contains(renglones, r => Columnas(r, "Total", "$3,506.00", "$1,366.00"));
    }

    [Fact]
    public void El_de_un_dia_sin_cerrar_y_el_del_piloto_lo_dicen()
    {
        var (sinCerrar, _) = Imprimir(Dia());
        var (deHoy, _) = Imprimir(Dia(deOtroDia: false));
        var (piloto, renglones) = Imprimir(Dia(piloto: true, enEleventa: "Corte en eleventa: contado $787.50, faltaron $12.50"));
        var (pilotoDeHoy, _) = Imprimir(Dia(piloto: true, deOtroDia: false));

        Assert.Contains("Ese día no se cerró.", sinCerrar);
        Assert.Contains("El día todavía no se cierra.", deHoy);
        // En el piloto aquí solo está lo de la caja nueva, el día se cierra con el corte de eleventa y lo contado allá se dice tal cual.
        Assert.Contains("Esta caja · viernes 16/10/2026", piloto);
        Assert.Contains("Del piloto con eleventa: el día se cerró con el corte de eleventa.", piloto);
        Assert.Contains("Piloto con eleventa: el día se cierra con el corte de eleventa.", pilotoDeHoy);
        Assert.Contains("  Corte en eleventa: contado $787.50, faltaron", renglones);
        Assert.Contains("  Corte en eleventa", renglones);
        Assert.DoesNotContain("Diferencias de corte", piloto);
    }

    [Fact]
    public void De_una_caja_principal_de_antes_sin_las_ventas_por_departamento_no_las_imprime()
    {
        var (texto, _) = Imprimir(Dia() with { PorDepartamento = null });

        Assert.DoesNotContain("DEPARTAMENTO", texto);
        Assert.Contains("MOVIMIENTOS", texto);
    }

    [Fact]
    public void Un_turno_que_se_corto_otro_dia_dice_que_su_corte_va_en_ese_dia()
    {
        var dia = Dia();
        var cortadoManana = dia.Turnos[0] with { CerradoLocal = new DateTime(2026, 10, 17, 10, 0, 0), EsperadoCentavos = null, ContadoCentavos = null,
            DiferenciaCentavos = null };

        var (texto, renglones) = Imprimir(dia with { Turnos = [cortadoManana, dia.Turnos[1]] });

        Assert.Contains(renglones, r => Columnas(r, "  09:00-17/10 10:00 · 12 tickets", "$3,500.00"));
        Assert.Contains("  Su corte, el 17/10, va en ese día.", renglones);
        Assert.DoesNotContain("Faltante", texto);
        Assert.DoesNotContain("Diferencias de corte", texto);
    }

    /// <summary>
    /// Un turno que se cortó antes de ese día está en él por lo que se cobró sin conexión y subió tarde, con la hora de ese día
    /// (revisión del PR #131): lo dice, y su corte va en el día en que se hizo.
    /// </summary>
    [Fact]
    public void Un_turno_que_se_corto_antes_esta_por_lo_que_se_cobro_sin_conexion()
    {
        var dia = Dia();
        var deAyer = dia.Turnos[0] with
        {
            AbiertoLocal = new DateTime(2026, 10, 15, 9, 0, 0), CerradoLocal = new DateTime(2026, 10, 15, 23, 55, 0), Tickets = 1, VendidoCentavos = 4000,
            EsperadoCentavos = null, ContadoCentavos = null, DiferenciaCentavos = null,
        };

        var (texto, renglones) = Imprimir(dia with { Turnos = [deAyer, dia.Turnos[1]] });

        // Con las dos fechas el horario no cabe con el monto: baja de renglón.
        Assert.Matches(@"15/10 09:00-15/10 23:55 · 1 ticket\s+\$40\.00 Cobrado sin conexión; su corte es del 15/10\.", texto);
        Assert.Contains("  Cobrado sin conexión; su corte es del 15/10.", renglones);
        Assert.DoesNotContain("Faltante", texto);
    }
}
