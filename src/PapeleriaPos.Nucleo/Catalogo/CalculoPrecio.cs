namespace PapeleriaPos.Nucleo.Catalogo;

public sealed record PrecioAplicado(Precio Precio, long ImporteCentavos)
{
    /// <summary>El menudeo del mismo grupo de escalas (la de cantidad más baja): contra él se mide el mayoreo.</summary>
    public Precio? Menudeo { get; init; }

    /// <summary>Lo que costaría la misma cantidad a precio de menudeo.</summary>
    public long ImporteMenudeoCentavos { get; init; }

    /// <summary>Si se aplicó una escala de mayoreo que de verdad baja el precio.</summary>
    public bool EsMayoreo => Menudeo is { } m && Precio.CantidadMinima > m.CantidadMinima && ImporteMenudeoCentavos > ImporteCentavos;

    public long AhorroCentavos => EsMayoreo ? ImporteMenudeoCentavos - ImporteCentavos : 0;

    /// <summary>
    /// La promoción que se aplicó (fase 3): le convenía más al cliente que el mayoreo y que las demás. Entonces
    /// <see cref="Precio"/> es el menudeo y el importe, el de la promoción.
    /// </summary>
    public PromocionDeProducto? Promocion { get; init; }

    /// <summary>
    /// Lo que se ahorró por la promoción contra el precio de la línea (<see cref="PrecioPorPresentacion"/>) por la cantidad: así
    /// el importe de la línea es siempre ese precio por la cantidad menos este ahorro, y se vuelve a armar igual al subir una
    /// venta sin conexión. Cero sin promoción.
    /// </summary>
    public long AhorroPromocionCentavos { get; init; }

    /// <summary>El precio de una presentación como va en la línea de la venta: el propio de la presentación, o el de la unidad base por las que trae.</summary>
    public long PrecioPorPresentacion(Presentacion presentacion) =>
        Precio.PresentacionId is null ? presentacion.Factor.Importe(Precio.PrecioCentavos) : Precio.PrecioCentavos;
}

/// <summary>Las escalas que tocan a una presentación: las suyas propias (raro) o las de la unidad base, de menor a mayor.</summary>
/// <param name="DePresentacion">Si son precios propios de la presentación: las cantidades son de presentaciones, no de piezas.</param>
public sealed record EscalasDePrecio(bool DePresentacion, IReadOnlyList<Precio> Escalas);

public static class CalculoPrecio
{
    /// <summary>
    /// Precio que aplica al vender <paramref name="cantidad"/> de una presentación en una sucursal:
    /// <list type="number">
    /// <item>Si la presentación tiene precio propio (raro), se usa ese, por cada presentación.</item>
    /// <item>Si no, el precio por unidad base: el paquete de 12 cuesta 12 piezas y cuenta como 12 para el mayoreo.</item>
    /// </list>
    /// En ambos casos se prefieren los precios de la sucursal sobre los base, y se toma la escala más alta que
    /// alcance la cantidad. Con <paramref name="mayoreoSiempre"/> (el privilegio de un cliente registrado), al menos la
    /// primera escala de mayoreo aunque no se llegue a su cantidad. Devuelve <c>null</c> si la variante no tiene precio.
    /// <para>
    /// Nunca se cobra más que a menudeo: si las escalas quedaron capturadas fuera de orden (una más cara que el menudeo o que
    /// una de menos piezas), se cobra la más barata de las que alcanza; y un paquete cuyo precio propio cuesta más que sus
    /// piezas a menudeo se cobra por sus piezas.
    /// </para>
    /// <para>
    /// Con las <paramref name="promociones"/> del producto que corren ese <paramref name="dia"/> (fase 3): no se acumulan. Cada
    /// una se calcula sobre el menudeo y se queda la que deja el importe más bajo, si es más bajo que el del mayoreo (o el
    /// menudeo); entre dos iguales, la de menor Id, para que la caja y el servicio elijan la misma. Con el
    /// <paramref name="descuentoDelCliente"/> (el fijo del cliente registrado, que tampoco se acumula con ellas), la promoción
    /// tiene que dejar menos que el mayoreo (o el menudeo) con ese descuento; si no, se queda el descuento. Es el único lugar
    /// donde se decide el precio: la pantalla de la caja, su cobro sin conexión y el servicio calculan igual.
    /// </para>
    /// </summary>
    /// <param name="descuentoDelCliente">En centésimas (500 = 5 %): el del cliente registrado que lleva el renglón.</param>
    public static PrecioAplicado? Calcular(
        IEnumerable<Precio> precios, Guid sucursalId, Presentacion presentacion, Cantidad cantidad, bool mayoreoSiempre = false,
        IReadOnlyList<PromocionDeProducto>? promociones = null, DateOnly? dia = null, int? descuentoDelCliente = null)
    {
        var regular = SinPromociones(precios, sucursalId, presentacion, cantidad, mayoreoSiempre);
        return regular is not null && promociones is { Count: > 0 } && dia is { } hoy && cantidad.EsPositiva
            ? ConLaQueMasConviene(regular, presentacion, cantidad, promociones, hoy, descuentoDelCliente)
            : regular;
    }

