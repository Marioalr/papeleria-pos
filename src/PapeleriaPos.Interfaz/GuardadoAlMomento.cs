using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// La Configuración se guarda sola (entrega 7, docs/propuesta-interfaz.md «Configuración»): no hay «Guardar». Lo que se
/// escribe se guarda al salir del campo o con Enter, nunca con cada tecla; el texto libre (un nombre, el encabezado),
/// también tras una pausa al escribir, sin tocar lo que sigue escribiéndose: el campo enseña lo que se escribió hasta que se
/// sale de él. Lo que se elige (un botón, un archivo), al momento. Se guarda una cosa a la vez y en orden, así cada cambio
/// parte del anterior. Si no se pudo (no es válido, o el servicio no lo deja), el error queda junto al ajuste y, al salir
/// del campo, el valor regresa a lo guardado. Lo escrito que todavía no se guardaba al cerrar la ventana (Esc con el cursor
/// en el campo) se guarda al cerrarla; si no se pudo, se dice a quien abrió la ventana (<see cref="NoSeGuardoAlCerrarAsync"/>).
/// </summary>
public abstract class GuardadoAlMomento : ComponentBase, IAsyncDisposable
{
    /// <summary>Lo que se espera sin teclear antes de guardar el texto libre.</summary>
    public static readonly TimeSpan Pausa = TimeSpan.FromMilliseconds(800);

    /// <summary>Lo escrito en un campo que todavía no se deja: cómo se guarda, dónde va su error y lo último que ya se guardó de él.</summary>
    private sealed record Pendiente(string Texto, Func<string, Task> Guardar, string Renglon, string? YaGuardado);

    private readonly Dictionary<string, Pendiente> _escrito = [];
    private readonly Dictionary<string, string> _errores = [];
    private readonly Dictionary<string, CancellationTokenSource> _pausas = [];
    private readonly SemaphoreSlim _turno = new(1, 1);

    /// <summary>Lo que no se pudo guardar al cerrar, para decirlo.</summary>
    private readonly List<string> _alCerrar = [];

    private bool _cerrado;

    /// <summary>La hora del último cambio guardado, para «Guardado a las HH:MM»; vacía mientras no se guarda nada.</summary>
    protected string? GuardadoA { get; private set; }

    /// <summary>«Guardado a las HH:MM», o «Se guarda solo» mientras no se ha cambiado nada.</summary>
    protected string Guardado => GuardadoA is { } hora ? $"Guardado a las {hora}" : "Se guarda solo";

    /// <summary>Lo que dice el campo: lo escrito mientras no se sale de él o, si no hay, lo guardado.</summary>
    protected string Escrito(string campo, string guardado) => _escrito.TryGetValue(campo, out var e) ? e.Texto : guardado;

    /// <summary>Por qué no se guardó lo último de ese renglón; vacío si se guardó.</summary>
    protected string? ErrorDe(string renglon) => _errores.GetValueOrDefault(renglon);

    /// <summary>
    /// Lo que se teclea (oninput): se recuerda y, con <paramref name="conPausa"/> (el texto libre), se guarda tras la pausa;
    /// si no, al salir del campo, con Enter o al cerrar. Un número a medias («15» camino de «150», «1500.») no se guarda
    /// solo. <paramref name="guardar"/> recibe el texto tal cual; si no es válido, lanza <see cref="ArgumentException"/> con
    /// el porqué.
    /// </summary>
    /// <param name="renglon">Dónde se dice el error (el ajuste); vacío, el mismo campo.</param>
    protected void Escribir(string campo, object? valor, Func<string, Task> guardar, string? renglon = null, bool conPausa = true)
    {
        var texto = valor?.ToString() ?? "";
        _escrito[campo] = new Pendiente(texto, guardar, renglon ?? campo, _escrito.GetValueOrDefault(campo)?.YaGuardado);
        // Vacío no se guarda tras la pausa: quien borra todo casi siempre va a escribir otra cosa.
        if (conPausa && texto.Trim().Length > 0)
            Programar(campo);
        else
            CancelarPausa(campo);
    }

