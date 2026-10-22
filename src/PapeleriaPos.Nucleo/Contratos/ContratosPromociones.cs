using System.Security.Cryptography;
using System.Text.Json;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Nucleo.Contratos;

// ── Promociones con fechas (fase 3, docs/fase-3-precios-clientes.md, «Promociones y kits») ─────────────────────────────
// Se aplican solas, no se acumulan (la que más le conviene al cliente, también contra el mayoreo) y nunca debajo del costo
// sin autorización. Viajan a las cajas con la copia, con sus fechas: cada caja decide con su fecha, también sin conexión.

/// <summary>
/// Una promoción como va en la copia de una caja: de su sucursal (o de todas) y sin terminar (también las que empiezan
/// después, para que la caja las aplique a medianoche sin bajar nada), con cada producto (variante) al que toca: los de sus
/// departamentos y sus marcas ya vienen como productos.
/// </summary>
public sealed record PromocionDto(
    Guid Id, string Nombre, TipoPromocion Tipo, int Lleva, int Paga, int Centesimas, DateOnly Desde, DateOnly? Hasta, DiasDeLaSemana Dias,
    IReadOnlyList<VarianteEnPromocionDto> Variantes)
{
    /// <summary>
    /// La huella de una lista de promociones (las de la copia de una caja): la caja la manda al pedir lo que cambió y, si ya no es
    /// la del servicio, le llega la lista completa. Así la copia puesta al día queda igual que la completa también cuando una
    /// promoción terminó sin que cambiara nada más (ya no va en la copia).
    /// </summary>
    public static string Huella(IReadOnlyList<PromocionDto> promociones) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(promociones, JsonSerializerOptions.Web)))[..16];
}

/// <param name="PrecioCentavos">Del precio especial: el de este producto, por unidad base y con IVA.</param>
public sealed record VarianteEnPromocionDto(Guid VarianteId, long? PrecioCentavos = null);

/// <summary>
/// Las promociones de una copia (o de lo que contestó el cobro), por producto: lo que pregunta el cálculo del precio
/// (<see cref="Ventas.PromocionesDeLaVenta"/>). No cambia nunca: con otra lista, se arma otra.
/// </summary>
public sealed class PromocionesPorProducto
{
    public static readonly PromocionesPorProducto Ninguna = new([]);

    private readonly Dictionary<Guid, List<PromocionDeProducto>> _porVariante = [];

    public PromocionesPorProducto(IReadOnlyList<PromocionDto> promociones)
    {
        Lista = promociones;
        foreach (var p in promociones)
        {
            foreach (var v in p.Variantes)
            {
                if (!_porVariante.TryGetValue(v.VarianteId, out var suyas))
                    _porVariante[v.VarianteId] = suyas = [];
                suyas.Add(new PromocionDeProducto(p.Id, p.Nombre, p.Tipo, p.Lleva, p.Paga, p.Centesimas, v.PrecioCentavos, p.Desde, p.Hasta, p.Dias));
            }
        }
    }

    public IReadOnlyList<PromocionDto> Lista { get; }

    public IReadOnlyList<PromocionDeProducto> De(Guid varianteId) => _porVariante.TryGetValue(varianteId, out var suyas) ? suyas : [];

    /// <summary>Las de ese producto que corren ese día (el verificador las dice).</summary>
    public IReadOnlyList<PromocionDeProducto> DeHoy(Guid varianteId, DateOnly dia) => De(varianteId).Where(p => p.AplicaEl(dia)).ToList();
}

/// <summary>Lo que se revisa al cobrar (fase 3), igual en el servicio y en la caja sin conexión.</summary>
public static class PromocionesDelCobro
{
    /// <summary>
    /// Los productos del ticket cuya promoción no es la que aplicó la pantalla de la caja, o con la que se ahorra otra cosa: lo
    /// que dicen sus líneas (<see cref="LineaSolicitada.PromocionId"/>) contra lo que se calculó al cobrar, producto por producto
    /// (una línea que manda la caja sin su presentación es la de la presentación base, y dos iguales se juntan en una). Las
    /// líneas con el precio cambiado no llevan promoción. Si hay alguno, no se cobra con un precio que el cajero no vio: se le
    /// dice, como con un precio que cambió.
    /// </summary>
    public static List<Guid> Distintas(Ventas.Venta venta, IReadOnlyList<LineaSolicitada> lineas)
    {
        var deLaCaja = lineas.Where(l => l.VarianteId is not null && l.PrecioCentavos is null)
            .GroupBy(l => l.VarianteId!.Value)
            .ToDictionary(g => g.Key, g => Resumen(g.Select(l => l.PromocionId), g.Sum(l => l.AhorroPromocionCentavos ?? 0)));
        var aqui = venta.Lineas.Where(l => l.VarianteId is not null && !l.EsImpresiones && !l.PrecioCambiado)
            .GroupBy(l => l.VarianteId!.Value)
            .ToDictionary(g => g.Key, g => Resumen(g.Select(l => l.PromocionId), g.Sum(l => l.AhorroPromocionCentavos)));
        return aqui.Where(x => !deLaCaja.TryGetValue(x.Key, out var suya) || suya != x.Value).Select(x => x.Key)
            .Concat(deLaCaja.Where(x => !aqui.ContainsKey(x.Key) && x.Value != Resumen([], 0)).Select(x => x.Key))
            .Distinct()
            .ToList();
    }

