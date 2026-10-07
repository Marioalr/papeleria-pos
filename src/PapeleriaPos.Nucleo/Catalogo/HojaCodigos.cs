namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>
/// Un producto de la hoja de códigos del mostrador de una sucursal (docs/fase-2-inventario.md, entrega 4, parte 2): lo
/// diminuto que no lleva etiqueta (gomas, clips sueltos) se escanea desde una hoja enmicada.
/// </summary>
public class RenglonHojaCodigos
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid SucursalId { get; init; }
    public Guid VarianteId { get; init; }

    /// <summary>En qué lugar sale: la hoja se arma en el orden que la deja el encargado.</summary>
    public int Orden { get; set; }
}
