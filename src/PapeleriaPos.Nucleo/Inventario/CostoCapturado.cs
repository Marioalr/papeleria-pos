namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>
/// El costo de un producto escrito a mano en su ficha (F3), sin que llegue mercancía: el dueño, 2026-11-16, al crear un producto
/// («¿existe la casilla del costo?»). Cuenta como el «último costo» (<c>UltimosCostos</c>) desde que se escribe, igual que lo
/// último agregado con «Agregar inventario»: así el producto nuevo tiene margen y precio sugerido antes de su primera compra. No
/// cambia la existencia ni su costo promedio, que siguen saliendo de lo que entra. Cada vez que se cambia queda otro, con quién
/// y cuándo (no se corrige: se escribe otro).
/// </summary>
public class CostoCapturado
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid VarianteId { get; init; }

    /// <summary>El costo de cada uno, por unidad base, con IVA (lo que se paga), como el de «Agregar inventario».</summary>
    public long CostoUnitarioCentavos { get; init; }

    public Guid UsuarioId { get; init; }
    public DateTime FechaUtc { get; init; }
}
