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

    /// <summary>El corte de cierre impreso, para guardarlo con el efectivo. Sin conexión, avisa que se cuadra al volver.</summary>
    public static List<Elemento> Corte(CorteDto c, string caja)
    {
        var e = new List<Elemento>
        {
            new Renglon(c.Tipo == TipoCorte.Z ? "CORTE DE CIERRE (Z)" : "CORTE PARCIAL (X)", Alineacion.Centro, Negrita: true, Grande: true),
        };
        if (c.EnContingencia)
            e.Add(new Renglon("Sin conexión: se cuadra al volver", Alineacion.Centro, Negrita: true));
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        e.Add(new Renglon($"Fecha: {c.FechaLocal.ToString("dd/MM/yyyy  HH:mm", Mexico)}"));
        e.Add(new Renglon($"{caja} · Cajero: {c.Cajero}"));
        e.Add(new Renglon($"Hizo el corte: {c.RealizadoPor}"));
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        void Linea(string concepto, long centavos, bool negrita = false) =>
            e.Add(new Renglon(concepto.PadRight(DisenoTicket.Columnas - 14) + DisenoTicket.Dinero(centavos).PadLeft(14), Negrita: negrita));
        e.Add(new Renglon($"Ventas: {c.Ventas}" + (c.Cancelaciones > 0 ? $" · canceladas: {c.Cancelaciones}" : "")));
        Linea("Fondo", c.FondoCentavos);
        Linea("Efectivo de ventas", c.EfectivoVentasCentavos);
        Linea("Entradas", c.EntradasCentavos);
        Linea("Salidas", -c.SalidasCentavos);
        if (c.DevolucionesCentavos > 0)
            Linea("Devoluciones y cancelaciones", -c.DevolucionesCentavos);
        Linea("Efectivo esperado", c.EsperadoCentavos, negrita: true);
        if (c.RedondeoCentavos != 0)
            Linea("(incluye redondeo al peso)", c.RedondeoCentavos);
        if (c.ContadoCentavos is { } contado)
        {
            Linea("Contado", contado, negrita: true);
            var diferencia = c.DiferenciaCentavos ?? 0;
            Linea(diferencia < 0 ? "Faltante" : diferencia > 0 ? "Sobrante" : "Diferencia", Math.Abs(diferencia), negrita: true);
        }
        Linea("Tarjeta", c.TarjetaCentavos);
        Linea("Transferencias", c.TransferenciaCentavos);
        e.Add(new Renglon(""));
        e.Add(new Renglon(""));
        e.Add(new Renglon("________________________________", Alineacion.Centro));
        e.Add(new Renglon("Firma del encargado", Alineacion.Centro));
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
        if (c.RedondeoCentavos != 0)
            e.Add(new Renglon($"Redondeo: {(c.RedondeoCentavos > 0 ? "+" : "-")}{DisenoTicket.Dinero(Math.Abs(c.RedondeoCentavos))}", Alineacion.Derecha));
        e.Add(new Renglon($"Efectivo devuelto: {DisenoTicket.Dinero(c.ReembolsoEfectivoCentavos)}", Alineacion.Derecha));
        if (c.CobradoSinEfectivoCentavos > 0)
            e.Add(new Renglon($"Se regresa por su medio de pago: {DisenoTicket.Dinero(c.CobradoSinEfectivoCentavos)}", Alineacion.Derecha));
        foreach (var parte in DisenoTicket.Partir($"Motivo: {c.Motivo}", DisenoTicket.Columnas))
            e.Add(new Renglon(parte));
        e.Add(new Renglon(""));
        e.Add(new Renglon(""));
        e.Add(new Renglon("________________________________", Alineacion.Centro));
        e.Add(new Renglon("Firma del cliente", Alineacion.Centro));
        return e;
    }
}
