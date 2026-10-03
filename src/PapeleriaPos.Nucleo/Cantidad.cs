using System.Globalization;

namespace PapeleriaPos.Nucleo;

/// <summary>
/// Cantidad en milésimas de la unidad base: 1 pieza = 1000, 1.5 m = 1500, 0.250 kg = 250.
/// Se guarda como entero para que las sumas sean exactas.
/// </summary>
public readonly record struct Cantidad(long Milesimas) : IComparable<Cantidad>
{
    public const long Escala = 1000;

    public static readonly Cantidad Cero = new(0);

    public static Cantidad Unidades(decimal unidades)
    {
        var milesimas = unidades * Escala;
        if (milesimas != decimal.Truncate(milesimas))
            throw new ArgumentException($"La cantidad {unidades} tiene más de tres decimales.", nameof(unidades));
        return new Cantidad(checked((long)milesimas));
    }

    public static Cantidad Piezas(long piezas) => new(checked(piezas * Escala));

    public decimal EnUnidades => (decimal)Milesimas / Escala;

    public bool EsEntera => Milesimas % Escala == 0;

    public bool EsPositiva => Milesimas > 0;

    public Cantidad Abs() => new(Math.Abs(Milesimas));

    /// <summary>Cantidad de presentaciones × su factor = cantidad en unidades base (1.5 paquetes × 12 = 18 piezas).</summary>
    public Cantidad Por(Cantidad factor) => new(Redondeo.Dividir((Int128)Milesimas * factor.Milesimas, Escala));

    /// <summary>Importe de esta cantidad a un precio por unidad, redondeado al centavo.</summary>
    public long Importe(long centavosPorUnidad) => Redondeo.Dividir((Int128)centavosPorUnidad * Milesimas, Escala);

    public int CompareTo(Cantidad otra) => Milesimas.CompareTo(otra.Milesimas);

    public static Cantidad operator +(Cantidad a, Cantidad b) => new(checked(a.Milesimas + b.Milesimas));
    public static Cantidad operator -(Cantidad a, Cantidad b) => new(checked(a.Milesimas - b.Milesimas));
    public static Cantidad operator -(Cantidad a) => new(checked(-a.Milesimas));
    public static bool operator <(Cantidad a, Cantidad b) => a.Milesimas < b.Milesimas;
    public static bool operator >(Cantidad a, Cantidad b) => a.Milesimas > b.Milesimas;
    public static bool operator <=(Cantidad a, Cantidad b) => a.Milesimas <= b.Milesimas;
    public static bool operator >=(Cantidad a, Cantidad b) => a.Milesimas >= b.Milesimas;

    public override string ToString() => EnUnidades.ToString("0.###", CultureInfo.InvariantCulture);
}
