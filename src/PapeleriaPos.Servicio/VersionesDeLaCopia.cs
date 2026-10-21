using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Las versiones de lo que lleva la copia de las cajas, en memoria (entrega 5, parte 2): el latido de cada caja, el aviso por
/// SSE y el catálogo en memoria las leen de aquí, sin ir a la base en cada petición. Se vuelven a leer de la base a lo más
/// cada <see cref="Vigencia"/> (también ven lo que escribió otro programa en la misma base).
/// <para>
/// La base de las versiones (<see cref="Base"/>) cambia cada vez que arranca el servicio y al empezar de cero la prueba: una
/// caja con una copia de otra base (o de antes de un respaldo restaurado) la baja completa, en lugar de pedir cambios desde
/// una versión que aquí quiere decir otra cosa.
/// </para>
/// </summary>
public sealed class VersionesDeLaCopia(IServiceScopeFactory alcances)
{
    /// <summary>
    /// Cómo se arma la copia (sus campos y lo que quiere decir cada versión). Si cambia, la caja y el servicio de versiones
    /// distintas no se piden cambios: la copia se baja completa. El 2 (entrega 5, parte 3) trae lo que necesita la búsqueda
    /// de la caja: todos los códigos, las marcas con que se leen los nombres, lo más vendido y lo elegido, con sus versiones.
    /// </summary>
    public const int Formato = 2;

    /// <summary>Cuánto vale lo leído de la base antes de volver a leerla.</summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _leyendo = new(1, 1);
    private VersionesCopiaDto? _ultimas;
    private long _leidasEn;

    /// <summary>Cambia con cada <see cref="Olvidar"/>: lo que se estaba leyendo antes ya no se recuerda.</summary>
    private int _generacion;

    public Guid Base { get; private set; } = Guid.NewGuid();

    /// <summary>La base cambió entera (la prueba empezó de cero): las copias de antes ya no sirven para pedir cambios.</summary>
    public void BaseNueva()
    {
        Base = Guid.NewGuid();
        Olvidar();
    }

    /// <summary>
    /// Se guardó algo en la base (<see cref="AlGuardarEnLaBase"/>): la siguiente lectura va a la base. Así quien acaba de
    /// cambiar algo ve su versión enseguida (el latido de la caja, el aviso por SSE, el catálogo en memoria).
    /// </summary>
    public void Olvidar()
    {
        Interlocked.Increment(ref _generacion);
        _ultimas = null;
    }

    /// <summary>Las de ahora (leídas de la base hace menos de <see cref="Vigencia"/>).</summary>
    public async Task<VersionesCopiaDto> ActualesAsync(CancellationToken ct = default)
    {
        if (Vigentes() is { } vigentes)
            return vigentes;
        await _leyendo.WaitAsync(ct);
        try
        {
            if (Vigentes() is { } yaLeidas)
                return yaLeidas;
            var generacion = Volatile.Read(ref _generacion);
            using var alcance = alcances.CreateScope();
            var leidas = await LeerAsync(alcance.ServiceProvider.GetRequiredService<PosDbContext>(), Base, ct);
            // Si mientras se leía se guardó algo, lo leído puede ser de antes: se usa, pero no se recuerda.
            if (generacion == Volatile.Read(ref _generacion))
                (_ultimas, _leidasEn) = (leidas, Stopwatch.GetTimestamp());
            return leidas;
        }
        finally
        {
            _leyendo.Release();
        }
    }

    private VersionesCopiaDto? Vigentes() =>
        _ultimas is { } ultimas && ultimas.Base == Base && Stopwatch.GetElapsedTime(_leidasEn) < Vigencia ? ultimas : null;

    /// <summary>Las tres versiones, de la base, en una sola lectura (dentro de la transacción de quien llama, si tiene una).</summary>
    public static async Task<VersionesCopiaDto> LeerAsync(PosDbContext db, Guid base_, CancellationToken ct = default)
    {
        var valores = await db.Secuencias.AsNoTracking()
            .Where(s => s.Nombre == Secuencia.Catalogo || s.Nombre == Secuencia.Existencias || s.Nombre == Secuencia.Credito)
            .ToDictionaryAsync(s => s.Nombre, s => s.Valor, ct);
        return new VersionesCopiaDto(base_, valores.GetValueOrDefault(Secuencia.Catalogo), valores.GetValueOrDefault(Secuencia.Existencias),
            valores.GetValueOrDefault(Secuencia.Credito), Formato);
    }
}

/// <summary>
/// Cada vez que algo queda guardado en la base (al terminar de guardar, o al confirmar la transacción de quien guarda dentro
/// de una), las versiones en memoria se vuelven a leer (<see cref="VersionesDeLaCopia.Olvidar"/>).
/// </summary>
public sealed class AlGuardarEnLaBase(VersionesDeLaCopia versiones) : DbTransactionInterceptor, ISaveChangesInterceptor
{
    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        versiones.Olvidar();
        return result;
    }

    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        versiones.Olvidar();
        return ValueTask.FromResult(result);
    }

    public override void TransactionCommitted(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData) => versiones.Olvidar();

    public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        versiones.Olvidar();
        return Task.CompletedTask;
    }
}
