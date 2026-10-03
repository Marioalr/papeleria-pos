namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>
/// Precio con IVA incluido. Lo normal es por unidad base de la variante (por pieza, metro, kilo); con
/// <see cref="PresentacionId"/> es el precio propio de una presentación, que es raro.
/// </summary>
public class Precio : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();

    public Guid VarianteId { get; set; }
    public Variante? Variante { get; set; }

    /// <summary>Vacío: precio por unidad base. Con valor: precio por cada presentación.</summary>
    public Guid? PresentacionId { get; set; }
    public Presentacion? Presentacion { get; set; }

    /// <summary>Vacío: precio base, para todas las sucursales.</summary>
    public Guid? SucursalId { get; set; }
    public Sucursal? Sucursal { get; set; }

    /// <summary>Desde qué cantidad aplica: la más baja es menudeo, las demás son escalas de mayoreo.</summary>
    public Cantidad CantidadMinima { get; set; } = Cantidad.Piezas(1);

    public long PrecioCentavos { get; set; }

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
