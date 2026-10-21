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

/// <summary>Por qué un producto (una de sus variantes) de los elegidos no cambia.</summary>
public enum PorQueNoCambia
{
    /// <summary>Con el redondeo, su precio queda como estaba.</summary>
    QuedaIgual,

    /// <summary>En el piloto con eleventa, su menudeo se cambia allá (y no tiene mayoreo de aquí que cambiar).</summary>
    EnEleventa,

    /// <summary>Su precio (o uno de su mayoreo) quedaría en $0 o menos.</summary>
    QuedariaEnCero,

    /// <summary>No tiene precio de menudeo.</summary>
    SinPrecio,

    /// <summary>
    /// Su mayoreo (una escala o un paquete) quedaría igual o más caro que el menudeo, o que una escala de menos piezas: sin
    /// el mayoreo, porque el menudeo baja hasta él; con el mayoreo, si no hay cómo dejarlo más barato (lo que se queda fijo,
    /// como lo de eleventa en el piloto, no lo deja).
    /// </summary>
    MayoreoMasCaro,
}

/// <summary>Un precio de una variante, como lo ve el cambio de precios de varios.</summary>
/// <param name="Paquete">Vacío: por unidad base (el menudeo o una escala). Con valor: el precio propio de ese paquete.</param>
/// <param name="FactorMilesimas">Las unidades base que trae el paquete, en milésimas; por unidad base, 1000.</param>
/// <param name="EnEleventa">En el piloto con eleventa, lo pone su lectura (el menudeo o una promoción): se queda como está.</param>
public sealed record PrecioDeVariante(
    Guid Id, Guid? Paquete, long FactorMilesimas, long CantidadMinimaMilesimas, long PrecioCentavos, bool EnEleventa = false);

