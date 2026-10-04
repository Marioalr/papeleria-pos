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
}
