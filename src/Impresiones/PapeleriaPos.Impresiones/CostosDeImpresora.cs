using System.Text.Json;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones;

/// <summary>
/// Lo propio de una impresora encima de los costos de la sucursal (2026-11-03). Lo comercial (márgenes y sus tramos, cobro mínimo,
/// redondeo, papel, tipos de papel, calidad, extras) es de la sucursal; lo físico, del modelo o de cada impresora:
/// <list type="bullet">
/// <item><see cref="Consumibles"/>: su tinta (cartuchos o botellas), su caja de mantenimiento, su luz y su depreciación. Vacío: los de la
/// sucursal, que son los de la WF-6590 (las dos WF-6590 usan los mismos cartuchos, y se cambian en un solo lugar). La L5590 trae los
/// suyos (<see cref="PerfilesImpresora"/>).</item>
/// <item><see cref="Calibracion"/>: la corrección de su tinta y lo que de verdad dura su caja de mantenimiento, medidas con sus propias
/// lecturas. Vacío: la de la sucursal si usa sus consumibles (la de la impresora 1), o ninguna.</item>
/// </list>
/// Así la impresora de antes (la 1, sin nada propio) cobra exactamente como antes, y en la caja 2 los costos de Control Print siguen
/// siendo los suyos.
/// <para>
/// Mientras a sus consumibles les falte un precio (la L5590 llega sin ellos), la impresora cobra con los de la sucursal, como una WF-6590:
/// con la tinta en cero, sin caja y sin depreciación, lo que saliera de ella costaría cero y se cobraría gratis (el cobro mínimo solo vale
/// con costo). Al escribir sus precios, cobra con los suyos.
/// </para>
/// </summary>
public sealed class CostosDeImpresora
{
    public ConsumiblesDeImpresora? Consumibles { get; set; }
    public CalibracionDeImpresora? Calibracion { get; set; }

    public bool Vacio => Consumibles is null && Calibracion is null;

    /// <summary>Sus consumibles tienen todos sus precios: hasta entonces la impresora cobra con los de la sucursal.</summary>
    public static bool Completos(ConsumiblesDeImpresora c) =>
        new[] { c.Black, c.Cyan, c.Magenta, c.Yellow }.All(t => t.Price > 0) && c.Maintenance is not { PagesPerBox: > 0, BoxPrice: <= 0 };

    public static CostosDeImpresora? Leer(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<CostosDeImpresora>(json, CostSettings.Json);

    /// <summary>En JSON para guardarlo en la impresora; vacío si no tiene nada propio.</summary>
    public static string? Escribir(CostosDeImpresora? costos) =>
        costos is null || costos.Vacio ? null : JsonSerializer.Serialize(costos, CostSettings.Json);

    public CostosDeImpresora Copia() => Leer(Escribir(this)) ?? new CostosDeImpresora();

    /// <summary>
    /// Los costos con que se cobra lo que sale de una impresora: los de la sucursal (de Control Print en la caja 2, o los de la
    /// Configuración) con lo propio de la impresora encima. Sin nada propio, son los de la sucursal tal cual.
    /// </summary>
    public static CostSettings Aplicar(CostSettings deLaSucursal, CostosDeImpresora? propios)
    {
        // Sin nada propio, o sin todos los precios de su tinta (como una WF-6590; su calibración sería de otra tinta): los de la sucursal tal cual.
        if (propios is null || propios.Vacio || propios.Consumibles is { } incompletos && !Completos(incompletos))
            return deLaSucursal;
        var c = deLaSucursal.Clone();
        if (propios.Consumibles is { } p)
        {
            (c.Black, c.Cyan, c.Magenta, c.Yellow) = (Copia(p.Black), Copia(p.Cyan), Copia(p.Magenta), Copia(p.Yellow));
            c.Maintenance = p.Maintenance is { } caja
                ? new MaintenanceBox { Sku = caja.Sku, BoxPrice = caja.BoxPrice, PagesPerBox = caja.PagesPerBox }
                : SinCaja();
            // La luz de la tienda ($/kWh) es de la sucursal; lo que gasta la impresora y qué tan rápido imprime, de ella.
            (c.Power.Watts, c.Power.PagesPerMinute) = (p.Watts, p.PaginasPorMinuto);
            c.Depreciation = new PrinterDepreciation
            {
                Enabled = p.Depreciation.Enabled, PurchasePrice = p.Depreciation.PurchasePrice, ResidualValue = p.Depreciation.ResidualValue,
                Months = p.Depreciation.Months, SidesPerMonth = p.Depreciation.SidesPerMonth,
            };
            // La calibración de la sucursal es de otra impresora (la de los cartuchos de la sucursal): esta empieza sin corregir.
            c.Calibration = new CalibrationSettings();
        }
        if (propios.Calibracion is { } k)
        {
            c.Calibration = new CalibrationSettings
            {
                Enabled = k.Calibration.Enabled, C = k.Calibration.C, M = k.Calibration.M, Y = k.Calibration.Y, K = k.Calibration.K,
                SinceUtc = k.Calibration.SinceUtc, UpdatedUtc = k.Calibration.UpdatedUtc,
            };
            if (k.PaginasPorCaja is { } paginas && c.Maintenance.PagesPerBox > 0)
                c.Maintenance.PagesPerBox = paginas;
        }
        return c;
    }

    /// <summary>Una impresora sin caja de mantenimiento: no cuesta nada por ella ni falta su precio.</summary>
    public static MaintenanceBox SinCaja() => new() { Sku = "", BoxPrice = 0, PagesPerBox = 0 };

    private static InkCartridge Copia(InkCartridge t) => new() { Name = t.Name, Sku = t.Sku, Price = t.Price, IsoYield = t.IsoYield };
}

/// <summary>La tinta, la caja de mantenimiento, la luz y la depreciación de una impresora que no usa las de la sucursal (la L5590).</summary>
public sealed class ConsumiblesDeImpresora
{
    public InkCartridge Black { get; set; } = new();
    public InkCartridge Cyan { get; set; } = new();
    public InkCartridge Magenta { get; set; } = new();
    public InkCartridge Yellow { get; set; } = new();

    /// <summary>Vacía: no tiene caja de mantenimiento (o no se sabe todavía).</summary>
    public MaintenanceBox? Maintenance { get; set; }

    /// <summary>Lo que gasta imprimiendo (de la ficha de Epson).</summary>
    public double Watts { get; set; }

    /// <summary>Qué tan rápido imprime (la velocidad ISO de la ficha): con los watts da la luz de cada cara.</summary>
    public double PaginasPorMinuto { get; set; }

    public PrinterDepreciation Depreciation { get; set; } = new();

    /// <summary>
    /// Botellas o cartuchos de esa tinta (sus nombres van en <see cref="InkCartridge.Name"/>: Negro, Cian, Magenta, Amarillo).
    /// </summary>
    public InkCartridge Tinta(InkChannel canal) => canal switch
    {
        InkChannel.Black => Black,
        InkChannel.Cyan => Cyan,
        InkChannel.Magenta => Magenta,
        _ => Yellow,
    };
}

/// <summary>La calibración de una impresora: la corrección de cada tinta y las caras que de verdad le duró la caja de mantenimiento.</summary>
public sealed class CalibracionDeImpresora
{
    public CalibrationSettings Calibration { get; set; } = new();

    /// <summary>Caras por caja de mantenimiento según sus lecturas; vacío, las de sus consumibles.</summary>
    public int? PaginasPorCaja { get; set; }
}
