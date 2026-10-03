using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>SQLite en memoria con las migraciones aplicadas: prueba también que las migraciones funcionan.</summary>
public sealed class BaseEnMemoria : IAsyncDisposable
{
    private readonly SqliteConnection _conexion = new("Data Source=:memory:");

    public RelojFijo Reloj { get; } = new(new DateTimeOffset(2026, 10, 3, 18, 0, 0, TimeSpan.Zero));

    public static async Task<BaseEnMemoria> CrearAsync()
    {
        var baseEnMemoria = new BaseEnMemoria();
        await baseEnMemoria._conexion.OpenAsync();
        await using var db = baseEnMemoria.Contexto();
        await BaseDeDatos.PrepararAsync(db);
        return baseEnMemoria;
    }

    /// <summary>Un contexto nuevo sobre la misma base, como lo haría cada operación del servicio.</summary>
    public PosDbContext Contexto() =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(_conexion).Options) { Reloj = Reloj };

    public async ValueTask DisposeAsync() => await _conexion.DisposeAsync();
}

/// <summary>Un catálogo mínimo: matriz, un departamento y una pluma con pieza y docena.</summary>
public sealed class CatalogoMinimo
{
    public Sucursal Matriz { get; } = new() { Clave = "M", Nombre = "Matriz" };
    public Departamento Escritura { get; } = new() { Nombre = "Escritura" };
    public Marca Bic { get; } = new() { Nombre = "Bic" };
    public Producto Pluma { get; }
    public Variante Azul { get; }
    public Presentacion Pieza { get; }
    public Presentacion Docena { get; }

    public CatalogoMinimo()
    {
        Pluma = new Producto { Nombre = "Pluma punto fino", MarcaId = Bic.Id, DepartamentoId = Escritura.Id };
        Azul = new Variante { ProductoId = Pluma.Id, Descripcion = "Azul" };
        Pieza = new Presentacion { VarianteId = Azul.Id, Nombre = "Pieza", EsBase = true };
        Docena = new Presentacion { VarianteId = Azul.Id, Nombre = "Docena", Factor = Cantidad.Piezas(12) };
        Pieza.Codigos.Add(new CodigoBarras { Codigo = "7501234567890" });
    }

    public void Agregar(PosDbContext db)
    {
        db.AddRange(Matriz, Escritura, Bic, Pluma, Azul, Pieza, Docena);
    }
}
