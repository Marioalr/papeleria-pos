using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Datos;

/// <summary>
/// Productos e Inventario mientras dure el piloto con eleventa, con su lectura prendida (decisión del dueño, 2026-10-10:
/// «durante el piloto, altas y precios en eleventa»). Cada pocos minutos la lectura (SincronizacionEleventa, en el puente)
/// pone en los productos de eleventa lo que dice allá y deja su existencia en la de allá (más lo que esta caja vendió o
/// devolvió y allá todavía no está): lo que se cambiara aquí lo regresaría la siguiente lectura sin decir nada, y mientras
/// tanto la caja vendería con lo que se escribió aquí. Por eso:
/// <list type="bullet">
/// <item>De un producto de eleventa (el que tiene su ID) no se cambia aquí lo que la lectura le pone: el nombre y si se vende
/// (si es de una sola variante, como allá), si la variante está activa, sus códigos de la pieza (se pueden agregar otros), el
/// precio de menudeo y las promociones de eleventa; tampoco se da de baja ni se junta. Lo que la lectura no toca sigue igual:
/// las escalas de mayoreo del F11 o capturadas en F3, las presentaciones, la marca, el tipo y los productos dados de alta
/// aquí, que van a eleventa como artículo común.</item>
/// <item>Desde el 2026-11-07 la base de aquí manda en el mínimo y el máximo de inventario, en el departamento de cada
/// producto y en los departamentos mismos (el dueño: «para que la lista de compra coincida», y reparte todos los productos
/// en departamentos nuevos): se cambian en F3, en Productos › Departamentos y con «Productos en Excel», que en el piloto
/// solo toma eso y la marca. La lectura ya no los cambia; solo un producto nuevo de eleventa llega con los suyos de allá, y
/// el departamento nuevo de allá donde llega se crea aquí. La unidad se cambia en F3 entre las que no cambian las
/// cantidades (pieza, pieza a granel, metro y litro); la lectura la sigue una vez cuando eleventa pasa un producto de por
/// pieza a granel, nunca al revés.</item>
/// <item>Lo que mueve existencias no se hace aquí: ajustes, mermas, abrir un paquete, el conteo del día, el conteo
/// completo y recibir compras. Ni con lo dado de alta aquí, que tampoco lleva existencia en eleventa: una compra o un
/// conteo junta los dos, y partirlos los dejaría a medias. Se consulta y se cancela lo que quedó abierto.</item>
/// </list>
/// Quien no tiene el permiso de algo oye lo del permiso, como siempre: esto es para quien sí lo tiene. Solo sirve para
/// convivir con eleventa: se quita al dejarlo.
/// </summary>
public static class TrastiendaEnElPiloto
{
    public const string ProductosEnEleventa =
        "Mientras dure el piloto con eleventa, las altas, los nombres, los códigos y los precios de los productos de eleventa se hacen en eleventa: "
        + "llegan aquí con su lectura, cada pocos minutos. El departamento, el mínimo, el máximo y la unidad se cambian aquí.";

    public const string InventarioEnEleventa =
        "Mientras dure el piloto con eleventa, el inventario se lleva en eleventa: las existencias llegan aquí con su lectura.";

    /// <summary>Como lo anotan la importación y la lectura (el ID de eleventa de cada producto y de cada promoción).</summary>
    private const string Eleventa = "eleventa";

    private const string Promocion = "Promocion";

    /// <summary>De estas variantes, las que vienen de eleventa: las que la lectura pone al día.</summary>
    public static async Task<HashSet<Guid>> DeEleventaAsync(PosDbContext db, IEnumerable<Guid> variantes, CancellationToken ct = default)
    {
        var ids = variantes.Distinct().ToList();
        return ids.Count == 0
            ? []
            : (await db.MapeosImportacion.AsNoTracking().Where(m => m.Origen == Eleventa && m.Entidad == nameof(Variante) && ids.Contains(m.IdNuevo))
                .Select(m => m.IdNuevo).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>De estos precios, los que son promociones por cantidad de eleventa: la lectura los pone, los cambia y los quita.</summary>
    public static async Task<HashSet<Guid>> PromocionesAsync(PosDbContext db, IEnumerable<Guid> precios, CancellationToken ct = default)
    {
        var ids = precios.Distinct().ToList();
        return ids.Count == 0
            ? []
            : (await db.MapeosImportacion.AsNoTracking().Where(m => m.Origen == Eleventa && m.Entidad == Promocion && ids.Contains(m.IdNuevo))
                .Select(m => m.IdNuevo).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>
    /// El precio de menudeo, el que pone la lectura en un producto de eleventa: por unidad base, desde una y para todas las
    /// sucursales. Con sus promociones (<see cref="PromocionesAsync"/>), lo que no se cambia aquí de sus precios.
    /// </summary>
    public static bool EsMenudeo(Precio precio) =>
        precio.PresentacionId is null && precio.SucursalId is null && precio.CantidadMinima == Cantidad.Unidades(1);

    /// <summary>Lo que mueve existencias, en el piloto: no se hace. Va después de revisar el permiso, que manda primero.</summary>
    public static void RevisarInventario(bool lecturaDeEleventa)
    {
        if (lecturaDeEleventa)
            throw new InvalidOperationException(InventarioEnEleventa);
    }

    /// <summary>
    /// Un ajuste o una merma en el piloto (los que se pueden autorizar): sin el permiso, el error de siempre; con él, o
    /// pidiéndolo, que el inventario se lleva en eleventa (no se pide una autorización para nada). Siempre falla.
    /// </summary>
    public static async Task RechazarAsync(PosDbContext db, Guid usuarioId, Guid sucursalId, AccionControlada accion, CancellationToken ct = default)
    {
        if ((await new ServicioControl(db).EvaluarAsync(usuarioId, sucursalId, accion, ct: ct)).Nivel == NivelPermiso.NoPermitido)
            throw new PermisoRequerido(accion, NivelPermiso.NoPermitido, Permisos.SinPermiso(accion));
        throw new InvalidOperationException(InventarioEnEleventa);
    }
}
