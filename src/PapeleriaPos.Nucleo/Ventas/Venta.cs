using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Ventas;

public enum EstadoVenta
{
    EnCaptura,
    /// <summary>F6: se deja para después (el cliente que regresa por algo más).</summary>
    Pendiente,
    Pagada,
    Cancelada,
}

/// <summary>Lo que la venta necesita saber de un producto del catálogo.</summary>
public sealed record ProductoEnVenta(Guid VarianteId, string Descripcion, Impuesto Impuesto, UnidadBase Unidad, bool LlevaInventario);

/// <summary>
/// Un ticket. Se arma en la caja (agregar, cambiar cantidad, quitar) y al cobrar recibe su folio. Los precios ya traen
/// IVA; el IVA solo se desglosa.
/// </summary>
public class Venta
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }
    public Guid CajaId { get; set; }
    public Guid TurnoId { get; set; }
    public Guid CajeroId { get; set; }

    public required string Serie { get; set; }

    /// <summary>Consecutivo de la serie; vacío hasta que se cobra.</summary>
    public int? Folio { get; private set; }

    public EstadoVenta Estado { get; set; } = EstadoVenta.EnCaptura;
    public DateTime CreadaUtc { get; set; }
    public DateTime? PagadaUtc { get; private set; }

    public DateTime? CanceladaUtc { get; private set; }
    public Guid? CanceladaPorId { get; private set; }
    public string? MotivoCancelacion { get; private set; }

    public long TotalCentavos { get; private set; }

    /// <summary>
    /// Lo que se cobró de más (+) o de menos (−) al redondear al peso el efectivo (fase 3). El total y el IVA siguen
    /// exactos: el redondeo va aparte en el ticket y en el corte.
    /// </summary>
    public long RedondeoCentavos { get; private set; }

    /// <summary>Lo que pagó el cliente: el total con el redondeo del efectivo.</summary>
    public long TotalCobradoCentavos => TotalCentavos + RedondeoCentavos;

    /// <summary>IVA incluido en el total.</summary>
    public long IvaCentavos { get; private set; }

    public List<VentaLinea> Lineas { get; set; } = [];
    public List<Cobro> Cobros { get; set; } = [];

    /// <summary>Cobrada en una caja sin conexión, con su copia del catálogo y su propio folio.</summary>
    public bool EnContingencia { get; set; }

    /// <summary>
    /// Cuándo llegó con la bandeja, si su turno ya tenía corte Z: no entró a ese corte (que no se cambia) y queda para
    /// revisarla. Ya estaba cobrada, así que no se rechaza.
    /// </summary>
    public DateTime? FueraDeCorteUtc { get; set; }

    public string? FolioTexto => Folio is { } f ? $"{Serie}-{f:000000}" : null;

    /// <summary>
    /// La nota del ticket: F4 al cobrar, como en eleventa (2026-10-22). Sale en el ticket y en sus reimpresiones, y en «Ventas
    /// del día». Vacía si no lleva (<see cref="NotaDelTicket"/>).
    /// </summary>
    public string? Nota { get; private set; }

    /// <summary>
    /// Su ticket no ha salido nunca (2026-10-22): se cobró con «Al cobrar» en «Ofrecerlo» o «No imprimirlo» y no se pidió, o
    /// no salió de la impresora y se dejó con «No imprimirlo». Su primera impresión no es una reimpresión: no pide motivo, y
    /// después de ella las demás sí. Lo de una caja sin conexión llega como impreso: ahí no se sabe si salió después, y
    /// reimprimirlo pide motivo, como siempre.
    /// </summary>
    public bool TicketSinImprimir { get; set; }

    /// <summary>
    /// Cuántas veces salió su ticket después del cobro, según la caja principal: su primera impresión (desde «Ventas del día» o
    /// con Ctrl+I) y cada reimpresión. «No imprimirlo» solo vuelve a marcar <see cref="TicketSinImprimir"/> si no salió otra
    /// después del trabajo que no salió (revisión del PR #133): si mientras tanto se reimprimió, ya salió.
    /// </summary>
    public int TicketImpresiones { get; set; }

    /// <summary>Pone (o quita, vacía) la nota del ticket, de uno o dos renglones (<see cref="NotaDelTicket.Maximo"/>).</summary>
    public void PonerNota(string? nota)
    {
        RequerirEditable();
        Nota = NotaDelTicket.Normalizar(nota);
    }

    /// <summary>Artículos para el ticket: piezas por cantidad; lo que se vende por metro o a granel cuenta uno por línea.</summary>
    public decimal NumeroDeArticulos => Lineas.Sum(l => l.Cantidad.EsEntera ? l.Cantidad.EnUnidades : 1);

    public bool EsEditable => Estado is EstadoVenta.EnCaptura or EstadoVenta.Pendiente;

    /// <summary>
    /// Agrega un producto. Si ya está en el ticket con la misma presentación, suma la cantidad (y puede alcanzar otra
    /// escala de mayoreo).
    /// </summary>
    public VentaLinea AgregarProducto(ProductoEnVenta producto, Presentacion presentacion, Cantidad cantidad, IReadOnlyList<Precio> precios)
    {
        RequerirEditable();
        var existente = Lineas.FirstOrDefault(l => l.VarianteId == producto.VarianteId && l.PresentacionId == presentacion.Id);
        if (existente is not null)
        {
            CambiarCantidad(existente, existente.Cantidad + cantidad, presentacion, precios);
            return existente;
        }

        var linea = new VentaLinea
        {
            VentaId = Id,
            VarianteId = producto.VarianteId,
            PresentacionId = presentacion.Id,
            Descripcion = producto.Descripcion,
            Impuesto = producto.Impuesto,
            Unidad = producto.Unidad,
            LlevaInventario = producto.LlevaInventario,
        };
        Lineas.Add(linea);
        try
        {
            CambiarCantidad(linea, cantidad, presentacion, precios);
        }
        catch
        {
            Lineas.Remove(linea);
            throw;
        }
        return linea;
    }

    /// <summary>Cambia la cantidad de una línea y vuelve a buscar el precio que le toca.</summary>
    public void CambiarCantidad(VentaLinea linea, Cantidad cantidad, Presentacion presentacion, IReadOnlyList<Precio> precios)
    {
        RequerirEditable();
        if (!cantidad.EsPositiva)
            throw new ArgumentException("La cantidad debe ser positiva.");
        var cantidadBase = cantidad.Por(presentacion.Factor);
        if (linea.Unidad == UnidadBase.Pieza && !cantidadBase.EsEntera)
            throw new ArgumentException("Este producto se vende por pieza: no admite fracciones.");

        var aplicado = CalculoPrecio.Calcular(precios, SucursalId, presentacion, cantidad, MayoreoDeCliente)
            ?? throw new InvalidOperationException($"«{linea.Descripcion}» no tiene precio.");

        linea.Cantidad = cantidad;
        linea.CantidadBase = cantidadBase;
        if (linea.PrecioCambiado)
        {
            // Con precio cambiado (autorizado), la cantidad nueva se cobra a ese precio y ya no es mayoreo.
            AplicarDescuento(linea, cantidad.Importe(linea.PrecioCentavos));
            (linea.MayoreoDesde, linea.AhorroMayoreoCentavos) = (null, 0);
        }
        else
        {
            AplicarDescuento(linea, aplicado.ImporteCentavos);
            linea.PrecioCentavos = aplicado.Precio.PresentacionId is null
                ? presentacion.Factor.Importe(aplicado.Precio.PrecioCentavos)
                : aplicado.Precio.PrecioCentavos;
            // Queda en la línea desde cuánto aplicó el mayoreo y cuánto se ahorró: el ticket y la reimpresión lo dicen.
            linea.MayoreoDesde = !aplicado.EsMayoreo ? null
                : aplicado.Precio.PresentacionId is null ? aplicado.Precio.CantidadMinima : aplicado.Precio.CantidadMinima.Por(presentacion.Factor);
            linea.AhorroMayoreoCentavos = aplicado.AhorroCentavos;
        }
        Recalcular();
    }

    /// <summary>
    /// El cliente registrado del ticket (fase 3, entrega 2), con descuento o sin él. Su descuento fijo ya lo aprobó quien
    /// le dio el privilegio.
    /// </summary>
    public Guid? ClienteId { get; set; }

    /// <summary>
    /// El cliente del ticket paga mayoreo siempre: cada línea toma al menos la primera escala de mayoreo. No se guarda: lo
    /// pone quien arma la venta (la caja, la caja sin conexión o el servicio), antes de agregar las líneas.
    /// </summary>
    public bool MayoreoDeCliente { get; set; }

    /// <summary>
    /// Cambia si el ticket va con el mayoreo del cliente y vuelve a buscar el precio de cada producto (los de precio
    /// cambiado se quedan como están). <paramref name="catalogo"/> da la presentación y los precios de una línea.
    /// </summary>
    public void CambiarMayoreoDeCliente(bool mayoreo, Func<VentaLinea, (Presentacion Presentacion, IReadOnlyList<Precio> Precios)?> catalogo)
    {
        RequerirEditable();
        MayoreoDeCliente = mayoreo;
        foreach (var linea in Lineas.Where(l => l.VarianteId is not null && !l.EsImpresiones).ToList())
        {
            if (catalogo(linea) is { } c)
                CambiarCantidad(linea, linea.Cantidad, c.Presentacion, c.Precios);
        }
    }

    /// <summary>La autorización del descuento más alto del ticket (la regla se revisa al cobrar).</summary>
    public Guid? AutorizacionDescuentoId { get; set; }

    /// <summary>La autorización para que un descuento o un precio cambiado deje líneas debajo del costo.</summary>
    public Guid? AutorizacionBajoCostoId { get; set; }

    /// <summary>La autorización para fiar arriba del límite del cliente (la regla se revisa al cobrar).</summary>
    public Guid? AutorizacionCreditoId { get; set; }

    /// <summary>Lo fiado en este ticket: el cobro a crédito. La venta ya cuenta completa; esto es lo que no entró a la caja.</summary>
    public long CreditoCentavos => Cobros.Where(c => c.Forma == FormaPago.Credito).Sum(c => c.MontoCentavos);

    public long DescuentoCentavos => Lineas.Sum(l => l.DescuentoCentavos);

    /// <summary>
    /// Descuento a una línea, en porcentaje (centésimas: 1000 = 10 %) o en monto, siempre con motivo. El porcentaje se
    /// vuelve a aplicar si cambia la cantidad. Las impresiones no llevan descuento (su precio especial va aparte).
    /// </summary>
    public void DescontarLinea(VentaLinea linea, int? centesimas, long? montoCentavos, string motivo)
    {
        RequerirEditable();
        if (linea.EsImpresiones)
            throw new InvalidOperationException("Las impresiones no llevan descuento: su precio especial va aparte.");
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("El descuento lleva motivo.");
        if ((centesimas is null) == (montoCentavos is null))
            throw new ArgumentException("El descuento va en porcentaje o en monto.");
        if (centesimas is <= 0 or > 10000)
            throw new ArgumentException("El porcentaje va de más de 0 a 100 %.");
        var bruto = linea.ImporteSinDescuentoCentavos;
        if (montoCentavos is { } monto && (monto <= 0 || monto > bruto))
            throw new ArgumentException($"El descuento de «{linea.Descripcion}» va de más de $0 a {bruto / 100m:C2}.");
        (linea.DescuentoCentesimas, linea.DescuentoCentavos, linea.MotivoDescuento) = (centesimas, montoCentavos ?? 0, motivo.Trim());
        AplicarDescuento(linea, bruto);
        Recalcular();
    }

    /// <summary>
    /// Descuento a todo el ticket (menos las impresiones). En porcentaje, cada línea lleva el mismo; en monto, se reparte
    /// en proporción al importe de cada línea (lo que sobra del redondeo va a la más grande).
    /// </summary>
    public void DescontarTicket(int? centesimas, long? montoCentavos, string motivo)
    {
        RequerirEditable();
        var lineas = Lineas.Where(l => !l.EsImpresiones).ToList();
        if (lineas.Count == 0)
            throw new InvalidOperationException("No hay artículos a los que hacer descuento.");
        if (centesimas is not null)
        {
            foreach (var l in lineas)
                DescontarLinea(l, centesimas, null, motivo);
            return;
        }
        if (montoCentavos is not { } monto)
            throw new ArgumentException("El descuento va en porcentaje o en monto.");
        var total = lineas.Sum(l => l.ImporteSinDescuentoCentavos);
        if (monto <= 0 || monto > total)
            throw new ArgumentException($"El descuento va de más de $0 a {total / 100m:C2}.");
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("El descuento lleva motivo.");
        var partes = lineas.ToDictionary(l => l, l => (long)(Int128.CreateTruncating(monto) * l.ImporteSinDescuentoCentavos / total));
        partes[lineas.MaxBy(l => l.ImporteSinDescuentoCentavos)!] += monto - partes.Values.Sum();
        foreach (var (l, parte) in partes)
        {
            if (parte > 0)
                DescontarLinea(l, null, parte, motivo);
            else
                QuitarDescuento(l);
        }
    }

    public void QuitarDescuento(VentaLinea linea)
    {
        RequerirEditable();
        var bruto = linea.ImporteSinDescuentoCentavos;
        (linea.DescuentoCentesimas, linea.DescuentoCentavos, linea.MotivoDescuento) = (null, 0, null);
        linea.ImporteCentavos = bruto;
        Recalcular();
    }

    /// <summary>El descuento que mandó la caja para una línea (porcentaje o monto); sin ninguno, no hace nada.</summary>
    public void AplicarDescuentoSolicitado(VentaLinea linea, int? centesimas, long? montoCentavos, string? motivo)
    {
        if (centesimas is not null)
            DescontarLinea(linea, centesimas, null, motivo ?? "");
        else if (montoCentavos is > 0)
            DescontarLinea(linea, null, montoCentavos, motivo ?? "");
    }

    /// <summary>Quita los descuentos (todos, o los que no tienen <paramref name="salvoMotivo"/>) y su autorización. El cliente se queda.</summary>
    public void QuitarDescuentos(string? salvoMotivo = null)
    {
        foreach (var l in Lineas.Where(l => l.TieneDescuento && (salvoMotivo is null || l.MotivoDescuento != salvoMotivo)))
            QuitarDescuento(l);
        AutorizacionDescuentoId = null;
    }

    /// <summary>
    /// El descuento más alto del ticket en centésimas de porcentaje, sin contar las líneas que llevan exactamente el del
    /// cliente registrado (<paramref name="preaprobado"/>): ese ya está aprobado. Cero si no hay nada que revisar.
    /// </summary>
    public int DescuentoARevisar(int? preaprobado) =>
        Lineas.Where(l => l.TieneDescuento && (preaprobado is null || l.DescuentoCentesimas != preaprobado))
            .Select(l => l.PorcentajeDescuento).DefaultIfEmpty(0).Max();

    /// <summary>El importe sale del bruto menos el descuento (el porcentaje se recalcula; el monto no pasa del bruto).</summary>
    private static void AplicarDescuento(VentaLinea linea, long bruto)
    {
        linea.DescuentoCentavos = linea.DescuentoCentesimas is { } c
            ? Redondeo.Dividir((Int128)bruto * c, 10000)
            : Math.Min(linea.DescuentoCentavos, bruto);
        linea.ImporteCentavos = bruto - linea.DescuentoCentavos;
    }

    /// <summary>
    /// Cambia el precio de una línea (por presentación, con IVA). Requiere autorización según las reglas: la venta solo
    /// guarda cuál; el servicio la revisa y la gasta al cobrar.
    /// </summary>
    public void CambiarPrecio(VentaLinea linea, long precioCentavos, Guid? autorizacionId)
    {
        RequerirEditable();
        if (precioCentavos <= 0)
            throw new ArgumentException("El precio debe ser mayor a cero.");
        if (linea.VarianteId is null)
            throw new InvalidOperationException("El artículo común ya lleva el precio que se escribió: bórralo y agrégalo de nuevo.");
        linea.PrecioOriginalCentavos ??= linea.PrecioCentavos;
        linea.PrecioCentavos = precioCentavos;
        AplicarDescuento(linea, linea.Cantidad.Importe(precioCentavos));
        linea.AutorizacionPrecioId = autorizacionId;
        (linea.MayoreoDesde, linea.AhorroMayoreoCentavos) = (null, 0);
        Recalcular();
    }

    /// <summary>Cancela un ticket cobrado. El reembolso y el inventario los mueve quien lo guarda.</summary>
    public void Cancelar(Guid usuarioId, string motivo, DateTime ahoraUtc)
    {
        if (Estado != EstadoVenta.Pagada)
            throw new InvalidOperationException("Solo se cancela un ticket cobrado.");
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("La cancelación lleva motivo.");
        Estado = EstadoVenta.Cancelada;
        CanceladaUtc = ahoraUtc;
        CanceladaPorId = usuarioId;
        MotivoCancelacion = motivo.Trim();
    }

    /// <summary>
    /// Las impresiones que se cobran en el ticket, en una sola línea (docs/fase-1-impresion.md). Si ya había, se
    /// reemplaza: la caja la vuelve a armar cada vez que se agregan o quitan trabajos. Sin trabajos, se quita.
    /// </summary>
    public VentaLinea? FijarImpresiones(long precioCentavos, long costoCentavos)
    {
        RequerirEditable();
        if (Lineas.FirstOrDefault(l => l.EsImpresiones) is { } anterior)
            Lineas.Remove(anterior);
        if (precioCentavos <= 0)
        {
            Recalcular();
            return null;
        }
        var linea = new VentaLinea
        {
            VentaId = Id,
            Descripcion = "Impresiones",
            Impuesto = Impuesto.Iva16,
            Unidad = UnidadBase.Pieza,
            Cantidad = Cantidad.Piezas(1),
            CantidadBase = Cantidad.Piezas(1),
            PrecioCentavos = precioCentavos,
            ImporteCentavos = precioCentavos,
            CostoCentavos = costoCentavos,
            EsImpresiones = true,
        };
        Lineas.Add(linea);
        Recalcular();
        return linea;
    }

    /// <summary>Ctrl+P: artículo común, con descripción y precio a mano. No descuenta inventario.</summary>
    public VentaLinea AgregarArticuloComun(string descripcion, long precioCentavos, Cantidad cantidad)
    {
        RequerirEditable();
        if (string.IsNullOrWhiteSpace(descripcion))
            throw new ArgumentException("El artículo común necesita una descripción.");
        if (precioCentavos <= 0 || !cantidad.EsPositiva)
            throw new ArgumentException("Precio y cantidad deben ser positivos.");

        var linea = new VentaLinea
        {
            VentaId = Id,
            Descripcion = descripcion.Trim(),
            Impuesto = Impuesto.Iva16,
            Unidad = UnidadBase.Pieza,
            Cantidad = cantidad,
            CantidadBase = cantidad,
            PrecioCentavos = precioCentavos,
            ImporteCentavos = cantidad.Importe(precioCentavos),
        };
        Lineas.Add(linea);
        Recalcular();
        return linea;
    }

    public void QuitarLinea(VentaLinea linea)
    {
        RequerirEditable();
        Lineas.Remove(linea);
        Recalcular();
    }

    /// <summary>
    /// F12: aplica los cobros y deja la venta pagada con su folio. Tarjeta y transferencia no pueden pasar del total;
    /// el efectivo cubre lo que falta, redondeado al peso (<see cref="Efectivo"/>), y da el cambio.
    /// </summary>
    /// <param name="redondear">Falso solo para lo que se cobró sin conexión antes de que existiera el redondeo.</param>
    public void Pagar(int folio, IReadOnlyList<CobroSolicitado> cobros, DateTime ahoraUtc, bool redondear = true)
    {
        if (ahoraUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("La fecha debe estar en UTC.", nameof(ahoraUtc));
        var (efectivo, recibido) = Revisar(cobros, redondear);

        Cobros = cobros.Where(c => c.Forma != FormaPago.Efectivo).Select(c => new Cobro
        {
            VentaId = Id,
            Forma = c.Forma,
            MontoCentavos = c.MontoCentavos,
            Referencia = c.Referencia,
            Verificacion = c.Forma == FormaPago.Transferencia ? Verificacion.PorVerificar : Verificacion.NoAplica,
        }).ToList();
        if (efectivo.ACobrarCentavos > 0)
            Cobros.Insert(0, new Cobro
            {
                VentaId = Id,
                Forma = FormaPago.Efectivo,
                MontoCentavos = efectivo.ACobrarCentavos,
                RecibidoCentavos = recibido,
                CambioCentavos = recibido - efectivo.ACobrarCentavos,
            });

        RedondeoCentavos = efectivo.RedondeoCentavos;
        Folio = folio;
        PagadaUtc = ahoraUtc;
        Estado = EstadoVenta.Pagada;
    }

    /// <summary>
    /// Revisa que los cobros alcancen sin cambiar nada. Devuelve lo que falta cubrir en efectivo (ya redondeado) y lo
    /// recibido.
    /// </summary>
    public (long Faltante, long Recibido) ValidarCobros(IReadOnlyList<CobroSolicitado> cobros, bool redondear = true)
    {
        var (efectivo, recibido) = Revisar(cobros, redondear);
        return (efectivo.ACobrarCentavos, recibido);
    }

    /// <summary>
    /// Lo que falta pagar en efectivo después de la tarjeta y la transferencia: al peso más cercano y, si falta algo,
    /// al menos $1 (fase 3). Las impresiones no se redondean (tienen sus propias reglas): el efectivo cubre primero la
    /// mercancía, que se redondea, y lo que queda de impresiones va exacto. La tarjeta y la transferencia van exactas.
    /// La ventana de cobro usa esto mismo para mostrar cuánto pedir.
    /// </summary>
    public EfectivoACobrar Efectivo(long sinEfectivoCentavos, bool redondear = true)
    {
        var exacto = Math.Max(0, TotalCentavos - sinEfectivoCentavos);
        if (!redondear || exacto == 0)
            return new EfectivoACobrar(exacto, exacto);
        var mercancia = Math.Min(exacto, Lineas.Where(l => !l.EsImpresiones).Sum(l => l.ImporteCentavos));
        var aCobrar = exacto - mercancia + Redondeo.AlPeso(mercancia);
        return new EfectivoACobrar(exacto, aCobrar == 0 ? 100 : aCobrar);
    }

    private (EfectivoACobrar Efectivo, long Recibido) Revisar(IReadOnlyList<CobroSolicitado> cobros, bool redondear)
    {
        RequerirEditable();
        if (Lineas.Count == 0)
            throw new InvalidOperationException("No hay nada que cobrar.");
        if (cobros.Any(c => c.MontoCentavos <= 0))
            throw new ArgumentException("Cada cobro debe ser mayor a cero.");
        if (cobros.Count(c => c.Forma == FormaPago.Efectivo) > 1)
            throw new ArgumentException("El efectivo va en un solo cobro.");
        if (cobros.Count(c => c.Forma == FormaPago.Credito) > 1)
            throw new ArgumentException("El crédito va en un solo cobro.");
        if (cobros.Any(c => c.Forma == FormaPago.Credito) && ClienteId is null)
            throw new InvalidOperationException("El crédito es para un cliente registrado: elígelo con CTRL+L.");

        // El crédito, como la tarjeta, va exacto: lo que no es efectivo no se redondea ni da cambio.
        var sinEfectivo = cobros.Where(c => c.Forma != FormaPago.Efectivo).Sum(c => c.MontoCentavos);
        if (sinEfectivo > TotalCentavos)
            throw new InvalidOperationException("Tarjeta, transferencia y crédito no pueden pasar del total: no dan cambio.");

        var efectivo = Efectivo(sinEfectivo, redondear);
        var recibido = cobros.SingleOrDefault(c => c.Forma == FormaPago.Efectivo)?.MontoCentavos ?? 0;
        if (recibido < efectivo.ACobrarCentavos)
            throw new InvalidOperationException($"Faltan {(efectivo.ACobrarCentavos - recibido) / 100m:C2} por cobrar.");
        return (efectivo, recibido);
    }

    public long CambioCentavos => Cobros.Sum(c => c.CambioCentavos);

    public bool LlevaEfectivo => Cobros.Any(c => c.Forma == FormaPago.Efectivo);

    private void Recalcular()
    {
        foreach (var l in Lineas)
            l.IvaCentavos = l.Impuesto == Impuesto.Iva16 ? l.ImporteCentavos - Redondeo.Dividir(l.ImporteCentavos * 100L, 116) : 0;
        TotalCentavos = Lineas.Sum(l => l.ImporteCentavos);
        IvaCentavos = Lineas.Sum(l => l.IvaCentavos);
    }

    private void RequerirEditable()
    {
        if (!EsEditable)
            throw new InvalidOperationException($"La venta ya está {Estado.ToString().ToLowerInvariant()}: no se puede cambiar.");
    }
}