    private static PrecioAplicado? SinPromociones(
        IEnumerable<Precio> precios, Guid sucursalId, Presentacion presentacion, Cantidad cantidad, bool mayoreoSiempre)
    {
        var deLaVariante = precios.Where(p => p.VarianteId == presentacion.VarianteId).ToList();

        var propias = DeSucursalOBase(deLaVariante.Where(p => p.PresentacionId == presentacion.Id), sucursalId);
        var porUnidad = DeSucursalOBase(deLaVariante.Where(p => p.PresentacionId is null), sucursalId);
        // El paquete nunca cuesta más que sus piezas a menudeo: si así quedó su precio propio, se cobra por sus piezas.
        if (ElegirEscala(propias, cantidad, mayoreoSiempre) is { } propio
            && (porUnidad.MinBy(p => p.CantidadMinima) is not { } menudeoPorPieza
                || propio.PrecioCentavos <= presentacion.Factor.Importe(menudeoPorPieza.PrecioCentavos)))
        {
            var menudeo = propias.MinBy(p => p.CantidadMinima)!;
            return new PrecioAplicado(propio, cantidad.Importe(propio.PrecioCentavos))
            {
                Menudeo = menudeo,
                ImporteMenudeoCentavos = cantidad.Importe(menudeo.PrecioCentavos),
            };
        }

        var unidadesBase = cantidad.Por(presentacion.Factor);
        if (ElegirEscala(porUnidad, unidadesBase, mayoreoSiempre) is not { } escala)
            return null;
        var deMenudeo = porUnidad.MinBy(p => p.CantidadMinima)!;
        return new PrecioAplicado(escala, unidadesBase.Importe(escala.PrecioCentavos))
        {
            Menudeo = deMenudeo,
            ImporteMenudeoCentavos = unidadesBase.Importe(deMenudeo.PrecioCentavos),
        };
    }

    /// <summary>
    /// La que más le conviene al cliente: el mayoreo (o el menudeo) que ya se calculó, o la promoción que deja el importe más
    /// bajo. Las promociones van sobre el menudeo del mismo grupo de precios (el de la unidad base, o el propio de la
    /// presentación) y cuentan en sus unidades: piezas (un paquete de 12 son 12 para el 3×2, como para el mayoreo) o
    /// presentaciones, si tienen precio propio. El precio especial es por unidad base.
    /// </summary>
    private static PrecioAplicado ConLaQueMasConviene(
        PrecioAplicado regular, Presentacion presentacion, Cantidad cantidad, IReadOnlyList<PromocionDeProducto> promociones, DateOnly dia,
        int? descuentoDelCliente)
    {
        var menudeo = regular.Menudeo ?? regular.Precio;
        var porPresentacion = menudeo.PresentacionId is not null;
        var unidades = porPresentacion ? cantidad : cantidad.Por(presentacion.Factor);
        var importeMenudeo = unidades.Importe(menudeo.PrecioCentavos);
        var conMenudeo = new PrecioAplicado(menudeo, importeMenudeo) { Menudeo = menudeo, ImporteMenudeoCentavos = importeMenudeo };
        // Lo que vale la línea a ese precio: la promoción tiene que quedar debajo de eso y del mayoreo; con el descuento del
        // cliente, del mayoreo con su descuento (como lo descuenta la venta).
        var importeDeLaLinea = cantidad.Importe(conMenudeo.PrecioPorPresentacion(presentacion));
        PromocionDeProducto? mejor = null;
        var importeMejor = Math.Min(regular.ImporteCentavos, importeDeLaLinea);
        if (descuentoDelCliente is > 0 and var descuento)
            importeMejor = Math.Min(importeMejor, regular.ImporteCentavos - Redondeo.Dividir((Int128)regular.ImporteCentavos * descuento, 10000));
        foreach (var promocion in promociones.Where(p => p.AplicaEl(dia)).OrderBy(p => p.PromocionId))
        {
            if (ImporteCon(promocion, menudeo.PrecioCentavos, unidades, importeMenudeo, cantidad.Por(presentacion.Factor)) is { } importe
                && importe > 0 && importe < importeMejor)
                (mejor, importeMejor) = (promocion, importe);
        }
        return mejor is null
            ? regular
            : conMenudeo with { ImporteCentavos = importeMejor, Promocion = mejor, AhorroPromocionCentavos = importeDeLaLinea - importeMejor };
    }

