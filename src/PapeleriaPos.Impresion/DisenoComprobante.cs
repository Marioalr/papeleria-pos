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

    /// <summary>El corte del turno impreso, para guardarlo con el efectivo. Sin conexión, avisa que se cuadra al volver.</summary>
    public static List<Elemento> Corte(CorteDto c, string caja)
    {
        var e = new List<Elemento>
        {
            new Renglon(c.Tipo == TipoCorte.Z ? "CORTE DEL TURNO (Z)" : "CORTE PARCIAL (X)", Alineacion.Centro, Negrita: true, Grande: true),
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

    /// <summary>
    /// El corte del día impreso (2026-10-17), como el de eleventa («Imprimir» lo manda a la impresora de tickets) y con el
    /// acomodo del corte del turno: el día y si se cerró, lo vendido y sus formas de pago, cada turno con lo que se contó, los
    /// movimientos, las ventas por departamento y, solo si viene (para quien ve la utilidad), la utilidad, también la de cada
    /// departamento. Lo mismo que ve en la pantalla quien lo imprime, de hoy o de un día que ya pasó.
    /// </summary>
    /// <param name="impresoPor">Quien lo imprime.</param>
    /// <param name="impresoLocal">Cuándo (hora de México).</param>
    public static List<Elemento> CorteDelDia(CorteDelDiaDto d, string impresoPor, DateTime impresoLocal)
    {
        const int anchoMonto = 14, anchoConcepto = DisenoTicket.Columnas - anchoMonto;
        var e = new List<Elemento>
        {
            new Renglon("CORTE DEL DÍA", Alineacion.Centro, Negrita: true, Grande: true),
            // En el piloto, aquí solo está lo de la caja nueva: lo de las demás cajas está en eleventa.
            new Renglon($"{(d.Piloto ? "Esta caja" : "Todas las cajas")} · {d.Fecha.ToString("dddd dd/MM/yyyy", Mexico)}", Alineacion.Centro),
            new Renglon(new string('-', DisenoTicket.Columnas)),
        };
        // Lo de cada turno va con sangría, también lo que baja de renglón.
        void Texto(string texto, bool negrita = false, int sangria = 0)
        {
            foreach (var parte in DisenoTicket.Partir(texto, DisenoTicket.Columnas - sangria))
                e.Add(new Renglon(new string(' ', sangria) + parte, Negrita: negrita));
        }
        // Un concepto largo (el motivo de una salida) baja de renglón; el monto va en el último.
        void Linea(string concepto, string monto, bool negrita = false, int sangria = 0)
        {
            var partes = DisenoTicket.Partir(concepto, anchoConcepto - 1 - sangria);
            var margen = new string(' ', sangria);
            for (var i = 0; i < partes.Count - 1; i++)
                e.Add(new Renglon(margen + partes[i], Negrita: negrita));
            e.Add(new Renglon((margen + partes[^1]).PadRight(anchoConcepto) + monto.PadLeft(anchoMonto), Negrita: negrita));
        }
        void Dinero(string concepto, long centavos, bool negrita = false, int sangria = 0) =>
            Linea(concepto, DisenoTicket.Dinero(centavos), negrita, sangria);
        void Seccion(string titulo)
        {
            e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
            e.Add(new Renglon(titulo, Negrita: true));
        }

        e.Add(new Renglon($"Impreso: {impresoLocal.ToString("dd/MM/yyyy  HH:mm", Mexico)}"));
        Texto($"Lo imprimió: {impresoPor}");
        Texto(d switch
        {
            { Piloto: true, DeOtroDia: true } => "Del piloto con eleventa: el día se cerró con el corte de eleventa.",
            { Piloto: true } => "Piloto con eleventa: el día se cierra con el corte de eleventa.",
            { Cierre: { } cierre } => $"Día cerrado a las {cierre.CerradoLocal.ToString("HH:mm", Mexico)} por {cierre.CerradoPor}.",
            { DeOtroDia: true } => "Ese día no se cerró.",
            _ => "El día todavía no se cierra.",
        }, negrita: true);
        e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
        Dinero("Vendido", d.VendidoCentavos, negrita: true);
        e.Add(new Renglon($"Tickets: {d.Tickets} en {d.Turnos.Count} {(d.Turnos.Count == 1 ? "turno" : "turnos")}"));
        Dinero("Efectivo", d.EfectivoCentavos);
        Dinero("Tarjeta", d.TarjetaCentavos);
        Dinero("Transferencia", d.TransferenciaCentavos);
        if (d.FiadoCentavos != 0)
            Dinero("Fiado (ventas a crédito)", d.FiadoCentavos);
        if (d.TransferenciasPorVerificar > 0)
            e.Add(new Renglon($"Transferencias por verificar: {d.TransferenciasPorVerificar}"));
        if (d.Utilidad is { } utilidad)
            Dinero("Utilidad bruta", utilidad.UtilidadCentavos, negrita: true);

        Seccion("TURNOS");
        if (d.Turnos.Count == 0)
            e.Add(new Renglon(d.DeOtroDia ? "Ese día no se abrió ningún turno." : "Hoy todavía no se abre ningún turno."));
        foreach (var t in d.Turnos)
        {
            Texto($"{t.Caja} · {t.Cajero}{(t.EnContingencia ? " (sin conexión)" : "")}");
            var horario = $"{Hora(t.AbiertoLocal, d.Fecha)}-{(t.CerradoLocal is { } cerrado ? Hora(cerrado, d.Fecha) : "abierto")}";
            Dinero($"{horario} · {t.Tickets} {(t.Tickets == 1 ? "ticket" : "tickets")}", t.VendidoCentavos, sangria: 2);
            if (t.ContadoCentavos is { } contado)
            {
                Texto($"Esperado {DisenoTicket.Dinero(t.EsperadoCentavos ?? 0)} · contado {DisenoTicket.Dinero(contado)}", sangria: 2);
                var diferencia = t.DiferenciaCentavos ?? 0;
                Dinero(diferencia < 0 ? "Faltante" : diferencia > 0 ? "Sobrante" : "Cuadra", Math.Abs(diferencia), sangria: 2);
            }
            else if (t.CorteEnEleventa is { } enEleventa)
                Texto(enEleventa, sangria: 2);
            // Su corte va en el día en que se hizo.
            else if (t.CerradoLocal is { } despues && DateOnly.FromDateTime(despues) > d.Fecha)
                Texto($"Su corte, el {despues.ToString("dd/MM", Mexico)}, va en ese día.", sangria: 2);
            // Se cerró antes: está por lo que se cobró en él sin conexión y subió tarde, con la hora de este día.
            else if (t.CerradoLocal is { } antes && DateOnly.FromDateTime(antes) < d.Fecha)
                Texto($"Cobrado sin conexión; su corte es del {antes.ToString("dd/MM", Mexico)}.", sangria: 2);
            else if (t.CerradoLocal is null)
                Texto(d.Piloto ? "Corte en eleventa" : t.EsperadoCentavos is { } esperado
                    ? $"Turno abierto · esperado {DisenoTicket.Dinero(esperado)}"
                    : "Turno abierto", sangria: 2);
        }
        if (d.Turnos.Any(t => t.ContadoCentavos is not null))
            Dinero("Diferencias de corte", d.Turnos.Sum(t => t.DiferenciaCentavos ?? 0), negrita: true);
        if (d.Turnos.Any(t => DateOnly.FromDateTime(t.AbiertoLocal) < d.Fecha))
            Texto("De un turno que viene de otro día, solo lo de este día; lo esperado y lo contado, de todo el turno.");

        Seccion("MOVIMIENTOS");
        if (d.EntradasCentavos != 0)
            Dinero("Entradas de efectivo", d.EntradasCentavos);
        foreach (var s in d.Salidas)
            Dinero($"Salidas · {s.Motivo}{(s.Veces > 1 ? $" ({s.Veces})" : "")}", -s.MontoCentavos);
        if (d.Salidas.Count == 0)
            Dinero("Salidas", 0);
        Dinero($"Devoluciones{(d.Devoluciones > 0 ? $" ({d.Devoluciones})" : "")}", -d.DevolucionesCentavos);
        Dinero($"Cancelaciones{(d.Cancelaciones > 0 ? $" ({d.Cancelaciones})" : "")}", -d.CancelacionesCentavos);
        if (d.Abonos > 0)
            Dinero($"Abonos recibidos ({d.Abonos})", d.AbonosCentavos);
        if (d.ImpresionesCentavos != 0)
            Dinero("Impresiones cobradas", d.ImpresionesCentavos);

        // Como en eleventa, las ventas por departamento las ve (y las imprime) quien ve el corte del día; la utilidad de cada
        // uno, solo quien ve la utilidad.
        if (d.PorDepartamento is { } porDepartamento)
        {
            var conUtilidad = d.Utilidad is not null;
            Seccion("VENTAS POR DEPARTAMENTO");
            void Departamento(string nombre, string venta, string? utilidad, bool negrita = false)
            {
                var anchoNombre = DisenoTicket.Columnas - (utilidad is null ? anchoMonto : 2 * 13);
                var partes = DisenoTicket.Partir(nombre, anchoNombre - 1);
                var montos = utilidad is null ? venta.PadLeft(anchoMonto) : venta.PadLeft(13) + utilidad.PadLeft(13);
                e.Add(new Renglon(partes[0].PadRight(anchoNombre) + montos, Negrita: negrita));
                foreach (var parte in partes.Skip(1))
                    e.Add(new Renglon("  " + parte, Negrita: negrita));
            }
            string? Utilidad(long? centavos) => conUtilidad ? DisenoTicket.Dinero(centavos ?? 0) : null;
            Departamento("Departamento", "Venta", conUtilidad ? "Utilidad" : null);
            foreach (var departamento in porDepartamento.Departamentos)
                Departamento(departamento.Departamento, DisenoTicket.Dinero(departamento.VentaCentavos), Utilidad(departamento.UtilidadCentavos));
            if (porDepartamento.ImpresionesCentavos != 0 || porDepartamento.ImpresionesCostoCentavos is not (null or 0))
                Departamento("Impresiones", DisenoTicket.Dinero(porDepartamento.ImpresionesCentavos), Utilidad(porDepartamento.ImpresionesUtilidadCentavos));
            if (porDepartamento.ArticuloComunCentavos != 0)
                // El ticket va en la página 850, que no tiene la raya larga.
                Departamento("Artículo común", DisenoTicket.Dinero(porDepartamento.ArticuloComunCentavos), conUtilidad ? "sin costo" : null);
            if (porDepartamento.Departamentos.Count == 0 && porDepartamento.ImpresionesCentavos == 0 && porDepartamento.ArticuloComunCentavos == 0)
                e.Add(new Renglon(d.DeOtroDia ? "Ese día no hubo ventas." : "Hoy todavía no hay ventas."));
            e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
            Departamento("Total", DisenoTicket.Dinero(porDepartamento.TotalCentavos), Utilidad(d.Utilidad?.UtilidadCentavos), negrita: true);
            if (d.Devoluciones > 0)
                Texto("Lo devuelto ese día ya se restó de su departamento.");
        }

        if (d.Utilidad is { } u)
        {
            Seccion("UTILIDAD");
            Dinero("Venta con costo", u.VentaCentavos);
            Dinero("Costo", -u.CostoCentavos);
            Dinero("Utilidad bruta", u.UtilidadCentavos, negrita: true);
            if (u.MargenCentesimas is { } margen)
                Linea("Margen", Nucleo.Catalogo.Margen.Texto(margen));
            if (d.PorDepartamento?.ArticuloComunCentavos is { } comun and not 0)
                Texto($"Sin el artículo común ({DisenoTicket.Dinero(comun)}): no tiene costo.");
            if (u.ServiciosSinCostoCentavos != 0)
                Texto($"Los servicios ({DisenoTicket.Dinero(u.ServiciosSinCostoCentavos)}) aún no tienen costo: cuentan completos.");
        }
        return e;
    }

    private static readonly string[] DiasCortos = ["dom", "lun", "mar", "mié", "jue", "vie", "sáb"];

    /// <summary>
    /// El resumen de «Ventas por período» (entrega 9, parte 2, 2026-10-22), como eleventa lo manda a la impresora de tickets pero sin
    /// cada ticket (eleventa advierte que gasta mucho papel): de qué días y de qué caja, lo de cada día (los tickets y lo vendido; lo
    /// devuelto y las formas de pago que no son efectivo, si las hubo), las formas de pago de todo el periodo y los totales, como en
    /// la pantalla: lo vendido, lo devuelto, la venta neta y las cancelaciones de esos días (como en el corte del día: el ticket
    /// cancelado ya no está en lo vendido).
    /// </summary>
    /// <param name="caja">La caja que se eligió; vacía: todas.</param>
    /// <param name="impresoPor">Quien lo imprime.</param>
    /// <param name="impresoLocal">Cuándo (hora de México).</param>
    public static List<Elemento> VentasPorPeriodo(VentasPorPeriodoDto d, string? caja, string impresoPor, DateTime impresoLocal)
    {
        const int anchoMonto = 14;
        var e = new List<Elemento>
        {
            new Renglon("VENTAS POR PERÍODO", Alineacion.Centro, Negrita: true, Grande: true),
            new Renglon(d.Desde == d.Hasta
                ? d.Desde.ToString("dddd dd/MM/yyyy", Mexico)
                : $"Del {d.Desde.ToString("dd/MM/yyyy", Mexico)} al {d.Hasta.ToString("dd/MM/yyyy", Mexico)}", Alineacion.Centro),
            // En el piloto, aquí solo está lo de la caja 2: lo de las demás cajas está en eleventa.
            new Renglon(d.Piloto ? "Solo esta caja (lo demás, en eleventa)" : caja ?? "Todas las cajas", Alineacion.Centro),
            new Renglon(new string('-', DisenoTicket.Columnas)),
            new Renglon($"Impreso: {impresoLocal.ToString("dd/MM/yyyy  HH:mm", Mexico)}"),
        };
        void Texto(string texto, bool negrita = false)
        {
            foreach (var parte in DisenoTicket.Partir(texto, DisenoTicket.Columnas))
                e.Add(new Renglon(parte, Negrita: negrita));
        }
        void Linea(string concepto, string monto, bool negrita = false) =>
            e.Add(new Renglon(Ajustar(concepto, DisenoTicket.Columnas - anchoMonto).PadRight(DisenoTicket.Columnas - anchoMonto) + monto.PadLeft(anchoMonto),
                Negrita: negrita));
        void Dinero(string concepto, long centavos, bool negrita = false) => Linea(concepto, DisenoTicket.Dinero(centavos), negrita);
        void Seccion(string titulo)
        {
            e.Add(new Renglon(new string('-', DisenoTicket.Columnas)));
            e.Add(new Renglon(titulo, Negrita: true));
        }
        Texto($"Lo imprimió: {impresoPor}");

        Seccion("POR DÍA");
        if (d.Dias.Count == 0)
            e.Add(new Renglon("No hubo ventas en esos días."));
        else
            Linea($"{"Día",-21}{"Tickets",7}", "Vendido");
        foreach (var dia in d.Dias)
        {
            var fecha = dia.Fecha!.Value;
            var nombre = $"{DiasCortos[(int)fecha.DayOfWeek]} {fecha.ToString("dd/MM", Mexico)}";
            Linea($"{nombre,-21}{dia.Tickets.ToString("N0", Mexico),7}", DisenoTicket.Dinero(dia.VendidoCentavos));
            // Las formas, solo si no fue todo en efectivo (casi siempre lo es).
            if (dia.Formas.Any(f => f.Forma != Nucleo.Ventas.FormaPago.Efectivo))
                foreach (var forma in dia.Formas)
                    Dinero($"  {NombreForma(forma.Forma)}", forma.Centavos);
            if (dia.Devoluciones > 0)
                Dinero($"  Devuelto ({dia.Devoluciones})", -dia.DevueltoCentavos);
        }

        var t = d.Totales;
        Seccion("POR FORMA DE PAGO");
        if (t.Formas.Count == 0)
            e.Add(new Renglon("Nada cobrado en esos días."));
        foreach (var forma in t.Formas)
            Dinero(NombreForma(forma.Forma), forma.Centavos);
        if (t.Formas.Any(f => f.Forma == Nucleo.Ventas.FormaPago.Efectivo))
            Texto("El efectivo, sin el redondeo al peso.");

        Seccion("TOTALES");
        Linea("Tickets", t.Tickets.ToString("N0", Mexico));
        Dinero("Vendido", t.VendidoCentavos, negrita: true);
        Dinero($"Devoluciones ({t.Devoluciones})", -t.DevueltoCentavos);
        Dinero("Venta neta", t.VentaCentavos, negrita: true);
        if (t.Cancelados > 0)
        {
            Dinero($"Cancelaciones ({t.Cancelados})", t.CanceladosCentavos);
            Texto("El ticket cancelado ya no cuenta en lo vendido.");
        }
        return e;
    }

    /// <summary>Lo que no cabe en su lugar se corta (los nombres van completos en la pantalla y en el Excel).</summary>
    private static string Ajustar(string texto, int ancho) => texto.Length < ancho ? texto : texto[..(ancho - 1)];

    private static string NombreForma(Nucleo.Ventas.FormaPago forma) => forma switch
    {
        Nucleo.Ventas.FormaPago.Efectivo => "Efectivo",
        Nucleo.Ventas.FormaPago.Tarjeta => "Tarjeta",
        Nucleo.Ventas.FormaPago.Transferencia => "Transferencia",
        _ => "Crédito (fiado)",
    };

    /// <summary>«09:00», o con la fecha si no es del día del corte (un turno que viene de otro día).</summary>
    private static string Hora(DateTime local, DateOnly dia) =>
        local.ToString(DateOnly.FromDateTime(local) == dia ? "HH:mm" : "dd/MM HH:mm", Mexico);

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
