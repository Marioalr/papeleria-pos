namespace PapeleriaPos.Nucleo.Catalogo;

// Lo que se señala (entrega 13, docs/propuesta-interfaz.md, «Lo que se señala: exhibidos y temporada»): lo exhibido y lo de
// temporada se vende tocándolo en la pantalla (Ctrl+M), en el mismo acomodo que en la tienda. El cajero no recuerda el
// nombre, recuerda dónde está, y ahí lo toca.

/// <summary>Qué es: lo que está a la vista todo el año o lo que se vende unas semanas.</summary>
public enum TipoExhibidor
{
    /// <summary>Lo que está a la vista todo el año: la vitrina del mostrador, la pared, lo colgado.</summary>
    Exhibidor,

    /// <summary>Lo que se vende unas semanas al año, entre sus fechas: Todos Santos, Navidad…</summary>
    Temporada,
}

/// <summary>
/// Un exhibidor de la tienda (la vitrina del mostrador, la pared) o una temporada, con sus lugares (la repisa de arriba, la
/// de abajo, la mesa de la entrada) y, en cada uno, sus productos en el orden en que están. Viaja a las cajas con la copia
/// del catálogo: Ctrl+M lo pinta sin la red.
/// </summary>
public class Exhibidor : IVersionado
{
    /// <summary>Lo más largo que puede ser su nombre: el de una temporada sale en un chip arriba de la venta.</summary>
    public const int LargoNombre = 40;

    public Guid Id { get; set; } = Ids.Nuevo();
    public required string Nombre { get; set; }
    public TipoExhibidor Tipo { get; set; }

    /// <summary>De una temporada: el día y el mes en que empieza, cada año. Sin fechas (las cuatro vacías), no corre nunca.</summary>
    public int? DesdeDia { get; set; }

    public int? DesdeMes { get; set; }

    /// <summary>El día y el mes en que termina (ese día todavía corre). Puede ser del año siguiente: Navidad, al 1 de enero.</summary>
    public int? HastaDia { get; set; }

    public int? HastaMes { get; set; }

    /// <summary>En qué lugar sale entre los de su tipo: el principal, primero.</summary>
    public int Orden { get; set; }

    /// <summary>Quitarlo lo desactiva: así las cajas se enteran con la versión del catálogo, y lo acomodado no se pierde.</summary>
    public bool Activo { get; set; } = true;

    public List<LugarExhibidor> Lugares { get; set; } = [];

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    /// <summary>Cuándo empieza; vacío si no tiene fechas.</summary>
    public DiaDelAnio? Desde => DiaDelAnio.De(DesdeMes, DesdeDia);

    /// <summary>Cuándo termina; vacío si no tiene fechas.</summary>
    public DiaDelAnio? Hasta => DiaDelAnio.De(HastaMes, HastaDia);

    /// <summary>Las fechas de una temporada: las dos, o ninguna (sin fechas, como la primavera mientras no se definen).</summary>
    public void PonerFechas(DiaDelAnio? desde, DiaDelAnio? hasta)
    {
        if (Tipo != TipoExhibidor.Temporada && (desde is not null || hasta is not null))
            throw new ArgumentException("Solo las temporadas tienen fechas.");
        if (desde is null != hasta is null)
            throw new ArgumentException("Escribe las dos fechas, cuándo empieza y cuándo termina, o ninguna.");
        if (desde is { } d && !d.EsValido || hasta is { } h && !h.EsValido)
            throw new ArgumentException("Esa fecha no existe: revisa el día y el mes.");
        (DesdeDia, DesdeMes, HastaDia, HastaMes) = (desde?.Dia, desde?.Mes, hasta?.Dia, hasta?.Mes);
    }

    /// <summary>Si es una temporada activa que corre ese día (el de la tienda).</summary>
    public bool CorreEl(DateOnly fecha) => Activo && Temporadas.Corre(Tipo, Desde, Hasta, fecha);