    /// <summary>
    /// El precio por unidad base más bajo al que la promoción deja un producto, con el mismo cálculo que la venta: con su grupo
    /// completo (3 en un 3×2, 2 en el segundo a mitad de precio) sobre el menudeo de cada grupo de precios con que lo vende (el
    /// de la unidad base y el propio de cada presentación que lo tiene y que la venta usa: el que no cuesta más que sus piezas),
    /// el más bajo. Una caja de 12 a $120 de un producto de $12 la pieza baja con un 3×2 a $6.67 la pieza, no a $8. Vacío si no
    /// lo baja en ninguno (o no tiene precio): entonces no lo deja debajo de nada. Con esto se revisa el costo al darla de alta y
    /// al llevarla a las cajas.
    /// </summary>
    public static decimal? PrecioMasBajoCon(PromocionDeProducto promocion, IEnumerable<Precio> precios, IEnumerable<Presentacion> presentaciones, Guid sucursalId)
    {
        var lista = precios.ToList();
        var menudeoPorUnidad = DeSucursalOBase(lista.Where(p => p.PresentacionId is null), sucursalId).MinBy(p => p.CantidadMinima)?.PrecioCentavos;
        var masBajo = menudeoPorUnidad is { } menudeo ? PorUnidadBase(promocion, menudeo, Cantidad.Piezas(1)) : null;
        foreach (var presentacion in presentaciones.Where(p => p.Factor.EsPositiva))
        {
            var propias = DeSucursalOBase(lista.Where(p => p.PresentacionId == presentacion.Id), sucursalId);
            // La venta usa su precio propio solo si alguna de sus escalas no cuesta más que sus piezas a menudeo.
            if (propias.Count == 0 || menudeoPorUnidad is { } porPieza && propias.Min(p => p.PrecioCentavos) > presentacion.Factor.Importe(porPieza))
                continue;
            if (PorUnidadBase(promocion, propias.MinBy(p => p.CantidadMinima)!.PrecioCentavos, presentacion.Factor) is { } propio
                && (masBajo is null || propio < masBajo))
                masBajo = propio;
        }
        return masBajo;
    }

    /// <summary>Un grupo completo de la promoción sobre el menudeo de un grupo de precios, por unidad base; vacío si no lo baja.</summary>
    /// <param name="factor">Las unidades base de cada unidad del grupo: una en el de la unidad base; las de la presentación en uno propio.</param>
    private static decimal? PorUnidadBase(PromocionDeProducto promocion, long menudeo, Cantidad factor)
    {
        var grupo = promocion.Tipo switch
        {
            TipoPromocion.LlevaNPagaM => Math.Max(1, promocion.Lleva),
            TipoPromocion.SegundoAMitad => 2,
            _ => 1,
        };
        var unidades = Cantidad.Piezas(grupo);
        var importe = unidades.Importe(menudeo);
        var unidadesBase = unidades.Por(factor);
        // Como la venta: la promoción solo se aplica si deja menos que el menudeo.
        return ImporteCon(promocion, menudeo, unidades, importe, unidadesBase) is { } conPromocion && conPromocion > 0 && conPromocion < importe
            ? conPromocion / (unidadesBase.Milesimas / (decimal)Cantidad.Escala)
            : null;
    }

