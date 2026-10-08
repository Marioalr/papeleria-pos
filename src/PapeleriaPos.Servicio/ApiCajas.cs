using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;

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
        api.MapGet("/estado", async (PosDbContext db, ModoPrueba prueba) =>
        {
            var versiones = await db.Secuencias.Where(s => s.Nombre == Secuencia.Catalogo || s.Nombre == Secuencia.Credito)
                .ToDictionaryAsync(s => s.Nombre, s => s.Valor);
            return new EstadoServicioDto("Papelería POS", Version, await db.Sucursales.OrderBy(s => s.Nombre).Select(s => s.Nombre).ToListAsync(),
                versiones.GetValueOrDefault(Secuencia.Catalogo), prueba.Activo, versiones.GetValueOrDefault(Secuencia.Credito));
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

        api.MapPost("/alta", async (SolicitudAltaCaja s, PosDbContext db) =>
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
            return new AltaCajaDto(new CajaDto(caja.Id, caja.Nombre, caja.Serie, sucursal.Id, sucursal.Nombre, caja.Equipo), llave);
        });

        // Con llave, pero desde cualquier caja: el dueño puede quitar el equipo de otra (una PC robada o reemplazada).
        api.MapPost("/cajas/{id:guid}/quitar-equipo", async (Guid id, SolicitudCredenciales s, PosDbContext db) =>
        {
            var caja = await db.Cajas.FindAsync(id) ?? throw new InvalidOperationException("La caja no existe.");
            var control = new ServicioControl(db);
            var dueno = await control.IdentificarAsync(s.Usuario, s.Pin, caja.SucursalId, AccionControlada.AdministrarCajas);
            if (!caja.TieneEquipo)
                throw new InvalidOperationException($"{caja.Nombre} no tiene equipo dado de alta.");
            control.Registrar(caja.SucursalId, caja.Id, dueno.Id, AccionControlada.AdministrarCajas, $"Quitó el equipo «{caja.Equipo}» de {caja.Nombre}");
            caja.QuitarEquipo();
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    [GeneratedRegex("^[A-Z]{1,3}$")]
    private static partial Regex SerieValida();
}
