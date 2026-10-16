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
    }

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
            ? new ConfiguracionTicketDto(c.RenglonesEncabezado, c.RenglonesPie, logo, Convert.ToBase64String(LogoTicket.ComoPng(logo)))
            : new ConfiguracionTicketDto(c.RenglonesEncabezado, c.RenglonesPie, null, null);

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
