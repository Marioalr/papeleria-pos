using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Impresiones (entrega 5): lo que mandan los agentes, lo que falta cobrar, las no vendidas, cotizar un PDF, las copias
/// del panel y la impresora de la sucursal (niveles, contador y calibración).
/// </summary>
public static class ApiImpresiones
{
    public static void MapearApiImpresiones(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // El agente de la caja manda cada trabajo impreso; el servicio le pone costo y precio y lo suma al lote que se
        // verifica con el contador de la impresora.
        api.MapPost("/impresiones", async (TrabajoMedidoDto t, PosDbContext db, VigilanciaImpresora vigilancia) =>
        {
            var nuevo = await db.TrabajosImpresion.FindAsync(t.Id) is null;
            var trabajo = await new RegistroImpresiones(db).RegistrarAsync(t);
            if (nuevo)
                await vigilancia.TrabajoRegistradoAsync(trabajo);
            return new { trabajo.Id, trabajo.PrecioCentavos };
        });

        // Lo que falta cobrar en la sucursal, de todas las PC. El costo solo va si quien pregunta es el dueño.
        api.MapGet("/sucursales/{sucursalId:guid}/impresiones/por-cobrar", async (Guid sucursalId, Guid usuarioId, PosDbContext db) =>
        {
            var registro = new RegistroImpresiones(db);
            var trabajos = await registro.PorCobrarAsync(sucursalId);
            var costos = await registro.CostosAsync(sucursalId);
            var esDueno = await RolAsync(db, usuarioId) == Rol.Dueno;
            var nombres = await NombresAsync(db, trabajos);
            return trabajos.Select(t => PorCobrar(t, nombres, esDueno, costos)).ToList();
        });

        api.MapPost("/impresiones/{id:guid}/no-vendida", async (Guid id, SolicitudNoVendida s, PosDbContext db) =>
        {
            await new RegistroImpresiones(db).NoVendidaAsync(id, s);
            return Results.NoContent();
        });

        // Con un atasco o una cancelación que contó la impresora: se cobra solo lo que salió.
        api.MapPost("/impresiones/{id:guid}/solo-lo-que-salio", async (Guid id, SolicitudSoloLoQueSalio s, PosDbContext db) =>
        {
            var registro = new RegistroImpresiones(db);
            var trabajo = await registro.SoloLoQueSalioAsync(id);
            return PorCobrar(trabajo, await NombresAsync(db, [trabajo]), await RolAsync(db, s.UsuarioId) == Rol.Dueno,
                await registro.CostosAsync(trabajo.SucursalId));
        });

        // El desglose de la línea "Impresiones": todo lo que se imprimió en un día y cómo se cobró (dueño y encargado).
        api.MapGet("/sucursales/{sucursalId:guid}/impresiones", async (Guid sucursalId, DateOnly fecha, Guid usuarioId, PosDbContext db) =>
        {
            var rol = await RolAsync(db, usuarioId);
            if (rol is not (Rol.Dueno or Rol.Encargado))
                throw new PermisoRequerido(AccionControlada.ConsultarBitacora, NivelPermiso.NoPermitido,
                    "El desglose de las impresiones solo lo ven el dueño y el encargado.");
            var trabajos = await new RegistroImpresiones(db).DelDiaAsync(sucursalId, fecha);
            var nombres = await NombresAsync(db, trabajos);
            return trabajos.Select(t => new ImpresionDelDiaDto(t.Id, t.Tipo, t.Documento, t.Pc, HoraMexico.ALocal(t.ImpresoUtc), t.Caras,
                t.CarasContadas, t.Color, t.PrecioCentavos, t.Cobro, t.MotivoNoVendida, t.AtendioId is { } a ? nombres.GetValueOrDefault(a) : null,
                rol == Rol.Dueno ? RegistroImpresiones.Costo(t) : null)).ToList();
        });

        // ── Cotizar y copias ─────────────────────────────────────────────────

        api.MapGet("/sucursales/{sucursalId:guid}/impresiones/opciones", (Guid sucursalId, PosDbContext db) =>
            new RegistroImpresiones(db).OpcionesAsync(sucursalId));

        // La caja midió el PDF; aquí se le pone precio con la configuración de la sucursal.
        api.MapPost("/impresiones/cotizar", async (SolicitudCotizacion s, PosDbContext db) =>
            await new RegistroImpresiones(db).CotizarAsync(s, s.UsuarioId is { } u && await RolAsync(db, u) == Rol.Dueno));

        api.MapPost("/impresiones/copias/precio", async (SolicitudPrecioCopias s, PosDbContext db) =>
        {
            var (costo, descripcion) = await new RegistroImpresiones(db).CalcularCopiasAsync(s.SucursalId, s.Cantidad, s.Color, s.Tamano, s.Duplex);
            return new PrecioCopiasDto((long)Math.Round(costo.Price * 100, MidpointRounding.AwayFromZero), descripcion);
        });

        api.MapPost("/impresiones/copias", async (SolicitudCopias s, PosDbContext db) =>
        {
            var registro = new RegistroImpresiones(db);
            var trabajo = await registro.RegistrarCopiasAsync(s);
            return PorCobrar(trabajo, await NombresAsync(db, [trabajo]), false, await registro.CostosAsync(trabajo.SucursalId));
        });

        // ── La impresora ─────────────────────────────────────────────────────

        api.MapGet("/sucursales/{sucursalId:guid}/impresora", (Guid sucursalId, PosDbContext db, VigilanciaImpresora vigilancia) =>
            new ServicioImpresora(db).EstadoAsync(sucursalId, vigilancia.Error(sucursalId)));

        api.MapPut("/sucursales/{sucursalId:guid}/impresora", async (Guid sucursalId, SolicitudConfiguracionImpresora s, PosDbContext db,
            VigilanciaImpresora vigilancia) =>
        {
            var configuracion = await new ServicioImpresora(db).GuardarConfiguracionAsync(sucursalId, s);
            var error = configuracion.ImpresoraHost is null ? null : await vigilancia.LeerAhoraAsync(sucursalId);
            return await new ServicioImpresora(db).EstadoAsync(sucursalId, error);
        });

        api.MapPost("/sucursales/{sucursalId:guid}/impresora/leer", async (Guid sucursalId, PosDbContext db, VigilanciaImpresora vigilancia) =>
        {
            var error = await vigilancia.LeerAhoraAsync(sucursalId);
            return await new ServicioImpresora(db).EstadoAsync(sucursalId, error);
        });

        api.MapGet("/sucursales/{sucursalId:guid}/impresora/calibracion", (Guid sucursalId, PosDbContext db) =>
            new ServicioImpresora(db).CalibracionAsync(sucursalId));

        api.MapPost("/sucursales/{sucursalId:guid}/impresora/calibracion", (Guid sucursalId, SolicitudCredenciales s, PosDbContext db) =>
            new ServicioImpresora(db).AplicarCalibracionAsync(sucursalId, s));
    }

