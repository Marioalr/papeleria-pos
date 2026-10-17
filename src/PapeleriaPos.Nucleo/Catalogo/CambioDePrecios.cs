using System.Globalization;

namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>Cómo cambian los precios de varios productos a la vez (Productos › Cambiar precios).</summary>
public enum ComoCambiarPrecios
{
    /// <summary>Un porcentaje más; el valor va en centésimas (1000 = 10 %).</summary>
    SubirPorcentaje,

    /// <summary>Un porcentaje menos, en centésimas: menos de 100 %.</summary>
    BajarPorcentaje,

    /// <summary>Una cantidad más, en centavos.</summary>
    SubirMonto,

    /// <summary>Una cantidad menos, en centavos.</summary>
    BajarMonto,

    /// <summary>El mismo precio para todos, en centavos («Establecer un precio fijo» de eleventa).</summary>
    PrecioFijo,
}

/// <summary>A cuánto se redondea lo que se calcula, como pone sus precios la tienda.</summary>
public enum RedondeoDePrecios
{
    SinRedondear,

    /// <summary>A los 50 centavos más cercanos: $12.20 → $12.00, $12.25 → $12.50.</summary>
    CincuentaCentavos,

    /// <summary>Al peso más cercano: $12.49 → $12.00, $12.50 → $13.00.</summary>
    UnPeso,
}

/// <summary>La regla con que cambian los precios de varios productos.</summary>
/// <param name="Valor">El porcentaje en centésimas (1000 = 10 %) o la cantidad en centavos, según <paramref name="Como"/>.</param>
/// <param name="ConMayoreo">
/// También su mayoreo (las escalas por cantidad y los precios por paquete), en la misma proporción que el menudeo.
/// </param>
public sealed record ReglaDePrecios(ComoCambiarPrecios Como, long Valor, bool ConMayoreo, RedondeoDePrecios Redondeo);

/// <summary>
/// Cuánto cambia un precio: el nuevo es el de antes × <see cref="Numerador"/> ÷ <see cref="Denominador"/>, sin redondear.
/// Con un porcentaje es la misma para todos; con una cantidad o un precio fijo, la de cada producto sale de su menudeo.
/// </summary>
public readonly record struct Proporcion(long Numerador, long Denominador)
{
    /// <summary>Más de cero: sube; menos de cero: baja; cero: se queda igual.</summary>
    public int Sentido => Numerador.CompareTo(Denominador);
}

/// <summary>
/// Cambiar los precios de varios productos a la vez, como «Actualizar varios» de eleventa (docs/paridad-eleventa.md): un
/// porcentaje o una cantidad más o menos, o un precio fijo, sobre el precio de menudeo; si se pide, el mayoreo cambia en la
/// misma proporción, y lo que se calcula se redondea como pone sus precios la tienda.
/// <list type="bullet">
/// <item>Al redondear se va al más cercano (los medios suben). Nunca queda al revés de lo pedido: si se sube y el más
/// cercano queda abajo del precio de antes (o se baja y queda arriba), el precio se queda como estaba. Y nunca se redondea a
/// $0: lo que vale menos de medio paso se queda sin redondear.</item>
/// <item>Un precio fijo queda tal cual se escribió; su mayoreo sí se redondea.</item>
/// <item>Lo que quedaría en $0 o menos no se cambia.</item>
/// </list>
/// </summary>
public static class CambioDePrecios
{
    /// <summary>
    /// Cuántos productos (variantes) se cambian a la vez, como mucho: la vista previa se lee entera antes de aplicar y todo
    /// va en una sola transacción.
    /// </summary>
    public const int Maximo = 1000;

    /// <summary>Lo más que se sube un porcentaje: 1000 % (diez veces el precio). Más parece un error de dedo.</summary>
    public const long MaximoPorcentaje = 100_000;

    /// <summary>La cantidad más grande que se escribe: $100,000.00.</summary>
    public const long MaximoCentavos = 10_000_000;

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Que la regla tenga sentido; si no, el error dice qué corregir.</summary>
    public static void Validar(ReglaDePrecios regla)
    {
        if (!Enum.IsDefined(regla.Como) || !Enum.IsDefined(regla.Redondeo))
            throw new ArgumentException("Elige cómo cambian los precios.");
        var porcentaje = regla.Como is ComoCambiarPrecios.SubirPorcentaje or ComoCambiarPrecios.BajarPorcentaje;
        if (regla.Valor <= 0)
            throw new ArgumentException(porcentaje ? "Escribe el porcentaje: más de cero."
                : regla.Como == ComoCambiarPrecios.PrecioFijo ? "Escribe el precio: más de cero." : "Escribe cuánto: más de cero.");
        if (regla.Como == ComoCambiarPrecios.BajarPorcentaje && regla.Valor >= 10_000)
            throw new ArgumentException("No se puede bajar 100 % o más: los precios quedarían en $0.");
        if (regla.Como == ComoCambiarPrecios.SubirPorcentaje && regla.Valor > MaximoPorcentaje)
            throw new ArgumentException("Revisa el porcentaje: se sube hasta 1000 %.");
        if (!porcentaje && regla.Valor > MaximoCentavos)
            throw new ArgumentException($"Revisa la cantidad: hasta {Pesos(MaximoCentavos)}.");
    }

