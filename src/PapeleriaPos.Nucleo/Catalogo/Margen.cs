namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>
/// El margen sobre el precio de venta, con IVA de los dos lados (el costo es lo que se paga): (precio − costo) ÷ precio.
/// En centésimas de porcentaje, como los descuentos: 3000 = 30 %.
/// </summary>
public static class Margen
{
    /// <summary>El que se pide si el dueño no pone otro (docs/fase-2-inventario.md, decisiones de la revisión).</summary>
    public const int MinimoInicial = 3000;

    /// <summary>Los precios sugeridos suben a los siguientes 50 centavos.</summary>
    public const long RedondeoSugeridoCentavos = 50;

    public static int Calcular(long precioCentavos, decimal costoCentavos)
    {
        if (precioCentavos <= 0)
            throw new ArgumentOutOfRangeException(nameof(precioCentavos), "El precio debe ser mayor a cero.");
        return (int)Math.Round((precioCentavos - costoCentavos) * 10000 / precioCentavos, MidpointRounding.AwayFromZero);
    }

    /// <summary>El precio más bajo que deja el margen mínimo con ese costo, subido a los siguientes 50 centavos.</summary>
    public static long PrecioSugerido(decimal costoCentavos, int margenMinimoCentesimas)
    {
        if (margenMinimoCentesimas is < 0 or >= 10000)
            throw new ArgumentOutOfRangeException(nameof(margenMinimoCentesimas), "El margen va de 0 a 99.99 %.");
        var exacto = costoCentavos * 10000 / (10000 - margenMinimoCentesimas);
        return Math.Max(RedondeoSugeridoCentavos, (long)Math.Ceiling(exacto / RedondeoSugeridoCentavos) * RedondeoSugeridoCentavos);
    }

    public static string Texto(int centesimas) =>
        $"{(centesimas / 100m).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("es-MX"))} %";
}
