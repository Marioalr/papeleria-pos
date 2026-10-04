using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Servicio;

/// <summary>Configuración del ticket por sucursal. Leerla es libre; cambiarla lo autoriza el encargado o el dueño.</summary>
public static class ApiConfiguracion
{
    public static void MapearApiConfiguracion(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/sucursales/{sucursalId:guid}/ticket").ConErrores();

        api.MapGet("", async (Guid sucursalId, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
            Dto(await ConfiguracionAsync(db, sucursalId, opciones.Value)));

        api.MapPut("/textos", async (Guid sucursalId, SolicitudTextosTicket s, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
        {
            var control = new ServicioControl(db);
            var usuario = await control.IdentificarAsync(s.Usuario, s.Pin, sucursalId, AccionControlada.ConfigurarTicket);
            control.Registrar(sucursalId, null, usuario.Id, AccionControlada.ConfigurarTicket, "Cambió el encabezado y el pie del ticket");
            var configuracion = await ParaCambiarAsync(db, sucursalId, opciones.Value);
            configuracion.CambiarTextos(s.Encabezado, s.Pie);
            await db.SaveChangesAsync();
            return Dto(configuracion);
        });

        api.MapPut("/logo", async (Guid sucursalId, SolicitudLogo s, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
        {
            var control = new ServicioControl(db);
            var usuario = await control.IdentificarAsync(s.Usuario, s.Pin, sucursalId, AccionControlada.ConfigurarTicket);
            control.Registrar(sucursalId, null, usuario.Id, AccionControlada.ConfigurarTicket, string.IsNullOrEmpty(s.ImagenBase64) ? "Quitó el logotipo del ticket" : "Cambió el logotipo del ticket");
            var configuracion = await ParaCambiarAsync(db, sucursalId, opciones.Value);
            if (string.IsNullOrEmpty(s.ImagenBase64))
                configuracion.QuitarLogo();
            else
            {
                var logo = LogoTicket.DesdeImagen(Convert.FromBase64String(s.ImagenBase64));
                configuracion.CambiarLogo(logo.Ancho, logo.Alto, Convert.FromBase64String(logo.BitsBase64));
            }
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
