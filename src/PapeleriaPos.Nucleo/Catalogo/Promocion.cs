using System.Globalization;

namespace PapeleriaPos.Nucleo.Catalogo;

// Las promociones con fechas (fase 3, docs/fase-3-precios-clientes.md, «Promociones y kits»): en el regreso a clases la tienda
// deja al costo los productos estratégicos (en eleventa, con F11) y hace sus 3×2. Se aplican solas, no se acumulan (se queda la
// que más le conviene al cliente, también contra el mayoreo) y nunca quedan debajo del costo sin autorización. Viajan a las
// cajas con la copia del catálogo, con sus fechas: cada caja decide con su fecha, también sin conexión y a medianoche.

/// <summary>Cómo baja el precio una promoción.</summary>
public enum TipoPromocion
{
    /// <summary>Lleva N y paga M (3×2): de cada N iguales, N − M van gratis. Cuenta cada producto aparte, como el mayoreo.</summary>
    LlevaNPagaM,

    /// <summary>El segundo a mitad de precio: de cada dos iguales, uno va a la mitad.</summary>
    SegundoAMitad,

    /// <summary>Un porcentaje menos sobre el menudeo, en un producto, un departamento o una marca.</summary>
    Porcentaje,

    /// <summary>Un precio por unos días, el suyo de cada producto: dejar al costo lo estratégico en el regreso a clases (el F11 de eleventa).</summary>
    PrecioEspecial,
}

/// <summary>Los días de la semana en que corre una promoción, entre sus fechas.</summary>
[Flags]
public enum DiasDeLaSemana
{
    Ninguno = 0,
    Lunes = 1,
    Martes = 2,
    Miercoles = 4,
    Jueves = 8,
    Viernes = 16,
    Sabado = 32,
    Domingo = 64,
    Todos = Lunes | Martes | Miercoles | Jueves | Viernes | Sabado | Domingo,
}

/// <summary>Los días de la semana: de qué día es una fecha y cómo se leen («lunes a viernes»).</summary>
public static class Semana
{
    /// <summary>En el orden de la semana de México: de lunes a domingo.</summary>
    public static readonly DiasDeLaSemana[] EnOrden =
    [
        DiasDeLaSemana.Lunes, DiasDeLaSemana.Martes, DiasDeLaSemana.Miercoles, DiasDeLaSemana.Jueves, DiasDeLaSemana.Viernes,
        DiasDeLaSemana.Sabado, DiasDeLaSemana.Domingo,
    ];

    public static DiasDeLaSemana De(DateOnly dia) => dia.DayOfWeek switch
    {
        DayOfWeek.Monday => DiasDeLaSemana.Lunes,
        DayOfWeek.Tuesday => DiasDeLaSemana.Martes,
        DayOfWeek.Wednesday => DiasDeLaSemana.Miercoles,
        DayOfWeek.Thursday => DiasDeLaSemana.Jueves,
        DayOfWeek.Friday => DiasDeLaSemana.Viernes,
        DayOfWeek.Saturday => DiasDeLaSemana.Sabado,
        _ => DiasDeLaSemana.Domingo,
    };

    public static bool Incluye(this DiasDeLaSemana dias, DateOnly dia) => (dias & De(dia)) != 0;

    public static string Nombre(DiasDeLaSemana dia) => dia switch
    {
        DiasDeLaSemana.Lunes => "lunes",
        DiasDeLaSemana.Martes => "martes",
        DiasDeLaSemana.Miercoles => "miércoles",
        DiasDeLaSemana.Jueves => "jueves",
        DiasDeLaSemana.Viernes => "viernes",
        DiasDeLaSemana.Sabado => "sábado",
        _ => "domingo",
    };

