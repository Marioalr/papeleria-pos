namespace PapeleriaPos.Interfaz;

/// <summary>
/// Volver a cargar la página en la ventana de Windows con la pantalla de venta abierta: el aviso de arriba, el botón de
/// «Algo falló», «Empezar de cero» de la prueba… Toda recarga pasa por aquí (MainWindow la detiene y la repite): antes,
/// AlgoFallo quita la pantalla mientras la página de ahora sigue ahí, y se espera un momento a que conteste. Sin esto,
/// Blazor desecha la pantalla hasta que se conecta la página nueva: lo que la pantalla de venta le pide a la página al
/// irse (quitar sus teclas) le llegaba a la nueva, que no lo tiene; ese error tumbaba la carga y la caja se quedaba en
/// «Cargando…» con el aviso otra vez, hasta volver a cargar una segunda vez (revisión del 2026-10-14). Si la página de
/// ahora ya no contesta (se cayó, o Blazor la dio por perdida tras un error), lo que se le pidió se queda sin respuesta y
/// no estorba.
/// </summary>
public sealed class RecargaDeLaCaja
{
    /// <summary>Lo que se espera a la página de ahora: viva, contesta en milisegundos, también si son dos cosas seguidas.</summary>
    public static readonly TimeSpan Espera = TimeSpan.FromMilliseconds(500);

    private Func<Task>? _soltar;

    /// <summary>
    /// Si hay una pantalla que quitar antes de volver a cargar. No la hay mientras carga la primera vez, ni después de
    /// quitarla: entonces la recarga sigue sin detenerse.
    /// </summary>
    public bool HayPantalla => _soltar is not null;

    /// <summary>AlgoFallo, el de toda la pantalla, al empezar en cada página.</summary>
    public void Conectar(Func<Task> soltar) => _soltar = soltar;

    /// <summary>Al desecharse: si ya se conectó el de la página nueva, se queda ese.</summary>
    public void Desconectar(Func<Task> soltar)
    {
        if (_soltar == soltar)
            _soltar = null;
    }

    /// <summary>
    /// Quita la pantalla de la página de ahora (dice «Volviendo a cargar…») y espera a que conteste lo que la pantalla le
    /// pidió al irse. Después ya se puede cargar la página otra vez.
    /// </summary>
    public async Task SoltarAsync(TimeSpan espera)
    {
        if (_soltar is not { } soltar)
            return;
        _soltar = null;
        await soltar();
        await Task.Delay(espera);
    }
}
