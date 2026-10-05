using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
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

        // La copia local: catálogo con existencias, usuarios (PIN como hash), reglas, ticket y el último folio de la caja.
        api.MapGet("/cajas/{cajaId:guid}/paquete", async (Guid cajaId, PosDbContext db, CatalogoEnMemoria catalogo, IOptions<OpcionesTicket> opciones) =>
        {
            var caja = await db.Cajas.FindAsync(cajaId) ?? throw new InvalidOperationException("La caja no existe.");
            var sucursal = await db.Sucursales.FindAsync(caja.SucursalId);
            var (version, productos, codigos) = await catalogo.TodosAsync(db, caja.SucursalId);
            var usuarios = await db.Usuarios.AsNoTracking()
                .Where(u => u.Activo && (u.SucursalId == null || u.SucursalId == caja.SucursalId))
                .Select(u => new UsuarioLocalDto(u.Id, u.NombreUsuario, u.Nombre, u.Rol, u.PinHash, u.Activo))
                .ToListAsync();
            var reglas = ApiControl.ReglasEfectivas(await new ServicioControl(db).ReglasAsync(caja.SucursalId), caja.SucursalId);
            var ticket = await ApiConfiguracion.ConfiguracionAsync(db, caja.SucursalId, opciones.Value);
            var limite = await db.ConfiguracionesSucursal.Where(c => c.SucursalId == caja.SucursalId).Select(c => c.LimiteEfectivoCentavos).FirstOrDefaultAsync();
            return new PaqueteContingenciaDto(version, new CajaDto(caja.Id, caja.Nombre, caja.Serie, caja.SucursalId, sucursal!.Nombre, caja.Equipo),
                productos, codigos, usuarios, reglas, ticket.RenglonesEncabezado, ticket.RenglonesPie, ApiConfiguracion.Logo(ticket),
                await new RegistroCaja(db).UltimoFolioAsync(caja.SucursalId, caja.Serie), limite,
                (await ApiDescuentos.ClientesAsync(db, caja.SucursalId)).Where(c => c.Activo).Select(ApiDescuentos.Dto).ToList());
        });

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

        bandeja.MapPost("/ventas", async (VentaContingenciaDto v, PosDbContext db, CatalogoEnMemoria catalogo) =>
        {
            if (await db.Ventas.AnyAsync(x => x.Id == v.Id))
                return Results.NoContent();
            var caja = await db.Cajas.FindAsync(v.CajaId) ?? throw new InvalidOperationException("La caja no existe.");
            var lineas = v.Lineas.Select(l => new LineaSolicitada(l.VarianteId, l.PresentacionId, l.CantidadMilesimas, l.Descripcion, l.PrecioCentavos,
                DescuentoCentesimas: l.DescuentoCentesimas, DescuentoCentavos: l.DescuentoCentavos, MotivoDescuento: l.MotivoDescuento)).ToList();
            var venta = await ApiCaja.ArmarVentaAsync(db, catalogo, caja, v.TurnoId, v.CajeroId, v.Id, lineas);
            venta.ClienteEspecialId = v.ClienteEspecialId;
            await new RegistroCaja(db).CobroDeContingenciaAsync(venta, v.Cobros, v.Folio, v.PagadaUtc);
            return Results.NoContent();
        });

        bandeja.MapPost("/turnos/{turnoId:guid}/movimientos", async (Guid turnoId, MovimientoContingenciaDto m, PosDbContext db) =>
        {
            await new RegistroCaja(db).MovimientoDeContingenciaAsync(turnoId, m);
            return Results.NoContent();
        });

        bandeja.MapPost("/bitacora", async (BitacoraContingenciaDto b, PosDbContext db) =>
        {
            await new ServicioControl(db).RegistrarDeContingenciaAsync(b);
            return Results.NoContent();
        });

        bandeja.MapPost("/turnos/{turnoId:guid}/corte-z", async (Guid turnoId, CorteContingenciaDto c, PosDbContext db, ServicioRespaldos respaldos) =>
        {
            var corte = await ApiCaja.CorteDtoAsync(db, await new RegistroCaja(db).CorteZDeContingenciaAsync(turnoId, c));
            _ = Task.Run(() => respaldos.Hacer($"z-{corte.FechaLocal:HHmm}"));
            return corte;
        });
    }

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
