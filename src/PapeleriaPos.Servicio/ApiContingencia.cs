using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
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
                await new RegistroCaja(db).UltimoFolioAsync(caja.SucursalId, caja.Serie), limite);
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

        api.MapPost("/bandeja/turnos", async (TurnoContingenciaDto t, PosDbContext db) =>
            await ApiCaja.TurnoDtoAsync(db, await new RegistroCaja(db).TurnoDeContingenciaAsync(t)));

        api.MapPost("/bandeja/ventas", async (VentaContingenciaDto v, PosDbContext db, CatalogoEnMemoria catalogo) =>
        {
            if (await db.Ventas.AnyAsync(x => x.Id == v.Id))
                return Results.NoContent();
            var caja = await db.Cajas.FindAsync(v.CajaId) ?? throw new InvalidOperationException("La caja no existe.");
            var lineas = v.Lineas.Select(l => new LineaSolicitada(l.VarianteId, l.PresentacionId, l.CantidadMilesimas, l.Descripcion, l.PrecioCentavos)).ToList();
            var venta = await ApiCaja.ArmarVentaAsync(db, catalogo, caja, v.TurnoId, v.CajeroId, v.Id, lineas);
            await new RegistroCaja(db).CobroDeContingenciaAsync(venta, v.Cobros, v.Folio, v.PagadaUtc);
            return Results.NoContent();
        });

        api.MapPost("/bandeja/turnos/{turnoId:guid}/movimientos", async (Guid turnoId, MovimientoContingenciaDto m, PosDbContext db) =>
        {
            await new RegistroCaja(db).MovimientoDeContingenciaAsync(turnoId, m);
            return Results.NoContent();
        });

        api.MapPost("/bandeja/bitacora", async (BitacoraContingenciaDto b, PosDbContext db) =>
        {
            await new ServicioControl(db).RegistrarDeContingenciaAsync(b);
            return Results.NoContent();
        });

        api.MapPost("/bandeja/turnos/{turnoId:guid}/corte-z", async (Guid turnoId, CorteContingenciaDto c, PosDbContext db, ServicioRespaldos respaldos) =>
        {
            var corte = await ApiCaja.CorteDtoAsync(db, await new RegistroCaja(db).CorteZDeContingenciaAsync(turnoId, c));
            _ = Task.Run(() => respaldos.Hacer($"z-{corte.FechaLocal:HHmm}"));
            return corte;
        });
    }
}