    /// <summary>Qué promociones y cuánto se ahorró, en un texto que se compara: «id,id|1250».</summary>
    private static string Resumen(IEnumerable<Guid?> promociones, long ahorro) =>
        $"{string.Join(",", promociones.OfType<Guid>().Distinct().Order())}|{ahorro}";
}

/// <summary>«3×2 (lleva 3 y paga 2)»: lo que hace una promoción, en el verificador y en la pantalla.</summary>
public static class PromocionTexto
{
    public static string QueHace(PromocionDeProducto p) => p.Tipo switch
    {
        TipoPromocion.LlevaNPagaM => $"{p.Lleva}×{p.Paga}: lleva {p.Lleva} y paga {p.Paga}",
        TipoPromocion.SegundoAMitad => "el segundo a mitad de precio",
        TipoPromocion.Porcentaje => $"{Margen.Texto(p.Centesimas)} menos",
        _ => p.PrecioCentavos is { } precio ? $"a {(precio / 100m).ToString("$#,##0.00", System.Globalization.CultureInfo.GetCultureInfo("es-MX"))}" : "precio especial",
    };
}

// ── Productos (F3) › Promociones ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>Una promoción en la lista de Productos › Promociones.</summary>
/// <param name="QueHace">«3×2», «15 % menos», «el segundo a mitad de precio», «precio especial».</param>
/// <param name="AQue">«12 productos», «el departamento «Cuadernos»», «la marca «Bic»».</param>
/// <param name="Fechas">«del 1/08/2027 al 31/08/2027 · lunes a viernes».</param>
/// <param name="Sucursal">Vacía: en todas.</param>
public sealed record PromocionResumenDto(
    Guid Id, string Nombre, TipoPromocion Tipo, string QueHace, string AQue, string Fechas, EstadoPromocion Estado, DateOnly Desde, DateOnly? Hasta,
    string? Sucursal);

/// <param name="Hoy">El día del servicio (hora de México): con él se dice qué corre hoy.</param>
/// <param name="EnElPiloto">En el piloto con eleventa: por qué no se aplican en esta caja y no se dan de alta aquí.</param>
/// <param name="VeCostos">Quien entró ve la utilidad: «Dejar al costo», el costo y el margen.</param>
public sealed record ListaPromocionesDto(DateOnly Hoy, IReadOnlyList<PromocionResumenDto> Promociones, string? EnElPiloto, bool VeCostos);

/// <summary>Un producto (variante) de una promoción, como se ve en Productos › Promociones.</summary>
/// <param name="MenudeoCentavos">Su menudeo de ahora, por unidad base.</param>
/// <param name="PrecioCentavos">Del precio especial: el de la promoción, por unidad base.</param>
/// <param name="Unidad">«pieza», «metro», «kilo», «litro»: de qué es el precio.</param>
/// <param name="CostoCentavos">El costo de una unidad (el promedio, o el último sin existencia): solo para quien ve la utilidad.</param>
/// <param name="MargenCentesimas">Con el precio de la promoción (o el menudeo): solo para quien ve la utilidad.</param>
/// <param name="DebajoDelCosto">Con la promoción queda debajo de lo que cuesta (lo ve quien la da de alta, sin el costo).</param>
public sealed record ProductoEnPromocionDto(
    Guid VarianteId, Guid ProductoId, string Producto, long MenudeoCentavos, long? PrecioCentavos, string Unidad, decimal? CostoCentavos = null,
    int? MargenCentesimas = null, bool DebajoDelCosto = false);

