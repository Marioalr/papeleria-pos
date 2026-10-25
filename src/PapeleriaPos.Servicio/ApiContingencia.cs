using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Contingencia (entrega 3, parte 2): lo que una caja guarda para vender sin la caja principal, y la bandeja con lo que
/// hizo mientras tanto. Todo con la llave de la caja y solo para su caja.
/// </summary>
public static class ApiContingencia
{
    public static void MapearApiContingencia(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();
        var bandeja = app.MapGroup("/api/bandeja").ConErrores().SoloSuCaja().AddEndpointFilter(AvisarSiNoSubeAsync);

        // La copia local completa: catálogo con existencias, usuarios (PIN como hash), sus permisos, ticket, la configuración de
        // la tienda, el último folio de la caja y lo que necesita su búsqueda (CopiaDeLaCaja). Una caja de ahora pide después solo
        // lo que cambió (ApiCopia).
        api.MapGet("/cajas/{cajaId:guid}/paquete", (Guid cajaId, PosDbContext db, CatalogoEnMemoria catalogo, VersionesDeLaCopia versiones,
            IOptions<OpcionesTicket> opciones, TimeProvider reloj, CancellationToken ct) =>
            CopiaDeLaCaja.PaqueteAsync(db, catalogo, versiones, cajaId, opciones.Value, ct, reloj));

        // Lo que lleva el turno, sin guardar un corte: la base de la cuenta que la caja lleva por su lado.
        api.MapGet("/turnos/{turnoId:guid}/resumen", async (Guid turnoId, PosDbContext db) =>
            await ApiCaja.CorteDtoAsync(db, await new RegistroCaja(db).ResumenAsync(turnoId)));

        api.MapPost("/cajas/{cajaId:guid}/folio-usado", async (Guid cajaId, FolioUsadoDto f, PosDbContext db) =>
        {
            await new RegistroCaja(db).FolioUsadoAsync(cajaId, f.Folio);
            return Results.NoContent();
        });

        // ── La bandeja: sube en el orden en que pasó ─────────────────────────────

        bandeja.MapPost("/turnos", async (TurnoContingenciaDto t, PosDbContext db) =>
            await ApiCaja.TurnoDtoAsync(db, await new RegistroCaja(db).TurnoDeContingenciaAsync(t)));

        bandeja.MapPost("/ventas", async (VentaContingenciaDto v, PosDbContext db, CatalogoEnMemoria catalogo, EnvioVentasEleventa eleventa) =>
        {
            // Ya está (subió antes, o se cobró en línea y la caja no supo): una vez. Si es otra con el mismo Id (la caja la cobró
            // otra vez, cambiada, sin saber que la primera había quedado), no se descarta callada: queda por revisar.
            if (await db.Ventas.AsNoTracking().Include(x => x.Lineas).SingleOrDefaultAsync(x => x.Id == v.Id) is { } yaEsta)
                return MismaVenta(yaEsta, v) ? Results.NoContent() : throw new InvalidOperationException(VentaDistinta(yaEsta, v));
            var caja = await db.Cajas.FindAsync(v.CajaId) ?? throw new InvalidOperationException("La caja no existe.");
            var lineas = v.Lineas.Select(l => new LineaSolicitada(l.VarianteId, l.PresentacionId, l.CantidadMilesimas, l.Descripcion, l.PrecioCentavos,
                DescuentoCentesimas: l.DescuentoCentesimas, DescuentoCentavos: l.DescuentoCentavos, MotivoDescuento: l.MotivoDescuento,
                PromocionId: l.PromocionId, AhorroPromocionCentavos: l.AhorroPromocionCentavos, Promocion: l.Promocion)).ToList();
            // El precio que se cobró manda, y la promoción con que se cobró (fase 3), aunque aquí ya haya cambiado o terminado; el
            // mayoreo del cliente (el que usó la caja) solo sirve para que el ticket diga el ahorro.
            var venta = await ApiCaja.ArmarVentaAsync(db, catalogo, caja, v.TurnoId, v.CajeroId, v.Id, lineas, v.MayoreoDeCliente == true, comoSeCobro: true);
            venta.ClienteId = v.ClienteId;
            // Su nota, como se imprimió; ya se cobró, así que no se rechaza por ella.
            venta.PonerNota(NotaDelTicket.Recortada(v.Nota));
            // También va a eleventa (el piloto); su ticket ya salió con el folio de la caja, así que se manda en la vuelta.
            // Aunque el piloto sea solo en efectivo, no se rechaza: ya se cobró. Si no fue en efectivo, queda por revisar.
            await eleventa.EncolarAsync(db, venta);
            try
            {
                // Si fió: con lo que sabía la caja (una caja de antes no fía sin conexión; si llegara, se revisa con el saldo real).
                await new RegistroCaja(db).CobroDeContingenciaAsync(venta, v.Cobros, v.Folio, v.PagadaUtc, redondear: v.RedondeoCentavos is not null,
                    fiado: v.SaldoConocidoCentavos is { } conocido ? new FiadoSinConexion(conocido, v.ExcesoAutorizadoCentavos, v.VenceCredito) : null);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                // El cobro en línea de esta misma venta (que la caja dejó de esperar) entró a la vez y primero, con el mismo folio
                // (la serie de la caja sigue igual por los dos caminos): ya está, una vez.
                if (await ApiCaja.YaCobradaAsync(db, v.Id) is null)
                    throw;
                return Results.NoContent();
            }
            eleventa.Despertar();
            return Results.NoContent();
        });

        // Un abono sin conexión: se guarda siempre (ya se cobró, también en el piloto con eleventa); lo que no cuadre queda
        // por revisar.
        bandeja.MapPost("/abonos", async (AbonoContingenciaDto a, PosDbContext db) =>
        {
            await new RegistroCredito(db).AbonoDeContingenciaAsync(a);
            return Results.NoContent();
        });

        bandeja.MapPost("/turnos/{turnoId:guid}/movimientos", async (Guid turnoId, MovimientoContingenciaDto m, PosDbContext db, EnvioVentasEleventa eleventa) =>
        {
            // También va a eleventa (el piloto), como la que se hace en línea.
            await new RegistroCaja(db).MovimientoDeContingenciaAsync(turnoId, m, aEleventa: eleventa.Activo);
            eleventa.Despertar();
            return Results.NoContent();
        });

        bandeja.MapPost("/bitacora", async (BitacoraContingenciaDto b, PosDbContext db) =>
        {
            await new ServicioControl(db).RegistrarDeContingenciaAsync(b);
            return Results.NoContent();
        });

        bandeja.MapPost("/turnos/{turnoId:guid}/corte-z", async (Guid turnoId, CorteContingenciaDto c, PosDbContext db, ServicioRespaldos respaldos, EnvioVentasEleventa eleventa) =>
        {
            // En el piloto con eleventa la caja ya no hace el corte sin conexión (se hace en eleventa). Si llega uno (una caja
            // de antes, o de antes del piloto), ya pasó: se guarda, porque lo que la caja hizo después va en otro turno y no
            // entraría. El cierre del turno de eleventa que encola, el puente no lo manda: queda por revisar (CorteEnElPiloto).
            var corte = await ApiCaja.CorteDtoAsync(db, await new RegistroCaja(db).CorteZDeContingenciaAsync(turnoId, c));
            eleventa.Despertar();
            _ = Task.Run(() => respaldos.Hacer($"z-{corte.FechaLocal:HHmm}"));
            return corte;
        });
    }