    private static async Task<Rol?> RolAsync(PosDbContext db, Guid usuarioId) =>
        await db.Usuarios.Where(u => u.Id == usuarioId && u.Activo).Select(u => (Rol?)u.Rol).FirstOrDefaultAsync();

    private static async Task<Dictionary<Guid, string>> NombresAsync(PosDbContext db, IEnumerable<TrabajoImpresion> trabajos)
    {
        var ids = trabajos.Where(t => t.AtendioId != null).Select(t => t.AtendioId!.Value).Distinct().ToList();
        return await db.Usuarios.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre);
    }

    private static ImpresionPorCobrarDto PorCobrar(TrabajoImpresion t, Dictionary<Guid, string> nombres, bool esDueno, CostSettings costos) =>
        new(t.Id, t.Documento, t.Pc, HoraMexico.ALocal(t.ImpresoUtc), t.Paginas, t.Caras, t.Color, t.Tamano, t.PrecioCentavos,
            t.AtendioId is { } a ? nombres.GetValueOrDefault(a) : null, t.Miniatura is { } m ? Convert.ToBase64String(m) : null, t.Nota,
            esDueno ? RegistroImpresiones.Costo(t) : null, t.Tipo, t.CarasContadas, RegistroImpresiones.PrecioSoloLoQueSalio(t, costos),
            t.PrecioCompletoCentavos);
}
