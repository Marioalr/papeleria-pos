using System.Globalization;
using System.Text.RegularExpressions;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que se escribe en Inventario › Agregar inventario (como en eleventa, 2026-10-24): «Cuántos llegaron» y «Costo de cada
/// uno». El servicio vuelve a revisar todo; esto es para decirlo antes, con las mismas reglas.
/// </summary>
public static partial class AgregarInventarioEnPantalla
{
    /// <summary>Una cantidad: hasta cinco cifras, con hasta tres decimales (con punto o coma, como en la venta).</summary>
    [GeneratedRegex(@"^[0-9]{1,5}([.,][0-9]{1,3})?$")]
    private static partial Regex FormaDeCantidad();

    /// <summary>
    /// Lo escrito en «Cuántos llegaron», en milésimas de la unidad base, con las reglas de las cantidades de la venta («5*código»,
    /// «Varios»: punto o coma decimal y hasta tres decimales): más de cero y hasta <see cref="EntradaInventario.CantidadMaxima"/>
    /// (más es un error de dedo, o un código que cayó ahí). Lo que se cuenta por pieza va entero y sin punto ni coma: así «1,000»
    /// nunca se lee como una pieza. Vacío si no sirve, con el porqué en <paramref name="problema"/>.
    /// </summary>
    /// <param name="producto">Su nombre, para decir que se cuenta en piezas enteras.</param>
    public static long? Cantidad(string? texto, UnidadBase unidad, string producto, out string? problema)
    {
        texto = (texto ?? "").Trim();
        problema = null;
        var fracciones = unidad.AdmiteFracciones();
        if (texto.Length == 0)
            problema = "Escribe cuántos llegaron.";
        else if (!FormaDeCantidad().IsMatch(texto))
            problema = NoEsCantidad(texto, fracciones);
        else if (!fracciones && (texto.Contains('.') || texto.Contains(',')))
            problema = $"«{producto}» se cuenta en piezas enteras: escribe cuántos, sin punto ni coma.";
        if (problema is not null)
            return null;
        var cantidad = decimal.Parse(texto.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        if (cantidad is > 0 and <= EntradaInventario.CantidadMaxima)
            return Nucleo.Cantidad.Unidades(cantidad).Milesimas;
        problema = NoEsCantidad(texto, fracciones);
        return null;
    }

    private static string NoEsCantidad(string texto, bool fracciones) =>
        $"«{texto}» no es una cantidad: escribe cuántos llegaron, más de cero y hasta {EntradaInventario.CantidadMaxima.ToString("N0", Formato.Mexico)}"
        + (fracciones ? ", con hasta tres decimales." : ", enteros.");

    /// <summary>
    /// Lo escrito en «Costo de cada uno», en centavos, como el dinero de toda la caja (<see cref="Formato.LeerPesos"/>: «$12.50»,
    /// «12.5» o «12»), de $0.00 a <see cref="EntradaInventario.CostoMaximoCentavos"/>. Vacío si no sirve.
    /// </summary>
    public static long? Costo(string? texto) =>
        Formato.LeerPesos(texto) is { } centavos && centavos <= EntradaInventario.CostoMaximoCentavos ? centavos : null;

    /// <summary>Lo que se dice si el costo no sirve.</summary>
    public static string CostoNoSirve(string texto) =>
        $"Revisa el costo de cada uno: «{texto.Trim()}» no es dinero de $0.00 a {Formato.Dinero(EntradaInventario.CostoMaximoCentavos)}.";

    /// <summary>Un costo como se escribe en su cuadro: «12.50».</summary>
    public static string TextoDeCosto(long centavos) => (centavos / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>La unidad en plural, para «Cuántos llegaron (piezas)» y «Hay 6 piezas».</summary>
    public static string Unidades(UnidadBase unidad) => unidad switch
    {
        UnidadBase.Metro => "metros",
        UnidadBase.Kilogramo => "kilos",
        UnidadBase.Litro => "litros",
        _ => "piezas",
    };

    /// <summary>La unidad en singular, para «Costo de cada pieza».</summary>
    public static string Unidad(UnidadBase unidad) => unidad switch
    {
        UnidadBase.Metro => "metro",
        UnidadBase.Kilogramo => "kilo",
        UnidadBase.Litro => "litro",
        _ => "pieza",
    };
}