    /// <summary>«todos los días», «lunes a viernes», «sábado y domingo», «lunes, miércoles y viernes».</summary>
    public static string Texto(DiasDeLaSemana dias)
    {
        var cuales = EnOrden.Where(d => (dias & d) != 0).ToList();
        if (cuales.Count == 7)
            return "todos los días";
        if (cuales.Count == 0)
            return "ningún día";
        // Seguidos (tres o más): «lunes a viernes».
        var indices = cuales.Select(d => Array.IndexOf(EnOrden, d)).ToList();
        if (cuales.Count >= 3 && indices[^1] - indices[0] == cuales.Count - 1)
            return $"{Nombre(cuales[0])} a {Nombre(cuales[^1])}";
        var nombres = cuales.Select(Nombre).ToList();
        return nombres.Count == 1 ? nombres[0] : $"{string.Join(", ", nombres.Take(nombres.Count - 1))} y {nombres[^1]}";
    }
}

/// <summary>Cómo está una promoción en un día: lo que dice la lista de Productos › Promociones.</summary>
public enum EstadoPromocion
{
    /// <summary>Hoy se aplica.</summary>
    Hoy,

    /// <summary>Entre sus fechas, pero hoy no es uno de sus días.</summary>
    HoyNoToca,

    /// <summary>Empieza después.</summary>
    PorVenir,

    /// <summary>Ya pasó su último día.</summary>
    Termino,

    /// <summary>Se terminó a mano antes de su último día («Terminar ahora»).</summary>
    Terminada,
}

/// <summary>
/// Una promoción con sus fechas, sus días y su sucursal (o todas). Toca a productos (sus variantes), a departamentos o a marcas
/// (<see cref="AlcancePromocion"/>). La da de alta y la cambia quien tiene «Productos, precios y etiquetas»; si deja algo
/// debajo del costo, además «Vender debajo del costo» (o quien lo autorice). No se borra: se termina.
/// </summary>
public class Promocion : IVersionado
{
    /// <summary>Lo más largo que puede ser su nombre: sale en el ticket, junto a lo que se ahorró.</summary>
    public const int LargoNombre = 40;

    /// <summary>A cuántos productos, departamentos o marcas puede tocar (como «Cambiar precios»).</summary>
    public const int MaximoAlcance = 1000;

    public Guid Id { get; set; } = Ids.Nuevo();
    public required string Nombre { get; set; }
    public TipoPromocion Tipo { get; set; }

    /// <summary>De «lleva N y paga M»: cuántas lleva (N). En las demás, cero.</summary>
    public int Lleva { get; set; }

    /// <summary>De «lleva N y paga M»: cuántas paga (M). En las demás, cero.</summary>
    public int Paga { get; set; }

    /// <summary>Del porcentaje: en centésimas (1000 = 10 %). En las demás, cero.</summary>
    public int Centesimas { get; set; }

    /// <summary>El primer día en que corre (hora de México).</summary>
    public DateOnly Desde { get; set; }

    /// <summary>El último día en que corre; vacío: hasta que se termine.</summary>
    public DateOnly? Hasta { get; set; }

    public DiasDeLaSemana Dias { get; set; } = DiasDeLaSemana.Todos;

    /// <summary>Vacía: en todas las sucursales.</summary>
    public Guid? SucursalId { get; set; }

    /// <summary>Se aplica (entre sus fechas y en sus días). Se apaga con «Terminar ahora» y se vuelve a prender con «Activar otra vez».</summary>
    public bool Activa { get; set; } = true;

    /// <summary>
    /// Al guardarla algo quedaba debajo del costo y se autorizó (lo pudo hacer quien la guardó, o con la autorización de quien
    /// puede): sus productos la llevan aunque queden debajo del costo. Sin esto, el producto al que con ella le quedaría el
    /// precio debajo de lo que cuesta (porque llegó después a su departamento o a su marca, bajó su precio o subió su costo) se
    /// queda fuera: las cajas no se la aplican y su detalle lo dice («Fuera por el costo»). Se vuelve a decidir cada vez que se
    /// guarda.
    /// </summary>
    public bool BajoCostoAutorizado { get; set; }

