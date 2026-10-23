using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// La Configuración nueva (entrega 7, docs/propuesta-interfaz.md «Configuración»): la de la tienda, los rápidos y el ticket.
/// Leerlas es libre (todas las cajas las usan). Cambiarlas lo hace quien entró a la caja con su permiso, sin volver a
/// escribir su PIN, como «Usuarios y permisos»: cada cambio se guarda al momento y queda en la bitácora.
/// </summary>
public static class ApiConfiguracion
{
    public static void MapearApiConfiguracion(this IEndpointRouteBuilder app)
    {
        var sucursales = app.MapGroup("/api/sucursales/{sucursalId:guid}").ConErrores().SoloSuCaja();

        // Tienda, Venta y cobro y Efectivo y corte: el nombre de la entrada, las fichas de la espera, el fondo sugerido…
        sucursales.MapGet("/configuracion", (Guid sucursalId, PosDbContext db) => new RegistroConfiguracion(db).LeerAsync(sucursalId));

        sucursales.MapGet("/ticket", async (Guid sucursalId, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
            Dto(await ConfiguracionAsync(db, sucursalId, opciones.Value)));

        // usuarioId de las solicitudes: quien entró a la caja.
        var api = app.MapGroup("/api/cajas/{cajaId:guid}/configuracion").ConErrores().SoloSuCaja();

        // Con «Configuración de la tienda».
        api.MapPut("", (Guid cajaId, SolicitudConfiguracionSucursal s, PosDbContext db) => new RegistroConfiguracion(db).GuardarAsync(cajaId, s));

        api.MapPut("/rapidos", (Guid cajaId, SolicitudRapidos s, PosDbContext db) => new RegistroConfiguracion(db).GuardarRapidosAsync(cajaId, s));

        // Con «Configurar el ticket».
        api.MapPut("/ticket/textos", async (Guid cajaId, SolicitudTextosTicket s, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
        {
            var (caja, control) = await ExigirTicketAsync(db, cajaId, s.UsuarioId);
            var configuracion = await ParaCambiarAsync(db, caja.SucursalId, opciones.Value);
            var (encabezado, pie) = (configuracion.Encabezado, configuracion.Pie);
            configuracion.CambiarTextos(s.Encabezado ?? [], s.Pie ?? []);
            var cambios = new List<string>();
            if (configuracion.Encabezado != encabezado)
                cambios.Add("el encabezado");
            if (configuracion.Pie != pie)
                cambios.Add("el pie");
            if (cambios.Count > 0)
            {
                control.Registrar(caja.SucursalId, caja.Id, s.UsuarioId, AccionControlada.ConfigurarTicket, $"Cambió {string.Join(" y ", cambios)} del ticket");
                await db.SaveChangesAsync();
            }
            return Dto(configuracion);
        });

        api.MapPut("/ticket/logo", async (Guid cajaId, SolicitudLogo s, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
        {
            var (caja, control) = await ExigirTicketAsync(db, cajaId, s.UsuarioId);
            var configuracion = await ParaCambiarAsync(db, caja.SucursalId, opciones.Value);
            if (string.IsNullOrEmpty(s.ImagenBase64))
                configuracion.QuitarLogo();
            else
            {
                var logo = LogoTicket.DesdeImagen(Convert.FromBase64String(s.ImagenBase64));
                configuracion.CambiarLogo(logo.Ancho, logo.Alto, Convert.FromBase64String(logo.BitsBase64));
            }
            control.Registrar(caja.SucursalId, caja.Id, s.UsuarioId, AccionControlada.ConfigurarTicket,
                string.IsNullOrEmpty(s.ImagenBase64) ? "Quitó el logotipo del ticket" : "Cambió el logotipo del ticket");
            await db.SaveChangesAsync();
            return Dto(configuracion);
        });

        // «Al cobrar»: imprimir el ticket, ofrecerlo o no imprimirlo, en todas las cajas de la sucursal. Llega a cada caja con
        // su copia (sin conexión) y con el ticket de cada cobro (en línea).
        api.MapPut("/ticket/al-cobrar", async (Guid cajaId, SolicitudAlCobrar s, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
        {
            var (caja, control) = await ExigirTicketAsync(db, cajaId, s.UsuarioId);
            var configuracion = await ParaCambiarAsync(db, caja.SucursalId, opciones.Value);
            if (configuracion.AlCobrar != s.AlCobrar)
            {
                configuracion.CambiarAlCobrar(s.AlCobrar);
                control.Registrar(caja.SucursalId, caja.Id, s.UsuarioId, AccionControlada.ConfigurarTicket,
                    $"Cambió qué se hace con el ticket al cobrar: «{ConfiguracionTicket.Nombre(s.AlCobrar)}»");
                await db.SaveChangesAsync();
            }
            return Dto(configuracion);
        });

        // Respaldos (2026-10-22): con «Configuración de la tienda». Ver dónde se guardan, el último, cómo va «Respaldar ahora», que
        // usa el mismo ServicioRespaldos de la copia diaria y la del corte Z, y la restauración pedida o la de este arranque.
        api.MapGet("/respaldos", async (Guid cajaId, Guid usuarioId, PosDbContext db, ServicioRespaldos respaldos, RestauracionDeRespaldos restauracion) =>
        {
            var caja = await CajaAsync(db, cajaId);
            await new ServicioControl(db).ExigirParaVerAsync(usuarioId, caja.SucursalId, AccionControlada.ConfigurarTienda);
            return EstadoDeRespaldos(respaldos, restauracion);
        });

        // «Respaldar ahora»: empieza la copia en segundo plano y contesta enseguida; la pantalla pregunta cómo va (revisión del PR
        // #133). Si ya hay una en camino, no empieza otra. Al terminar queda en la bitácora, bien o mal.
        api.MapPost("/respaldos", async (Guid cajaId, SolicitudRespaldo s, PosDbContext db, ServicioRespaldos respaldos, RestauracionDeRespaldos restauracion,
            IServiceScopeFactory alcances) =>
        {
            var caja = await CajaAsync(db, cajaId);
            await new ServicioControl(db).ExigirDeLaSesionAsync(s.UsuarioId, caja.SucursalId, caja.Id, AccionControlada.ConfigurarTienda);
            respaldos.IniciarAhora(async terminado =>
            {
                await using var alcance = alcances.CreateAsyncScope();
                var otra = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
                new ServicioControl(otra).Registrar(caja.SucursalId, caja.Id, s.UsuarioId, AccionControlada.ConfigurarTienda,
                    terminado.Hecho is { } hecho ? $"Respaldó la base: {Path.GetFileName(hecho.Ruta)} ({hecho.Tamano})" : $"Pidió respaldar la base: {terminado.Fallo}");
                await otra.SaveChangesAsync();
            });
            return EstadoDeRespaldos(respaldos, restauracion);
        });

        // Restaurar un respaldo (2026-10-23): la lista, con el de antes del apagón señalado si la base no pasó la revisión al arrancar.
        api.MapGet("/respaldos/lista", async (Guid cajaId, Guid usuarioId, PosDbContext db, RestauracionDeRespaldos restauracion, RevisionAlArrancar revision,
            IReinicioDelServicio reinicio, OpcionesEleventa eleventa) =>
        {
            var caja = await CajaAsync(db, cajaId);
            await new ServicioControl(db).ExigirParaVerAsync(usuarioId, caja.SucursalId, AccionControlada.ConfigurarTienda);
            return new RestaurablesDto(restauracion.Listar(revision.BaseDanada ? revision.Respaldo : null), reinicio.Automatico, eleventa.Escribir);
        });

        // Quien tiene «Configuración de la tienda» y vuelve a escribir su PIN: deja el pedido y, si es servicio de Windows, en cuanto
        // sale la respuesta el servicio se detiene y Windows lo vuelve a arrancar ya con el respaldo (RestauracionDeRespaldos). En la
        // caja del navegador se restaura la próxima vez que se arranque.
        api.MapPost("/respaldos/restaurar", async (Guid cajaId, SolicitudRestaurar s, PosDbContext db, ServicioRespaldos respaldos,
            RestauracionDeRespaldos restauracion, IReinicioDelServicio reinicio, HttpContext http, ILoggerFactory registros) =>
        {
            var caja = await CajaAsync(db, cajaId);
            var control = new ServicioControl(db);
            await control.ExigirDeLaSesionAsync(s.UsuarioId, caja.SucursalId, caja.Id, AccionControlada.ConfigurarTienda);
            await control.ConfirmarPinAsync(s.UsuarioId, s.Pin, caja.SucursalId, caja.Id, AccionControlada.ConfigurarTienda, $"restaurar el respaldo {s.Archivo}");
            if (respaldos.EstadoAhora is { Haciendo: true })
                throw new InvalidOperationException("Se está haciendo un respaldo («Respaldar ahora»): restaura cuando termine.");
            var pedida = restauracion.Pedir(s.Archivo, s.UsuarioId, caja.Id, caja.SucursalId, reinicio.Automatico);
            control.Registrar(caja.SucursalId, caja.Id, s.UsuarioId, AccionControlada.ConfigurarTienda,
                $"Pidió restaurar {pedida.Descripcion}: " + (reinicio.Automatico ? "la caja principal se reinicia" : "se restaura al volver a arrancar la caja principal"));
            try
            {
                await db.SaveChangesAsync();
            }
            catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
            {
                // La base que se va a reemplazar puede ser la dañada: el pedido ya está; la base restaurada lo anota al arrancar.
                registros.CreateLogger("Restaurar").LogWarning(ex, "No se pudo anotar en la bitácora el pedido de restaurar {Archivo}.", s.Archivo);
            }
            // Después de que la respuesta salió: la caja que lo pidió sabe que se pidió antes de que el servicio se detenga.
            if (reinicio.Automatico)
                http.Response.OnCompleted(() =>
                {
                    reinicio.Reiniciar();
                    return Task.CompletedTask;
                });
            return EstadoDeRespaldos(respaldos, restauracion);
        });

        // Sin el reinicio automático (la caja del navegador): quitar el pedido mientras la caja principal no vuelve a arrancar.
        api.MapPost("/respaldos/restaurar/cancelar", async (Guid cajaId, SolicitudRespaldo s, PosDbContext db, ServicioRespaldos respaldos,
            RestauracionDeRespaldos restauracion) =>
        {
            var caja = await CajaAsync(db, cajaId);
            var control = new ServicioControl(db);
            await control.ExigirDeLaSesionAsync(s.UsuarioId, caja.SucursalId, caja.Id, AccionControlada.ConfigurarTienda);
            if (restauracion.Cancelar() is { } cancelada)
            {
                control.Registrar(caja.SucursalId, caja.Id, s.UsuarioId, AccionControlada.ConfigurarTienda, $"Ya no se restaura {cancelada.Descripcion}");
                await db.SaveChangesAsync();
            }
            return EstadoDeRespaldos(respaldos, restauracion);
        });
    }

    private static RespaldosDto EstadoDeRespaldos(ServicioRespaldos respaldos, RestauracionDeRespaldos restauracion) =>
        new(respaldos.Carpeta, respaldos.Dias, respaldos.Ultimo(), respaldos.NoSeHacenPorque, respaldos.EstadoAhora, restauracion.Ultima);

    private static async Task<Caja> CajaAsync(PosDbContext db, Guid cajaId) =>
        await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cajaId) ?? throw new InvalidOperationException("La caja no existe.");

    /// <summary>La de la sucursal o, si nunca se ha guardado, los textos de appsettings.</summary>
    public static async Task<ConfiguracionTicket> ConfiguracionAsync(PosDbContext db, Guid sucursalId, OpcionesTicket opciones)
    {
        if (await db.ConfiguracionesTicket.AsNoTracking().SingleOrDefaultAsync(c => c.SucursalId == sucursalId) is { } guardada)
            return guardada;
        var predeterminada = new ConfiguracionTicket { SucursalId = sucursalId };
        predeterminada.CambiarTextos(opciones.Encabezado, opciones.Pie);
        return predeterminada;
    }

    public static LogoTicketDto? Logo(ConfiguracionTicket c) =>
        c.LogoBits is { } bits ? new LogoTicketDto(c.LogoAncho!.Value, c.LogoAlto!.Value, Convert.ToBase64String(bits)) : null;

    private static ConfiguracionTicketDto Dto(ConfiguracionTicket c) =>
        Logo(c) is { } logo
            ? new ConfiguracionTicketDto(c.RenglonesEncabezado, c.RenglonesPie, logo, Convert.ToBase64String(LogoTicket.ComoPng(logo)), c.AlCobrar)
            : new ConfiguracionTicketDto(c.RenglonesEncabezado, c.RenglonesPie, null, null, c.AlCobrar);

    /// <summary>Quien entró a la caja, con «Configurar el ticket» en «Puede»: si no, el error dice quién lo tiene.</summary>
    private static async Task<(Caja Caja, ServicioControl Control)> ExigirTicketAsync(PosDbContext db, Guid cajaId, Guid usuarioId)
    {
        var caja = await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cajaId) ?? throw new InvalidOperationException("La caja no existe.");
        var control = new ServicioControl(db);
        await control.ExigirDeLaSesionAsync(usuarioId, caja.SucursalId, caja.Id, AccionControlada.ConfigurarTicket);
        return (caja, control);
    }

    private static async Task<ConfiguracionTicket> ParaCambiarAsync(PosDbContext db, Guid sucursalId, OpcionesTicket opciones)
    {
        if (!await db.Sucursales.AnyAsync(s => s.Id == sucursalId))
            throw new InvalidOperationException("La sucursal no existe.");
        if (await db.ConfiguracionesTicket.SingleOrDefaultAsync(c => c.SucursalId == sucursalId) is { } guardada)
            return guardada;
        var nueva = await ConfiguracionAsync(db, sucursalId, opciones);
        db.ConfiguracionesTicket.Add(nueva);
        return nueva;
    }
}
