using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Eleventa;

/// <summary>Cómo se interpreta un producto de eleventa: lo mismo al importar la base que al leerla cada pocos minutos.</summary>
public static class ReglasEleventa
{
    public const string AvisoMedidaKg =
        "Granel con la medida KG en eleventa (desactivada; la tienda no tiene báscula): entró como pieza a granel, sin «kg»; su unidad se decide en la depuración";
    public const string AvisoFraccionEnPieza = "Por pieza en eleventa pero vendido o contado con fracciones: se importó a granel";

    public const string AvisoMinimoConFracciones =
        "Mínimo o máximo de inventario con fracciones en un producto por pieza: no se tomó (revisarlo en eleventa)";
    public const string AvisoMinimoDemasiadoGrande = "Mínimo o máximo de inventario de más de un millón: no se tomó (revisarlo en eleventa)";
    public const string AvisoMaximoDebajoDelMinimo = "Máximo de inventario menor que el mínimo: se tomó solo el mínimo";

    /// <summary>
    /// 'U' es por pieza; 'D' (granel) según la medida: metro, litro o, si no, la pieza a granel: fracciones sin «kg» (2026-10-24).
    /// Así entran las impresiones (la medida PIEZA) y lo que tiene la medida KG (revisión del PR #139; decidido, por revisar): en la
    /// tienda está desactivada y no hay báscula, y casi todo eso se vende por pieza suelta o por metro. Con KG se avisa que su unidad
    /// se decide en la depuración; si el nombre dice metro, se toma metro. Sin medida conocida, también la pieza a granel. El kilo
    /// queda para lo que se decida en la depuración.
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
        if (p.MedidaId == 4 && p.TipoVenta == 'D')
            avisar(AvisoMedidaKg);
        return UnidadBase.PiezaAGranel;
    }

    /// <summary>
    /// El nombre de la presentación base de lo que llega de eleventa: el de su unidad («Pieza», «Metro», «Kilogramo»); la pieza
    /// a granel, «Pieza» (así se ve al recibir una compra).
    /// </summary>
    public static string NombreDeLaPresentacion(UnidadBase unidad) => unidad == UnidadBase.PiezaAGranel ? "Pieza" : unidad.ToString();

    /// <summary>
    /// Corrige lo que una importación de antes puso en kilo: lo a granel con la medida PIEZA, como las impresiones (el ticket decía
    /// «0.2 kg»), sin medida o con la medida KG, que en la tienda está desactivada (revisión del PR #139). Pasa a la pieza a granel,
    /// como se importa ahora, y su presentación base, si se llama como la unidad, a «Pieza». Las cantidades y los precios no cambian:
    /// solo cómo se dice la unidad. Lo de eleventa en metros o litros, y lo demás de la unidad, no se cambia solo. Regresa si
    /// corrigió.
    /// </summary>
    public static bool CorregirKilo(Variante variante, FilaProducto p, Presentacion? presentacionBase)
    {
        if (variante.UnidadBase != UnidadBase.Kilogramo || Unidad(p, conFracciones: true, _ => { }) != UnidadBase.PiezaAGranel)
            return false;
        variante.UnidadBase = UnidadBase.PiezaAGranel;
        if (presentacionBase is { Nombre: nameof(UnidadBase.Kilogramo) })
            presentacionBase.Nombre = NombreDeLaPresentacion(UnidadBase.PiezaAGranel);
        return true;
    }

    private static bool PareceMetro(string descripcion)
    {
        var palabras = Texto.Normalizar(descripcion).Replace(".", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return palabras.Any(p => p is "METRO" or "METROS" or "MT" or "MTS" or "XM" or "XMT" or "XMETRO");
    }

    public static bool EsEntero(double cantidad) => Math.Abs(cantidad - Math.Round(cantidad)) < 0.0005;

    /// <summary>
    /// Si es el departamento de lo que no tiene departamento: el «- Sin Departamento -» de eleventa o el «Sin departamento» que
    /// se crea aquí sin él. Ahí va lo que llega sin departamento, sin crear otro junto al de eleventa (2026-10-24).
    /// </summary>
    public static bool EsSinDepartamento(string nombre) =>
        string.Join(' ', Texto.Normalizar(new string(nombre.Where(c => char.IsLetter(c) || char.IsWhiteSpace(c)).ToArray()))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)) == "SIN DEPARTAMENTO";

    /// <summary>Si es el departamento donde eleventa pone sus artículos comunes («Productos Comunes»).</summary>
    public static bool EsDeComunes(string nombre) =>
        string.Equals(nombre.Trim(), EnvioEleventa.DepartamentoComunes, StringComparison.OrdinalIgnoreCase);

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
    /// La marca (un renglón de <c>MapeosImportacion</c>, con <see cref="IdMinimosTraidos"/>) de que los mínimos y máximos de
    /// eleventa ya se trajeron a esta base, una sola vez: con la importación de la base nueva, la lectura del piloto o la primera
    /// importación final que los trae. Desde ahí son del POS: una importación final no los vuelve a poner, ni el que se quitó en F3
    /// (revisión del PR #139). Lo que llega después (un producto nuevo) trae los suyos al darse de alta.
    /// </summary>
    public const string EntidadMinimosTraidos = "MinimosYMaximos";

    /// <summary>El <c>IdOrigen</c> de la marca de <see cref="EntidadMinimosTraidos"/> (su <c>IdNuevo</c> no se usa).</summary>
    public const string IdMinimosTraidos = "traidos";

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
