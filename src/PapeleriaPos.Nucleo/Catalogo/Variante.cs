namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>Lo que se cuenta y se vende ("Azul"). Un producto sin variantes tiene exactamente una.</summary>
public class Variante : IVersionado
{
    /// <summary>Lo más que se puede escribir de mínimo o de máximo, en unidades: más que eso ya es un error de dedo.</summary>
    public const int TopeMinimoYMaximo = 1_000_000;

    public Guid Id { get; set; } = Ids.Nuevo();

    public Guid ProductoId { get; set; }
    public Producto? Producto { get; set; }

    /// <summary>Texto libre ("Azul", "Carta · 75 g"). Vacía si es la única variante.</summary>
    public string Descripcion { get; set; } = "";

    public UnidadBase UnidadBase { get; set; } = UnidadBase.Pieza;
    public bool ManejaCaducidad { get; set; }
    public bool Activa { get; set; } = true;

    /// <summary>Si se juntó con otra (estaba capturada dos veces): su historia se queda aquí, lo demás pasó a esa.</summary>
    public Guid? JuntadaEnId { get; set; }

    /// <summary>
    /// Su mínimo de inventario, en unidad base, como el «inventario mínimo» de eleventa: con eso o menos sale en «Poca
    /// existencia» (Productos, F3) y en «Bajos en inventario» (Inventario, F4). Vacío: la regla de siempre
    /// (<c>PocaExistencia.Hasta</c> o menos). El de un producto de eleventa (su <c>DINVMINIMO</c>) lo trae la importación y,
    /// en el piloto, la lectura, que lo pone cada vez: el inventario se lleva allá y aquí no se cambia (2026-10-24). Uno para
    /// todas las sucursales, como el catálogo.
    /// </summary>
    public Cantidad? InventarioMinimo { get; set; }

    /// <summary>
    /// Su máximo de inventario, en unidad base: «Bajos en inventario» dice cuánto pedir para llegar a él. Vacío: lo que pide
    /// el sugerido de compra, que calcula el suyo con lo que se vende. Puede ser igual al mínimo, como los pone la tienda en
    /// eleventa (<c>DINVMAXIMO</c>): con eso o menos sale, y se pide lo que falta para llegar a él.
    /// </summary>
    public Cantidad? InventarioMaximo { get; set; }

    public List<Presentacion> Presentaciones { get; set; } = [];

    public bool AdmiteCantidad(Cantidad cantidad) => UnidadBase.AdmiteFracciones() || cantidad.EsEntera;

    /// <summary>
    /// Que el mínimo y el máximo de <paramref name="quien"/> se puedan guardar: sin negativos, el máximo mayor a cero y no
    /// menor que el mínimo, en piezas enteras si se vende por pieza y hasta <see cref="TopeMinimoYMaximo"/>. Si no, el error
    /// que se le dice a quien edita la ficha.
    /// </summary>
    public static void RevisarMinimoYMaximo(UnidadBase unidad, Cantidad? minimo, Cantidad? maximo, string quien)
    {
        var tope = Cantidad.Piezas(TopeMinimoYMaximo);
        if (minimo is { } min)
        {
            if (min.Milesimas < 0)
                throw new ArgumentException($"El mínimo de «{quien}» no puede ser negativo.");
            if (min > tope)
                throw new ArgumentException($"El mínimo de «{quien}» es demasiado grande.");
            if (!unidad.AdmiteFracciones() && !min.EsEntera)
                throw new ArgumentException($"El mínimo de «{quien}» va en piezas enteras.");
        }
        if (maximo is { } max)
        {
            if (!max.EsPositiva)
                throw new ArgumentException($"El máximo de «{quien}» va mayor a cero.");
            if (max > tope)
                throw new ArgumentException($"El máximo de «{quien}» es demasiado grande.");
            if (!unidad.AdmiteFracciones() && !max.EsEntera)
                throw new ArgumentException($"El máximo de «{quien}» va en piezas enteras.");
            if (minimo is { } conMinimo && max < conMinimo)
                throw new ArgumentException($"El máximo de «{quien}» no puede ser menor que su mínimo.");
        }
    }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