    /// <summary>La proporción de un producto con ese menudeo; la misma para su mayoreo.</summary>
    public static Proporcion ProporcionDe(ReglaDePrecios regla, long menudeoCentavos)
    {
        if (menudeoCentavos <= 0)
            throw new ArgumentException("El precio de menudeo es mayor que cero.");
        return regla.Como switch
        {
            ComoCambiarPrecios.SubirPorcentaje => new(10_000 + regla.Valor, 10_000),
            ComoCambiarPrecios.BajarPorcentaje => new(10_000 - regla.Valor, 10_000),
            ComoCambiarPrecios.SubirMonto => new(menudeoCentavos + regla.Valor, menudeoCentavos),
            ComoCambiarPrecios.BajarMonto => new(menudeoCentavos - regla.Valor, menudeoCentavos),
            _ => new(regla.Valor, menudeoCentavos),
        };
    }

    /// <summary>El menudeo nuevo: un precio fijo tal cual; lo demás, calculado y redondeado. Vacío si quedaría en $0 o menos.</summary>
    public static long? Menudeo(ReglaDePrecios regla, long antesCentavos)
    {
        if (antesCentavos <= 0)
            return null;
        return regla.Como == ComoCambiarPrecios.PrecioFijo
            ? regla.Valor
            : EnProporcion(antesCentavos, ProporcionDe(regla, antesCentavos), regla.Redondeo);
    }

    /// <summary>Un precio en esa proporción (el menudeo calculado o uno de su mayoreo), redondeado. Vacío si quedaría en $0 o menos.</summary>
    public static long? EnProporcion(long antesCentavos, Proporcion proporcion, RedondeoDePrecios redondeo)
    {
        if (antesCentavos <= 0 || proporcion.Numerador <= 0 || proporcion.Denominador <= 0)
            return null;
        if (proporcion.Sentido == 0)
            return antesCentavos;
        var numerador = (Int128)antesCentavos * proporcion.Numerador;
        var exacto = Redondeo.Dividir(numerador, proporcion.Denominador);
        var paso = Paso(redondeo);
        if (paso == 0)
            return exacto > 0 ? exacto : null;
        var cercano = Redondeo.Dividir(numerador, (Int128)proporcion.Denominador * paso) * paso;
        long nuevo;
        if (cercano <= 0)
            nuevo = exacto;
        else if ((proporcion.Sentido > 0 && cercano < antesCentavos) || (proporcion.Sentido < 0 && cercano > antesCentavos))
            nuevo = antesCentavos;
        else
            nuevo = cercano;
        return nuevo > 0 ? nuevo : null;
    }

    /// <summary>Los centavos del paso del redondeo; cero sin redondear.</summary>
    public static long Paso(RedondeoDePrecios redondeo) => redondeo switch
    {
        RedondeoDePrecios.CincuentaCentavos => 50,
        RedondeoDePrecios.UnPeso => 100,
        _ => 0,
    };

    /// <summary>«subir 10 %, con el mayoreo en la misma proporción, redondeado a los 50 centavos más cercanos».</summary>
    public static string Texto(ReglaDePrecios regla)
    {
        var como = regla.Como switch
        {
            ComoCambiarPrecios.SubirPorcentaje => $"subir {Porcentaje(regla.Valor)}",
            ComoCambiarPrecios.BajarPorcentaje => $"bajar {Porcentaje(regla.Valor)}",
            ComoCambiarPrecios.SubirMonto => $"subir {Pesos(regla.Valor)}",
            ComoCambiarPrecios.BajarMonto => $"bajar {Pesos(regla.Valor)}",
            _ => $"ponerlos a {Pesos(regla.Valor)}",
        };
        var mayoreo = regla.ConMayoreo ? "con el mayoreo en la misma proporción" : "sin cambiar el mayoreo";
        // Un precio fijo queda tal cual: sin el mayoreo, no hay nada que redondear.
        if (regla.Como == ComoCambiarPrecios.PrecioFijo && !regla.ConMayoreo)
            return $"{como}, {mayoreo}";
        var redondeo = regla.Redondeo switch
        {
            RedondeoDePrecios.CincuentaCentavos => "redondeado a los 50 centavos más cercanos",
            RedondeoDePrecios.UnPeso => "redondeado al peso más cercano",
            _ => "sin redondear",
        };
        return $"{como}, {mayoreo}, {redondeo}";
    }

    /// <summary>«10 %», «12.5 %» (en centésimas: 1250).</summary>
    public static string Porcentaje(long centesimas) => $"{(centesimas / 100m).ToString("0.##", Mexico)} %";

    private static string Pesos(long centavos) => (centavos / 100m).ToString("$#,##0.00", Mexico);
}
