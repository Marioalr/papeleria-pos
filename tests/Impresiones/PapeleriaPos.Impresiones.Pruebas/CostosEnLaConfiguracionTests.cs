using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Los costos de impresión en la Configuración (2026-10-31): lo principal de <see cref="CostSettings"/> dicho en español, para verlo y
/// cambiarlo; lo demás se queda como estaba, lo que no sirve se dice sin cambiar nada y lo que cambió se dice para la bitácora.
/// </summary>
public class CostosEnLaConfiguracionTests
{
    /// <summary>Como los de una sucursal que ya trabajó: calibrada, con su luz y con un tipo de papel propio.</summary>
    private static CostSettings DeLaTienda()
    {
        var c = new CostSettings { PaperMarginPercent = 40 };
        (c.Calibration.K, c.Calibration.C, c.Power.Watts) = (1.25, 0.9, 31);
        c.Media.Add(new MediaSetting { Key = "Kraft", Name = "Kraft", Group = "Normal", InkFactor = 1.1 });
        return c;
    }

    [Fact]
    public void Se_ven_los_cartuchos_el_papel_el_margen_el_minimo_y_el_redondeo()
    {
        var costos = CostosEnLaConfiguracion.De(new CostSettings());

        Assert.Equal([new("Negro", 1200, 10000), new("Cian", 1200, 7000), new("Magenta", 1200, 7000), new("Amarillo", 1200, 7000)], costos.Cartuchos);
        Assert.Equal(new PapelCostoDto("Carta", "Normal", 0, 500), costos.Papeles[0]);
        Assert.Equal(6, costos.Papeles.Count);
        Assert.Equal((MargenDeImpresiones.PorCostoDelTrabajo, 100m, 850m), (costos.Margen, costos.MargenUnico, costos.CajaMantenimiento));
        Assert.Equal([new(0, 100), new(100, 80), new(500, 60), new(1000, 50)], costos.TramosPorTrabajo);
        Assert.Equal((2m, false, 1m), (costos.CobroMinimo, costos.MinimoATodos, costos.RedondeoA));
        // De fábrica, el papel va en cero: es lo que falta.
        Assert.Equal(["Papel Carta normal", "Papel Oficio normal", "Papel A4 normal"], CostosEnLaConfiguracion.Faltan(new CostSettings()));
    }

    [Fact]
    public void Al_cambiarlos_solo_cambia_lo_que_se_ve_y_lo_demas_se_queda()
    {
        var antes = DeLaTienda();
        var pedidos = CostosEnLaConfiguracion.De(antes) is var c
            ? c with
            {
                Cartuchos = [c.Cartuchos[0] with { Precio = 1150, Rendimiento = 9000 }, .. c.Cartuchos.Skip(1)],
                Papeles = [c.Papeles[0] with { PrecioPaquete = 125, HojasPaquete = 500 }, .. c.Papeles.Skip(1)],
                Margen = MargenDeImpresiones.PorCostoDeLaHoja,
                TramosPorHoja = [new(3, 60), new(0, 100)],
                CobroMinimo = 3,
                MinimoATodos = true,
                RedondeoA = 0.5m,
            }
            : null!;

        var despues = CostosEnLaConfiguracion.Aplicar(antes, pedidos);

        Assert.Equal((1150m, 9000), (despues.Black.Price, despues.Black.IsoYield));
        Assert.Equal(0.25m, despues.Papers[0].PricePerSheet);
        Assert.Equal(ScalingMode.BySheetCost, despues.Scaling);
        Assert.Equal([(0m, 100m), (3m, 60m)], despues.SheetCostTiers.Select(t => (t.From, t.Margin)));
        Assert.Equal((3m, MinimumScope.AllJobs, 0.5m), (despues.MinimumPrice, despues.MinimumApplies, despues.RoundTo));
        // Lo demás, como estaba: la calibración, la luz, el margen del papel, los tipos de papel y los tramos que no se usan.
        Assert.Equal((1.25, 0.9, 31.0, (decimal?)40m), (despues.Calibration.K, despues.Calibration.C, despues.Power.Watts, despues.PaperMarginPercent));
        Assert.Contains(despues.Media, m => m.Key == "Kraft");
        Assert.Equal(antes.JobCostTiers.Select(t => (t.From, t.Margin)), despues.JobCostTiers.Select(t => (t.From, t.Margin)));
        // Y los de antes no se tocan.
        Assert.Equal(1200m, antes.Black.Price);
        Assert.DoesNotContain("Papel Carta normal", CostosEnLaConfiguracion.Faltan(despues));
    }

