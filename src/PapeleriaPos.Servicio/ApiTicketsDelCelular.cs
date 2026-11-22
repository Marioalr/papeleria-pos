using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Mandar el ticket del celular a una caja (docs/celular-como-caja.md, «Mandar el ticket a una caja»; la preventa de la fase 8).
/// El celular, con su llave, ve a qué cajas puede mandar y manda qué y cuánto; el servicio busca cada producto en su catálogo,
/// calcula el total con sus precios (nunca con los del celular) y lo deja en el buzón de esa caja, que lo recoge cada 2 s mientras
/// tiene la venta abierta y lo pone en su espera. El celular espera la respuesta hasta que la caja lo guardó (unos segundos).
/// </summary>
public static class ApiTicketsDelCelular
{
    /// <summary>Lo más que trae un ticket del celular, y lo más de un renglón (9,999 piezas).</summary>
    public const int MaximoRenglones = 200;
    public const long MaximoMilesimas = 9_999_000;

    /// <summary>Lo que el celular espera a que la caja lo recoja y lo guarde (<c>Celular:SegundosParaRecibir</c>).</summary>
    public const double SegundosParaRecibir = 10;

    public static void MapearApiTicketsDelCelular(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // Las cajas de su sucursal a las que se puede mandar: las que abren turnos (no otros celulares), con si tienen la venta
        // abierta ahora, quién atiende, cuántos tickets esperan y si tienen un turno abierto (en el que puede cobrar el celular).
        api.MapGet("/cajas/{cajaId:guid}/para-mandar", async (Guid cajaId, PosDbContext db, BuzonDeTickets buzon, CancellationToken ct) =>
        {
            var sucursal = await db.Cajas.Where(c => c.Id == cajaId).Select(c => (Guid?)c.SucursalId).SingleOrDefaultAsync(ct)
                ?? throw new RechazoConCodigo("Esta caja ya no está dada de alta.", "caja-sin-alta", StatusCodes.Status401Unauthorized);
            var cajas = await db.Cajas.Where(c => c.Activa && c.SucursalId == sucursal && c.Id != cajaId && db.Turnos.Any(t => t.CajaId == c.Id))
                .OrderBy(c => c.Serie).Select(c => new { c.Id, c.Nombre, c.Serie, Turno = db.Turnos.Any(t => t.CajaId == c.Id && t.CerradoUtc == null) })
                .ToListAsync(ct);
            var como = cajas.ToDictionary(c => c.Id, c => buzon.Como(c.Id));
            var quienes = como.Values.Select(c => c.Atiende).OfType<Guid>().Distinct().ToList();
            var nombres = await db.Usuarios.Where(u => quienes.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
            return cajas.Select(c =>
            {
                var (recibe, esperan, atiende) = como[c.Id];
                return new CajaParaMandarDto(c.Id, c.Nombre, c.Serie, recibe, atiende is { } a ? nombres.GetValueOrDefault(a) : null, esperan, c.Turno);
            }).ToList();
        });

        api.MapPost("/cajas/{cajaId:guid}/mandar", async (Guid cajaId, SolicitudMandarTicket s, HttpContext http, PosDbContext db,
            CatalogoEnMemoria catalogo, BuzonDeTickets buzon, TimeProvider reloj, IConfiguration configuracion, CancellationToken ct) =>
        {
            // Solo con la llave del que lo manda: su nombre sale en la ficha de la caja.
            if (!http.Items.TryGetValue(SeguridadCajas.CajaDeLaLlave, out var deLaLlave) || deLaLlave is not Guid)
                throw new RechazoConCodigo("Solo un celular dado de alta manda tickets a las cajas.", "sin-llave", StatusCodes.Status401Unauthorized);
            var espera = TimeSpan.FromSeconds(configuracion.GetValue("Celular:SegundosParaRecibir", SegundosParaRecibir));

            var envio = buzon.Ya(s.Id);
            if (envio is null)
            {
                if (await db.Ventas.AnyAsync(v => v.Id == s.Id, ct))
                    throw new RechazoConCodigo("Ese ticket ya se cobró en la caja.", "ya-cobrado", StatusCodes.Status409Conflict);
                envio = await ArmarAsync(cajaId, s, db, catalogo, reloj, ct);
                if (!buzon.Como(envio.Destino).Recibe)
                    throw new RechazoConCodigo($"La {envio.Respuesta.Caja} no tiene la venta abierta (o está apagada): que entren a vender ahí y vuelve a mandarlo.",
                        "caja-no-recibe", StatusCodes.Status409Conflict);
                buzon.Poner(envio);
            }
            else if (envio.Origen != cajaId)
                throw new RechazoConCodigo("Ese ticket es de otro celular.", "otro-celular", StatusCodes.Status409Conflict);

            try
            {
                await envio.Recibido.Task.WaitAsync(espera, reloj, ct);
                return envio.Respuesta;
            }
            catch (TimeoutException)
            {
                if (buzon.Retirar(envio))
                    throw new RechazoConCodigo($"La {envio.Respuesta.Caja} no lo recogió: revisa que tenga la venta abierta y vuelve a mandarlo.",
                        "caja-no-recibe", StatusCodes.Status409Conflict);
                // Lo recogió y no alcanzó a decir que lo guardó: puede que sí esté. Mandarlo otra vez no lo duplica.
                return envio.Respuesta with { Recibido = false };
            }
        });

        // La caja con la venta abierta pregunta por los suyos (cada 2 s): dice quién atiende y cuántos esperan.
        api.MapGet("/cajas/{cajaId:guid}/recibidos", (Guid cajaId, BuzonDeTickets buzon, int esperan = 0, Guid? atiende = null) =>
        {
            buzon.Escucha(cajaId, Math.Max(0, esperan), atiende);
            return buzon.Pendientes(cajaId);
        });

        // Los que ya guardó en su espera.
        api.MapPost("/cajas/{cajaId:guid}/recibidos", (Guid cajaId, SolicitudRecibidos s, BuzonDeTickets buzon) =>
        {
            buzon.Confirmar(cajaId, s.Ids ?? []);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// El ticket como lo va a armar la caja: cada producto del catálogo del servicio (por su variante y presentación o, sin ellas,
    /// por su código, con la regla de los códigos) y los artículos comunes, las líneas sin precio (salvo el del artículo común) y el
    /// total con los precios y promociones de hoy.
    /// </summary>
    internal static async Task<BuzonDeTickets.Envio> ArmarAsync(Guid origen, SolicitudMandarTicket s, PosDbContext db, CatalogoEnMemoria catalogo,
        TimeProvider reloj, CancellationToken ct)
    {
        if (s.Id == Guid.Empty)
            throw new RechazoConCodigo("El ticket no trae su número: actualiza la app.", "sin-id");
        var de = await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == origen, ct)
            ?? throw new RechazoConCodigo("Este celular ya no está dado de alta.", "caja-sin-alta", StatusCodes.Status401Unauthorized);
        var renglones = await RenglonesAsync(de.SucursalId, s.Renglones, db, catalogo, ct);
        var destino = await db.Cajas.Where(c => c.Id == s.Destino && c.Activa).Select(c => new { c.Id, c.Nombre, c.SucursalId }).SingleOrDefaultAsync(ct);
        if (destino is null || destino.Id == origen || destino.SucursalId != de.SucursalId)
            throw new RechazoConCodigo("Esa caja ya no está en la tienda: elige otra.", "otra-caja-destino");

        var venta = await CalcularAsync(de, renglones, db, catalogo, reloj, ct);
        var lineas = venta.Lineas.Select(l => l.VarianteId is null
            ? new LineaSolicitada(null, null, l.Cantidad.Milesimas, l.Descripcion, l.PrecioCentavos)
            : new LineaSolicitada(l.VarianteId, l.PresentacionId, l.Cantidad.Milesimas)).ToList();
        var ticket = new TicketRecibidoDto(s.Id, de.Nombre, reloj.GetUtcNow().UtcDateTime, lineas, [.. renglones.Productos.Values], venta.TotalCentavos);
        var respuesta = new TicketMandadoDto(s.Id, destino.Id, destino.Nombre, lineas.Count, venta.NumeroDeArticulos, venta.TotalCentavos, true);
        return new BuzonDeTickets.Envio(ticket, origen, destino.Id, respuesta);
    }

    /// <summary>Los renglones del celular como líneas de la caja (una por renglón, sin juntar) y los productos que trae.</summary>
    internal sealed record RenglonesDelCelular(List<LineaSolicitada> Lineas, Dictionary<Guid, ProductoVentaDto> Productos);

    /// <summary>
    /// Cada renglón del celular, revisado: el producto del catálogo del servicio (por su variante y presentación o, sin ellas, por su
    /// código, con la regla de los códigos) o el artículo común, con su descripción y su precio. Lo mismo al mandar, cotizar y cobrar.
    /// </summary>
    internal static async Task<RenglonesDelCelular> RenglonesAsync(Guid sucursalId, IReadOnlyList<RenglonMandado>? renglones, PosDbContext db,
        CatalogoEnMemoria catalogo, CancellationToken ct)
    {
        if (renglones is not { Count: > 0 })
            throw new RechazoConCodigo("El ticket está vacío.", "vacio");
        if (renglones.Count > MaximoRenglones)
            throw new RechazoConCodigo($"El ticket trae {renglones.Count} renglones; van hasta {MaximoRenglones}: pártelo en dos.", "demasiados");
        var lineas = new List<LineaSolicitada>();
        var productos = new Dictionary<Guid, ProductoVentaDto>();
        var faltan = new List<string>();
        foreach (var r in renglones)
        {
            if (r.CantidadMilesimas <= 0 || r.CantidadMilesimas > MaximoMilesimas)
                throw new RechazoConCodigo("Una cantidad del ticket no es válida: va de 1 a 9,999.", "cantidad");
            if (r.PrecioCentavos is { } precio)
            {
                lineas.Add(ArticuloComun(r, precio));
                continue;
            }
            ProductoVentaDto? producto = null;
            if (r.VarianteId is { } varianteId)
                producto = await catalogo.PorVarianteAsync(db, varianteId, sucursalId, ct, conExistencia: false);
            else if (!string.IsNullOrWhiteSpace(r.Codigo))
                producto = (await catalogo.EscanearAsync(db, r.Codigo.Trim(), sucursalId)).Producto;
            else
                throw new RechazoConCodigo(ComunIncompleto, "comun");
            if (producto is null)
            {
                faltan.Add(r.Codigo ?? r.VarianteId?.ToString() ?? "?");
                continue;
            }
            var presentacion = r.PresentacionId ?? producto.Presentacion.Id;
            if (producto.Presentaciones.All(p => p.Id != presentacion))
            {
                faltan.Add(r.Codigo ?? producto.Descripcion);
                continue;
            }
            productos.TryAdd(producto.VarianteId, producto);
            lineas.Add(new LineaSolicitada(producto.VarianteId, presentacion, r.CantidadMilesimas));
        }
        if (faltan.Count > 0)
            throw new RechazoConCodigo(faltan.Count == 1
                ? $"«{faltan[0]}» ya no está en el catálogo de la caja principal: quítalo del ticket."
                : $"{faltan.Count} productos ya no están en el catálogo de la caja principal ({string.Join(", ", faltan.Take(5))}): quítalos del ticket.",
                "no-esta");
        return new RenglonesDelCelular(lineas, productos);
    }

    public const string ComunIncompleto =
        "Un renglón sin producto es un artículo común: lleva su descripción (hasta 80 letras) y su precio, de $0.01 a $99,999.99.";

    public const string ProductoConPrecio = "Un producto del catálogo no lleva precio: lo pone la caja principal. Mándalo sin precio o como artículo común.";

    private static LineaSolicitada ArticuloComun(RenglonMandado r, long precio)
    {
        if (r.VarianteId is not null)
            throw new RechazoConCodigo(ProductoConPrecio, "comun");
        var descripcion = r.Descripcion?.Trim() ?? "";
        if (descripcion.Length is 0 or > ArticuloComunDelCelular.LargoDescripcion || precio is <= 0 or > ArticuloComunDelCelular.PrecioMaximoCentavos)
            throw new RechazoConCodigo(ComunIncompleto, "comun");
        return new LineaSolicitada(null, null, r.CantidadMilesimas, descripcion, precio);
    }

    /// <summary>
    /// La venta con los precios del catálogo del servicio y sus promociones de hoy en la sucursal de <paramref name="caja"/> (la del
    /// celular al cotizar y al mandar; la de la caja donde entra al cobrar): el mismo armado que el cobro de la caja
    /// (<see cref="ApiCaja.ArmarVentaAsync"/>), así que lo que se cotiza y lo que se cobra no se separan.
    /// </summary>
    internal static async Task<Venta> CalcularAsync(PapeleriaPos.Nucleo.Turnos.Caja caja, RenglonesDelCelular renglones, PosDbContext db,
        CatalogoEnMemoria catalogo, TimeProvider reloj, CancellationToken ct, Guid? turnoId = null, Guid? cajeroId = null, Guid? ventaId = null)
    {
        var promociones = await ApiCaja.PromocionesDelCobroAsync(db, catalogo, caja.SucursalId, CopiaDeLaCaja.Hoy(reloj), reloj, ct);
        return await ApiCaja.ArmarVentaAsync(db, catalogo, caja, turnoId ?? Guid.Empty, cajeroId ?? Guid.Empty, ventaId ?? Ids.Nuevo(), renglones.Lineas,
            promociones: promociones.Venta);
    }
}
