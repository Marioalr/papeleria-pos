using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>
/// Lo que llegó de un producto, agregado a su existencia: Inventario › «Agregar inventario», como en eleventa (2026-10-24). En
/// la tienda se usa todo el día (unas 7 al día), siempre de un producto, sin proveedor ni orden de compra, cada una con el
/// costo de cada uno. Es el documento de su movimiento del kardex (<see cref="TipoMovimiento.AgregarInventario"/>), con quién
/// lo hizo. Su Id lo pone la caja: si la misma petición llega dos veces (se perdió la respuesta), se agrega una. No se deshace:
/// lo equivocado se corrige con un ajuste, como en eleventa.
/// </summary>
public class EntradaInventario
{
    /// <summary>Lo más que se agrega de una vez, en la unidad del producto: más es un error de dedo (o un código escaneado ahí).</summary>
    public const int CantidadMaxima = 99_999;

    /// <summary>Lo más que vale cada uno, como los precios del catálogo.</summary>
    public const long CostoMaximoCentavos = CambioDePrecios.MaximoCentavos;

    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid SucursalId { get; init; }
    public Guid VarianteId { get; init; }

    /// <summary>Lo que llegó, en unidad base (siempre positivo).</summary>
    public Cantidad Cantidad { get; init; }

    /// <summary>El costo de cada uno, por unidad base: el que se escribió o, si no, el de ese momento (redondeado al centavo).</summary>
    public long CostoUnitarioCentavos { get; init; }

    /// <summary>Lo que vale lo que llegó: el costo de su movimiento del kardex.</summary>
    public long CostoCentavos { get; init; }

    /// <summary>Si quien lo agregó escribió el costo; si no, entró al costo de ese momento.</summary>
    public bool CostoEscrito { get; init; }

    public Guid UsuarioId { get; init; }
    public DateTime FechaUtc { get; init; }
}
