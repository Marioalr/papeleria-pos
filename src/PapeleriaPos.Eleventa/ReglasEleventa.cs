using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Eleventa;

/// <summary>Cómo se interpreta un producto de eleventa: lo mismo al importar la base que al leerla cada pocos minutos.</summary>
public static class ReglasEleventa
{
    public const string AvisoUnidadKilo = "Granel con medida KG en eleventa (la medida está desactivada): revisar la unidad";
    public const string AvisoFraccionEnPieza = "Por pieza en eleventa pero vendido o contado con fracciones: se importó a granel";

    /// <summary>
    /// 'U' es por pieza; 'D' (granel) según la medida: metro, litro o kilo. Muchos productos a granel de eleventa
    /// tienen la medida KG aunque se venden por metro o por pieza suelta; si el nombre dice metro, se toma metro.
    /// </summary>
    public static UnidadBase Unidad(FilaProducto p, bool conFracciones, Action<string> avisar)
    {
        if (p.TipoVenta != 'D' && !conFracciones)
            return UnidadBase.Pieza;
        if (p.TipoVenta != 'D')
            avisar(AvisoFraccionEnPieza);

        if (p.MedidaId == 3 || PareceMetro(p.Descripcion))
            return UnidadBase.Metro;
        if (p.MedidaId == 5)
            return UnidadBase.Litro;
        if (p.TipoVenta == 'D')
            avisar(AvisoUnidadKilo);
        return UnidadBase.Kilogramo;
    }

    private static bool PareceMetro(string descripcion)
    {
        var palabras = Texto.Normalizar(descripcion).Replace(".", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return palabras.Any(p => p is "METRO" or "METROS" or "MT" or "MTS" or "XM" or "XMT" or "XMETRO");
    }

    public static bool EsEntero(double cantidad) => Math.Abs(cantidad - Math.Round(cantidad)) < 0.0005;

    /// <summary>Eleventa guarda los montos como FLOAT: se redondean al centavo.</summary>
    public static long Centavos(double pesos) => (long)Math.Round((decimal)pesos * 100, MidpointRounding.AwayFromZero);

    /// <summary>Las cantidades también son FLOAT: se redondean a la milésima.</summary>
    public static Cantidad CantidadDe(double cantidad) => Cantidad.Unidades(Math.Round((decimal)cantidad, 3));
}