    /// <summary>El nombre sin espacios de más: ni al principio, ni al final, ni dos seguidos. Vacío si no trae nada.</summary>
    public static string NombreLimpio(string? nombre) => string.Join(' ', (nombre ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// Un lugar de un exhibidor o de una temporada: la repisa de arriba, la de abajo, la mesa de la entrada. Sus productos van
/// en el orden en que están, de izquierda a derecha.
/// </summary>
public class LugarExhibidor : IVersionado
{
    public const int LargoNombre = 40;

    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid ExhibidorId { get; set; }
    public Exhibidor? Exhibidor { get; set; }
    public required string Nombre { get; set; }

    /// <summary>De arriba abajo (o como se recorra en la tienda): el primero sale arriba en Ctrl+M.</summary>
    public int Orden { get; set; }

    public List<ProductoExhibido> Productos { get; set; } = [];

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}

/// <summary>Un producto en un lugar, en su posición (de izquierda a derecha). Un producto va una vez por lugar.</summary>
public class ProductoExhibido : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid LugarId { get; set; }
    public LugarExhibidor? Lugar { get; set; }
    public Guid VarianteId { get; set; }
    public Variante? Variante { get; set; }
    public int Posicion { get; set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}

/// <summary>
/// Un día del año sin el año, como las fechas de una temporada: el 16 de septiembre de cada año. El 29 de febrero vale (en un
/// año sin él, ese día no llega).
/// </summary>
public readonly record struct DiaDelAnio(int Mes, int Dia)
{
    public static readonly IReadOnlyList<string> Meses =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    /// <summary>Si existe: un mes del 1 al 12 y un día de ese mes (febrero, hasta el 29).</summary>
    public bool EsValido => Mes is >= 1 and <= 12 && Dia >= 1 && Dia <= DateTime.DaysInMonth(2024, Mes);

    /// <summary>El de un mes y un día guardados; vacío si falta alguno o no existe.</summary>
    public static DiaDelAnio? De(int? mes, int? dia) => mes is { } m && dia is { } d && new DiaDelAnio(m, d) is { EsValido: true } valido ? valido : null;

    public static DiaDelAnio De(DateOnly fecha) => new(fecha.Month, fecha.Day);

    /// <summary>Para comparar en el año: el 16 de septiembre es 916.</summary>
    private int Clave => Mes * 100 + Dia;

    /// <summary>
    /// Si la fecha cae entre los dos días, con los dos incluidos. Si el último es antes que el primero en el año, cruza el fin
    /// de año: Navidad, del 20 de noviembre al 1 de enero, corre del 20 de noviembre al 31 de diciembre y el 1 de enero.
    /// </summary>
    public static bool Entre(DateOnly fecha, DiaDelAnio desde, DiaDelAnio hasta)
    {
        var dia = De(fecha).Clave;
        return desde.Clave <= hasta.Clave ? dia >= desde.Clave && dia <= hasta.Clave : dia >= desde.Clave || dia <= hasta.Clave;
    }

    /// <summary>«16 de septiembre».</summary>
    public override string ToString() => EsValido ? $"{Dia} de {Meses[Mes - 1]}" : $"{Dia}/{Mes}";
}

/// <summary>Cuándo corre una temporada: lo mismo en la caja principal, en la copia de cada caja y en la Configuración.</summary>
public static class Temporadas
{
    /// <summary>Si es una temporada con fechas y ese día cae entre ellas (con las dos incluidas).</summary>
    public static bool Corre(TipoExhibidor tipo, DiaDelAnio? desde, DiaDelAnio? hasta, DateOnly fecha) =>
        tipo == TipoExhibidor.Temporada && desde is { EsValido: true } d && hasta is { EsValido: true } h && DiaDelAnio.Entre(fecha, d, h);

    /// <summary>«del 16 de septiembre al 5 de noviembre»; vacío sin fechas.</summary>
    public static string? Fechas(DiaDelAnio? desde, DiaDelAnio? hasta) =>
        desde is { } d && hasta is { } h ? d == h ? $"el {d}" : $"del {d} al {h}" : null;

    /// <summary>El día de hoy en la tienda (la hora de la Ciudad de México): con él se decide qué temporada corre.</summary>
    public static DateOnly Hoy(TimeProvider reloj) => DateOnly.FromDateTime(HoraMexico.ALocal(reloj.GetUtcNow().UtcDateTime));

    /// <summary>Los meses como se escriben, completos o por su principio (desde tres letras): «sep», «sept» y «setiembre» también.</summary>
    private static readonly (string Nombre, int Mes)[] NombresDeMes =
    [
        ("ENERO", 1), ("FEBRERO", 2), ("MARZO", 3), ("ABRIL", 4), ("MAYO", 5), ("JUNIO", 6), ("JULIO", 7), ("AGOSTO", 8),
        ("SEPTIEMBRE", 9), ("SETIEMBRE", 9), ("OCTUBRE", 10), ("NOVIEMBRE", 11), ("DICIEMBRE", 12),
    ];

    /// <summary>
    /// Las fechas como se escriben en la Configuración: «del 16 de septiembre al 5 de noviembre», «16/9 al 5/11», «16 sep - 5
    /// nov»… Vacío, sin fechas. Si no se entiende, dice por qué.
    /// </summary>
    public static (DiaDelAnio? Desde, DiaDelAnio? Hasta) LeerFechas(string? texto)
    {
        var limpio = Texto.Normalizar(texto ?? "").Replace('–', '-').Replace('—', '-');
        if (limpio.Length == 0)
            return (null, null);
        var unDia = limpio.StartsWith("EL ", StringComparison.Ordinal);
        foreach (var inicio in new[] { "DEL ", "EL " })
        {
            if (limpio.StartsWith(inicio, StringComparison.Ordinal))
                limpio = limpio[inicio.Length..];
        }
        var partes = System.Text.RegularExpressions.Regex.Split(limpio, @"\s+(?:AL|A|HASTA)\s+|\s+-\s+");
        // Una temporada de un día, como la escribe Fechas: «el 30 de abril».
        if (partes.Length == 1 && unDia && LeerDia(partes[0]) is var dia)
            return (dia, dia);
        if (partes.Length != 2)
            throw new ArgumentException("Escribe cuándo empieza y cuándo termina: «16/9 al 5/11», o «del 16 de septiembre al 5 de noviembre».");
        return (LeerDia(partes[0]), LeerDia(partes[1]));
    }

    private static DiaDelAnio LeerDia(string texto)
    {
        var m = System.Text.RegularExpressions.Regex.Match(texto.Trim(), @"^(\d{1,2})\s*(?:/|-|\.|\s+DE\s+|\s+)\s*(\d{1,2}|[A-ZÑ]{3,})$");
        if (!m.Success)
            throw new ArgumentException($"«{texto.Trim().ToLowerInvariant()}» no es un día: escribe el día y el mes, 16/9 o 16 de septiembre.");
        var dia = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var mes = int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : NombresDeMes.FirstOrDefault(x => x.Nombre.StartsWith(m.Groups[2].Value, StringComparison.Ordinal)).Mes;
        var leido = new DiaDelAnio(mes, dia);
        return leido.EsValido ? leido : throw new ArgumentException($"El {dia}/{(mes == 0 ? m.Groups[2].Value.ToLowerInvariant() : mes)} no existe: revisa el día y el mes.");
    }
}