/// <summary>Una promoción completa, para verla o cambiarla.</summary>
/// <param name="SucursalId">Vacía: en todas las sucursales.</param>
/// <param name="Productos">Los productos de la promoción (sin los de sus departamentos y marcas, que van aparte).</param>
/// <param name="Departamentos">Los departamentos a los que toca.</param>
/// <param name="Marcas">Las marcas a las que toca, por su nombre.</param>
/// <param name="CuantosToca">Cuántos productos que se venden toca hoy (con los de sus departamentos y marcas).</param>
/// <param name="Creo">«Luis, el 22/10/2026 a las 10:30».</param>
/// <param name="Termino">«Luis, el 22/10/2026 a las 18:00», si se terminó a mano.</param>
public sealed record PromocionFichaDto(
    Guid Id, string Nombre, TipoPromocion Tipo, int Lleva, int Paga, int Centesimas, DateOnly Desde, DateOnly? Hasta, DiasDeLaSemana Dias,
    Guid? SucursalId, EstadoPromocion Estado, string QueHace, string Fechas, IReadOnlyList<ProductoEnPromocionDto> Productos,
    IReadOnlyList<OpcionCatalogoDto> Departamentos, IReadOnlyList<string> Marcas, int CuantosToca, string Creo, string? Termino, bool VeCostos);

/// <summary>A qué le toca, como lo manda la pantalla: un producto (su variante), un departamento o una marca (por su nombre).</summary>
/// <param name="PrecioCentavos">Del precio especial: el de esa variante, por unidad base y con IVA.</param>
public sealed record AlcanceSolicitadoDto(Guid? VarianteId = null, Guid? DepartamentoId = null, string? Marca = null, long? PrecioCentavos = null);

/// <summary>Dar de alta o cambiar una promoción (con <paramref name="Id"/> de una que ya está, se cambia y se vuelve a activar).</summary>
/// <param name="UsuarioId">Quien entró a la caja: con «Productos, precios y etiquetas» en «Puede».</param>
/// <param name="SoloEstaSucursal">Solo en la sucursal de la caja; si no, en todas.</param>
/// <param name="AutorizacionBajoCostoId">Si deja algo debajo del costo y quien la da de alta no lo puede hacer solo: quien lo autorizó.</param>
/// <param name="Id">
/// El de la promoción: la caja lo pone también en una nueva, así que si la respuesta se pierde y se manda otra vez, no quedan
/// dos.
/// </param>
public sealed record SolicitudPromocion(
    Guid UsuarioId, Guid SucursalId, Guid? CajaId, string Nombre, TipoPromocion Tipo, int Lleva, int Paga, int Centesimas, DateOnly Desde,
    DateOnly? Hasta, DiasDeLaSemana Dias, bool SoloEstaSucursal, IReadOnlyList<AlcanceSolicitadoDto> Alcance, Guid? AutorizacionBajoCostoId = null,
    Guid? Id = null);

/// <summary>Cómo queda antes de guardarla: lo que toca, lo que queda debajo del costo y, para quien ve la utilidad, el margen.</summary>
/// <param name="Productos">Los que toca (hasta <see cref="MaximoEnLaVista"/>; <paramref name="CuantosToca"/> dice cuántos son).</param>
/// <param name="BajoCosto">Los que quedan debajo del costo y cuánto falta en total (sin el costo de cada uno).</param>
public sealed record VistaPromocionDto(IReadOnlyList<ProductoEnPromocionDto> Productos, int CuantosToca, BajoCostoDto BajoCosto, bool VeCostos)
{
    public const int MaximoEnLaVista = 200;
}

/// <summary>Los productos (con sus variantes activas) que se agregan a una promoción; con <paramref name="AlCosto"/>, cada uno a su costo.</summary>
public sealed record SolicitudProductosPromocion(Guid UsuarioId, Guid SucursalId, IReadOnlyList<Guid> Productos, bool AlCosto = false);

/// <param name="Productos">Cada variante activa, con su menudeo; al costo, con el precio de su costo (redondeado hacia arriba al centavo).</param>
/// <param name="SinCosto">Al costo: los que no tienen costo todavía (no se agregaron).</param>
public sealed record ProductosPromocionDto(IReadOnlyList<ProductoEnPromocionDto> Productos, IReadOnlyList<string> SinCosto);

/// <summary>Terminar una promoción ahora.</summary>
public sealed record SolicitudTerminarPromocion(Guid UsuarioId, Guid SucursalId, Guid? CajaId = null);