    /// <summary>
    /// Al salir del campo (onblur) o con Enter: se guarda ya (si no se guardó así tras la pausa) y el campo enseña lo guardado;
    /// si no se pudo, regresa a lo guardado.
    /// </summary>
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

    /// <summary>Algo que no se guarda pero va en orden con lo que se guarda (volver a leer lo de la pantalla): después de lo que está en camino.</summary>
    protected async Task EnSuTurnoAsync(Func<Task> accion)
    {
        await _turno.WaitAsync();
        try
        {
            await accion();
        }
        finally
        {
            _turno.Release();
        }
    }

    /// <summary>La ventana ya se cerró: no hay que volver a pintar.</summary>
    protected bool Cerrado => _cerrado;

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

    /// <summary>Al cerrar la ventana no se pudo guardar lo que quedó escrito: por qué, para decirlo donde se ve.</summary>
    protected virtual Task NoSeGuardoAlCerrarAsync(IReadOnlyList<string> errores) => Task.CompletedTask;

    /// <summary>Algo de adentro (un bloque con lo suyo) se guardó: «Guardado a las…» dice esta hora.</summary>
    protected void MarcarGuardado() => GuardadoA = Ahora();

    private static string Ahora() => HoraMexico.ALocal(DateTime.UtcNow).ToString("HH:mm", Formato.Mexico);

    private void Programar(string campo)
    {
        CancelarPausa(campo);
        var pausa = new CancellationTokenSource();
        _pausas[campo] = pausa;
        _ = TrasLaPausaAsync(campo, pausa.Token);
    }

    private void CancelarPausa(string campo)
    {
        if (_pausas.Remove(campo, out var pausa))
        {
            pausa.Cancel();
            pausa.Dispose();
        }
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
            // Sigue en el campo: se guarda sin tocar lo que dice; si no es válido, lo dice y se queda para corregirlo.
            await GuardarEscritoAsync(campo, alSalir: false);
            if (!_cerrado)
                StateHasChanged();
        });
    }

    private async Task GuardarEscritoAsync(string campo, bool alSalir)
    {
        CancelarPausa(campo);
        await _turno.WaitAsync();
        try
        {
            if (!_escrito.TryGetValue(campo, out var escrito))
                return;
            // Ya se guardó así tras la pausa: al salir solo se deja de enseñar lo escrito.
            if (escrito.Texto == escrito.YaGuardado)
            {
                if (alSalir)
                    _escrito.Remove(campo);
                return;
            }
            try
            {
                await escrito.Guardar(escrito.Texto);
                _errores.Remove(escrito.Renglon);
                if (_escrito.TryGetValue(campo, out var ahora))
                {
                    // Al salir, el campo enseña lo guardado (como lo dejó el servicio). Sin salir (tras la pausa), sigue lo que
                    // se está escribiendo: si se siguió escribiendo mientras se guardaba, lo nuevo se guarda después.
                    if (alSalir && ahora.Texto == escrito.Texto)
                        _escrito.Remove(campo);
                    else
                        _escrito[campo] = ahora with { YaGuardado = escrito.Texto };
                }
                await GuardadoAsync();
            }
            catch (Exception ex) when (ex is ErrorCaja or ArgumentException)
            {
                _errores[escrito.Renglon] = ex.Message;
                if (_cerrado)
                    _alCerrar.Add(ex.Message);
                // Al salir regresa a lo guardado (si no se siguió escribiendo mientras tanto).
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
        // Esc con el cursor en un campo cierra la ventana sin salir de él: lo escrito se guarda igual, y si no se pudo, se dice.
        await GuardarPendientesAsync();
        if (_alCerrar.Count > 0)
            await NoSeGuardoAlCerrarAsync([.. _alCerrar]);
        GC.SuppressFinalize(this);
    }
}
