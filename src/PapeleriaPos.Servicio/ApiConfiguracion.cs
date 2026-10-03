using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Servicio;

/// <summary>Configuración del ticket por sucursal. Leerla es libre; cambiarla lo autoriza el encargado o el dueño.</summary>
public static class ApiConfiguracion
{
    public static void MapearApiConfiguracion(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/sucursales/{sucursalId:guid}/ticket").AddEndpointFilter(async (contexto, siguiente) =>
        {
            try
            {
                return await siguiente(contexto);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            {
                return Results.BadRequest(new ErrorDto(ex.Message));
            }
        });

        api.MapGet("", async (Guid sucursalId, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
            Dto(await ConfiguracionAsync(db, sucursalId, opciones.Value)));

        api.MapPut("/textos", async (Guid sucursalId, SolicitudTextosTicket s, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
        {
            await AutorizarAsync(db, s.Usuario, s.Pin);
            var configuracion = await ParaCambiarAsync(db, sucursalId, opciones.Value);
            configuracion.CambiarTextos(s.Encabezado, s.Pie);
            await db.SaveChangesAsync();
            return Dto(configuracion);
        });

        api.MapPut("/logo", async (Guid sucursalId, SolicitudLogo s, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
        {
            await AutorizarAsync(db, s.Usuario, s.Pin);
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

    /// <summary>Usuario y PIN de un encargado o del dueño; si no, error.</summary>
    public static async Task<Usuario> AutorizarAsync(PosDbContext db, string usuario, string pin)
    {
        var encontrado = await db.Usuarios.SingleOrDefaultAsync(u => u.NombreUsuario == usuario.Trim().ToLower());
        var resultado = encontrado?.VerificarPin(pin, db.Reloj.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync();
        return resultado switch
        {
            ResultadoPin.Correcto when encontrado!.PuedeHacerCorte => encontrado,
            ResultadoPin.Correcto => throw new InvalidOperationException("Esto lo autoriza el encargado o el dueño."),
            ResultadoPin.Bloqueado => throw new InvalidOperationException("Demasiados intentos: espera unos minutos."),
            _ => throw new InvalidOperationException("Usuario o PIN del encargado incorrecto."),
        };
    }
}
