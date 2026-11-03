namespace PapeleriaPos.Interfaz;

/// <summary>Lo que ya se probó en esta caja para terminar la instalación, y cuándo se dio por terminada.</summary>
public sealed record LoQueSeProbo(DateTime? TicketDePruebaUtc = null, DateTime? LectorUtc = null, string? LectorLeyo = null, DateTime? TerminadaUtc = null);

/// <summary>
/// «Terminar la instalación» en esta caja (2026-10-31): lo que ya se probó aquí (el ticket de prueba en su impresora, un código con su
/// lector) y si ya se dio por terminada (entonces la Configuración ya no la enseña). Es de cada caja, como su impresora y su lector:
/// con la base local de la caja (la ventana de Windows) se queda en ella, para cualquier cuenta de Windows de la PC; sin ella (el
/// navegador), mientras corra el servicio. Lo de toda la tienda (los costos, eleventa, Control Print) se le pregunta al servicio.
/// Empieza cuando esta caja se da de alta o crea la tienda (<see cref="EmpezarAsync"/>): en una caja que ya estaba instalada antes
/// (la caja 2 del piloto) no sale.
/// </summary>
public sealed class InstalacionDeLaCaja
{
    private const string Clave = "terminar-la-instalacion";

    private readonly AlmacenLocal? _almacen;
    private readonly TimeProvider _reloj;

    /// <summary>Vacío: esta caja no empezó su instalación con esta versión.</summary>
    private LoQueSeProbo? _lo;

    public InstalacionDeLaCaja(AlmacenLocal? almacen, TimeProvider? reloj = null)
    {
        (_almacen, _reloj) = (almacen, reloj ?? TimeProvider.System);
        _lo = almacen?.Leer<LoQueSeProbo>(Clave);
    }

    public LoQueSeProbo Lo => _lo ?? new LoQueSeProbo();

    /// <summary>Se empezó en esta caja y todavía no se da por terminada: la Configuración la enseña.</summary>
    public bool Pendiente => _lo is { TerminadaUtc: null };

    /// <summary>Ya se dio por terminada en esta caja.</summary>
    public bool Terminada => _lo is { TerminadaUtc: not null };

    /// <summary>Cambió lo que se probó (para que la Configuración lo vuelva a dibujar).</summary>
    public event Action? Cambio;

    /// <summary>Esta caja se dio de alta, o creó la tienda: empieza de cero (lo que se probó antes, con otra base, ya no cuenta).</summary>
    public Task EmpezarAsync() => GuardarAsync(new LoQueSeProbo());

    public Task TicketDePruebaAsync() => GuardarAsync(Lo with { TicketDePruebaUtc = Ahora });

    public Task LectorAsync(string leyo) => GuardarAsync(Lo with { LectorUtc = Ahora, LectorLeyo = leyo.Length > 40 ? leyo[..40] + "…" : leyo });

    public Task TerminarAsync() => GuardarAsync(Lo with { TerminadaUtc = Ahora });

    private DateTime Ahora => _reloj.GetUtcNow().UtcDateTime;

    private async Task GuardarAsync(LoQueSeProbo lo)
    {
        _lo = lo;
        Cambio?.Invoke();
        if (_almacen is not null)
            await _almacen.EscribirAsync(l => l.Guardar(Clave, lo), "terminar la instalación");
    }
}
