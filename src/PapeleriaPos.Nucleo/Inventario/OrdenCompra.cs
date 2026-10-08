namespace PapeleriaPos.Nucleo.Inventario;

public enum EstadoOrden
{
    /// <summary>Se está armando o ya se pidió y no ha llegado.</summary>
    Abierta,

    /// <summary>Llegó: una compra cerrada la recibió.</summary>
    Recibida,

    Cancelada,
}

/// <summary>
/// Lo que se le pide a un proveedor (docs/fase-2-inventario.md, entrega 2). Es opcional: al capturar la nota de una
/// compra se puede decir que viene de esta orden, y la comparación muestra también lo pedido. Quien cuenta no la ve.
/// </summary>
public class OrdenCompra
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid SucursalId { get; init; }
    public Guid ProveedorId { get; init; }

    /// <summary>Consecutivo de la sucursal: OC-000012.</summary>
    public int Folio { get; init; }

    public EstadoOrden Estado { get; private set; } = EstadoOrden.Abierta;
    public string? Notas { get; set; }
    public Guid UsuarioId { get; init; }
    public DateTime CreadaUtc { get; init; }

    /// <summary>La compra que la recibió.</summary>
    public Guid? CompraId { get; private set; }

    public List<OrdenRenglon> Renglones { get; init; } = [];

    public string FolioTexto => $"OC-{Folio:D6}";

    /// <summary>Pone lo que se pide de una presentación (cero la quita). Solo mientras está abierta.</summary>
    public void Pedir(Guid varianteId, Guid presentacionId, Cantidad cantidad, Cantidad factor)
    {
        if (Estado != EstadoOrden.Abierta)
            throw new InvalidOperationException("La orden ya no se puede cambiar.");
        if (cantidad.Milesimas < 0)
            throw new ArgumentException("La cantidad no puede ser negativa.");
        var renglon = Renglones.FirstOrDefault(r => r.PresentacionId == presentacionId);
        if (cantidad.Milesimas == 0)
        {
            if (renglon is not null)
                Renglones.Remove(renglon);
            return;
        }
        if (renglon is null)
        {
            renglon = new OrdenRenglon { OrdenId = Id, VarianteId = varianteId, PresentacionId = presentacionId };
            Renglones.Add(renglon);
        }
        renglon.Cantidad = cantidad;
        renglon.CantidadBase = cantidad.Por(factor);
    }

    public void Recibir(Guid compraId)
    {
        if (Estado != EstadoOrden.Abierta)
            throw new InvalidOperationException(Estado == EstadoOrden.Recibida ? "Esa orden ya se recibió." : "Esa orden se canceló.");
        Estado = EstadoOrden.Recibida;
        CompraId = compraId;
    }

    public void Cancelar()
    {
        if (Estado != EstadoOrden.Abierta)
            throw new InvalidOperationException("Solo se cancela una orden abierta.");
        Estado = EstadoOrden.Cancelada;
    }
}

public class OrdenRenglon
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid OrdenId { get; init; }
    public Guid VarianteId { get; init; }
    public Guid PresentacionId { get; init; }

    /// <summary>En presentaciones: 5 cajas.</summary>
    public Cantidad Cantidad { get; set; }

    public Cantidad CantidadBase { get; set; }
}
