using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Datos;

/// <summary>
/// Los sinónimos de la tienda para la búsqueda (docs/propuesta-interfaz.md): los da de alta quien edita el catálogo, uno
/// por uno; ninguno viene puesto. Viajan a las cajas con la versión del catálogo.
/// </summary>
public sealed class RegistroSinonimos(PosDbContext db)
{
    public async Task<List<SinonimoDto>> ListarAsync(CancellationToken ct = default) =>
        await db.Sinonimos.AsNoTracking().Where(s => s.Activo).OrderBy(s => s.Palabra).ThenBy(s => s.Equivale)
            .Select(s => new SinonimoDto(s.Id, s.Palabra, s.Equivale)).ToListAsync(ct);

    public async Task<List<SinonimoDto>> AgregarAsync(SolicitudSinonimo s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, s.SucursalId, AccionControlada.EditarCatalogo,
            "Los sinónimos los dan de alta el dueño y el encargado.", ct);
        var palabra = Sinonimo.Normalizar(s.Palabra) ?? throw new ArgumentException("Escribe una sola palabra en «al escribir».");
        var equivale = Sinonimo.Normalizar(s.Equivale) ?? throw new ArgumentException("Escribe una sola palabra en «también busca».");
        if (palabra == equivale)
            throw new ArgumentException("Las dos palabras son la misma.");

        foreach (var (de, a) in s.AlReves ? new[] { (palabra, equivale), (equivale, palabra) } : new[] { (palabra, equivale) })
        {
            if (await db.Sinonimos.SingleOrDefaultAsync(x => x.Palabra == de && x.Equivale == a, ct) is { } existente)
                existente.Activo = true;
            else
                db.Sinonimos.Add(new Sinonimo { Palabra = de, Equivale = a });
        }
        new ServicioControl(db).Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.EditarCatalogo,
            $"Sinónimo: «{palabra.ToLowerInvariant()}» {(s.AlReves ? "=" : "→")} «{equivale.ToLowerInvariant()}»", null);
        await db.SaveChangesAsync(ct);
        return await ListarAsync(ct);
    }

    public async Task<List<SinonimoDto>> QuitarAsync(Guid id, SolicitudQuitarSinonimo s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, s.SucursalId, AccionControlada.EditarCatalogo,
            "Los sinónimos los quitan el dueño y el encargado.", ct);
        var sinonimo = await db.Sinonimos.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ArgumentException("Ese sinónimo ya no existe.");
        if (sinonimo.Activo)
        {
            sinonimo.Activo = false;
            new ServicioControl(db).Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.EditarCatalogo,
                $"Quitó el sinónimo «{sinonimo.Palabra.ToLowerInvariant()}» → «{sinonimo.Equivale.ToLowerInvariant()}»", null);
            await db.SaveChangesAsync(ct);
        }
        return await ListarAsync(ct);
    }
}
