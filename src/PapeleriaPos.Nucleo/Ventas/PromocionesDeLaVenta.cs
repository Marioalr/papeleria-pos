using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Ventas;

/// <summary>
/// De dónde saca una venta las promociones de cada producto y con qué día las aplica (fase 3). La caja las saca de su copia y
/// lee el día de su reloj cada vez que calcula un precio: una promoción que empieza o termina a medianoche se aplica (o deja de
/// aplicarse) sin bajar nada, también sin conexión. El servicio, de su catálogo, con el día del cobro.
/// </summary>
public sealed class PromocionesDeLaVenta(Func<Guid, IReadOnlyList<PromocionDeProducto>> deLaVariante, Func<DateOnly> dia)
{
    /// <summary>Las de un producto (su variante), corran o no ese día: el cálculo del precio decide con <see cref="Dia"/>.</summary>
    public IReadOnlyList<PromocionDeProducto> De(Guid varianteId) => deLaVariante(varianteId);

    /// <summary>El día (hora de México) con que se aplican.</summary>
    public DateOnly Dia => dia();
}
