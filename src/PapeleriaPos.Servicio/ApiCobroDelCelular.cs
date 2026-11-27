using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>
/// El celular cobra (dueño, 2026-11-22; docs/celular-como-caja.md, «Cobrar en el celular»). Con su llave, el celular cobra el ticket en una
/// sola llamada: el servicio arma la venta con sus precios y promociones de hoy (nunca los del celular) y la cobra en el turno abierto de
/// una caja de su sucursal, por el mismo camino que el cobro de esa caja (<see cref="RegistroCaja.CobrarAsync"/>): su folio, su corte, el
/// inventario, la bitácora y, en el piloto, la bandeja hacia eleventa. Cotizar es el mismo cálculo sin cobrar.
/// </summary>
public static class ApiCobroDelCelular
{
    /// <summary>Lo más largo de la referencia de una tarjeta o una transferencia.</summary>
    public const int LargoReferencia = 60;

    public static void MapearApiCobroDelCelular(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // Lo que costaría el ticket (con los cobros, si vienen: cuánto falta o el cambio), sin cobrar nada.
        api.MapPost("/cajas/{cajaId:guid}/cotizar", async (Guid cajaId, SolicitudCobroDelCelular s, HttpContext http, PosDbContext db,
            CatalogoEnMemoria catalogo, OpcionesEleventa opcionesEleventa, TimeProvider reloj, CancellationToken ct) =>
        {
            var celular = await ElCelularAsync(http, cajaId, db, ct);
            var renglones = await ApiTicketsDelCelular.RenglonesAsync(celular.SucursalId, s.Renglones, db, catalogo, ct);
            if (s.Cobros is { Count: > 0 } cobros)
                RevisarFormas(cobros, opcionesEleventa.SoloEfectivo);
            var venta = await ApiTicketsDelCelular.CalcularAsync(celular, renglones, db, catalogo, reloj, ct);
            var todoEnEfectivo = venta.Efectivo(0);
            return new CotizacionDelCelularDto(Lineas(venta), venta.Lineas.Count, venta.NumeroDeArticulos, venta.TotalCentavos,
                todoEnEfectivo.ACobrarCentavos, todoEnEfectivo.RedondeoCentavos, s.Cobros is { Count: > 0 } ? Pago(venta, s.Cobros) : null);
        });

        api.MapPost("/cajas/{cajaId:guid}/cobrar", async (Guid cajaId, SolicitudCobroDelCelular s, HttpContext http, PosDbContext db,
            CatalogoEnMemoria catalogo, IOptions<OpcionesTicket> ticket, EnvioVentasEleventa eleventa, OpcionesEleventa opcionesEleventa,
            BuzonDeTickets buzon, ImpresionDelCelular impresion, TimeProvider reloj, ILoggerFactory registros, CancellationToken ct) =>
        {
            var celular = await ElCelularAsync(http, cajaId, db, ct);
            if (s.Id == Guid.Empty)
                throw new RechazoConCodigo("El ticket no trae su número: actualiza la app.", "sin-id");
            // Su ticket, en la impresora de la caja donde entra (paso 11): lo que pida el celular; si no dice (una app de antes), lo que
            // diga «Imprimir lo que cobra el celular» de Configuración › Ticket.
            var imprimir = s.Imprimir ?? (await ApiConfiguracion.ConfiguracionAsync(db, celular.SucursalId, ticket.Value)).ImprimirLoDelCelular;
            // Otra vez el mismo (se perdió la respuesta en el Wi-Fi), o ya se cobró en una caja: la venta como quedó, sin cobrar otra. Su
            // ticket sale solo si sigue sin imprimir (no salió la primera vez): nunca dos papeles por mandar el cobro dos veces.
            if (await ApiCaja.YaCobradaAsync(db, s.Id) is { } cobrada)
                return await RespuestaAsync(db, cobrada, ticket.Value, yaCobrada: true, await ImprimirAsync(impresion, registros, imprimir,
                    () => impresion.OtraVezAlCobrarAsync(db, cobrada, celular, s.CajeroId ?? cobrada.CajeroId, ticket.Value, ct)));

            var renglones = await ApiTicketsDelCelular.RenglonesAsync(celular.SucursalId, s.Renglones, db, catalogo, ct);
            if (s.Cobros is not { Count: > 0 } cobros)
                throw new RechazoConCodigo("Falta cómo paga: efectivo, tarjeta o transferencia.", "sin-cobro");
            RevisarFormas(cobros, opcionesEleventa.SoloEfectivo);
            var (caja, turno) = await DondeEntraAsync(db, celular, s.Caja, ct);
            var cajeroId = await CajeroAsync(db, s.CajeroId, turno, caja.SucursalId, ct);
            var venta = await ApiTicketsDelCelular.CalcularAsync(caja, renglones, db, catalogo, reloj, ct, turno.Id, cajeroId, s.Id);
            if (s.TotalVisto is { } visto && visto != venta.TotalCentavos)
                throw new RechazoConCodigo($"El total cambió: ahora es {AlertasTelegram.Dinero(venta.TotalCentavos)} (el celular decía "
                    + $"{AlertasTelegram.Dinero(visto)}). Revísalo con el cliente y vuelve a cobrar.", "total-distinto", StatusCodes.Status409Conflict);
            if (Pago(venta, cobros) is { FaltanCentavos: > 0 } corto)
                throw new RechazoConCodigo($"Faltan {AlertasTelegram.Dinero(corto.FaltanCentavos)} por cobrar.", "falta");
            var control = new ServicioControl(db);
            // El artículo común, como en la caja (Ctrl+P): solo quien lo tiene en «Puede» (todos, al llegar). Queda en la bitácora al cobrar.
            if (venta.Lineas.Any(l => l.VarianteId is null)
                && (await control.EvaluarAsync(cajeroId, caja.SucursalId, AccionControlada.ArticuloComun, null, ct)).Nivel != NivelPermiso.Permitido)
                throw new RechazoConCodigo("Quien cobra no tiene permiso para el artículo común: quítalo del ticket o mándalo a una caja.",
                    "no-permitido", StatusCodes.Status403Forbidden);

            // Al cobrar todavía no sale: queda sin imprimir (su primera impresión, en «Ventas del día» de esa caja o pedida desde el
            // celular, no pide motivo). Si no se pidió el ticket, queda en la bitácora, con el celular (decidido, por revisar); si se
            // pidió, se imprime después de cobrar y solo se anota si no salió (ImpresionDelCelular).
            venta.TicketSinImprimir = true;
            if (!imprimir)
                control.Registrar(caja.SucursalId, celular.Id, cajeroId, AccionControlada.CobrarSinTicket,
                    $"Cobró en {celular.Nombre}, en el turno de la {caja.Nombre}, sin imprimir el ticket", venta.TotalCentavos, venta.Id);
            // Si ese ticket también se mandó a una caja y todavía no lo recoge, ya no se le da: se cobró aquí.
            if (buzon.Ya(s.Id) is { } mandado)
                buzon.Retirar(mandado);
            await eleventa.EncolarAsync(db, venta);
            try
            {
                await new RegistroCaja(db).CobrarAsync(venta, cobros, ct);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                // El mismo Id llegó dos veces a la vez: queda una, y se contesta esa.
                if (await ApiCaja.YaCobradaAsync(db, venta.Id) is not { } yaCobrada)
                    throw;
                return await RespuestaAsync(db, yaCobrada, ticket.Value, yaCobrada: true, await ImprimirAsync(impresion, registros, imprimir,
                    () => impresion.OtraVezAlCobrarAsync(db, yaCobrada, celular, cajeroId, ticket.Value, ct)));
            }
            // En el piloto, como la caja: el folio de eleventa si contesta a tiempo; si no, va en la vuelta de la bandeja.
            await eleventa.MandarAlCobrarAsync(venta.Id, venta.FolioTexto);
            registros.CreateLogger("PapeleriaPos.Servicio.Celular").LogInformation(
                "Cobro del celular · {Celular} cobró la venta {Folio} por {Total} en el turno de la {Caja} · venta {Venta}",
                celular.Nombre, venta.FolioTexto, AlertasTelegram.Dinero(venta.TotalCobradoCentavos), caja.Nombre, venta.Id);
            // Ya cobrada y con su folio de eleventa (si contestó): su ticket, a la impresora de esa caja. Lo que pase con el papel no
            // cambia el cobro.
            return await RespuestaAsync(db, venta, ticket.Value, yaCobrada: false, await ImprimirAsync(impresion, registros, imprimir,
                () => impresion.ImprimirAsync(db, venta, celular, cajeroId, ticket.Value, alCobrar: true, ct: ct)));
        });
    }