    [Fact]
    public void Lo_que_cambio_se_dice_para_la_bitacora()
    {
        var antes = new CostSettings();
        var c = CostosEnLaConfiguracion.De(antes);

        var cambios = CostosEnLaConfiguracion.Cambios(antes, CostosEnLaConfiguracion.Aplicar(antes, c with
        {
            Cartuchos = [c.Cartuchos[0] with { Precio = 1150 }, .. c.Cartuchos.Skip(1)],
            Papeles = [c.Papeles[0] with { PrecioPaquete = 125 }, .. c.Papeles.Skip(1)],
            Margen = MargenDeImpresiones.Unico,
        }));

        Assert.Equal(
        [
            "el cartucho negro: $1,200.00 → $1,150.00",
            "el paquete de papel Carta normal: $0.00 por 500 hojas → $125.00 por 500 hojas",
            "el margen: por el costo del trabajo → uno para todo",
        ], cambios);
        Assert.Empty(CostosEnLaConfiguracion.Cambios(antes, CostosEnLaConfiguracion.Aplicar(antes, c)));
    }

    [Theory]
    [InlineData("rinde", "Lo que rinde el cartucho negro va de 1 a 1,000,000 páginas (lo dice su caja).")]
    [InlineData("precio", "El precio del cartucho cian va de $0.00 a $100,000.00.")]
    [InlineData("hojas", "El paquete de papel Carta normal trae de 1 a 100,000 hojas.")]
    [InlineData("margen", "El margen va de 0 a 1,000 %.")]
    [InlineData("tramos", "Dos tramos del costo del trabajo empiezan en $100.00: cada uno empieza en otro costo.")]
    [InlineData("sin tramos", "Con el margen por tramos del costo del trabajo, escribe al menos un tramo.")]
    [InlineData("papel", "No hay papel Tabloide «Normal» en los costos.")]
    [InlineData("cartucho", "Falta el cartucho amarillo.")]
    public void Lo_que_no_sirve_se_dice_sin_cambiar_nada(string que, string mensaje)
    {
        var antes = new CostSettings();
        var c = CostosEnLaConfiguracion.De(antes);
        var pedidos = que switch
        {
            "rinde" => c with { Cartuchos = [c.Cartuchos[0] with { Rendimiento = 0 }, .. c.Cartuchos.Skip(1)] },
            "precio" => c with { Cartuchos = [c.Cartuchos[0], c.Cartuchos[1] with { Precio = -1 }, .. c.Cartuchos.Skip(2)] },
            "hojas" => c with { Papeles = [c.Papeles[0] with { HojasPaquete = 0 }, .. c.Papeles.Skip(1)] },
            "margen" => c with { MargenUnico = 1500 },
            "tramos" => c with { TramosPorTrabajo = [new(0, 100), new(100, 80), new(100, 70)] },
            "sin tramos" => c with { TramosPorTrabajo = [] },
            "papel" => c with { Papeles = [new PapelCostoDto("Tabloide", "Normal", 10, 100)] },
            _ => c with { Cartuchos = c.Cartuchos.Take(3).ToList() },
        };

        var error = Assert.Throws<ArgumentException>(() => CostosEnLaConfiguracion.Aplicar(antes, pedidos));

        Assert.Equal(mensaje, error.Message);
        Assert.Equal(1200m, antes.Cyan.Price);
    }
}