    public Guid CreadaPorId { get; set; }
    public DateTime CreadaUtc { get; set; }

    /// <summary>Cuándo y quién la terminó a mano; vacío si no.</summary>
    public DateTime? TerminadaUtc { get; set; }

    public Guid? TerminadaPorId { get; set; }

    public List<AlcancePromocion> Alcance { get; set; } = [];

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    /// <summary>Cómo está ese día.</summary>
    public EstadoPromocion EstadoEl(DateOnly dia) => Estado(Activa, Desde, Hasta, Dias, dia);

    public static EstadoPromocion Estado(bool activa, DateOnly desde, DateOnly? hasta, DiasDeLaSemana dias, DateOnly dia) =>
        !activa ? EstadoPromocion.Terminada
        : hasta is { } h && dia > h ? EstadoPromocion.Termino
        : dia < desde ? EstadoPromocion.PorVenir
        : dias.Incluye(dia) ? EstadoPromocion.Hoy
        : EstadoPromocion.HoyNoToca;

    /// <summary>
    /// Que se pueda guardar: su nombre, lo de su tipo, sus fechas y sus días, y a qué toca. Un precio especial es el de cada
    /// producto: solo toca productos, cada uno con su precio. Lanza <see cref="ArgumentException"/> con lo que falta.
    /// </summary>
    public void Validar()
    {
        Nombre = Departamento.NombreLimpio(Nombre);
        if (Nombre.Length == 0)
            throw new ArgumentException("Ponle nombre a la promoción: sale en el ticket.");
        if (Nombre.Length > LargoNombre)
            throw new ArgumentException($"El nombre va de hasta {LargoNombre} letras: sale en el ticket.");
        switch (Tipo)
        {
            case TipoPromocion.LlevaNPagaM:
                if (Lleva is < 2 or > 100 || Paga < 1 || Paga >= Lleva)
                    throw new ArgumentException("En «lleva y paga», lleva más de las que paga (como 3×2), y paga al menos una.");
                Centesimas = 0;
                break;
            case TipoPromocion.Porcentaje:
                if (Centesimas is <= 0 or >= 10000)
                    throw new ArgumentException("El porcentaje va de más de 0 a menos de 100 %.");
                (Lleva, Paga) = (0, 0);
                break;
            default:
                (Lleva, Paga, Centesimas) = (0, 0, 0);
                break;
        }
        if (Hasta is { } hasta && hasta < Desde)
            throw new ArgumentException("El último día va después del primero.");
        if ((Dias & DiasDeLaSemana.Todos) == DiasDeLaSemana.Ninguno)
            throw new ArgumentException("Elige al menos un día de la semana.");
        Dias &= DiasDeLaSemana.Todos;
        if (Alcance.Count == 0)
            throw new ArgumentException(Tipo == TipoPromocion.PrecioEspecial
                ? "Elige los productos de la promoción, cada uno con su precio."
                : "Elige a qué le toca: productos, un departamento o una marca.");
        if (Alcance.Count > MaximoAlcance)
            throw new ArgumentException($"Una promoción toca hasta {MaximoAlcance:N0} productos: pártela en dos.");
        foreach (var a in Alcance)
        {
            var cuantos = (a.VarianteId is null ? 0 : 1) + (a.DepartamentoId is null ? 0 : 1) + (a.MarcaId is null ? 0 : 1);
            if (cuantos != 1)
                throw new ArgumentException("Cada renglón de la promoción es un producto, un departamento o una marca.");
            if (Tipo == TipoPromocion.PrecioEspecial)
            {
                if (a.VarianteId is null)
                    throw new ArgumentException("Un precio especial es el de cada producto: elige productos, no un departamento ni una marca.");
                if (a.PrecioCentavos is not > 0)
                    throw new ArgumentException("Cada producto del precio especial lleva su precio.");
            }
            else
                a.PrecioCentavos = null;
        }
        if (Alcance.Where(a => a.VarianteId is not null).GroupBy(a => a.VarianteId).Any(g => g.Count() > 1)
            || Alcance.Where(a => a.DepartamentoId is not null).GroupBy(a => a.DepartamentoId).Any(g => g.Count() > 1)
            || Alcance.Where(a => a.MarcaId is not null).GroupBy(a => a.MarcaId).Any(g => g.Count() > 1))
            throw new ArgumentException("Un producto, un departamento o una marca va una sola vez en la promoción.");
    }

