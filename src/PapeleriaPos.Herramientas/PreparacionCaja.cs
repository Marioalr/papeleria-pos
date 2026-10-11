using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Configuracion;
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

        // Los rápidos que decidió el dueño (2026-10-10): la copia y la CURP, que vende 10 impresiones mientras Control
        // Print siga aparte. Solo si la base trae esos servicios (la ficticia sí; una importada, con otros nombres, no).
        var configuracion = await db.ConfiguracionesSucursal.FindAsync([sucursal.Id], ct);
        if (configuracion?.Rapidos is null)
        {
            var rapidos = new List<Rapido>();
            if (await VarianteDeAsync(db, "Copia blanco y negro", "Carta", ct) is { } copia)
                rapidos.Add(new Rapido("Copia B/N", copia));
            if (await VarianteDeAsync(db, "Impresión blanco y negro", "Carta", ct) is { } impresion)
                rapidos.Add(new Rapido("CURP", impresion, 10));
            if (rapidos.Count > 0)
            {
                if (configuracion is null)
                {
                    configuracion = new ConfiguracionSucursal { SucursalId = sucursal.Id };
                    db.ConfiguracionesSucursal.Add(configuracion);
                }
                configuracion.Rapidos = rapidos;
                hecho.Add($"Rápidos: {string.Join(", ", rapidos.Select(r => r.Nombre))}");
            }
        }

        await db.SaveChangesAsync(ct);
        return hecho;
    }

    /// <summary>La variante activa de un producto (por su nombre y el de la variante), si existe.</summary>
    private static Task<Guid?> VarianteDeAsync(PosDbContext db, string producto, string variante, CancellationToken ct) =>
        db.Variantes.Where(v => v.Activa && v.Producto!.Activo && v.Producto.Nombre == producto && v.Descripcion == variante)
            .Select(v => (Guid?)v.Id).FirstOrDefaultAsync(ct);
}
