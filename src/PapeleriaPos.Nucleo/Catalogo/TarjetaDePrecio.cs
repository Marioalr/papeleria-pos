namespace PapeleriaPos.Nucleo.Catalogo;

// Las tarjetas de precio y lo que más se pregunta (entrega 13, parte 3; docs/propuesta-interfaz.md, «Lo que se señala»,
// puntos 5 y 6): lo exhibido y lo de temporada lleva una tarjeta con el precio grande, para que el cliente lo vea sin
// preguntar, y el código, para que el cajero la escanee en lugar del producto. Si cambia el precio, el POS dice qué tarjetas
// volver a imprimir; y el verificador cuenta lo que se pregunta, para saber a qué ponerle tarjeta primero.

/// <summary>
/// La tarjeta de precio de un producto que se imprimió en una sucursal: con qué precio y qué código salió. Una por producto
/// (la variante): imprimirla otra vez la pone al día. Si su precio de hoy (o su código) ya no es el de la tarjeta, sale en
/// «Por reimprimir» hasta que se vuelve a imprimir.
/// </summary>
public class TarjetaDePrecio
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }
    public Guid VarianteId { get; set; }

    /// <summary>El precio de menudeo que salió en la tarjeta.</summary>
    public long PrecioCentavos { get; set; }

    /// <summary>El código que salió en la tarjeta: el que el cajero escanea.</summary>
    public required string Codigo { get; set; }

    public DateTime ImpresaUtc { get; set; }
    public Guid ImpresaPorId { get; set; }

    /// <summary>Lo más largo que se guarda del código (los de fábrica y los internos son mucho más cortos).</summary>
    public const int LargoCodigo = 64;

    /// <summary>
    /// Por qué hay que volver a imprimirla: el precio de hoy no es el de la tarjeta, o su código ya no es de este producto (lo
    /// escanearían y saldría otro o nada). Vacío si la tarjeta sigue bien.
    /// </summary>
    /// <param name="precioDeHoy">El menudeo de hoy; vacío si ya no tiene precio.</param>
    /// <param name="codigos">Los códigos que tiene hoy el producto.</param>
    public string? PorQueReimprimir(long? precioDeHoy, IReadOnlyCollection<string> codigos)
    {
        if (!codigos.Contains(Codigo, StringComparer.OrdinalIgnoreCase))
            return "su código cambió";
        if (precioDeHoy != PrecioCentavos)
            return precioDeHoy is null ? "ya no tiene precio" : "cambió el precio";
        return null;
    }
}

/// <summary>
/// Una consulta del verificador (F9): qué producto se preguntó, en qué caja y cuándo. Con ellas, Productos dice lo que más
/// se pregunta y no tiene tarjeta de precio. Se guarda una por consulta, como lo que se busca; sin conexión no se cuenta.
/// </summary>
public class ConsultaDePrecio
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid SucursalId { get; set; }

    /// <summary>La caja donde se preguntó; vacía si no se supo (el navegador en la PC de la caja principal).</summary>
    public Guid? CajaId { get; set; }

    public Guid VarianteId { get; set; }
    public DateTime FechaUtc { get; set; }

    /// <summary>De cuántos días para atrás se cuentan las consultas en «Lo más preguntado»: los mismos 90 que lo más vendido.</summary>
    public const int DiasQueCuentan = 90;
}