    /// <summary>«3×2», «el segundo a mitad de precio», «15 % menos», «precio especial»: lo que hace.</summary>
    public static string TipoTexto(TipoPromocion tipo, int lleva, int paga, int centesimas) => tipo switch
    {
        TipoPromocion.LlevaNPagaM => $"{lleva}×{paga}",
        TipoPromocion.SegundoAMitad => "el segundo a mitad de precio",
        TipoPromocion.Porcentaje => $"{Margen.Texto(centesimas)} menos",
        _ => "precio especial",
    };

    public string TipoTexto() => TipoTexto(Tipo, Lleva, Paga, Centesimas);

    /// <summary>«del 1/08/2027 al 31/08/2027 · lunes a viernes», «desde el 22/10/2026».</summary>
    public static string FechasTexto(DateOnly desde, DateOnly? hasta, DiasDeLaSemana dias)
    {
        var mexico = CultureInfo.GetCultureInfo("es-MX");
        var fechas = hasta is { } h
            ? h == desde ? $"el {desde.ToString("d/MM/yyyy", mexico)}" : $"del {desde.ToString("d/MM/yyyy", mexico)} al {h.ToString("d/MM/yyyy", mexico)}"
            : $"desde el {desde.ToString("d/MM/yyyy", mexico)}";
        return (dias & DiasDeLaSemana.Todos) == DiasDeLaSemana.Todos ? fechas : $"{fechas} · {Semana.Texto(dias)}";
    }

    /// <summary>La regla que toca a una variante, como la aplica el cálculo del precio (<see cref="CalculoPrecio"/>).</summary>
    /// <param name="precioCentavos">De un precio especial, el de esa variante (por unidad base, con IVA).</param>
    public PromocionDeProducto Regla(long? precioCentavos) => new(Id, Nombre, Tipo, Lleva, Paga, Centesimas, precioCentavos, Desde, Hasta, Dias);
}

/// <summary>
/// A qué le toca una promoción: un producto (una de sus variantes, con su precio especial si la promoción es de precio
/// especial), un departamento o una marca. Uno de los tres.
/// </summary>
public class AlcancePromocion : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid PromocionId { get; set; }

    public Guid? VarianteId { get; set; }
    public Guid? DepartamentoId { get; set; }
    public Guid? MarcaId { get; set; }

    /// <summary>Del precio especial: el de esta variante, por unidad base (pieza, metro, kilo) y con IVA.</summary>
    public long? PrecioCentavos { get; set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}

/// <summary>
/// Una promoción como la ve el cálculo del precio para un producto: lo de su tipo, sus fechas y sus días. Así la aplican igual
/// la pantalla de la caja, su cobro sin conexión y el servicio (<see cref="CalculoPrecio.Calcular"/>).
/// </summary>
/// <param name="PrecioCentavos">De un precio especial: el de este producto, por unidad base y con IVA.</param>
public sealed record PromocionDeProducto(
    Guid PromocionId, string Nombre, TipoPromocion Tipo, int Lleva, int Paga, int Centesimas, long? PrecioCentavos, DateOnly Desde, DateOnly? Hasta,
    DiasDeLaSemana Dias)
{
    /// <summary>Si corre ese día (la fecha de la caja o la del cobro, en hora de México).</summary>
    public bool AplicaEl(DateOnly dia) => dia >= Desde && (Hasta is not { } hasta || dia <= hasta) && Dias.Incluye(dia);
}