    /// <summary>
    /// Lo que subió es la venta que ya está: las mismas líneas (producto, presentación, cantidad, precio, lo que se ahorró por la
    /// promoción y descuento; el artículo común, por su descripción), en cualquier orden.
    /// </summary>
    private static bool MismaVenta(Venta yaEsta, VentaContingenciaDto v)
    {
        static string Clave(Guid? variante, Guid? presentacion, string descripcion, long cantidad, long precio, long promocion, int? porcentaje, long? monto) =>
            $"{variante}|{presentacion}|{(variante is null ? descripcion.Trim().ToUpperInvariant() : "")}|{cantidad}|{precio}|{promocion}|{porcentaje}|{monto}";
        var alla = yaEsta.Lineas.Select(l => Clave(l.VarianteId, l.PresentacionId, l.Descripcion, l.Cantidad.Milesimas, l.PrecioCentavos,
            l.AhorroPromocionCentavos, l.DescuentoCentesimas, l.DescuentoCentesimas is null && l.DescuentoCentavos > 0 ? l.DescuentoCentavos : null));
        var subio = v.Lineas.Select(l => Clave(l.VarianteId, l.PresentacionId, l.Descripcion, l.CantidadMilesimas, l.PrecioCentavos,
            l.AhorroPromocionCentavos, l.DescuentoCentesimas, l.DescuentoCentesimas is null && l.DescuentoCentavos > 0 ? l.DescuentoCentavos : null));
        return alla.Order(StringComparer.Ordinal).SequenceEqual(subio.Order(StringComparer.Ordinal));
    }

    private static string VentaDistinta(Venta yaEsta, VentaContingenciaDto v) =>
        $"La venta {yaEsta.FolioTexto} ya estaba en la caja principal ({AlertasTelegram.Dinero(yaEsta.TotalCentavos)}, "
        + $"{yaEsta.Lineas.Count} {(yaEsta.Lineas.Count == 1 ? "línea" : "líneas")}), y esta caja la cobró otra vez sin conexión con otras "
        + $"líneas, con el folio {yaEsta.Serie}-{v.Folio:000000}: no se guardó dos veces; hay que revisarla.";

    /// <summary>
    /// Si algo de la bandeja no se puede guardar, el dueño se entera por Telegram (una vez por cada cosa, aunque la caja
    /// lo reintente). Se avisa con otra conexión a la base: la de la operación quedó a medias.
    /// </summary>
    private static async ValueTask<object?> AvisarSiNoSubeAsync(EndpointFilterInvocationContext contexto, EndpointFilterDelegate siguiente)
    {
        try
        {
            return await siguiente(contexto);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            var id = contexto.Arguments.Select(a => a?.GetType().GetProperty("Id")?.GetValue(a)).OfType<Guid>().FirstOrDefault();
            using var alcance = contexto.HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            if (contexto.HttpContext.Items.TryGetValue(SeguridadCajas.CajaDeLaLlave, out var valor) && valor is Guid cajaId && await db.Cajas.FindAsync(cajaId) is { } caja)
            {
                AlertasTelegram.Avisar(db, caja.SucursalId, TipoAlerta.CajaSinConexion,
                    $"No se pudo subir · {caja.Nombre}\nLo que hizo sin conexión no entró: {ex.Message}\nQueda en la caja para revisarlo y se reintenta solo.",
                    $"bandeja:{id}");
                await db.SaveChangesAsync();
            }
            throw;
        }
    }
}
