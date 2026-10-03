using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>Crea los movimientos del kardex con sus reglas. Las cantidades se pasan positivas; el signo lo pone el tipo.</summary>
public static class Kardex
{
    /// <summary>Una entrada lleva su costo real (el de la compra, el del traspaso, el del conteo inicial).</summary>
    public static MovimientoInventario Entrada(
        Guid sucursalId, Variante variante, TipoMovimiento tipo, Cantidad cantidad, long costoCentavos,
        DateTime fechaUtc, string? motivo = null, string? documentoTipo = null, Guid? documentoId = null)
    {
        if (!tipo.PuedeEntrar())
            throw new ArgumentException($"Un movimiento de tipo {tipo} no puede ser entrada.", nameof(tipo));
        if (costoCentavos < 0)
            throw new ArgumentOutOfRangeException(nameof(costoCentavos), "El costo de una entrada no puede ser negativo.");
        Validar(variante, tipo, cantidad, fechaUtc, motivo);

        return new MovimientoInventario
        {
            SucursalId = sucursalId,
            VarianteId = variante.Id,
            FechaUtc = fechaUtc,
            Tipo = tipo,
            Cantidad = cantidad,
            CostoCentavos = costoCentavos,
            Motivo = motivo,
            DocumentoTipo = documentoTipo,
            DocumentoId = documentoId,
        };
    }

    /// <summary>
    /// Una salida lleva el costo promedio de la sucursal por la cantidad. Sin existencia (se permite vender sin ella),
    /// usa el último costo conocido.
    /// </summary>
    public static MovimientoInventario Salida(
        Guid sucursalId, Variante variante, TipoMovimiento tipo, Cantidad cantidad, Existencia existencia,
        long ultimoCostoCentavos, DateTime fechaUtc, string? motivo = null, string? documentoTipo = null,
        Guid? documentoId = null)
    {
        if (!tipo.PuedeSalir())
            throw new ArgumentException($"Un movimiento de tipo {tipo} no puede ser salida.", nameof(tipo));
        if (existencia.SucursalId != sucursalId || existencia.VarianteId != variante.Id)
            throw new ArgumentException("La existencia es de otra sucursal o variante.", nameof(existencia));
        Validar(variante, tipo, cantidad, fechaUtc, motivo);

        return new MovimientoInventario
        {
            SucursalId = sucursalId,
            VarianteId = variante.Id,
            FechaUtc = fechaUtc,
            Tipo = tipo,
            Cantidad = -cantidad,
            CostoCentavos = -CostoDeSalida(existencia, cantidad, ultimoCostoCentavos),
            Motivo = motivo,
            DocumentoTipo = documentoTipo,
            DocumentoId = documentoId,
        };
    }

    /// <summary>
    /// Costo promedio × cantidad, redondeado al centavo. Si sale todo lo que hay, sale todo el valor y el inventario
    /// queda en cero exacto.
    /// </summary>
    public static long CostoDeSalida(Existencia existencia, Cantidad cantidad, long ultimoCostoCentavos)
    {
        if (existencia.Cantidad.EsPositiva)
            return Redondeo.Dividir((Int128)existencia.CostoCentavos * cantidad.Milesimas, existencia.Cantidad.Milesimas);
        return cantidad.Importe(ultimoCostoCentavos);
    }

    private static void Validar(Variante variante, TipoMovimiento tipo, Cantidad cantidad, DateTime fechaUtc, string? motivo)
    {
        if (!cantidad.EsPositiva)
            throw new ArgumentOutOfRangeException(nameof(cantidad), "La cantidad debe ser positiva; el signo lo pone el tipo.");
        if (!variante.AdmiteCantidad(cantidad))
            throw new ArgumentException($"Una variante por {variante.UnidadBase} no admite la cantidad {cantidad}.", nameof(cantidad));
        if (fechaUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("La fecha debe estar en UTC.", nameof(fechaUtc));
        if (tipo.RequiereMotivo() && string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException($"Un movimiento de tipo {tipo} requiere motivo.", nameof(motivo));
    }
}
