namespace PapeleriaPos.Nucleo.Catalogo;

public enum TipoProducto
{
    Mercancia,
    Servicio,
    Kit,
    /// <summary>Recargas y pagos de servicios: el dinero es del proveedor y solo la comisión es ingreso.</summary>
    CobroTerceros,
}

public enum Impuesto
{
    Iva16,
    Iva0,
    Exento,
}

public enum UnidadBase
{
    Pieza,
    Metro,
    Kilogramo,
    Litro,
}

public enum OrigenCodigo
{
    Fabrica,
    Interno,
}

public static class UnidadBaseExtensiones
{
    /// <summary>Solo la pieza exige cantidades enteras; metro, kilo y litro se venden con fracciones.</summary>
    public static bool AdmiteFracciones(this UnidadBase unidad) => unidad != UnidadBase.Pieza;
}
