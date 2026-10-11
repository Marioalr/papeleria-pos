namespace PapeleriaPos.Interfaz;

/// <summary>
/// El cuadro del código de la pantalla de venta. El lector teclea el código y un Enter, y Blazor atiende sus teclas sin
/// esperar a que termine la consulta del Enter anterior (con el Wi-Fi lento tarda). Por eso, al Enter, el texto se toma y
/// el cuadro se vacía antes de consultar nada, y los escaneos se procesan uno tras otro, en el orden del lector: ninguno
/// borra lo que se está escaneando ni se agrega antes que el anterior.
/// </summary>
public sealed class CapturaCodigo
{
    private Task _anterior = Task.CompletedTask;

    /// <summary>Lo que hay en el cuadro: el <c>@bind</c> del input.</summary>
    public string Texto { get; set; } = "";

    /// <summary>
    /// El Enter: toma lo escrito y vacía el cuadro sin esperar nada; lo procesa cuando terminan los escaneos anteriores. La
    /// tarea termina cuando termina este escaneo.
    /// </summary>
    public Task EnterAsync(Func<string, Task> procesar)
    {
        var texto = Texto.Trim();
        Texto = "";
        if (texto.Length == 0)
            return Task.CompletedTask;
        return _anterior = EnOrdenAsync(_anterior, texto, procesar);
    }

    private static async Task EnOrdenAsync(Task anterior, string texto, Func<string, Task> procesar)
    {
        // Si el anterior falló, su error ya le llegó a su propio Enter: este escaneo sigue.
        await anterior.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        await procesar(texto);
    }
}