    /// <summary>
    /// Lo que pasó con el ticket: si no se pidió, eso; si se pidió, lo que diga la impresión. La venta ya está cobrada: si pedirlo
    /// truena, el cobro se contesta igual, con que no salió (se pide otra vez con <c>POST …/ventas/{id}/imprimir</c>).
    /// </summary>
    private static async Task<ImpresionDelCelularDto> ImprimirAsync(ImpresionDelCelular impresion, ILoggerFactory registros, bool imprimir,
        Func<Task<ImpresionDelCelularDto>> pedir)
    {
        if (!imprimir)
            return new ImpresionDelCelularDto(ImpresionDelCelularDto.NoPedida, null, "No se pidió imprimir el ticket.");
        try
        {
            return await pedir();
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            registros.CreateLogger("PapeleriaPos.Servicio.Celular").LogError(ex, "No se pudo pedir el ticket de un cobro del celular");
            return new ImpresionDelCelularDto(ImpresionDelCelularDto.NoImpreso, CodigosDeImpresion.NoSalio,
                "La venta quedó cobrada, pero no se pudo pedir su ticket: pídelo otra vez.");
        }
    }

    /// <summary>
    /// Quien llama, con su llave, es un celular dado de alta: una caja que nunca ha abierto un turno. Una caja de Windows cobra en su
    /// pantalla de venta, con su turno.
    /// </summary>
    internal static async Task<Caja> ElCelularAsync(HttpContext http, Guid cajaId, PosDbContext db, CancellationToken ct,
        string sinLlave = "Solo un celular dado de alta cobra desde el celular.")
    {
        if (!http.Items.TryGetValue(SeguridadCajas.CajaDeLaLlave, out var deLaLlave) || deLaLlave is not Guid)
            throw new RechazoConCodigo(sinLlave, "sin-llave", StatusCodes.Status401Unauthorized);
        var celular = await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cajaId && c.Activa, ct)
            ?? throw new RechazoConCodigo("Este celular ya no está dado de alta.", "caja-sin-alta", StatusCodes.Status401Unauthorized);
        if (await db.Turnos.AnyAsync(t => t.CajaId == cajaId, ct))
            throw new RechazoConCodigo("Esta caja abre turnos: cobra en su pantalla de venta.", "no-es-celular", StatusCodes.Status403Forbidden);
        return celular;
    }

    /// <summary>
    /// El turno donde entra el cobro: el de la caja que eligió el celular o, sin elegir, el único turno abierto de su sucursal (en la
    /// sucursal 2, el de la caja principal). Su corte cuenta el efectivo (decidido, por revisar).
    /// </summary>
    private static async Task<(Caja Caja, Turno Turno)> DondeEntraAsync(PosDbContext db, Caja celular, Guid? pedida, CancellationToken ct)
    {
        var abiertos = await (from t in db.Turnos.AsNoTracking()
                              join c in db.Cajas.AsNoTracking() on t.CajaId equals c.Id
                              where t.CerradoUtc == null && c.Activa && c.SucursalId == celular.SucursalId && c.Id != celular.Id
                              orderby c.Serie
                              select new { Turno = t, Caja = c }).ToListAsync(ct);
        if (pedida is { } id)
        {
            var caja = await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.Activa, ct);
            if (caja is null || caja.Id == celular.Id || caja.SucursalId != celular.SucursalId)
                throw new RechazoConCodigo("Esa caja ya no está en la tienda: elige otra.", "otra-caja-destino");
            var suyo = abiertos.FirstOrDefault(a => a.Caja.Id == id)
                ?? throw new RechazoConCodigo($"La {caja.Nombre} no tiene un turno abierto: que lo abran ahí, o elige otra caja.", "sin-turno",
                    StatusCodes.Status409Conflict);
            return (suyo.Caja, suyo.Turno);
        }
        return abiertos switch
        {
            [] => throw new RechazoConCodigo("Ninguna caja tiene un turno abierto: abre el turno en la caja principal y vuelve a cobrar.", "sin-turno",
                StatusCodes.Status409Conflict),
            [var unico] => (unico.Caja, unico.Turno),
            _ => throw new RechazoConCodigo($"Hay {abiertos.Count} cajas con turno abierto ({string.Join(", ", abiertos.Select(a => a.Caja.Nombre))}): "
                + "elige en cuál entra el cobro.", "elige-caja"),
        };
    }

    /// <summary>Quien cobra: el usuario que entró en el celular, si sigue activo en la sucursal; sin él, el cajero del turno.</summary>
    private static async Task<Guid> CajeroAsync(PosDbContext db, Guid? pedido, Turno turno, Guid sucursalId, CancellationToken ct)
    {
        if (pedido is not { } id)
            return turno.CajeroId;
        if (!await db.Usuarios.AnyAsync(u => u.Id == id && u.Activo && (u.SucursalId == null || u.SucursalId == sucursalId), ct))
            throw new RechazoConCodigo("Ese usuario ya no cobra en esta sucursal: entra otra vez en el celular.", "cajero");
        return id;
    }

    /// <summary>
    /// Desde el celular: efectivo, tarjeta o transferencia; lo fiado, en una caja (lleva cliente y ticket). En el piloto con eleventa, solo
    /// efectivo, como en la caja.
    /// </summary>
    internal static void RevisarFormas(IReadOnlyList<CobroSolicitado> cobros, bool soloEfectivo)
    {
        if (cobros.Any(c => c.Forma is not (FormaPago.Efectivo or FormaPago.Tarjeta or FormaPago.Transferencia)))
            throw new RechazoConCodigo("Desde el celular se cobra en efectivo, tarjeta o transferencia: lo fiado, en una caja.", "forma");
        if (soloEfectivo && cobros.Any(c => c.Forma != FormaPago.Efectivo))
            throw new RechazoConCodigo(SoloEfectivoEnElPiloto.NoSeCobra, "solo-efectivo");
        if (cobros.Any(c => c.MontoCentavos <= 0))
            throw new RechazoConCodigo("Cada cobro debe ser mayor a cero.", "cobro");
        if (cobros.Count(c => c.Forma == FormaPago.Efectivo) > 1)
            throw new RechazoConCodigo("El efectivo va en un solo cobro.", "cobro");
        if (cobros.Any(c => c.Referencia?.Length > LargoReferencia))
            throw new RechazoConCodigo($"La referencia va hasta {LargoReferencia} letras.", "cobro");
    }

    /// <summary>
    /// Lo que se cobra con esos cobros (el mismo cálculo que el cobro: <see cref="Venta.Efectivo"/>): tarjeta y transferencia exactas y sin
    /// pasar del total; el efectivo cubre lo demás, al peso.
    /// </summary>
    internal static PagoCotizadoDto Pago(Venta venta, IReadOnlyList<CobroSolicitado> cobros)
    {
        var sinEfectivo = cobros.Where(c => c.Forma != FormaPago.Efectivo).Sum(c => c.MontoCentavos);
        if (sinEfectivo > venta.TotalCentavos)
            throw new RechazoConCodigo("Tarjeta y transferencia no pueden pasar del total: no dan cambio.", "pasa-del-total");
        var efectivo = venta.Efectivo(sinEfectivo);
        var recibido = cobros.Where(c => c.Forma == FormaPago.Efectivo).Sum(c => c.MontoCentavos);
        return new PagoCotizadoDto(sinEfectivo, efectivo.ACobrarCentavos, efectivo.RedondeoCentavos, recibido,
            Math.Max(0, efectivo.ACobrarCentavos - recibido), Math.Max(0, recibido - efectivo.ACobrarCentavos));
    }

    private static List<RenglonCotizadoDto> Lineas(Venta venta) =>
        [.. venta.Lineas.Select(l => new RenglonCotizadoDto(l.VarianteId, l.PresentacionId, l.Descripcion, l.Cantidad.Milesimas, l.PrecioCentavos,
            l.ImporteCentavos, l.AhorroMayoreoCentavos, l.AhorroPromocionCentavos, l.Promocion, l.VarianteId is null))];

    private static async Task<CobroDelCelularDto> RespuestaAsync(PosDbContext db, Venta venta, OpcionesTicket opciones, bool yaCobrada,
        ImpresionDelCelularDto impresion)
    {
        var ticket = await ApiCaja.TicketDelCobroAsync(db, venta, opciones) with { YaCobrada = yaCobrada };
        var recibido = venta.Cobros.Where(c => c.Forma == FormaPago.Efectivo).Sum(c => c.RecibidoCentavos);
        return new CobroDelCelularDto(venta.Id, venta.FolioTexto ?? "", ticket.FolioEleventa, venta.CajaId, ticket.Caja, ticket.Cajero, venta.Lineas.Count,
            venta.NumeroDeArticulos, venta.TotalCentavos, venta.RedondeoCentavos, venta.TotalCobradoCentavos, recibido, venta.CambioCentavos, ticket.Cobros,
            yaCobrada, ticket, impresion);
    }
}
