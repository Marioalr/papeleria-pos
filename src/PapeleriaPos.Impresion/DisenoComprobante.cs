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
        if (c.AbonosEfectivoCentavos != 0)
            Linea("Abonos de clientes en efectivo", c.AbonosEfectivoCentavos);
        if (c.SaldoDevueltoCentavos != 0)
            Linea("Saldo a favor devuelto", -c.SaldoDevueltoCentavos);
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
        // Los abonos (no son venta) y lo fiado (ya es venta, pero no es dinero) van aparte.
        if (c.Abonos > 0)
            e.Add(new Renglon($"Abonos de clientes: {c.Abonos}"));
        if (c.AbonosTarjetaCentavos != 0)
            Linea("Abonos con tarjeta", c.AbonosTarjetaCentavos);
        if (c.AbonosTransferenciaCentavos != 0)
            Linea("Abonos por transferencia", c.AbonosTransferenciaCentavos);
        if (c.CreditoCentavos != 0)
            Linea("Fiado (ventas a crédito)", c.CreditoCentavos);
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
        if (c.Cliente is { } cliente)
            foreach (var parte in DisenoTicket.Partir($"Cliente: {cliente}", DisenoTicket.Columnas))
                e.Add(new Renglon(parte));
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
        if (c.AlCreditoCentavos > 0)
        {
            e.Add(new Renglon($"Se descuenta de su cuenta: {DisenoTicket.Dinero(c.AlCreditoCentavos)}", Alineacion.Derecha));
            if (c.SaldoClienteCentavos is { } saldo)
                e.Add(new Renglon(saldo < 0 ? $"Saldo a su favor: {DisenoTicket.Dinero(-saldo)}" : $"Saldo de su cuenta: {DisenoTicket.Dinero(saldo)}",
                    Alineacion.Derecha, Negrita: true));
        }
        foreach (var parte in DisenoTicket.Partir($"Motivo: {c.Motivo}", DisenoTicket.Columnas))
            e.Add(new Renglon(parte));
        e.Add(new Renglon(""));
        e.Add(new Renglon(""));
        e.Add(new Renglon("________________________________", Alineacion.Centro));
        e.Add(new Renglon("Firma del cliente", Alineacion.Centro));
        return e;
    }

    /// <summary>
    /// Comprobante de un abono a la cuenta de un cliente (o de su cancelación): lo que debía, lo que abonó y con qué, y su
    /// saldo. Sin conexión, el saldo es el que conoce la caja.
    /// </summary>
    public static List<Elemento> Abono(ComprobanteAbonoDto c)
    {
        if (c.EsSaldoDevuelto)
            return SaldoDevuelto(c);
        var e = new List<Elemento>();
        if (c.Encabezado.Count > 0)
            e.Add(new Renglon(c.Encabezado[0], Alineacion.Centro, Negrita: true));
        e.Add(new Renglon(c.Cancelado ? "CANCELACIÓN DE ABONO" : "ABONO A SU CUENTA", Alineacion.Centro, Negrita: true, Grande: true));
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        e.Add(new Renglon($"Abono {c.Folio}"));
        e.Add(new Renglon($"Fecha: {c.FechaLocal.ToString("dd/MM/yyyy  HH:mm", Mexico)}"));
        e.Add(new Renglon($"{c.Caja} · Recibió: {c.Usuario}"));
        foreach (var parte in DisenoTicket.Partir($"Cliente: {c.Cliente}", DisenoTicket.Columnas))
            e.Add(new Renglon(parte));
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        void Linea(string concepto, long centavos, bool negrita = false) =>
            e.Add(new Renglon(concepto.PadRight(DisenoTicket.Columnas - 14) + DisenoTicket.Dinero(centavos).PadLeft(14), Negrita: negrita));
        var efectivo = c.Forma == Nucleo.Ventas.FormaPago.Efectivo;
        var forma = c.Forma switch
        {
            Nucleo.Ventas.FormaPago.Tarjeta => "con tarjeta",
            Nucleo.Ventas.FormaPago.Transferencia => "por transferencia",
            _ => "en efectivo",
        };
        if (c.Cancelado)
        {
            // Lo que se regresa y lo que vuelve a deber; el cobro de entonces (pagó con, cambio) ya no importa.
            Linea($"Abono {forma} cancelado", c.MontoCentavos, negrita: true);
            e.Add(new Renglon(efectivo
                ? $"Se le regresan en efectivo: {DisenoTicket.Dinero(c.CobradoCentavos)}"
                : $"Se regresa por su medio de pago: {DisenoTicket.Dinero(c.MontoCentavos)}", Alineacion.Derecha));
            e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
            var saldo = c.SaldoTrasCancelarCentavos ?? c.SaldoCentavos + c.MontoCentavos;
            e.Add(new Renglon($"Saldo: {DisenoTicket.Dinero(saldo)}", Alineacion.Derecha, Negrita: true, Grande: true));
            e.Add(new Renglon($"Vuelve a deber {DisenoTicket.Dinero(c.MontoCentavos)}.", Alineacion.Centro, Negrita: true));
            if (c.MotivoCancelacion is { } motivo)
                foreach (var parte in DisenoTicket.Partir($"Motivo: {motivo}", DisenoTicket.Columnas))
                    e.Add(new Renglon(parte));
        }
        else
        {
            Linea("Debía", c.SaldoAnteriorCentavos);
            Linea($"Abono {forma}", c.MontoCentavos, negrita: true);
            if (c.RedondeoCentavos != 0)
                e.Add(new Renglon($"Redondeo: {(c.RedondeoCentavos > 0 ? "+" : "-")}{DisenoTicket.Dinero(Math.Abs(c.RedondeoCentavos))}", Alineacion.Derecha));
            if (efectivo)
                e.Add(new Renglon($"Efectivo: {DisenoTicket.Dinero(c.RecibidoCentavos)}  Cambio: {DisenoTicket.Dinero(c.CambioCentavos)}", Alineacion.Derecha));
            else if (c.Referencia is { Length: > 0 } referencia)
                foreach (var parte in DisenoTicket.Partir($"Referencia: {referencia}", DisenoTicket.Columnas))
                    e.Add(new Renglon(parte, Alineacion.Derecha));
            if (c.Forma == Nucleo.Ventas.FormaPago.Transferencia)
                e.Add(new Renglon("La transferencia queda por verificar", Alineacion.Derecha));
            e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
            e.Add(new Renglon($"Saldo: {DisenoTicket.Dinero(c.SaldoCentavos)}", Alineacion.Derecha, Negrita: true, Grande: true));
            if (c.SaldoCentavos <= 0)
                e.Add(new Renglon("Al corriente. ¡Gracias!", Alineacion.Centro));
            else if (c.Vence is { } vence)
                e.Add(new Renglon(vence < DateOnly.FromDateTime(c.FechaLocal)
                    ? $"Lo más antiguo venció el {vence.ToString("dd/MM/yyyy", Mexico)}"
                    : $"Paga antes del {vence.ToString("dd/MM/yyyy", Mexico)} · sin recargos", Alineacion.Centro));
        }
        if (c.Estimado)
            e.Add(new Renglon("(saldo según esta caja; se confirma al volver)", Alineacion.Centro));
        e.Add(new Renglon(""));
        e.Add(new Renglon(""));
        e.Add(new Renglon("________________________________", Alineacion.Centro));
        e.Add(new Renglon("Firma de quien recibe", Alineacion.Centro));
        return e;
    }

    /// <summary>
    /// Comprobante de un saldo a favor que se le devolvió en efectivo (parte 3): lo que tenía, lo que se le entregó y lo
    /// que le queda; lo firma el cliente.
    /// </summary>
    private static List<Elemento> SaldoDevuelto(ComprobanteAbonoDto c)
    {
        var e = new List<Elemento>();
        if (c.Encabezado.Count > 0)
            e.Add(new Renglon(c.Encabezado[0], Alineacion.Centro, Negrita: true));
        e.Add(new Renglon("SALDO A FAVOR DEVUELTO", Alineacion.Centro, Negrita: true, Grande: true));
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        e.Add(new Renglon($"Comprobante {c.Folio}"));
        e.Add(new Renglon($"Fecha: {c.FechaLocal.ToString("dd/MM/yyyy  HH:mm", Mexico)}"));
        e.Add(new Renglon($"{c.Caja} · Entregó: {c.Usuario}"));
        foreach (var parte in DisenoTicket.Partir($"Cliente: {c.Cliente}", DisenoTicket.Columnas))
            e.Add(new Renglon(parte));
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        void Linea(string concepto, long centavos, bool negrita = false) =>
            e.Add(new Renglon(concepto.PadRight(DisenoTicket.Columnas - 14) + DisenoTicket.Dinero(centavos).PadLeft(14), Negrita: negrita));
        Linea("Tenía a favor", -c.SaldoAnteriorCentavos);
        Linea("Se le entregan en efectivo", c.CobradoCentavos, negrita: true);
        if (c.RedondeoCentavos != 0)
            e.Add(new Renglon($"(redondeo al peso: {(c.RedondeoCentavos > 0 ? "+" : "-")}{DisenoTicket.Dinero(Math.Abs(c.RedondeoCentavos))})", Alineacion.Derecha));
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        e.Add(c.SaldoCentavos < 0
            ? new Renglon($"Le quedan a favor: {DisenoTicket.Dinero(-c.SaldoCentavos)}", Alineacion.Derecha, Negrita: true, Grande: true)
            : new Renglon("Al corriente", Alineacion.Centro, Negrita: true, Grande: true));
        e.Add(new Renglon(""));
        e.Add(new Renglon(""));
        e.Add(new Renglon("________________________________", Alineacion.Centro));
        e.Add(new Renglon("Firma del cliente (recibí el efectivo)", Alineacion.Centro));
        return e;
    }

    /// <summary>
    /// El estado de cuenta de un cliente (parte 3), para dárselo impreso: lo que debía al empezar, cada compra a crédito y
    /// cada abono con el saldo que dejó, lo que debe y lo que falta pagar de cada compra con su vencimiento.
    /// </summary>
    public static List<Elemento> EstadoCuenta(EstadoCuentaDto c)
    {
        const int monto = 13, saldo = 13, concepto = DisenoTicket.Columnas - monto - saldo;
        var e = new List<Elemento>();
        for (var i = 0; i < c.Encabezado.Count; i++)
            e.Add(new Renglon(c.Encabezado[i], Alineacion.Centro, Negrita: i == 0));
        e.Add(new Renglon("ESTADO DE CUENTA", Alineacion.Centro, Negrita: true, Grande: true));
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        foreach (var parte in DisenoTicket.Partir($"Cliente: {c.Cliente}", DisenoTicket.Columnas))
            e.Add(new Renglon(parte));
        e.Add(new Renglon($"Fecha: {c.FechaLocal.ToString("dd/MM/yyyy  HH:mm", Mexico)}"));
        if (c.LimiteCentavos > 0)
            e.Add(new Renglon($"Crédito de {DisenoTicket.Dinero(c.LimiteCentavos)} a {c.PlazoDias} días, sin recargos"));
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        e.Add(new Renglon("Fecha y concepto".PadRight(concepto) + "Monto".PadLeft(monto) + "Saldo".PadLeft(saldo)));
        e.Add(new Renglon(new string('=', DisenoTicket.Columnas)));
        e.Add(new Renglon($"Saldo antes del {Fecha(c.Desde)}".PadRight(concepto + monto) + DisenoTicket.Dinero(c.SaldoAnteriorCentavos).PadLeft(saldo)));
        foreach (var m in c.Movimientos)
        {
            var partes = DisenoTicket.Partir($"{Dia(c, m)} {Movimiento(m)}", concepto - 1);
            e.Add(new Renglon(partes[0].PadRight(concepto) + Firmado(m.MontoCentavos).PadLeft(monto) + DisenoTicket.Dinero(m.SaldoCentavos).PadLeft(saldo)));
            foreach (var parte in partes.Skip(1))
                e.Add(new Renglon("      " + parte));
        }
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        e.Add(new Renglon(Saldo(c), Alineacion.Derecha, Negrita: true, Grande: true));
        if (c.VencidoCentavos > 0)
            e.Add(new Renglon($"Vencido: {DisenoTicket.Dinero(c.VencidoCentavos)}", Alineacion.Derecha, Negrita: true));
        if (c.PorVerificarCentavos > 0)
            e.Add(new Renglon($"Transferencias por verificar: {DisenoTicket.Dinero(c.PorVerificarCentavos)}", Alineacion.Derecha));
        if (c.Pendientes.Count > 0)
        {
            e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
            e.Add(new Renglon("LO QUE FALTA PAGAR", Negrita: true));
            var hoy = DateOnly.FromDateTime(c.FechaLocal);
            foreach (var p in c.Pendientes)
            {
                var partes = DisenoTicket.Partir($"{Fecha(p.Fecha)} {p.Concepto}{(p.Folio is { } folio ? $" {folio}" : "")}", DisenoTicket.Columnas - monto - 1);
                e.Add(new Renglon(partes[0].PadRight(DisenoTicket.Columnas - monto) + DisenoTicket.Dinero(p.PendienteCentavos).PadLeft(monto)));
                foreach (var parte in partes.Skip(1))
                    e.Add(new Renglon("  " + parte));
                e.Add(new Renglon("  " + (p.PendienteCentavos < p.MontoCentavos ? $"(de {DisenoTicket.Dinero(p.MontoCentavos)}) " : "")
                    + (p.Vence < hoy ? $"VENCIÓ EL {Fecha(p.Vence)}" : $"vence el {Fecha(p.Vence)}"), Negrita: p.Vence < hoy));
            }
        }
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        e.Add(new Renglon(c.SaldoCentavos > 0 ? "Sin recargos. ¡Gracias por su preferencia!" : "¡Gracias por su preferencia!", Alineacion.Centro));
        return e;
    }

    /// <summary>El estado de cuenta como texto, para pegarlo en WhatsApp (los asteriscos son negritas).</summary>
    public static string EstadoCuentaTexto(EstadoCuentaDto c)
    {
        var l = new List<string>();
        if (c.Encabezado.Count > 0)
            l.Add($"*{c.Encabezado[0]}*");
        l.Add($"Estado de cuenta de *{c.Cliente}*");
        l.Add($"Al {c.FechaLocal.ToString("dd/MM/yyyy HH:mm", Mexico)}");
        l.Add("");
        l.Add($"Saldo antes del {Fecha(c.Desde)}: {DisenoTicket.Dinero(c.SaldoAnteriorCentavos)}");
        foreach (var m in c.Movimientos)
            l.Add($"{Dia(c, m)} {Movimiento(m)}: {Firmado(m.MontoCentavos)} (saldo {DisenoTicket.Dinero(m.SaldoCentavos)})");
        l.Add("");
        l.Add($"*{Saldo(c)}*");
        if (c.VencidoCentavos > 0)
            l.Add($"Vencido: {DisenoTicket.Dinero(c.VencidoCentavos)}");
        if (c.PorVerificarCentavos > 0)
            l.Add($"Transferencias por verificar: {DisenoTicket.Dinero(c.PorVerificarCentavos)}");
        if (c.Pendientes.Count > 0)
        {
            l.Add("");
            l.Add("Lo que falta pagar:");
            var hoy = DateOnly.FromDateTime(c.FechaLocal);
            foreach (var p in c.Pendientes)
                l.Add($"- {p.Concepto}{(p.Folio is { } folio ? $" {folio}" : "")} del {Fecha(p.Fecha)}: {DisenoTicket.Dinero(p.PendienteCentavos)}"
                    + (p.PendienteCentavos < p.MontoCentavos ? $" de {DisenoTicket.Dinero(p.MontoCentavos)}" : "")
                    + (p.Vence < hoy ? $", *venció el {Fecha(p.Vence)}*" : $", vence el {Fecha(p.Vence)}"));
        }
        l.Add("");
        l.Add(c.SaldoCentavos > 0 ? "Sin recargos. ¡Gracias por su preferencia!" : "¡Gracias por su preferencia!");
        return string.Join("\n", l);
    }

    /// <summary>"05/10"; si el estado de cuenta empieza en otro año, "05/10/25".</summary>
    private static string Dia(EstadoCuentaDto c, MovimientoCreditoDto m) =>
        m.FechaLocal.ToString(c.Desde.Year == c.FechaLocal.Year ? "dd/MM" : "dd/MM/yy", Mexico);

    private static string Movimiento(MovimientoCreditoDto m) => m.Folio is { } folio ? $"{m.Concepto} {folio}" : m.Concepto;

    private static string Saldo(EstadoCuentaDto c) => c.SaldoCentavos switch
    {
        > 0 => $"Debe: {DisenoTicket.Dinero(c.SaldoCentavos)}",
        < 0 => $"Tiene a favor: {DisenoTicket.Dinero(-c.SaldoCentavos)}",
        _ => "Al corriente",
    };

    /// <summary>"+$250.00" o "-$150.00".</summary>
    private static string Firmado(long centavos) => (centavos < 0 ? "-" : "+") + DisenoTicket.Dinero(Math.Abs(centavos));

    private static string Fecha(DateOnly fecha) => fecha.ToString("dd/MM/yyyy", Mexico);
}
