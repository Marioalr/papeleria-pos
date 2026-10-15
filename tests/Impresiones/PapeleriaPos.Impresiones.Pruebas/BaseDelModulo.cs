using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// SQLite en memoria con solo las tablas del módulo (su mapeo, <see cref="ModeloImpresiones"/>) y un POS falso
/// (<see cref="PosDePrueba"/>): si el módulo necesitara algo del POS fuera del puerto, aquí no funcionaría.
/// </summary>
public sealed class BaseDelModulo : IAsyncDisposable
{
    private readonly SqliteConnection _conexion = new("Data Source=:memory:");

    public PosDePrueba Pos { get; } = new();

    public static async Task<BaseDelModulo> CrearAsync()
    {
        var baseDelModulo = new BaseDelModulo();
        await baseDelModulo._conexion.OpenAsync();
        await using var db = baseDelModulo.Contexto();
        await db.Database.EnsureCreatedAsync();
        return baseDelModulo;
    }

    /// <summary>Un contexto nuevo sobre la misma base, como cada operación del servicio.</summary>
    public ContextoImpresiones Contexto() => new(new DbContextOptionsBuilder<ContextoImpresiones>().UseSqlite(_conexion).Options);

    public RegistroImpresiones Registro(DbContext db) => new(db, Pos);

    public ServicioImpresora Impresora(DbContext db) => new(db, Pos);

    public ImpresionesEnLaVenta EnLaVenta(DbContext db) => new(db, Pos);

    public async ValueTask DisposeAsync() => await _conexion.DisposeAsync();
}

/// <summary>Un contexto con solo las tablas del módulo. Guarda como el del POS: los enum como texto y las fechas en UTC.</summary>
public sealed class ContextoImpresiones(DbContextOptions<ContextoImpresiones> opciones) : DbContext(opciones)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<FechaUtc>();
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ModeloImpresiones.Configurar(modelBuilder);
}

/// <summary>SQLite no guarda la zona: al leer, las fechas se marcan como UTC (como en el POS).</summary>
internal sealed class FechaUtc() : ValueConverter<DateTime, DateTime>(
    f => f.Kind == DateTimeKind.Local ? f.ToUniversalTime() : DateTime.SpecifyKind(f, DateTimeKind.Utc),
    f => DateTime.SpecifyKind(f, DateTimeKind.Utc));

/// <summary>
/// El POS de las pruebas del módulo: una hora fija, las cajas que la prueba registra (todas de la misma sucursal) y los
/// avisos que el módulo pide, para revisarlos. Como el del POS, un aviso con una clave que ya salió no se repite.
/// </summary>
public sealed class PosDePrueba : IPosParaImpresiones
{
    public static readonly Guid Sucursal = Guid.NewGuid();

    /// <summary>15 de octubre de 2026 a mediodía en la Ciudad de México.</summary>
    public RelojFijo Hora { get; } = new(new DateTimeOffset(2026, 10, 15, 18, 0, 0, TimeSpan.Zero));

    public List<(Guid SucursalId, AvisoImpresiones Tipo, string Texto, string? Clave)> Avisos { get; } = [];

    /// <summary>Si la tienda está cerrada (fuera de su horario).</summary>
    public bool Cerrada { get; set; }

    private readonly Dictionary<Guid, CajaDelPos> _cajas = [];

    public TimeProvider Reloj => Hora;

    public CajaDelPos NuevaCaja(string nombre)
    {
        var caja = new CajaDelPos(Guid.NewGuid(), Sucursal, nombre);
        _cajas[caja.Id] = caja;
        return caja;
    }

    public Task<CajaDelPos?> CajaAsync(Guid cajaId, CancellationToken ct = default) => Task.FromResult(_cajas.GetValueOrDefault(cajaId));

    public bool Avisar(Guid sucursalId, AvisoImpresiones tipo, string texto, string? clave = null)
    {
        if (clave is not null && Avisos.Any(a => a.Clave == clave))
            return false;
        Avisos.Add((sucursalId, tipo, texto, clave));
        return true;
    }

    public bool FueraDeHorario(Guid sucursalId, DateTime utc) => Cerrada;
}
