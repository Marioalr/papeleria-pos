using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Servicio.Telegram;

namespace PapeleriaPos.Servicio;

/// <summary>Varias cajas (entrega 3): saber si el servicio está vivo, dar de alta un equipo como caja y quitarlo.</summary>
public static partial class ApiCajas
{
    private static readonly string Version =
        typeof(ApiCajas).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    public static void MapearApiCajas(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores();

        // Abierto: la caja lo usa para encontrar el servicio en la red y, después, como latido.
        // Con el piloto con eleventa: la caja solo ofrece el efectivo, y el corte se hace en eleventa.
        api.MapGet("/estado", async (PosDbContext db, ModoPrueba prueba, OpcionesEleventa eleventa, RevisionAlArrancar revision, HttpContext http,
            PresenciaCajas presencia, VersionesDeLaCopia versionesCopia, Guid? caja, int? porSubir, int? conError) =>
        {
            // En su latido, la caja dice lo que tiene por subir de cuando estuvo sin conexión (entrega 6): el corte del día
            // espera a que suba. Solo de sí misma (con su llave) o desde la PC del servicio.
            if (caja is { } id && porSubir is { } cuantas
                && (http.Items.TryGetValue(SeguridadCajas.CajaDeLaLlave, out var deLaLlave)
                    ? deLaLlave is Guid propia && propia == id
                    : SeguridadCajas.EsLaMismaPc(http.Connection.RemoteIpAddress)))
                presencia.Bandeja(id, cuantas, conError ?? 0, db.Reloj.GetUtcNow().UtcDateTime);
            // Las versiones, de memoria (entrega 5, parte 2): con ellas la caja sabe si pedir lo que cambió.
            var versiones = await versionesCopia.ActualesAsync();
            // La red vista desde aquí (si hay internet y si contesta el router): la franja de las cajas (la red de la tienda en el POS).
            var red = http.RequestServices.GetService<VigiaDeLaRed>()?.Estado;
            return new EstadoServicioDto("Papelería POS", Version, await db.Sucursales.OrderBy(s => s.Nombre).Select(s => s.Nombre).ToListAsync(),
                versiones.Catalogo, prueba.Activo, versiones.Credito, eleventa.SoloEfectivo, revision.Aviso, versiones.Existencias, versiones.Base,
                versiones.Formato, red);
        });

        // Abierto, pero solo contesta con el PIN del dueño: qué sucursales y cajas hay para elegir.
        api.MapPost("/alta/opciones", async (SolicitudCredenciales s, PosDbContext db) =>
        {
            var sucursales = await db.Sucursales.OrderBy(x => x.Nombre).ToListAsync();
            if (sucursales.Count == 0)
                throw new InvalidOperationException("El servicio todavía no tiene sucursales.");
            await new ServicioControl(db).IdentificarAsync(s.Usuario, s.Pin, sucursales[0].Id, AccionControlada.AdministrarCajas);
            var cajas = await db.Cajas.Where(c => c.Activa).OrderBy(c => c.Serie).ToListAsync();
            return sucursales.Select(x => new SucursalAltaDto(x.Id, x.Nombre,
                cajas.Where(c => c.SucursalId == x.Id).Select(c => new CajaAltaDto(c.Id, c.Nombre, c.Serie, c.Equipo)).ToList())).ToList();
        });

        api.MapPost("/alta", async (SolicitudAltaCaja s, PosDbContext db, LlavesDeCajas llaves, Lazy<LlaveDelServicio> llaveDelServicio) =>
        {
            var control = new ServicioControl(db);
            var dueno = await control.IdentificarAsync(s.Usuario, s.Pin, s.SucursalId, AccionControlada.AdministrarCajas);
            var sucursal = await db.Sucursales.FindAsync(s.SucursalId) ?? throw new InvalidOperationException("La sucursal no existe.");

            Caja caja;
            if (s.CajaId is { } cajaId)
            {
                caja = await db.Cajas.SingleOrDefaultAsync(c => c.Id == cajaId && c.SucursalId == s.SucursalId && c.Activa)
                    ?? throw new InvalidOperationException("La caja no existe.");
            }
            else
            {
                var nombre = s.Nombre?.Trim();
                var serie = s.Serie?.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(nombre) || serie is null || !SerieValida().IsMatch(serie))
                    throw new ArgumentException("Escribe el nombre de la caja y su serie de folios: de 1 a 3 letras (A, B, C…).");
                if (await db.Cajas.AnyAsync(c => c.SucursalId == s.SucursalId && c.Serie == serie))
                    throw new InvalidOperationException($"La serie {serie} ya es de otra caja.");
                caja = new Caja { SucursalId = s.SucursalId, Nombre = nombre, Serie = serie };
                db.Cajas.Add(caja);
            }

            var anterior = caja.Equipo;
            var llave = caja.DarDeAlta(s.Equipo, db.Reloj.GetUtcNow().UtcDateTime);
            control.Registrar(s.SucursalId, caja.Id, dueno.Id, AccionControlada.AdministrarCajas,
                $"Dio de alta el equipo «{caja.Equipo}» como {caja.Nombre}" + (anterior is null ? "" : $" (reemplaza a «{anterior}»)"));
            await db.SaveChangesAsync();
            // La llave de antes de esta caja (otra PC) ya no entra.
            llaves.Olvidar();
            // Con la llave pública del servicio: la caja la recuerda y no se cambia sola a otro que no firme con ella.
            return new AltaCajaDto(new CajaDto(caja.Id, caja.Nombre, caja.Serie, sucursal.Id, sucursal.Nombre, caja.Equipo), llave,
                llaveDelServicio.Value.Publica);
        });

        // Con llave, pero desde cualquier caja: quien entró con «Dar de alta y quitar cajas» quita el equipo de otra (una PC
        // robada o reemplazada), sin volver a escribir su PIN (la Configuración nueva, entrega 7).
        api.MapPost("/cajas/{id:guid}/quitar-equipo", async (Guid id, SolicitudQuitarEquipo s, PosDbContext db, LlavesDeCajas llaves) =>
        {
            var caja = await db.Cajas.FindAsync(id) ?? throw new InvalidOperationException("La caja no existe.");
            var control = new ServicioControl(db);
            await control.ExigirDeLaSesionAsync(s.UsuarioId, caja.SucursalId, caja.Id, AccionControlada.AdministrarCajas);
            if (!caja.TieneEquipo)
                throw new InvalidOperationException($"{caja.Nombre} no tiene equipo dado de alta.");
            control.Registrar(caja.SucursalId, caja.Id, s.UsuarioId, AccionControlada.AdministrarCajas, $"Quitó el equipo «{caja.Equipo}» de {caja.Nombre}");
            caja.QuitarEquipo();
            await db.SaveChangesAsync();
            llaves.Olvidar();
            return Results.NoContent();
        });
    }

    [GeneratedRegex("^[A-Z]{1,3}$")]
    private static partial Regex SerieValida();
}
