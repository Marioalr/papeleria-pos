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

/// <summary>Permisos, autorizaciones, bitácora y efectivo en caja (entrega 2; los permisos por usuario, entrega 1).</summary>
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

        // Todo lo que puede quien entró, en una sola consulta: la caja pinta sus pestañas, teclas y candados con esto. Al entrar
        // ya viene en la sesión; esto queda para pedirlo otra vez (y para una caja de antes).
        api.MapGet("/usuarios/{usuarioId:guid}/permisos", async (Guid usuarioId, Guid sucursalId, PosDbContext db) =>
        {
            var usuarios = await new ServicioControl(db).UsuariosQueEntranAsync(sucursalId);
            var usuario = usuarios.SingleOrDefault(u => u.Id == usuarioId) ?? throw new ArgumentException("Ese usuario no está activo en esta sucursal.");
            return Permisos.DeLaSesion(usuario.Permisos, usuarios, usuario.Id);
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

        // La bitácora de la sucursal de esa caja, para quien entró con «Consultar la bitácora» (sin volver a escribir su PIN).
        api.MapGet("/cajas/{cajaId:guid}/bitacora", async (Guid cajaId, Guid usuarioId, DateOnly desde, DateOnly hasta, PosDbContext db) =>
        {
            var caja = await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cajaId) ?? throw new InvalidOperationException("La caja no existe.");
            if (hasta < desde)
                throw new ArgumentException("La fecha «hasta» va después de «desde».");
            var control = new ServicioControl(db);
            await control.ExigirParaVerAsync(usuarioId, caja.SucursalId, AccionControlada.ConsultarBitacora);
            var registros = await control.ConsultarAsync(caja.SucursalId,
                HoraMexico.AUtc(desde.ToDateTime(TimeOnly.MinValue)), HoraMexico.AUtc(hasta.AddDays(1).ToDateTime(TimeOnly.MinValue)));
            return registros.Select(r => new BitacoraDto(HoraMexico.ALocal(r.Registro.FechaUtc), r.Usuario, r.Registro.Accion, r.Registro.Detalle,
                r.Registro.MontoCentavos, r.Autorizo, r.Registro.Resultado)).ToList();
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
}
