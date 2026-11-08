using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones;

/// <summary>Lo que trae un modelo de impresora (<see cref="PerfilImpresora"/>).</summary>
/// <param name="NombreCorto">Como se le dice («WF-6590»): el nombre de una impresora nueva de ese modelo.</param>
/// <param name="Driver">El driver de Windows con que se instala su cola (así lo busca Control Print).</param>
/// <param name="ConBotellas">EcoTank: la tinta va en botellas y tanques, no en cartuchos.</param>
/// <param name="ConCajaDeMantenimiento">Si tiene caja de mantenimiento que se cambia (y su barra en la página de la impresora).</param>
/// <param name="PorVerificar">Lo que no se ha comprobado de este modelo en la tienda (vacío: nada).</param>
public sealed record PerfilDeImpresora(
    PerfilImpresora Clave, string Modelo, string NombreCorto, string Driver, bool ConBotellas, bool ConCajaDeMantenimiento, string? PorVerificar);

/// <summary>
/// Los modelos de impresora que conoce el módulo (2026-11-03): la WF-6590, con la que nació Control Print y que se midió y se leyó en la
/// tienda, y la EcoTank L5590, que todavía no (su spool, su SNMP y su caja de mantenimiento se revisan en la tienda,
/// docs/pruebas-en-tienda.md; la ficha de Epson lista una que se cambia, la C12C934461, y la Configuración deja ponérsela). Los consumibles de la L5590 salen de la ficha de Epson para Latinoamérica (C11CK57301, consultada el
/// 2026-11-03) sin precios: el precio de cada botella lo escribe el dueño en la Configuración.
/// </summary>
public static class PerfilesImpresora
{
    public static readonly PerfilDeImpresora WF6590 = new(PerfilImpresora.WF6590, "Epson WorkForce Pro WF-6590", "WF-6590", "EPSON WF-6590 Series",
        ConBotellas: false, ConCajaDeMantenimiento: true, PorVerificar: null);

    public static readonly PerfilDeImpresora L5590 = new(PerfilImpresora.L5590, "Epson EcoTank L5590", "L5590", "EPSON L5590 Series",
        ConBotellas: true, ConCajaDeMantenimiento: false,
        PorVerificar: "Por verificar en la tienda: si Windows guarda sus trabajos en EMF, qué contesta por SNMP (niveles, contador, serie) y su "
            + "caja de mantenimiento (Control Print dice que no tiene; la ficha de Epson lista una que se cambia, la C12C934461: si la tiene, "
            + "escribe su precio y cuántas caras dura en sus costos).");

    public static IReadOnlyList<PerfilDeImpresora> Todos { get; } = [WF6590, L5590];

    public static PerfilDeImpresora De(PerfilImpresora perfil) => perfil switch
    {
        PerfilImpresora.L5590 => L5590,
        _ => WF6590,
    };

    /// <summary>El modelo de un driver de Windows («EPSON L5590 Series»); vacío si no es ninguno de los que se conocen.</summary>
    public static PerfilDeImpresora? PorDriver(string? driver) =>
        string.IsNullOrWhiteSpace(driver) ? null
        : Todos.FirstOrDefault(p => driver.Contains(p.NombreCorto, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Lo propio de una impresora nueva de ese modelo: la WF-6590 usa los consumibles de la sucursal (los mismos cartuchos que la otra);
    /// la L5590, sus botellas, sin caja de mantenimiento, con lo que gasta de luz y sin precios (los escribe el dueño).
    /// </summary>
    public static CostosDeImpresora? CostosIniciales(PerfilImpresora perfil) => perfil switch
    {
        PerfilImpresora.L5590 => new CostosDeImpresora { Consumibles = BotellasL5590() },
        _ => null,
    };

    /// <summary>
    /// Las botellas T544 de la L5590 (ficha de Epson: la negra rinde hasta 4,300 páginas y las de color hasta 7,300, con el patrón ISO/IEC
    /// 19752 y la metodología de Epson, no el ISO/IEC 24711 de los cartuchos: la calibración lo corrige), 12 W imprimiendo y 15 páginas por
    /// minuto (ISO, en negro). Sin precios ni depreciación: el precio de compra de la impresora no se conoce todavía. Mientras le falte el
    /// precio de una botella, cobra con los cartuchos de la sucursal (<see cref="CostosDeImpresora.Completos"/>), nunca gratis.
    /// </summary>
    public static ConsumiblesDeImpresora BotellasL5590() => new()
    {
        Black = new InkCartridge { Name = "Negro", Sku = "T544120-AL", Price = 0, IsoYield = 4300 },
        Cyan = new InkCartridge { Name = "Cian", Sku = "T544220-AL", Price = 0, IsoYield = 7300 },
        Magenta = new InkCartridge { Name = "Magenta", Sku = "T544320-AL", Price = 0, IsoYield = 7300 },
        Yellow = new InkCartridge { Name = "Amarillo", Sku = "T544420-AL", Price = 0, IsoYield = 7300 },
        Maintenance = null,
        Watts = 12,
        PaginasPorMinuto = 15,
        Depreciation = new PrinterDepreciation { Enabled = true, PurchasePrice = 0, ResidualValue = 0, Months = 24, SidesPerMonth = 5000 },
    };

    /// <summary>«cartucho negro» o «botella negra»: para los avisos y lo que falta.</summary>
    public static string Consumible(PerfilDeImpresora perfil, InkChannel canal) => perfil.ConBotellas
        ? canal switch
        {
            InkChannel.Black => "botella negra",
            InkChannel.Cyan => "botella cian",
            InkChannel.Magenta => "botella magenta",
            _ => "botella amarilla",
        }
        : $"cartucho {Calibrator.Name(canal).ToLowerInvariant()}";

    /// <summary>
    /// En los avisos de los niveles: la tinta de la WF-6590 está en sus cartuchos; la de la L5590, en sus tanques (se rellenan con las
    /// botellas).
    /// </summary>
    public static string NivelDe(PerfilDeImpresora perfil, InkChannel canal) => perfil.ConBotellas
        ? canal switch
        {
            InkChannel.Black => "tinta negra",
            InkChannel.Cyan => "tinta cian",
            InkChannel.Magenta => "tinta magenta",
            _ => "tinta amarilla",
        }
        : $"cartucho {Calibrator.Name(canal).ToLowerInvariant()}";

    /// <summary>Los precios que le faltan a los consumibles propios de una impresora («Botella negra de la L5590»).</summary>
    public static IEnumerable<string> Faltan(string nombre, PerfilDeImpresora perfil, ConsumiblesDeImpresora consumibles)
    {
        foreach (var canal in new[] { InkChannel.Black, InkChannel.Cyan, InkChannel.Magenta, InkChannel.Yellow })
            if (consumibles.Tinta(canal).Price <= 0)
                yield return $"{Mayuscula(Consumible(perfil, canal))} de la {nombre}";
        if (consumibles.Maintenance is { PagesPerBox: > 0, BoxPrice: <= 0 })
            yield return $"Caja de mantenimiento de la {nombre}";
    }

    private static string Mayuscula(string texto) => texto.Length == 0 ? texto : char.ToUpperInvariant(texto[0]) + texto[1..];
}
