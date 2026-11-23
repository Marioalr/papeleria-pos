using System.Globalization;
using PapeleriaPos.Nucleo.Configuracion;
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

/// <summary>
/// Cómo sale lo que sigue (Configuración › Ticket, 2026-11-21): la letra B (64 caracteres por renglón en lugar de 48), la letra
/// alta (doble alto en todo) y el interlineado. Va al principio del ticket de venta; sin él (los comprobantes, el corte), como
/// siempre: letra A, normal, 48 caracteres.
/// </summary>
public sealed record Estilo(int Columnas, bool LetraB = false, bool Alta = false, InterlineadoTicket Interlineado = InterlineadoTicket.Normal) : Elemento;

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

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    public static string Dinero(long centavos) => (centavos / 100m).ToString("$#,##0.00", Mexico);

    /// <summary>
    /// El ticket de venta, con <see cref="TicketDto.Formato"/> (vacío: como siempre). Lo que no se imprime (auditoría con el dueño,
    /// 2026-11-21): el desglose del IVA («Incluye IVA», «precios con IVA incluido»), «Mayoreo desde …» (sale «Ahorro por mayoreo»)
    /// y los privilegios del cliente junto a su nombre.
    /// </summary>
    public static List<Elemento> Armar(TicketDto t)
    {
        var f = t.Formato ?? FormatoTicket.Predeterminado;
        var columnas = f.Columnas;
        var anchoDescripcion = columnas - AnchoCantidad - AnchoImporte - 1;
        var e = new List<Elemento>();
        // Con la letra y el interlineado de siempre no lleva nada: a la impresora le llega lo mismo que antes.
        if (f is not { Letra: LetraTicket.A, Tamano: TamanoLetraTicket.Normal, Interlineado: InterlineadoTicket.Normal })
            e.Add(new Estilo(columnas, f.Letra == LetraTicket.B, f.Tamano == TamanoLetraTicket.Alta, f.Interlineado));
        if (t.Logo is not null)
            e.Add(new Imagen(t.Logo));
        for (var i = 0; i < t.Encabezado.Count; i++)
            e.Add(new Renglon(t.Encabezado[i], Alineacion.Centro, Negrita: i == 0, Grande: i == 0));

        e.Add(Linea('-', columnas));
        e.Add(new Renglon(DosColumnas($"Folio {t.FolioImpreso}", t.FechaLocal.ToString("dd/MM/yyyy  HH:mm", Mexico), columnas)));
        e.Add(new Renglon(f.MostrarCajero ? DosColumnas(t.Caja, $"Atendió: {t.Cajero}", columnas) : t.Caja));
        if (t.Cliente is { } cliente)
        {
            foreach (var parte in Partir($"Cliente: {cliente}", columnas))
                e.Add(new Renglon(parte));
        }
        e.Add(Linea('-', columnas));
        e.Add(new Renglon(Columna("Cant.", AnchoCantidad) + Columna("Descripción", anchoDescripcion + 1) + "Importe".PadLeft(AnchoImporte)));
        e.Add(Linea('=', columnas));

        foreach (var l in t.Lineas)
        {
            var descripcion = f.NombresConMayusculas ? NombreEnElTicket.ConMayusculas(l.Descripcion) : l.Descripcion;
            var partes = Partir(descripcion, anchoDescripcion);
            // Lo que no cabe en su columna va en su propio renglón, completo: el papel no dice otra cantidad que la vendida.
            var cabe = l.Cantidad.Length < AnchoCantidad;
            if (!cabe)
                e.Add(new Renglon(l.Cantidad));
            for (var i = 0; i < partes.Count; i++)
            {
                var cantidad = i == 0 && cabe ? l.Cantidad : "";
                // Con mayoreo, promoción o descuento, el renglón lleva el importe a menudeo y abajo va lo que se ahorró; sin
                // «Lo que se ahorró», lo que se cobró y nada más.
                var importe = i == 0
                    ? Dinero(f.MostrarAhorro ? l.ImporteCentavos + l.DescuentoCentavos + l.AhorroMayoreoCentavos + l.AhorroPromocionCentavos : l.ImporteCentavos)
                    : "";
                e.Add(new Renglon(Columna(cantidad, AnchoCantidad) + Columna(partes[i], anchoDescripcion + 1) + importe.PadLeft(AnchoImporte)));
            }
            if (!f.MostrarAhorro)
                continue;
            // «Ahorro por mayoreo», no «Mayoreo desde 12»: desde cuántas es cosa de la caja, no del cliente (dueño, 2026-11-21).
            if (l.AhorroMayoreoCentavos > 0)
                e.Add(new Renglon(new string(' ', AnchoCantidad) + Columna(AhorroPorMayoreo, anchoDescripcion + 1)
                    + $"-{Dinero(l.AhorroMayoreoCentavos)}".PadLeft(AnchoImporte)));
            if (l.AhorroPromocionCentavos > 0)
            {
                // «Promoción Regreso a clases  -$6.00», como el mayoreo: cuál fue y cuánto se ahorró; un nombre largo sigue abajo.
                var promocion = Partir(PromocionTexto(l.Promocion), anchoDescripcion);
                for (var i = 0; i < promocion.Count; i++)
                    e.Add(new Renglon(new string(' ', AnchoCantidad) + Columna(promocion[i], anchoDescripcion + 1)
                        + (i == 0 ? $"-{Dinero(l.AhorroPromocionCentavos)}" : "").PadLeft(AnchoImporte)));
            }
            if (l.DescuentoCentavos > 0)
                e.Add(new Renglon(new string(' ', AnchoCantidad) + Columna(l.Descuento is { } p ? $"Descuento {p}" : "Descuento", anchoDescripcion + 1)
                    + $"-{Dinero(l.DescuentoCentavos)}".PadLeft(AnchoImporte)));
        }

        e.Add(Linea('-', columnas));
        e.Add(new Renglon($"No. de artículos: {t.NumeroDeArticulos.ToString("0.###", Mexico),5}", Alineacion.Derecha));
        // Los descuentos del ticket, con su porcentaje sobre el total sin ellos (dueño, 2026-11-22): «Descuentos: -$10.00 (10 %)».
        // Con «Lo que se ahorró» apagado no sale, como lo de cada renglón.
        if (f.MostrarAhorro && t.Lineas.Sum(l => l.DescuentoCentavos) is var descuentos and > 0)
            e.Add(new Renglon(DescuentosDelTicket(descuentos, t.TotalCentavos), Alineacion.Derecha));
        if (f.MostrarAhorro && t.AhorroCentavos > 0)
            e.Add(new Renglon($"Usted ahorró: {Dinero(t.AhorroCentavos)}", Alineacion.Derecha));
        var resaltado = f.TotalResaltado;
        if (t.RedondeoCentavos == 0)
            e.Add(new Renglon($"Total: {Dinero(t.TotalCentavos)}", Alineacion.Derecha, Negrita: resaltado, Grande: resaltado));
        else
        {
            // El efectivo se cobra al peso: el total exacto, el redondeo y lo que se pagó.
            e.Add(new Renglon($"Total: {Dinero(t.TotalCentavos)}", Alineacion.Derecha));
            e.Add(new Renglon($"Redondeo: {(t.RedondeoCentavos > 0 ? "+" : "-")}{Dinero(Math.Abs(t.RedondeoCentavos))}", Alineacion.Derecha));
            e.Add(new Renglon($"Total a pagar: {Dinero(t.TotalCobradoCentavos)}", Alineacion.Derecha, Negrita: resaltado, Grande: resaltado));
        }
        foreach (var c in t.Cobros)
        {
            e.Add(new Renglon(c.Forma switch
            {
                FormaPago.Efectivo => $"Efectivo: {Dinero(c.RecibidoCentavos)}  Cambio: {Dinero(c.CambioCentavos)}",
                FormaPago.Tarjeta => $"Tarjeta: {Dinero(c.MontoCentavos)}" + (c.Referencia is { Length: > 0 } r ? $" ({r})" : ""),
                FormaPago.Transferencia => $"Transferencia: {Dinero(c.MontoCentavos)}",
                FormaPago.Credito => $"Crédito (a su cuenta): {Dinero(c.MontoCentavos)}",
                // Una forma nueva nunca sale como otra: el ticket se imprime aunque falte su texto.
                _ => $"{c.Forma}: {Dinero(c.MontoCentavos)}",
            }, Alineacion.Derecha));
        }
        // Sin IVA (dueño, 2026-11-21): ni «Incluye IVA» ni «(precios con IVA incluido)». Los precios ya lo traen; el desglose, si
        // algún día se pide, es la factura.
        if (t.Credito is { } credito)
        {
            // Lo fiado (fase 3): lo que quedó a deber, el saldo de su cuenta con esto y hasta cuándo se paga esta compra (lo de
            // antes vence cuando decía su ticket).
            e.Add(Linea('-', columnas));
            e.Add(new Renglon("SU CUENTA", Alineacion.Centro, Negrita: true));
            // Si lo cubrió su saldo a favor, no debe nada: no hay fecha que pagar.
            var saldo = credito.SaldoCentavos;
            e.Add(new Renglon(DosColumnas(saldo > 0 ? "Fiado en esta compra" : "De su saldo a favor", Dinero(credito.FiadoCentavos), columnas)));
            e.Add(new Renglon(saldo switch
            {
                > 0 => DosColumnas("Saldo de su cuenta", Dinero(saldo), columnas),
                < 0 => DosColumnas("Le quedan a favor", Dinero(-saldo), columnas),
                _ => "Al corriente",
            }, saldo == 0 ? Alineacion.Centro : Alineacion.Izquierda, Negrita: true));
            if (saldo > 0 && credito.Vence is { } vence)
                e.Add(new Renglon($"Paga esta compra antes del {vence.ToString("dd/MM/yyyy", Mexico)}", Alineacion.Centro));
            // Sin la caja principal, el saldo es el que sabía esta caja: el cliente lo lee como «por confirmar», sin «al volver»,
            // que es cosa de la red (2026-11-21, por revisar).
            if (credito.Estimado)
                e.Add(new Renglon(SaldoPorConfirmar, Alineacion.Centro));
        }
        if (t.Nota is { Length: > 0 } nota)
        {
            // La nota del ticket (F4 al cobrar, como en eleventa; 2026-10-22): uno o dos renglones, antes del pie.
            e.Add(Linea('-', columnas));
            foreach (var renglon in RenglonesDeLaNota(nota, columnas))
                e.Add(new Renglon(renglon));
        }
        e.Add(Linea('-', columnas));
        // Sin QR por ahora, como el ticket de eleventa (dueño, 2026-10-16: «primero igualar a eleventa»): para devolver se
        // escribe el folio impreso.
        foreach (var renglon in t.Pie)
            e.Add(new Renglon(renglon, Alineacion.Centro));
        return e;
    }

    /// <summary>«Descuentos: -$10.00 (10 %)»: lo descontado en todo el ticket y su porcentaje sobre el total sin descuentos.</summary>
    public static string DescuentosDelTicket(long descuentoCentavos, long totalCentavos) =>
        $"Descuentos: -{Dinero(descuentoCentavos)} ({PorcentajeDelDescuento.Texto(descuentoCentavos, totalCentavos + descuentoCentavos)})";

    /// <summary>Bajo un producto a precio de mayoreo: lo que se ahorró, en palabras del cliente.</summary>
    public const string AhorroPorMayoreo = "Ahorro por mayoreo";

    /// <summary>Lo fiado sin la caja principal: el saldo impreso es el que sabía esta caja.</summary>
    public const string SaldoPorConfirmar = "(saldo por confirmar)";

    private static Renglon Linea(char c, int columnas) => new(new string(c, columnas));

    /// <summary>
    /// «Nota: …» en los renglones del ticket, sin cortar palabras; si así no cabe en dos (una palabra muy larga), cortada justo
    /// en el renglón: la nota llega hasta <see cref="NotaDelTicket.Maximo"/> letras, que con «Nota: » caben en dos.
    /// </summary>
    public static List<string> RenglonesDeLaNota(string nota, int columnas = Columnas)
    {
        var texto = $"Nota: {nota}";
        var partes = Partir(texto, columnas);
        if (partes.Count <= 2)
            return partes;
        return Enumerable.Range(0, (texto.Length + columnas - 1) / columnas)
            .Select(i => texto.Substring(i * columnas, Math.Min(columnas, texto.Length - i * columnas)).Trim()).ToList();
    }

    private static string Columna(string texto, int ancho) => texto.Length >= ancho ? texto[..(ancho - 1)] + " " : texto.PadRight(ancho);

    private static string DosColumnas(string izquierda, string derecha, int columnas)
    {
        // «Atendió: » con un nombre de 39 o 40 letras no cabe junto a la caja: se recorta, y el ticket sale (auditoría, 2026-11-04).
        if (derecha.Length > columnas - 1)
            derecha = derecha[..(columnas - 1)];
        var espacio = columnas - derecha.Length - 1;
        return (izquierda.Length > espacio ? izquierda[..espacio] : izquierda).PadRight(columnas - derecha.Length) + derecha;
    }

    /// <summary>«Promoción Regreso a clases»: lo que dice el ticket bajo el renglón de un producto con promoción (fase 3).</summary>
    public static string PromocionTexto(string? nombre) =>
        string.IsNullOrWhiteSpace(nombre) ? "Promoción"
        : nombre.StartsWith("Promo", StringComparison.CurrentCultureIgnoreCase) ? nombre
        : $"Promoción {nombre}";

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