/// <summary>Lo que pasa con los precios de una variante.</summary>
/// <param name="NoCambia">Por qué no cambia nada; vacío si algo cambia.</param>
/// <param name="Cambios">Los precios que cambian (por su Id), con el que queda.</param>
/// <param name="MenudeoAntes">El menudeo de ahora; cero si no tiene.</param>
/// <param name="MenudeoDespues">El menudeo con que queda (el mismo si no cambia).</param>
/// <param name="MenudeoEnEleventa">En el piloto, su menudeo lo pone eleventa: aquí se queda.</param>
/// <param name="MayoreoAjustado">
/// Algo de su mayoreo no quedó en su proporción redondeada: para seguir más barato que el menudeo (o que la escala de menos
/// piezas), se redondeó hacia abajo o se quedó como estaba.
/// </param>
public sealed record CambioDeVariante(
    PorQueNoCambia? NoCambia, IReadOnlyDictionary<Guid, long> Cambios, long MenudeoAntes, long MenudeoDespues, bool MenudeoEnEleventa, bool MayoreoAjustado);

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
/// <item>El mayoreo nunca queda más caro que el menudeo ni que una escala de menos piezas, y un paquete no cuesta más que sus
/// piezas a menudeo; si tenía descuento, lo sigue teniendo (<see cref="Variante"/>). Si el redondeo lo empareja o lo pasa,
/// se redondea hacia abajo (sin ir al revés de lo pedido) o se queda como estaba; si ni así, el producto no cambia. Sin el
/// mayoreo, un menudeo que bajaría hasta su mayoreo tampoco cambia.</item>
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

    private static readonly IReadOnlyDictionary<Guid, long> SinCambios = new Dictionary<Guid, long>();

    /// <summary>
    /// Los precios de una variante con la regla: el menudeo (o, en el piloto, el de eleventa, que se queda) y, con el mayoreo,
    /// sus escalas y sus paquetes en la misma proporción. Lo que se cobra siempre queda en orden: cada escala igual o más
    /// barata que la de menos piezas (y más barata si ya lo era) hasta llegar al menudeo, y cada paquete, por pieza, igual o
    /// más barato que el menudeo (y más barato si ya lo era). Lo que no se mueve (lo de eleventa, o el mayoreo cuando no se
    /// cambia) también cuenta. Cuando el redondeo de una escala o un paquete no cabe, va al paso de abajo si eso no lo lleva
    /// al revés de lo pedido; si no, se queda como estaba; si tampoco, sin redondear; si nada cabe, la variante no cambia. El
    /// menudeo, el que se pidió, no se ajusta: si no cabe, la variante no cambia.
    /// </summary>
    public static CambioDeVariante Variante(ReglaDePrecios regla, IReadOnlyList<PrecioDeVariante> precios)
    {
        var porUnidad = precios.Where(p => p.Paquete is null).OrderBy(p => p.CantidadMinimaMilesimas).ToList();
        if (porUnidad.FirstOrDefault() is not { PrecioCentavos: > 0 } menudeo)
            return new CambioDeVariante(PorQueNoCambia.SinPrecio, SinCambios, 0, 0, false, false);
        CambioDeVariante NoCambia(PorQueNoCambia porQue) =>
            new(porQue, SinCambios, menudeo.PrecioCentavos, menudeo.PrecioCentavos, menudeo.EnEleventa, false);

        var proporcion = ProporcionDe(regla, menudeo.PrecioCentavos);
        var nuevoMenudeo = menudeo.EnEleventa ? menudeo.PrecioCentavos : Menudeo(regla, menudeo.PrecioCentavos);
        if (nuevoMenudeo is null)
            return NoCambia(PorQueNoCambia.QuedariaEnCero);

        var finales = new Dictionary<Guid, long>();
        var ajustado = false;
        // Lo que se queda como está: lo de eleventa, lo que vale $0 y, si no se pidió, el mayoreo.
        bool Fijo(PrecioDeVariante p) => p.EnEleventa || p.PrecioCentavos <= 0 || (p != menudeo && !regla.ConMayoreo);
        long? Calculado(PrecioDeVariante p) => p == menudeo ? nuevoMenudeo : EnProporcion(p.PrecioCentavos, proporcion, regla.Redondeo);

        if (Escalonar(porUnidad, null, false) is { } porQue)
            return NoCambia(porQue);
        var menudeoFinal = finales[menudeo.Id];
        foreach (var paquete in precios.Where(p => p.Paquete is not null).GroupBy(p => p.Paquete))
        {
            // El paquete, por pieza, nunca más caro que el menudeo: sus piezas a menudeo son su techo.
            var cadena = paquete.OrderBy(p => p.CantidadMinimaMilesimas).ToList();
            var factor = new Cantidad(cadena[0].FactorMilesimas);
            if (Escalonar(cadena, factor.Importe(menudeoFinal), cadena[0].PrecioCentavos < factor.Importe(menudeo.PrecioCentavos)) is { } delPaquete)
                return NoCambia(delPaquete);
        }

        var cambios = precios.Where(p => finales[p.Id] != p.PrecioCentavos).ToDictionary(p => p.Id, p => finales[p.Id]);
        if (cambios.Count == 0)
            return NoCambia(menudeo.EnEleventa ? PorQueNoCambia.EnEleventa : PorQueNoCambia.QuedaIgual);
        return new CambioDeVariante(null, cambios, menudeo.PrecioCentavos, menudeoFinal, menudeo.EnEleventa, ajustado);

        // Una cadena de precios, de menos a más piezas: cada uno cabe abajo del anterior (el primero, abajo del techo) y arriba
        // de lo que se queda fijo después. Devuelve por qué no cabe; vacío si cupo todo.
        PorQueNoCambia? Escalonar(List<PrecioDeVariante> cadena, long? techo, bool techoEstricto)
        {
            for (var i = 0; i < cadena.Count; i++)
            {
                var nivel = cadena[i];
                var antes = nivel.PrecioCentavos;
                var arriba = i == 0 ? techo : finales[cadena[i - 1].Id];
                var estricto = i == 0 ? techoEstricto : antes < cadena[i - 1].PrecioCentavos;
                var piso = cadena.Skip(i + 1).Where(Fijo).Select(p => p.PrecioCentavos + (p.PrecioCentavos < antes ? 1 : 0)).DefaultIfEmpty(1).Max();
                bool Cabe(long c) => c >= Math.Max(piso, 1) && (arriba is not { } t || (estricto ? c < t : c <= t));

                if (Fijo(nivel))
                {
                    finales[nivel.Id] = antes;
                    if (!Cabe(antes))
                        return PorQueNoCambia.MayoreoMasCaro;
                    continue;
                }
                if (Calculado(nivel) is not { } calculado)
                    return PorQueNoCambia.QuedariaEnCero;
                if (Cabe(calculado))
                {
                    finales[nivel.Id] = calculado;
                    continue;
                }
                // El menudeo es el que se pidió: no se ajusta.
                if (nivel == menudeo || arriba is not { } tope)
                    return PorQueNoCambia.MayoreoMasCaro;
                var ajuste = Ajustes(nivel, tope, estricto).FirstOrDefault(c => c > 0 && Cabe(c));
                if (ajuste == 0)
                    return PorQueNoCambia.MayoreoMasCaro;
                (finales[nivel.Id], ajustado) = (ajuste, true);
            }
            return null;
        }

        // Lo que se intenta cuando el redondeo de un precio de mayoreo no cabe: el paso de abajo (sin ir al revés de lo
        // pedido), el de antes y, al último, el exacto, sin redondear.
        IEnumerable<long> Ajustes(PrecioDeVariante nivel, long arriba, bool estricto)
        {
            var antes = nivel.PrecioCentavos;
            var paso = Paso(regla.Redondeo);
            var maximo = estricto ? arriba - 1 : arriba;
            var abajo = paso == 0 ? maximo : maximo / paso * paso;
            if (proporcion.Sentido > 0 ? abajo >= antes : abajo <= antes)
                yield return abajo;
            yield return antes;
            yield return Redondeo.Dividir((Int128)antes * proporcion.Numerador, proporcion.Denominador);
        }
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
