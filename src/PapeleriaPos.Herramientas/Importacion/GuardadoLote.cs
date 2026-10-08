using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Herramientas.Importacion;

public static class GuardadoLote
{
    private const int Lote = 2_000;

    /// <summary>
    /// Guarda los datos en una base ya preparada. Las existencias pasan por <see cref="RegistroInventario"/>, el
    /// mismo camino que usará la importación real.
    /// </summary>
    public static async Task GuardarAsync(LoteDeDatos datos, PosDbContext db, CancellationToken ct = default)
    {
        using var _ = datos.GeneradorIds is { } generador ? Ids.Usar(generador) : null;
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        db.AddRange(datos.Sucursales);
        db.AddRange(datos.Departamentos);
        db.AddRange(datos.Marcas);
        db.AddRange(datos.Proveedores);
        db.AddRange(datos.Productos);
        db.AddRange(datos.Variantes);
        db.AddRange(datos.Presentaciones);
        db.AddRange(datos.Codigos);
        db.AddRange(datos.Precios);
        db.AddRange(datos.ProveedoresVariantes);
        db.AddRange(datos.MayoreoPorRevisar);
        await GuardarYLimpiarAsync(db, ct);

        await GuardarPorLotesAsync(db, datos.Ventas, ct);

        var registro = new RegistroInventario(db);
        foreach (var movimiento in datos.Movimientos)
            await registro.AgregarAsync(movimiento, ct);
        await GuardarYLimpiarAsync(db, ct);

        await GuardarPorLotesAsync(db, datos.Mapeos, ct);
    }

    private static async Task GuardarPorLotesAsync<T>(PosDbContext db, List<T> registros, CancellationToken ct) where T : class
    {
        foreach (var lote in registros.Chunk(Lote))
        {
            db.AddRange(lote);
            await GuardarYLimpiarAsync(db, ct);
        }
    }

    private static async Task GuardarYLimpiarAsync(PosDbContext db, CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }
}
