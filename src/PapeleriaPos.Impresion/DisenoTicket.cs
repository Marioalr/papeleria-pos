using System.Globalization;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Impresion;

public enum Alineacion
{
    Izquierda,
    Centro,
    Derecha,
}

public abstract record Elemento;

/// <summary>Un renglón de texto. Grande es doble alto: conserva los 48 caracteres por línea.</summary>
public sealed record Renglon(string Texto, Alineacion Alineacion = Alineacion.Izquierda, bool Negrita = false, bool Grande = false) : Elemento;

public sealed record Qr(string Datos) : Elemento;

public sealed record Imagen(LogoTicketDto Logo) : Elemento;

/// <summary>
/// El orden del ticket, igual que el de eleventa: encabezado, folio, artículos, total, cobro y pie. Lo usan la
/// impresión ESC/POS y la vista previa en texto.
/// </summary>
public static class DisenoTicket
{
    /// <summary>Papel de 80 mm con la letra normal.</summary>
    public const int Columnas = 48;

    private const int AnchoCantidad = 6;
    private const int AnchoImporte = 11;
    private const int AnchoDescripcion = Columnas - AnchoCantidad - AnchoImporte - 1;

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    public static string Dinero(long centavos) => (centavos / 100m).ToString("$#,##0.00", Mexico);

    public static List<Elemento> Armar(TicketDto t)
    {
        var e = new List<Elemento>();
        if (t.Logo is not null)
            e.Add(new Imagen(t.Logo));
        for (var i = 0; i < t.Encabezado.Count; i++)
            e.Add(new Renglon(t.Encabezado[i], Alineacion.Centro, Negrita: i == 0, Grande: i == 0));

        e.Add(Linea('-'));
        e.Add(new Renglon(DosColumnas($"Folio {t.Folio}", t.FechaLocal.ToString("dd/MM/yyyy  HH:mm", Mexico))));
        e.Add(new Renglon(DosColumnas(t.Caja, $"Atendió: {t.Cajero}")));
        e.Add(Linea('-'));
        e.Add(new Renglon(Columna("Cant.", AnchoCantidad) + Columna("Descripción", AnchoDescripcion + 1) + "Importe".PadLeft(AnchoImporte)));
        e.Add(Linea('='));

        foreach (var l in t.Lineas)
        {
            var partes = Partir(l.Descripcion, AnchoDescripcion);
            for (var i = 0; i < partes.Count; i++)
            {
                var cantidad = i == 0 ? l.Cantidad : "";
                // Con mayoreo o descuento, el renglón lleva el importe a menudeo y abajo va lo que se ahorró.
                var importe = i == 0 ? Dinero(l.ImporteCentavos + l.DescuentoCentavos + l.AhorroMayoreoCentavos) : "";
                e.Add(new Renglon(Columna(cantidad, AnchoCantidad) + Columna(partes[i], AnchoDescripcion + 1) + importe.PadLeft(AnchoImporte)));
            }
            if (l.AhorroMayoreoCentavos > 0)
                e.Add(new Renglon(new string(' ', AnchoCantidad) + Columna(l.Mayoreo ?? "Mayoreo", AnchoDescripcion + 1)
                    + $"-{Dinero(l.AhorroMayoreoCentavos)}".PadLeft(AnchoImporte)));
            if (l.DescuentoCentavos > 0)
                e.Add(new Renglon(new string(' ', AnchoCantidad) + Columna(l.Descuento is { } p ? $"Descuento {p}" : "Descuento", AnchoDescripcion + 1)
                    + $"-{Dinero(l.DescuentoCentavos)}".PadLeft(AnchoImporte)));
        }

        e.Add(Linea('-'));
        e.Add(new Renglon($"No. de artículos: {t.NumeroDeArticulos.ToString("0.###", Mexico),5}", Alineacion.Derecha));
        if (t.AhorroCentavos > 0)
            e.Add(new Renglon($"Usted ahorró: {Dinero(t.AhorroCentavos)}", Alineacion.Derecha));
        if (t.RedondeoCentavos == 0)
            e.Add(new Renglon($"Total: {Dinero(t.TotalCentavos)}", Alineacion.Derecha, Negrita: true, Grande: true));
        else
        {
            // El efectivo se cobra al peso: el total exacto, el redondeo y lo que se pagó.
            e.Add(new Renglon($"Total: {Dinero(t.TotalCentavos)}", Alineacion.Derecha));
            e.Add(new Renglon($"Redondeo: {(t.RedondeoCentavos > 0 ? "+" : "-")}{Dinero(Math.Abs(t.RedondeoCentavos))}", Alineacion.Derecha));
            e.Add(new Renglon($"Total a pagar: {Dinero(t.TotalCobradoCentavos)}", Alineacion.Derecha, Negrita: true, Grande: true));
        }
        foreach (var c in t.Cobros)
        {
            e.Add(new Renglon(c.Forma switch
            {
                FormaPago.Efectivo => $"Efectivo: {Dinero(c.RecibidoCentavos)}  Cambio: {Dinero(c.CambioCentavos)}",
                FormaPago.Tarjeta => $"Tarjeta: {Dinero(c.MontoCentavos)}" + (c.Referencia is { Length: > 0 } r ? $" ({r})" : ""),
                _ => $"Transferencia: {Dinero(c.MontoCentavos)}",
            }, Alineacion.Derecha));
        }
        if (t.IvaCentavos > 0)
            e.Add(new Renglon($"Incluye IVA: {Dinero(t.IvaCentavos)}", Alineacion.Derecha));
        e.Add(new Renglon("(precios con IVA incluido)", Alineacion.Centro));
        e.Add(Linea('-'));
        e.Add(new Qr(t.Folio));
        foreach (var renglon in t.Pie)
            e.Add(new Renglon(renglon, Alineacion.Centro));
        return e;
    }

    private static Renglon Linea(char c) => new(new string(c, Columnas));

    private static string Columna(string texto, int ancho) => texto.Length >= ancho ? texto[..(ancho - 1)] + " " : texto.PadRight(ancho);

    private static string DosColumnas(string izquierda, string derecha)
    {
        var espacio = Columnas - derecha.Length - 1;
        return (izquierda.Length > espacio ? izquierda[..espacio] : izquierda).PadRight(Columnas - derecha.Length) + derecha;
    }

    /// <summary>Parte el texto en renglones del ancho dado, sin cortar palabras si se puede.</summary>
    public static List<string> Partir(string texto, int ancho)
    {
        var renglones = new List<string>();
        var actual = "";
        foreach (var palabra in texto.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var resto = palabra;
            while (resto.Length > ancho)
            {
                if (actual.Length > 0)
                {
                    renglones.Add(actual);
                    actual = "";
                }
                renglones.Add(resto[..ancho]);
                resto = resto[ancho..];
            }
            if (actual.Length == 0)
                actual = resto;
            else if (actual.Length + 1 + resto.Length <= ancho)
                actual += " " + resto;
            else
            {
                renglones.Add(actual);
                actual = resto;
            }
        }
        if (actual.Length > 0 || renglones.Count == 0)
            renglones.Add(actual);
        return renglones;
    }
}
