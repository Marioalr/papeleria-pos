using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>Los pasos de una temporada en Productos › Temporadas, en el orden en que se hacen en el año.</summary>
public enum PasoDeTemporada
{
    /// <summary>Empezarla: la lista del año pasado y los precios, que se confirman una vez.</summary>
    Precios,

    /// <summary>La hoja de la temporada: la foto, el nombre, el precio y el código de cada producto.</summary>
    Hoja,

    /// <summary>Las tarjetas de precio de la mesa.</summary>
    Tarjetas,

    /// <summary>Terminarla: lo que sobró se cuenta y queda el resumen.</summary>
    Terminar,
}

/// <summary>
/// Lo que se recuerda de Productos › Temporadas mientras se va a la ficha de un producto (a tomarle su foto) y se regresa: la
/// temporada elegida, el paso y los precios que se habían escrito.
/// </summary>
public sealed class TemporadasVistas
{
    public Guid? Elegida { get; set; }
    public PasoDeTemporada Paso { get; set; }

    /// <summary>Lo escrito en «Precio» de cada producto, sin confirmar todavía (de <see cref="PreciosDe"/>).</summary>
    public Dictionary<Guid, string> Precios { get; } = [];

    /// <summary>De qué temporada son <see cref="Precios"/>: al cambiar de temporada se olvidan.</summary>
    public Guid? PreciosDe { get; set; }

    /// <summary>Lo escrito en «Contado» al terminarla, por producto.</summary>
    public Dictionary<Guid, string> Contado { get; } = [];
}

/// <summary>
/// Lo de Productos › Temporadas que no es la página (entrega 13, parte 3): qué precio queda en cada renglón, si se puede, y
/// cómo se lee el resumen. Lo revisan las pruebas.
/// </summary>
public static class TemporadaEnPantalla
{
    /// <summary>El precio de un renglón: lo escrito, si se escribió algo; si no, el de hoy. Vacío si lo escrito no es un precio.</summary>
    public static long? Precio(ProductoDeTemporadaDto producto, string? escrito) =>
        escrito is null ? producto.PrecioCentavos : Formato.LeerPesos(escrito) is { } pesos && pesos > 0 ? pesos : null;

    /// <summary>
    /// Qué le pasa al precio de un renglón; vacío si está bien. Lo de eleventa (en el piloto) y lo desactivado no se cambia aquí.
    /// </summary>
    public static string? Problema(ProductoDeTemporadaDto producto, string? escrito)
    {
        if (escrito is null)
            return null;
        if (Precio(producto, escrito) is not { } precio)
            return "no es un precio";
        if (precio == producto.PrecioCentavos)
            return null;
        if (producto.EnEleventa)
            return "se cambia en eleventa";
        if (!producto.Activo)
            return "está desactivado";
        if (precio < producto.PisoCentavos)
            return "más barato que su mayoreo";
        return null;
    }

    /// <summary>
    /// Los precios que se mandan a confirmar: los de lo activo con precio (lo escrito o el de hoy); lo de eleventa, como está.
    /// Vacío si alguno tiene un problema: primero se corrige.
    /// </summary>
    public static List<PrecioDeTemporadaDto>? ParaConfirmar(IEnumerable<ProductoDeTemporadaDto> productos, IReadOnlyDictionary<Guid, string> escritos)
    {
        var lista = new List<PrecioDeTemporadaDto>();
        foreach (var p in productos)
        {
            var escrito = escritos.TryGetValue(p.VarianteId, out var texto) ? texto : null;
            if (Problema(p, escrito) is not null)
                return null;
            if (!p.Activo || Precio(p, escrito) is not { } precio)
                continue;
            lista.Add(new PrecioDeTemporadaDto(p.VarianteId, p.PrecioCentavos ?? 0, p.EnEleventa ? p.PrecioCentavos ?? precio : precio));
        }
        return lista;
    }

    /// <summary>Cómo se vendió en la temporada: «se acabó», «no se vendió» o qué parte de lo que había («80 %»).</summary>
    public static string ComoSeVendio(ResumenDeProductoDto r)
    {
        if (r.Vendido <= 0)
            return "no se vendió";
        if (r.LlevaInventario && r.Quedo <= 0)
            return "se acabó";
        if (r.Inicial <= 0)
            return "";
        return $"{Math.Round(Math.Min(r.Vendido / r.Inicial, 1m) * 100):0} %";
    }

    /// <summary>«del 20/11/2026 al 1/1/2027».</summary>
    public static string Periodo(DateOnly inicio, DateOnly fin) => $"del {Dia(inicio)} al {Dia(fin)}";

    /// <summary>«20/11/2026».</summary>
    public static string Dia(DateOnly dia) => dia.ToString("d/M/yyyy", System.Globalization.CultureInfo.InvariantCulture);
}