    /// <summary>Lo que cuesta la cantidad con esa promoción; vacío si no se puede aplicar (le falta lo de su tipo).</summary>
    /// <param name="precioMenudeo">El menudeo, por cada una de <paramref name="unidades"/>.</param>
    /// <param name="unidades">En lo que cuenta el menudeo: unidades base, o presentaciones con precio propio.</param>
    /// <param name="unidadesBase">Para el precio especial, que es por unidad base.</param>
    private static long? ImporteCon(PromocionDeProducto p, long precioMenudeo, Cantidad unidades, long importeMenudeo, Cantidad unidadesBase)
    {
        switch (p.Tipo)
        {
            case TipoPromocion.Porcentaje when p.Centesimas is > 0 and < 10000:
                return importeMenudeo - Redondeo.Dividir((Int128)importeMenudeo * p.Centesimas, 10000);
            case TipoPromocion.PrecioEspecial when p.PrecioCentavos is > 0 and var especial:
                return unidadesBase.Importe(especial);
            case TipoPromocion.LlevaNPagaM when p.Paga >= 1 && p.Lleva > p.Paga:
                // De cada N completas (se cuentan las enteras: 3.5 m son un grupo de 3), N − M van gratis.
                var grupos = unidades.Milesimas / (p.Lleva * Cantidad.Escala);
                return importeMenudeo - Cantidad.Piezas(grupos * (p.Lleva - p.Paga)).Importe(precioMenudeo);
            case TipoPromocion.SegundoAMitad:
                var pares = unidades.Milesimas / (2 * Cantidad.Escala);
                return importeMenudeo - Redondeo.Dividir(Cantidad.Piezas(pares).Importe(precioMenudeo), 2);
            default:
                return null;
        }
    }

    /// <summary>Las escalas de una presentación en la sucursal, con la misma regla que <see cref="Calcular"/> (F11 las muestra).</summary>
    public static EscalasDePrecio Escalas(IEnumerable<Precio> precios, Guid sucursalId, Presentacion presentacion)
    {
        var deLaVariante = precios.Where(p => p.VarianteId == presentacion.VarianteId).ToList();
        var propias = DeSucursalOBase(deLaVariante.Where(p => p.PresentacionId == presentacion.Id), sucursalId);
        return propias.Count > 0
            ? new EscalasDePrecio(true, propias.OrderBy(p => p.CantidadMinima).ToList())
            : new EscalasDePrecio(false, DeSucursalOBase(deLaVariante.Where(p => p.PresentacionId is null), sucursalId).OrderBy(p => p.CantidadMinima).ToList());
    }

    private static List<Precio> DeSucursalOBase(IEnumerable<Precio> precios, Guid sucursalId)
    {
        var lista = precios.ToList();
        var deSucursal = lista.Where(p => p.SucursalId == sucursalId).ToList();
        return deSucursal.Count > 0 ? deSucursal : lista.Where(p => p.SucursalId is null).ToList();
    }

    /// <summary>
    /// La escala más alta que alcanza la cantidad; si no alcanza ninguna (medio metro), la más baja. Con mayoreo siempre,
    /// nunca menos que la primera de mayoreo (la segunda escala). Con las escalas en orden (cada una más barata que la
    /// anterior) es la más barata de las que alcanza, y eso se toma: si una quedó fuera de orden (más cara que el menudeo o
    /// que una de menos piezas), no se cobra más por llevar más.
    /// </summary>
    private static Precio? ElegirEscala(List<Precio> escalas, Cantidad cantidad, bool mayoreoSiempre)
    {
        var abs = cantidad.Abs();
        var alcanzadas = escalas.Where(p => p.CantidadMinima <= abs).ToList();
        if (mayoreoSiempre && escalas.OrderBy(p => p.CantidadMinima).Skip(1).FirstOrDefault() is { } primeraDeMayoreo
            && !alcanzadas.Contains(primeraDeMayoreo))
            alcanzadas.Add(primeraDeMayoreo);
        // Entre dos al mismo precio, la de más piezas, como antes.
        return alcanzadas.OrderBy(p => p.PrecioCentavos).ThenByDescending(p => p.CantidadMinima).FirstOrDefault()
            ?? escalas.MinBy(p => p.CantidadMinima);
    }
}
