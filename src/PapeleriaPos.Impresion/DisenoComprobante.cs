using System.Globalization;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Impresion;

/// <summary>Documentos de caja que no son ventas, para la misma impresora de tickets.</summary>
public static class DisenoComprobante
{
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Comprobante de una salida (o entrada) de efectivo, con espacio para la firma de quien lo recibe.</summary>
    public static List<Elemento> Movimiento(ComprobanteMovimientoDto c)
    {
        var salida = c.Tipo == TipoMovimientoCaja.Salida;
        var e = new List<Elemento>();
        if (c.Encabezado.Count > 0)
            e.Add(new Renglon(c.Encabezado[0], Alineacion.Centro, Negrita: true));
        e.Add(new Renglon(salida ? "SALIDA DE EFECTIVO" : "ENTRADA DE EFECTIVO", Alineacion.Centro, Negrita: true, Grande: true));
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        e.Add(new Renglon($"Comprobante {c.Folio}"));
        e.Add(new Renglon($"Fecha: {c.FechaLocal.ToString("dd/MM/yyyy  HH:mm", Mexico)}"));
        e.Add(new Renglon($"{c.Caja} · {(salida ? "Retiró" : "Recibió")}: {c.Usuario}"));
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        e.Add(new Renglon($"Monto: {DisenoTicket.Dinero(c.MontoCentavos)}", Alineacion.Derecha, Negrita: true, Grande: true));
        foreach (var parte in DisenoTicket.Partir($"Motivo: {c.Motivo}", DisenoTicket.Columnas))
            e.Add(new Renglon(parte));
        e.Add(new Renglon(""));
        e.Add(new Renglon(""));
        e.Add(new Renglon("________________________________", Alineacion.Centro));
        e.Add(new Renglon(salida ? "Firma de quien recibe el efectivo" : "Firma", Alineacion.Centro));
        return e;
    }

    /// <summary>Comprobante de una cancelación o devolución: qué regresó y cuánto efectivo salió de la caja.</summary>
    public static List<Elemento> Devolucion(ComprobanteDevolucionDto c)
    {
        var e = new List<Elemento>();
        if (c.Encabezado.Count > 0)
            e.Add(new Renglon(c.Encabezado[0], Alineacion.Centro, Negrita: true));
        e.Add(new Renglon(c.EsCancelacion ? "CANCELACIÓN" : "DEVOLUCIÓN", Alineacion.Centro, Negrita: true, Grande: true));
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        e.Add(new Renglon($"Ticket {c.Folio}"));
        e.Add(new Renglon($"Fecha: {c.FechaLocal.ToString("dd/MM/yyyy  HH:mm", Mexico)}"));
        e.Add(new Renglon($"{c.Caja} · Atendió: {c.Usuario}"));
        if (c.Autorizo is { } autorizo)
            e.Add(new Renglon($"Autorizó: {autorizo}"));
        e.Add(new Renglon(new string('=', DisenoTicket.Columnas)));
        foreach (var l in c.Lineas)
        {
            var partes = DisenoTicket.Partir(l.Descripcion, DisenoTicket.Columnas - 18);
            for (var i = 0; i < partes.Count; i++)
                e.Add(new Renglon((i == 0 ? l.Cantidad : "").PadRight(6) + partes[i].PadRight(DisenoTicket.Columnas - 17) + (i == 0 ? DisenoTicket.Dinero(l.ImporteCentavos) : "").PadLeft(11)));
        }
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        e.Add(new Renglon($"Total: {DisenoTicket.Dinero(c.TotalCentavos)}", Alineacion.Derecha, Negrita: true, Grande: true));
        e.Add(new Renglon($"Efectivo devuelto: {DisenoTicket.Dinero(c.ReembolsoEfectivoCentavos)}", Alineacion.Derecha));
        if (c.ReembolsoEfectivoCentavos < c.TotalCentavos)
            e.Add(new Renglon("El resto se reembolsa por el mismo medio de pago", Alineacion.Derecha));
        foreach (var parte in DisenoTicket.Partir($"Motivo: {c.Motivo}", DisenoTicket.Columnas))
            e.Add(new Renglon(parte));
        e.Add(new Renglon(""));
        e.Add(new Renglon(""));
        e.Add(new Renglon("________________________________", Alineacion.Centro));
        e.Add(new Renglon("Firma del cliente", Alineacion.Centro));
        return e;
    }
}
