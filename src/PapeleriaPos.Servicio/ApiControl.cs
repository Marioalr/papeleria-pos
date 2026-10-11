using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Servicio;

/// <summary>Errores de negocio para el cajero: 400 con el mensaje, o 403 cuando lo detiene una regla.</summary>
public static class ErroresApi
{
    public static RouteGroupBuilder ConErrores(this RouteGroupBuilder grupo) =>
        grupo.AddEndpointFilter(async (contexto, siguiente) =>
        {
            try
            {
                return await siguiente(contexto);
            }
            catch (PermisoRequerido ex)
            {
                var codigo = ex.Nivel == NivelPermiso.RequiereAutorizacion ? "requiere-autorizacion" : "no-permitido";
                return Results.Json(new ErrorDto(ex.Message, codigo), statusCode: StatusCodes.Status403Forbidden);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            {
                return Results.BadRequest(new ErrorDto(ex.Message));
            }
        });
}

/// <summary>Reglas, autorizaciones, bitácora y efectivo en caja (entrega 2).</summary>
public static class ApiControl
{
    public static void MapearApiControl(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        api.MapPost("/permisos", async (SolicitudPermiso s, PosDbContext db) =>
        {
            var decision = await new ServicioControl(db).EvaluarAsync(s.UsuarioId, s.SucursalId, s.Accion, s.MontoCentavos);
            return new DecisionDto(decision.Nivel, decision.PideMotivo);
        });

        // Todo lo que puede quien entró, en una sola consulta: la caja pinta sus pestañas, teclas y candados con esto.
        api.MapGet("/usuarios/{usuarioId:guid}/permisos", async (Guid usuarioId, Guid sucursalId, PosDbContext db) =>
        {
            var usuarios = await db.Usuarios.AsNoTracking().Where(u => u.Activo && (u.SucursalId == null || u.SucursalId == sucursalId))
                .Select(u => new { u.Id, u.Nombre, u.Rol }).ToListAsync();
            var usuario = usuarios.SingleOrDefault(u => u.Id == usuarioId) ?? throw new ArgumentException("Ese usuario no está activo en esta sucursal.");
            return Permisos.DeLaSesion(await new ServicioControl(db).ReglasAsync(sucursalId), usuarios.Select(u => (u.Id, u.Nombre, u.Rol)),
                usuario.Id, usuario.Rol, sucursalId);
        });

        api.MapPost("/autorizaciones", async (SolicitudAutorizacion s, PosDbContext db) =>
        {
            var autorizacion = await new ServicioControl(db).AutorizarAsync(s.Accion, s.SucursalId, s.CajaId, s.SolicitanteId, s.Usuario, s.Pin, s.MontoCentavos, s.Detalle);
            var nombre = await db.Usuarios.Where(u => u.Id == autorizacion.AutorizadorId).Select(u => u.Nombre).SingleAsync();
            return new AutorizacionDto(autorizacion.Id, autorizacion.VenceUtc, nombre);
        });

        // Solo lo que el servicio no puede ver por sí mismo: quitar un producto antes de cobrar.
        api.MapPost("/bitacora", async (SolicitudBitacora s, PosDbContext db) =>
        {
            if (s.Accion != AccionControlada.QuitarProducto)
                throw new ArgumentException("Esa acción la registra el servicio al hacerla.");
            await new ServicioControl(db).RequerirAsync(s.UsuarioId, s.SucursalId, s.CajaId, s.Accion, s.MontoCentavos, s.Detalle);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        api.MapGet("/turnos/{turnoId:guid}/efectivo", async (Guid turnoId, PosDbContext db) =>
        {
            var (esperado, limite) = await new RegistroCaja(db).EfectivoAsync(turnoId);
            return new EfectivoCajaDto(esperado, limite);
        });

        api.MapGet("/sucursales/{sucursalId:guid}/configuracion", async (Guid sucursalId, PosDbContext db) =>
            new ConfiguracionSucursalDto(await db.ConfiguracionesSucursal.Where(c => c.SucursalId == sucursalId)
                .Select(c => c.LimiteEfectivoCentavos).FirstOrDefaultAsync()));

        api.MapPut("/sucursales/{sucursalId:guid}/configuracion", async (Guid sucursalId, SolicitudConfiguracionSucursal s, PosDbContext db) =>
        {
            if (s.LimiteEfectivoCentavos is <= 0)
                throw new ArgumentException("El límite de efectivo debe ser mayor a cero (o vacío para no tener límite).");
            var control = new ServicioControl(db);
            var dueno = await control.IdentificarAsync(s.Usuario, s.Pin, sucursalId, AccionControlada.EditarReglas);
            var configuracion = await db.ConfiguracionesSucursal.FindAsync(sucursalId);
            if (configuracion is null)
            {
                configuracion = new ConfiguracionSucursal { SucursalId = sucursalId };
                db.ConfiguracionesSucursal.Add(configuracion);
            }
            configuracion.LimiteEfectivoCentavos = s.LimiteEfectivoCentavos;
            control.Registrar(sucursalId, null, dueno.Id, AccionControlada.EditarReglas,
                s.LimiteEfectivoCentavos is { } l ? $"Límite de efectivo en caja: {l / 100m:N2}" : "Sin límite de efectivo en caja");
            await db.SaveChangesAsync();
            return new ConfiguracionSucursalDto(configuracion.LimiteEfectivoCentavos);
        });

        api.MapGet("/sucursales/{sucursalId:guid}/reglas", async (Guid sucursalId, PosDbContext db) =>
            ReglasEfectivas(await new ServicioControl(db).ReglasAsync(sucursalId), sucursalId));

        api.MapPut("/sucursales/{sucursalId:guid}/reglas", async (Guid sucursalId, SolicitudReglas s, PosDbContext db) =>
        {
            var control = new ServicioControl(db);
            var dueno = await control.IdentificarAsync(s.Usuario, s.Pin, sucursalId, AccionControlada.EditarReglas);
            var guardadas = await db.ReglasPermiso.Where(r => r.SucursalId == sucursalId).ToListAsync();
            foreach (var r in s.Reglas.Where(r => r.Rol != Rol.Dueno && !Permisos.SoloDelDueno(r.Accion)))
            {
                if (r.UmbralCentavos is <= 0)
                    throw new ArgumentException("Los umbrales deben ser mayores a cero.");
                var regla = guardadas.FirstOrDefault(g => g.Accion == r.Accion && g.Rol == r.Rol);
                if (regla is null)
                {
                    regla = new ReglaPermiso { Accion = r.Accion, Rol = r.Rol, SucursalId = sucursalId };
                    db.ReglasPermiso.Add(regla);
                }
                (regla.Nivel, regla.UmbralCentavos, regla.PideMotivo) = (r.Nivel, r.UmbralCentavos, r.PideMotivo);
            }
            control.Registrar(sucursalId, null, dueno.Id, AccionControlada.EditarReglas, "Cambió las reglas de la sucursal");
            await db.SaveChangesAsync();
            return ReglasEfectivas(await control.ReglasAsync(sucursalId), sucursalId);
        });
    }

    /// <summary>
    /// Movimiento de efectivo: lo valida el control, se guarda y regresa el comprobante para imprimir. En el piloto de la
    /// caja 2 también entra a la bandeja hacia eleventa, junto con el movimiento, y se manda ya.
    /// </summary>
    public static async Task<ComprobanteMovimientoDto> MovimientoAsync(
        Guid turnoId, SolicitudMovimiento s, PosDbContext db, OpcionesTicket opciones, EnvioVentasEleventa eleventa)
    {
        var movimiento = await new RegistroCaja(db).RegistrarMovimientoAsync(turnoId, s.Tipo, s.MontoCentavos, s.Motivo, s.UsuarioId, s.Id,
            aEleventa: eleventa.Activo);
        eleventa.Despertar();
        var turno = await db.Turnos.FindAsync(turnoId);
        var caja = await db.Cajas.FindAsync(turno!.CajaId);
        var usuario = await db.Usuarios.Where(u => u.Id == s.UsuarioId).Select(u => u.Nombre).SingleAsync();
        var encabezado = (await ApiConfiguracion.ConfiguracionAsync(db, turno.SucursalId, opciones)).RenglonesEncabezado;
        return new ComprobanteMovimientoDto(movimiento.Id.ToString()[^6..].ToUpperInvariant(), movimiento.Tipo, movimiento.MontoCentavos,
            movimiento.Motivo, HoraMexico.ALocal(movimiento.FechaUtc), caja!.Nombre, usuario, encabezado);
    }

    /// <summary>Todas las combinaciones acción × rol (sin el dueño), con la regla que aplica hoy en la sucursal.</summary>
    internal static List<ReglaDto> ReglasEfectivas(List<ReglaPermiso> reglas, Guid sucursalId) =>
        Enum.GetValues<AccionControlada>().Where(a => !Permisos.SoloDelDueno(a))
            .SelectMany(a => new[] { Rol.Cajero, Rol.Encargado }.Select(rol =>
            {
                var regla = reglas.FirstOrDefault(r => r.Accion == a && r.Rol == rol && r.SucursalId == sucursalId)
                    ?? reglas.FirstOrDefault(r => r.Accion == a && r.Rol == rol && r.SucursalId == null)
                    ?? Permisos.Iniciales.FirstOrDefault(r => r.Accion == a && r.Rol == rol);
                return regla is null
                    ? new ReglaDto(a, rol, NivelPermiso.RequiereAutorizacion, null, false)
                    : new ReglaDto(a, rol, regla.Nivel, regla.UmbralCentavos, regla.PideMotivo);
            }))
            .ToList();
}
