using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Eleventa;

/// <summary>Cómo se interpreta un producto de eleventa: lo mismo al importar la base que al leerla cada pocos minutos.</summary>
public static class ReglasEleventa
{
    public const string AvisoUnidadKilo = "Granel con medida KG en eleventa (la medida está desactivada): revisar la unidad";
    public const string AvisoFraccionEnPieza = "Por pieza en eleventa pero vendido o contado con fracciones: se importó a granel";

    public const string AvisoMinimoConFracciones =
        "Mínimo o máximo de inventario con fracciones en un producto por pieza: no se tomó (revisarlo en eleventa)";
    public const string AvisoMinimoDemasiadoGrande = "Mínimo o máximo de inventario de más de un millón: no se tomó (revisarlo en eleventa)";
    public const string AvisoMaximoDebajoDelMinimo = "Máximo de inventario menor que el mínimo: se tomó solo el mínimo";

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

    /// <summary>
    /// Las cantidades también son FLOAT: se redondean a la milésima. Es la conversión de toda cantidad de eleventa (la
    /// existencia, lo vendido, desde cuántas va una promoción, el mínimo y el máximo), al importar y al leer.
    /// </summary>
    public static Cantidad CantidadDe(double cantidad) => Cantidad.Unidades(Math.Round((decimal)cantidad, 3));

    /// <summary>
    /// El mínimo y el máximo de inventario de un producto de eleventa (<c>DINVMINIMO</c>, <c>DINVMAXIMO</c>) como los guarda
    /// el POS en su variante: en su unidad y con la conversión de la existencia (<see cref="CantidadDe"/>); 0 o vacío es sin
    /// mínimo (o sin máximo). En la tienda el mínimo va igual al máximo, y el POS lo acepta: con eso o menos sale en «Bajos en
    /// inventario», y pide lo que falta para el máximo. Un máximo menor que el mínimo se deja fuera (queda el mínimo). Lo que
    /// la ficha no aceptaría (<see cref="Variante.RevisarMinimoYMaximo"/>), fracciones en un producto por pieza o más de
    /// <see cref="Variante.TopeMinimoYMaximo"/>, no se toma: vacío, y se avisa.
    /// </summary>
    /// <param name="unidad">La de la variante del POS: la de la importación, o la que tenga si la depuración la cambió.</param>
    public static (Cantidad? Minimo, Cantidad? Maximo)? MinimoYMaximo(FilaProducto p, UnidadBase unidad, Action<string> avisar)
    {
        // Lo enorme se revisa antes de convertirlo: no cabe en milésimas.
        if (p.InventarioMinimo > Variante.TopeMinimoYMaximo || p.InventarioMaximo > Variante.TopeMinimoYMaximo
            || double.IsNaN(p.InventarioMinimo) || double.IsNaN(p.InventarioMaximo))
        {
            avisar(AvisoMinimoDemasiadoGrande);
            return null;
        }
        // Lo negativo tampoco es un mínimo, ni lo que no llega a una milésima.
        static Cantidad? Leer(double valor) => valor > 0 && CantidadDe(valor) is { EsPositiva: true } cantidad ? cantidad : null;
        var (minimo, maximo) = (Leer(p.InventarioMinimo), Leer(p.InventarioMaximo));
        if (!unidad.AdmiteFracciones() && (minimo is { EsEntera: false } || maximo is { EsEntera: false }))
        {
            avisar(AvisoMinimoConFracciones);
            return null;
        }
        if (minimo is { } min && maximo is { } max && max < min)
        {
            avisar(AvisoMaximoDebajoDelMinimo);
            maximo = null;
        }
        return (minimo, maximo);
    }

    /// <summary>
    /// Pone en la variante el mínimo y el máximo de eleventa (<see cref="MinimoYMaximo"/>): al importar y en la lectura del
    /// piloto, donde eleventa manda (2026-10-24: el inventario se lleva allá). Lo que no lleva inventario (un servicio) no
    /// tiene, y lo que el POS no acepta deja la variante como estaba. Regresa si cambió algo.
    /// </summary>
    public static bool PonerMinimoYMaximo(Variante variante, bool llevaInventario, FilaProducto p, Action<string> avisar)
    {
        if (!llevaInventario || MinimoYMaximo(p, variante.UnidadBase, avisar) is not { } nuevos)
            return false;
        if ((variante.InventarioMinimo, variante.InventarioMaximo) == nuevos)
            return false;
        (variante.InventarioMinimo, variante.InventarioMaximo) = nuevos;
        return true;
    }
}
