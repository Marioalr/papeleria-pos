using System.Globalization;
using PapeleriaPos.Nucleo;
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

/// <summary>Un precio escrito en la lista de la temporada, todavía sin confirmar.</summary>
/// <param name="Antes">
/// El precio de hoy que estaba en la lista al escribirlo: es el que se manda como «el de antes». Si mientras tanto alguien lo
/// cambia (otra caja), al volver a abrir la lista el renglón lo dice y no se confirma sin escribirlo otra vez.
/// </param>
public sealed record PrecioEscrito(string Texto, long? Antes);

/// <summary>
/// Lo que se recuerda de Productos › Temporadas mientras se va a la ficha de un producto (a tomarle su foto) y se regresa: la
/// temporada elegida, el paso y los precios que se habían escrito.
/// </summary>
public sealed class TemporadasVistas
{
    public Guid? Elegida { get; set; }
    public PasoDeTemporada Paso { get; set; }

    /// <summary>Lo escrito en «Precio» de cada producto, sin confirmar todavía (de <see cref="PreciosDe"/>), con el de hoy que se veía.</summary>
    public Dictionary<Guid, PrecioEscrito> Precios { get; } = [];

    /// <summary>De qué temporada son <see cref="Precios"/>: al cambiar de temporada se olvidan.</summary>
    public Guid? PreciosDe { get; set; }

    /// <summary>Lo escrito en «Contado» al terminarla que todavía no guarda la caja principal (se guarda al salir del cuadro o con Enter).</summary>
    public Dictionary<Guid, string> Contado { get; } = [];
}

/// <summary>
/// Lo de Productos › Temporadas que no es la página (entrega 13, parte 3): qué precio queda en cada renglón, si se puede, y
/// cómo se lee el resumen. Lo revisan las pruebas.
/// </summary>
public static class TemporadaEnPantalla
{
    /// <summary>El precio de un renglón: lo escrito, si se escribió algo; si no, el de hoy. Vacío si lo escrito no es un precio.</summary>
    public static long? Precio(ProductoDeTemporadaDto producto, PrecioEscrito? escrito) =>
        escrito is null ? producto.PrecioCentavos : Formato.LeerPesos(escrito.Texto) is { } pesos && pesos > 0 ? pesos : null;

    /// <summary>
    /// Qué le pasa al precio de un renglón; vacío si está bien. Si el de hoy cambió desde que se escribió (otra caja), se marca:
    /// confirmarlo así pisaría ese cambio sin verlo. Lo de eleventa (en el piloto) y lo desactivado no se cambia aquí.
    /// </summary>
    public static string? Problema(ProductoDeTemporadaDto producto, PrecioEscrito? escrito)
    {
        if (escrito is null)
            return null;
        if (Precio(producto, escrito) is not { } precio)
            return Formato.ProblemaDePesos(escrito.Texto) is { } problema ? char.ToLowerInvariant(problema[0]) + problema[1..].TrimEnd('.') : "no es un precio";
        if (precio == producto.PrecioCentavos)
            return null;
        if (escrito.Antes != producto.PrecioCentavos)
            return $"el de hoy cambió (era {(escrito.Antes is { } antes ? Formato.Dinero(antes) : "sin precio")}): escríbelo otra vez";
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
    /// <remarks>
    /// De lo escrito, «el de antes» es el de hoy que se veía al escribirlo (no el de la lista que se volvió a leer): si alguien lo
    /// cambió mientras tanto, la caja principal no lo confirma.
    /// </remarks>
    public static List<PrecioDeTemporadaDto>? ParaConfirmar(IEnumerable<ProductoDeTemporadaDto> productos, IReadOnlyDictionary<Guid, PrecioEscrito> escritos)
    {
        var lista = new List<PrecioDeTemporadaDto>();
        foreach (var p in productos)
        {
            var escrito = escritos.TryGetValue(p.VarianteId, out var suyo) ? suyo : null;
            if (Problema(p, escrito) is not null)
                return null;
            if (!p.Activo || Precio(p, escrito) is not { } precio)
                continue;
            var antes = escrito is null ? p.PrecioCentavos ?? 0 : escrito.Antes ?? 0;
            lista.Add(new PrecioDeTemporadaDto(p.VarianteId, antes, p.EnEleventa ? p.PrecioCentavos ?? precio : precio));
        }
        return lista;
    }

    /// <summary>
    /// Lo escrito en «Contado», en milésimas de la unidad base. Vacío (sin nada escrito) es «sin contar». Falso si no es una
    /// cantidad: negativa, con más de tres decimales (lo más chico que se lleva es una milésima) o de más de un millón.
    /// </summary>
    public static bool LeerCantidad(string? texto, out long? milesimas)
    {
        milesimas = null;
        var limpio = (texto ?? "").Trim();
        if (limpio.Length == 0)
            return true;
        // Con la regla de los números del POS: «1,000» son mil y «2,5» no se adivina (antes eran 2.5 y 1; revisión del PR #138).
        if (Formato.LeerCantidad(limpio) is not { } cantidad || cantidad < 0 || cantidad > 1_000_000 || decimal.Round(cantidad, 3) != cantidad)
            return false;
        milesimas = Cantidad.Unidades(cantidad).Milesimas;
        return true;
    }

    /// <summary>Una cantidad como se escribe en el cuadro: «12», «2.5».</summary>
    public static string TextoDeCantidad(decimal cantidad) => cantidad.ToString("0.###", CultureInfo.InvariantCulture);

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