public class VentaLinea
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid VentaId { get; set; }

    /// <summary>Vacía en el artículo varios o común.</summary>
    public Guid? VarianteId { get; set; }

    public Guid? PresentacionId { get; set; }

    /// <summary>Como se vendió: si después cambia el nombre en el catálogo, el ticket no cambia.</summary>
    public required string Descripcion { get; set; }

    public Impuesto Impuesto { get; set; }
    public UnidadBase Unidad { get; set; }
    public bool LlevaInventario { get; set; }

    /// <summary>En presentaciones (2 paquetes, 1.5 metros).</summary>
    public Cantidad Cantidad { get; set; }

    /// <summary>En unidad base (24 piezas): lo que sale del inventario.</summary>
    public Cantidad CantidadBase { get; set; }

    /// <summary>Precio de una presentación, con IVA.</summary>
    public long PrecioCentavos { get; set; }

    public long ImporteCentavos { get; set; }
    public long IvaCentavos { get; set; }

    /// <summary>Costo de lo vendido, del kardex al momento de cobrar.</summary>
    public long CostoCentavos { get; set; }

    /// <summary>El precio del catálogo, si se cambió en la venta.</summary>
    public long? PrecioOriginalCentavos { get; set; }

    public Guid? AutorizacionPrecioId { get; set; }

    /// <summary>Lo que ya se devolvió de esta línea, en presentaciones: no se puede devolver dos veces.</summary>
    public Cantidad CantidadDevuelta { get; set; }

    public bool PrecioCambiado => PrecioOriginalCentavos is not null;

    public Cantidad CantidadPorDevolver => Cantidad - CantidadDevuelta;

    /// <summary>La línea "Impresiones": junta los trabajos de impresión que se cobran en este ticket (sin inventario).</summary>
    public bool EsImpresiones { get; set; }

    /// <summary>Lo que se descontó del importe (ya restado de <see cref="ImporteCentavos"/>).</summary>
    public long DescuentoCentavos { get; set; }

    /// <summary>Si el descuento es en porcentaje: centésimas (1000 = 10 %). Vacío si es un monto.</summary>
    public int? DescuentoCentesimas { get; set; }

    public string? MotivoDescuento { get; set; }

    public bool TieneDescuento => DescuentoCentavos > 0;

    /// <summary>
    /// Desde qué cantidad aplicó la escala de mayoreo (fase 3), en unidades base (piezas, metros, kilos). Vacío si se
    /// cobró a menudeo o a un precio cambiado.
    /// </summary>
    public Cantidad? MayoreoDesde { get; set; }

    /// <summary>Lo que se ahorró por el mayoreo contra el menudeo, antes del descuento. No es un descuento: no pide autorización.</summary>
    public long AhorroMayoreoCentavos { get; set; }

    public bool TieneMayoreo => MayoreoDesde is not null;

    /// <summary>El mayoreo se aplicó antes de llegar a la cantidad: es el «mayoreo siempre» del cliente registrado.</summary>
    public bool MayoreoDeCliente => MayoreoDesde is { } desde && CantidadBase < desde;

    public long ImporteSinDescuentoCentavos => ImporteCentavos + DescuentoCentavos;

    /// <summary>El descuento como porcentaje del importe, en centésimas (redondeado hacia arriba: 10.001 % cuenta como más de 10 %).</summary>
    public int PorcentajeDescuento =>
        DescuentoCentesimas ?? (ImporteSinDescuentoCentavos == 0 ? 0 : (int)Math.Ceiling(DescuentoCentavos * 10000m / ImporteSinDescuentoCentavos));
}

