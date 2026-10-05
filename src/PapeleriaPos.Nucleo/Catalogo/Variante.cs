namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>Lo que se cuenta y se vende ("Azul"). Un producto sin variantes tiene exactamente una.</summary>
public class Variante : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();

    public Guid ProductoId { get; set; }
    public Producto? Producto { get; set; }

    /// <summary>Texto libre ("Azul", "Carta · 75 g"). Vacía si es la única variante.</summary>
    public string Descripcion { get; set; } = "";

    public UnidadBase UnidadBase { get; set; } = UnidadBase.Pieza;
    public bool ManejaCaducidad { get; set; }
    public bool Activa { get; set; } = true;

    /// <summary>Si se juntó con otra (estaba capturada dos veces): su historia se queda aquí, lo demás pasó a esa.</summary>
    public Guid? JuntadaEnId { get; set; }

    public List<Presentacion> Presentaciones { get; set; } = [];

    public bool AdmiteCantidad(Cantidad cantidad) => UnidadBase.AdmiteFracciones() || cantidad.EsEntera;

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }
}
