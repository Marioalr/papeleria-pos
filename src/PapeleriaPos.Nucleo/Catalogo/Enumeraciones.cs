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

    /// <summary>
    /// Por pieza, pero a granel (2026-10-24): admite fracciones y se escribe sin unidad, como la pieza. Así llega de eleventa lo
    /// que se vende a granel con la medida PIEZA (TVENTA 'D', MEDIDA_ID 1), como las impresiones («0.2», no «0.2 kg»). Va al
    /// último: se guarda por su nombre, y una caja de antes lo recibe como un número que no conoce (lo trata como granel).
    /// </summary>
    PiezaAGranel,
}

public enum OrigenCodigo
{
    Fabrica,
    Interno,
}

public static class UnidadBaseExtensiones
{
    /// <summary>Solo la pieza exige cantidades enteras; metro, kilo, litro y la pieza a granel se venden con fracciones.</summary>
    public static bool AdmiteFracciones(this UnidadBase unidad) => unidad != UnidadBase.Pieza;
}
