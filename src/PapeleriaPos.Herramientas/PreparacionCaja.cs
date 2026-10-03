using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Herramientas;

/// <summary>
/// Deja una base lista para probar la caja: la "Caja 1" (serie A) en la primera sucursal y, si no hay usuarios,
/// tres usuarios de prueba. Solo para bases de prueba (ficticia o copia importada), nunca para la de la tienda.
/// </summary>
public static class PreparacionCaja
{
    /// <summary>Usuarios de prueba: (usuario, nombre, rol, PIN). También están en el README.</summary>
    public static readonly (string Usuario, string Nombre, Rol Rol, string Pin)[] UsuariosDePrueba =
    [
        ("cajero", "Cajero de prueba", Rol.Cajero, "1111"),
        ("encargado", "Encargado de prueba", Rol.Encargado, "2222"),
        ("dueno", "Dueño de prueba", Rol.Dueno, "3333"),
    ];

    public static async Task<List<string>> PrepararAsync(PosDbContext db, CancellationToken ct = default)
    {
        var hecho = new List<string>();
        var sucursal = await db.Sucursales.OrderBy(s => s.Clave).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("La base no tiene sucursales: primero genera o importa datos.");

        if (!await db.Cajas.AnyAsync(c => c.SucursalId == sucursal.Id, ct))
        {
            db.Cajas.Add(new Caja { SucursalId = sucursal.Id, Nombre = "Caja 1", Serie = "A", EsPrincipal = true });
            hecho.Add($"Caja 1 (serie A) en {sucursal.Nombre}");
        }

        if (!await db.Usuarios.AnyAsync(ct))
        {
            foreach (var (usuario, nombre, rol, pin) in UsuariosDePrueba)
            {
                var u = new Usuario { Nombre = nombre, NombreUsuario = usuario, Rol = rol, SucursalId = rol == Rol.Dueno ? null : sucursal.Id };
                u.EstablecerPin(pin);
                db.Usuarios.Add(u);
            }
            hecho.Add($"{UsuariosDePrueba.Length} usuarios de prueba (ver README)");
        }

        await db.SaveChangesAsync(ct);
        return hecho;
    }
}
