using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// La Configuración se guarda sola (entrega 7, docs/propuesta-interfaz.md «Configuración»): no hay «Guardar». Lo que se
/// escribe se guarda al salir del campo, con Enter o tras una pausa al escribir, nunca con cada tecla; lo que se elige (un
/// botón, un archivo), al momento. Se guarda una cosa a la vez y en orden, así cada cambio parte del anterior. Si no se
/// pudo (no es válido, o el servicio no lo deja), el error queda junto al ajuste y, al salir del campo, el valor regresa a
/// lo guardado. Lo escrito que todavía no se guardaba al cerrar la ventana (Esc con el cursor en el campo) se guarda al
/// cerrarla.
/// </summary>
public abstract class GuardadoAlMomento : ComponentBase, IAsyncDisposable
{
    /// <summary>Lo que se espera sin teclear antes de guardar lo escrito.</summary>
    public static readonly TimeSpan Pausa = TimeSpan.FromMilliseconds(800);

    /// <summary>Lo escrito en cada campo que todavía no se guarda, con cómo se guarda y el renglón donde va su error.</summary>
    private readonly Dictionary<string, (string Texto, Func<string, Task> Guardar, string Renglon)> _escrito = [];

    private readonly Dictionary<string, string> _errores = [];
    private readonly Dictionary<string, CancellationTokenSource> _pausas = [];
    private readonly SemaphoreSlim _turno = new(1, 1);
    private bool _cerrado;

    /// <summary>La hora del último cambio guardado, para «Guardado a las HH:MM»; vacía mientras no se guarda nada.</summary>
    protected string? GuardadoA { get; private set; }

    /// <summary>«Guardado a las HH:MM», o «Se guarda solo» mientras no se ha cambiado nada.</summary>
    protected string Guardado => GuardadoA is { } hora ? $"Guardado a las {hora}" : "Se guarda solo";

    /// <summary>Lo que dice el campo: lo escrito sin guardar o, si no hay, lo guardado.</summary>
    protected string Escrito(string campo, string guardado) => _escrito.TryGetValue(campo, out var e) ? e.Texto : guardado;

    /// <summary>Por qué no se guardó lo último de ese renglón; vacío si se guardó.</summary>
    protected string? ErrorDe(string renglon) => _errores.GetValueOrDefault(renglon);

    /// <summary>
    /// Lo que se teclea (oninput): se recuerda y se guarda tras la pausa. <paramref name="guardar"/> recibe el texto tal
    /// cual; si no es válido, lanza <see cref="ArgumentException"/> con el porqué.
    /// </summary>
    /// <param name="renglon">Dónde se dice el error (el ajuste); vacío, el mismo campo.</param>
    protected void Escribir(string campo, object? valor, Func<string, Task> guardar, string? renglon = null)
    {
        _escrito[campo] = (valor?.ToString() ?? "", guardar, renglon ?? campo);
        Programar(campo);
    }

    /// <summary>Al salir del campo (onchange) o con Enter: se guarda ya y, si no se pudo, regresa a lo guardado.</summary>
    protected Task SalirAsync(string campo) => GuardarEscritoAsync(campo, alSalir: true);

    /// <summary>Enter en un campo de un renglón: lo guarda como al salir de él.</summary>
    protected Task AlTeclearAsync(KeyboardEventArgs e, string campo) => e.Key == "Enter" ? SalirAsync(campo) : Task.CompletedTask;

    /// <summary>Lo que se elige (un botón, un archivo): se guarda al momento, en su turno, después de lo escrito en otros campos.</summary>
    /// <returns>Si se guardó.</returns>
    protected async Task<bool> HacerAsync(string renglon, Func<Task> guardar)
    {
        await GuardarPendientesAsync();
        await _turno.WaitAsync();
        try
        {
            await guardar();
            _errores.Remove(renglon);
            await GuardadoAsync();
            return true;
        }
        catch (Exception ex) when (ex is ErrorCaja or ArgumentException)
        {
            _errores[renglon] = ex.Message;
            return false;
        }
        finally
        {
            _turno.Release();
        }
    }

    /// <summary>Un error que no viene de guardar (al leer lo que había, por ejemplo), junto a su renglón.</summary>
    protected void Error(string renglon, string? mensaje)
    {
        if (mensaje is null)
            _errores.Remove(renglon);
        else
            _errores[renglon] = mensaje;
    }

    /// <summary>Guarda lo escrito en todos los campos (antes de mover o quitar algo de una lista, o al cerrar).</summary>
    protected async Task GuardarPendientesAsync()
    {
        foreach (var campo in _escrito.Keys.ToList())
            await GuardarEscritoAsync(campo, alSalir: true);
    }

    /// <summary>Después de cada cambio guardado (por ejemplo, para que la venta tome los nuevos).</summary>
    protected virtual Task AlGuardarAsync() => Task.CompletedTask;

    /// <summary>Algo de adentro (un bloque con lo suyo) se guardó: «Guardado a las…» dice esta hora.</summary>
    protected void MarcarGuardado() => GuardadoA = Ahora();

    private static string Ahora() => HoraMexico.ALocal(DateTime.UtcNow).ToString("HH:mm", Formato.Mexico);

    private void Programar(string campo)
    {
        if (_pausas.Remove(campo, out var antes))
        {
            antes.Cancel();
            antes.Dispose();
        }
        var pausa = new CancellationTokenSource();
        _pausas[campo] = pausa;
        _ = TrasLaPausaAsync(campo, pausa.Token);
    }

    private async Task TrasLaPausaAsync(string campo, CancellationToken token)
    {
        try
        {
            await Task.Delay(Pausa, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await InvokeAsync(async () =>
        {
            if (_cerrado)
                return;
            // Sigue en el campo: si no es válido, lo dice y se queda lo escrito para corregirlo.
            await GuardarEscritoAsync(campo, alSalir: false);
            if (!_cerrado)
                StateHasChanged();
        });
    }

    private async Task GuardarEscritoAsync(string campo, bool alSalir)
    {
        if (_pausas.Remove(campo, out var pausa))
        {
            pausa.Cancel();
            pausa.Dispose();
        }
        await _turno.WaitAsync();
        try
        {
            if (!_escrito.TryGetValue(campo, out var escrito))
                return;
            try
            {
                await escrito.Guardar(escrito.Texto);
                // Si se siguió escribiendo mientras se guardaba, lo nuevo se guarda después.
                if (_escrito.TryGetValue(campo, out var ahora) && ahora.Texto == escrito.Texto)
                    _escrito.Remove(campo);
                _errores.Remove(escrito.Renglon);
                await GuardadoAsync();
            }
            catch (Exception ex) when (ex is ErrorCaja or ArgumentException)
            {
                _errores[escrito.Renglon] = ex.Message;
                // Al salir del campo regresa a lo guardado (si no se siguió escribiendo mientras tanto).
                if (alSalir && _escrito.TryGetValue(campo, out var ahora) && ahora.Texto == escrito.Texto)
                    _escrito.Remove(campo);
            }
        }
        finally
        {
            _turno.Release();
        }
    }

    private async Task GuardadoAsync()
    {
        GuardadoA = Ahora();
        await AlGuardarAsync();
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_cerrado)
            return;
        _cerrado = true;
        foreach (var pausa in _pausas.Values)
            pausa.Cancel();
        // Esc con el cursor en un campo cierra la ventana sin salir de él: lo escrito se guarda igual.
        await GuardarPendientesAsync();
        GC.SuppressFinalize(this);
    }
}
