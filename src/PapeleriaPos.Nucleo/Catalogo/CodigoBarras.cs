namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>Apunta a la presentación: escanear el código del paquete vende un paquete.</summary>
public class CodigoBarras : IVersionado
{
    public Guid Id { get; set; } = Ids.Nuevo();

    /// <summary>Único en todo el catálogo.</summary>
    public required string Codigo { get; set; }

    public Guid PresentacionId { get; set; }
    public Presentacion? Presentacion { get; set; }

    public OrigenCodigo Origen { get; set; } = OrigenCodigo.Fabrica;

    public long Version { get; set; }
    public DateTime ModificadoUtc { get; set; }

    /// <summary>
    /// De fábrica o interno, según el código; la misma regla en el catálogo y al importar o leer eleventa. Los EAN/UPC de
    /// fábrica tienen 8, 12, 13 o 14 dígitos; lo demás es interno (claves que puso la tienda), y también los EAN-13 que
    /// empiezan con 2: es el rango de uso interno de las tiendas, del que salen los códigos internos que da el POS.
    /// </summary>
    public static OrigenCodigo OrigenDe(string codigo) =>
        codigo.Length is 8 or 12 or 13 or 14 && codigo.All(char.IsAsciiDigit) && !(codigo.Length == 13 && codigo[0] == '2')
            ? OrigenCodigo.Fabrica
            : OrigenCodigo.Interno;
}
