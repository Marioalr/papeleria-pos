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
        // abierta ahora, quién atiende y cuántos tickets esperan.
        api.MapGet("/cajas/{cajaId:guid}/para-mandar", async (Guid cajaId, PosDbContext db, BuzonDeTickets buzon, CancellationToken ct) =>
        {
            var sucursal = await db.Cajas.Where(c => c.Id == cajaId).Select(c => (Guid?)c.SucursalId).SingleOrDefaultAsync(ct)
                ?? throw new RechazoConCodigo("Esta caja ya no está dada de alta.", "caja-sin-alta", StatusCodes.Status401Unauthorized);
            var cajas = await db.Cajas.Where(c => c.Activa && c.SucursalId == sucursal && c.Id != cajaId && db.Turnos.Any(t => t.CajaId == c.Id))
                .OrderBy(c => c.Serie).Select(c => new { c.Id, c.Nombre, c.Serie }).ToListAsync(ct);
            var como = cajas.ToDictionary(c => c.Id, c => buzon.Como(c.Id));
            var quienes = como.Values.Select(c => c.Atiende).OfType<Guid>().Distinct().ToList();
            var nombres = await db.Usuarios.Where(u => quienes.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
            return cajas.Select(c =>
            {
                var (recibe, esperan, atiende) = como[c.Id];
                return new CajaParaMandarDto(c.Id, c.Nombre, c.Serie, recibe, atiende is { } a ? nombres.GetValueOrDefault(a) : null, esperan);
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
    /// por su código, con la regla de los códigos), las líneas sin precio y el total con los precios y promociones de hoy.
    /// </summary>
    internal static async Task<BuzonDeTickets.Envio> ArmarAsync(Guid origen, SolicitudMandarTicket s, PosDbContext db, CatalogoEnMemoria catalogo,
        TimeProvider reloj, CancellationToken ct)
    {
        if (s.Id == Guid.Empty)
            throw new RechazoConCodigo("El ticket no trae su número: actualiza la app.", "sin-id");
        if (s.Renglones is not { Count: > 0 } renglones)
            throw new RechazoConCodigo("El ticket está vacío.", "vacio");
        if (renglones.Count > MaximoRenglones)
            throw new RechazoConCodigo($"El ticket trae {renglones.Count} renglones; van hasta {MaximoRenglones}: pártelo en dos.", "demasiados");
        var de = await db.Cajas.Where(c => c.Id == origen).Select(c => new { c.Nombre, c.SucursalId }).SingleOrDefaultAsync(ct)
            ?? throw new RechazoConCodigo("Este celular ya no está dado de alta.", "caja-sin-alta", StatusCodes.Status401Unauthorized);
        var destino = await db.Cajas.Where(c => c.Id == s.Destino && c.Activa).Select(c => new { c.Id, c.Nombre, c.SucursalId }).SingleOrDefaultAsync(ct);
        if (destino is null || destino.Id == origen || destino.SucursalId != de.SucursalId)
            throw new RechazoConCodigo("Esa caja ya no está en la tienda: elige otra.", "otra-caja-destino");

        var promociones = await ApiCaja.PromocionesDelCobroAsync(db, catalogo, de.SucursalId, CopiaDeLaCaja.Hoy(reloj), reloj, ct);
        var venta = new Venta { Serie = "", SucursalId = de.SucursalId, Promociones = promociones.Venta };
        var productos = new Dictionary<Guid, ProductoVentaDto>();
        var faltan = new List<string>();
        foreach (var r in renglones)
        {
            if (r.CantidadMilesimas <= 0 || r.CantidadMilesimas > MaximoMilesimas)
                throw new RechazoConCodigo("Una cantidad del ticket no es válida: va de 1 a 9,999.", "cantidad");
            ProductoVentaDto? producto = null;
            if (r.VarianteId is { } varianteId)
                producto = await catalogo.PorVarianteAsync(db, varianteId, de.SucursalId, ct, conExistencia: false);
            else if (!string.IsNullOrWhiteSpace(r.Codigo))
                producto = (await catalogo.EscanearAsync(db, r.Codigo.Trim(), de.SucursalId)).Producto;
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
            venta.AgregarProducto(producto.ParaVenta(), producto.PresentacionModelo(presentacion), new Cantidad(r.CantidadMilesimas), producto.PreciosModelo());
        }
        if (faltan.Count > 0)
            throw new RechazoConCodigo(faltan.Count == 1
                ? $"«{faltan[0]}» ya no está en el catálogo de la caja principal: quítalo del ticket."
                : $"{faltan.Count} productos ya no están en el catálogo de la caja principal ({string.Join(", ", faltan.Take(5))}): quítalos del ticket.",
                "no-esta");

        var lineas = venta.Lineas.Select(l => new LineaSolicitada(l.VarianteId, l.PresentacionId, l.Cantidad.Milesimas)).ToList();
        var ticket = new TicketRecibidoDto(s.Id, de.Nombre, reloj.GetUtcNow().UtcDateTime, lineas, [.. productos.Values], venta.TotalCentavos);
        var respuesta = new TicketMandadoDto(s.Id, destino.Id, destino.Nombre, lineas.Count, venta.NumeroDeArticulos, venta.TotalCentavos, true);
        return new BuzonDeTickets.Envio(ticket, origen, destino.Id, respuesta);
    }
}
