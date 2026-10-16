using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>Las llamadas HTTP a la caja principal: cada una lleva la llave de esta caja y se convierte en <see cref="ErrorCaja"/> si falla.</summary>
public sealed class Transporte(HttpClient http, ConexionCaja conexion)
{
    /// <summary>
    /// Lo más que el cajero espera una lectura de la venta que también se puede hacer con la copia (entrar, el turno, los
    /// permisos, la configuración; entrega 5, parte 2): pasado esto, se hace con la copia y la red cuenta como lenta. En la red
    /// de la tienda una lectura tarda milisegundos; 800 ms ya no es la red buena.
    /// </summary>
    public static readonly TimeSpan PlazoLectura = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Lo más que el cajero espera el cobro y lo demás de la venta que escribe (el turno, una entrada o salida, un abono):
    /// pasado esto, va a la bandeja de salida con el mismo Id, como sin conexión. Son 2.5 s y no 2: en el piloto la caja
    /// principal espera hasta 2 s el folio de eleventa (<c>Eleventa:SegundosAlCobrar</c>), y con medio segundo más para la red y
    /// la base, un cobro con la red buena nunca se va a la bandeja solo por eso.
    /// </summary>
    public static readonly TimeSpan PlazoEscritura = TimeSpan.FromMilliseconds(2500);

    private static readonly AsyncLocal<(TimeSpan? Lectura, TimeSpan? Escritura)?> _plazosDeLaVenta = new();

    /// <summary>
    /// Lo que se hace con esto puesto lleva los plazos de la venta (en la caja de verdad, <see cref="PlazoLectura"/> y
    /// <see cref="PlazoEscritura"/>) en vez del tope del cliente: lo pone <see cref="ClienteCaja"/> en lo que también se puede
    /// hacer con la copia, solo para esa operación.
    /// </summary>
    internal static (TimeSpan? Lectura, TimeSpan? Escritura)? PlazosDeLaVenta
    {
        get => _plazosDeLaVenta.Value;
        set => _plazosDeLaVenta.Value = value;
    }

    public async Task<T> PutAsync<T>(string url, object cuerpo)
    {
        using var respuesta = await EnviarAsync(HttpMethod.Put, url, cuerpo);
        await RevisarAsync(respuesta);
        return (await respuesta.Content.ReadFromJsonAsync<T>())!;
    }

    public async Task<T?> GetAsync<T>(
        string url, bool nadaEsNulo = false, bool noEncontradoEsNulo = false, Uri? servicio = null, TimeSpan? espera = null)
    {
        using var respuesta = await EnviarAsync(HttpMethod.Get, url, servicio: servicio, espera: espera);
        if ((nadaEsNulo && respuesta.StatusCode == HttpStatusCode.NoContent) || (noEncontradoEsNulo && respuesta.StatusCode == HttpStatusCode.NotFound))
            return default;
        await RevisarAsync(respuesta);
        return await respuesta.Content.ReadFromJsonAsync<T>();
    }

    public async Task<T> PostAsync<T>(string url, object? cuerpo, bool sinRespuesta = false, Uri? servicio = null, TimeSpan? espera = null)
    {
        using var respuesta = await EnviarAsync(HttpMethod.Post, url, cuerpo, servicio, espera);
        await RevisarAsync(respuesta);
        return sinRespuesta ? default! : (await respuesta.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Para la bandeja: el cuerpo ya va en JSON, tal como se guardó.</summary>
    public async Task PostJsonAsync(string url, string json, TimeSpan? espera = null)
    {
        using var respuesta = await EnviarAsync(HttpMethod.Post, url, espera: espera, contenido: new StringContent(json, Encoding.UTF8, "application/json"));
        await RevisarAsync(respuesta);
    }

    /// <summary>Cada llamada va a la caja principal guardada y lleva la llave de esta caja.</summary>
    private async Task<HttpResponseMessage> EnviarAsync(
        HttpMethod metodo, string url, object? cuerpo = null, Uri? servicio = null, TimeSpan? espera = null, HttpContent? contenido = null)
    {
        // La prueba con una persona real puede simular que no hay conexión (lo suyo, la guía y los avisos, sí contesta).
        if (conexion.SimularSinConexion && !url.StartsWith("api/prueba/", StringComparison.Ordinal))
            throw new ErrorCaja("No hay conexión con el servicio de la caja principal (simulado en la prueba).", "sin-conexion");
        using var mensaje = new HttpRequestMessage(metodo, new Uri(servicio ?? conexion.Servicio, url));
        if (conexion.Llave is { } llave)
            mensaje.Headers.Add(ClienteCaja.EncabezadoLlave, llave);
        if (contenido is not null)
            mensaje.Content = contenido;
        else if (metodo != HttpMethod.Get)
            mensaje.Content = JsonContent.Create(cuerpo, cuerpo?.GetType() ?? typeof(object));
        if (espera is null && PlazosDeLaVenta is { } plazos)
            espera = metodo == HttpMethod.Get ? plazos.Lectura : plazos.Escritura;
        using var limite = new CancellationTokenSource(espera ?? Timeout.InfiniteTimeSpan);
        try
        {
            return await http.SendAsync(mensaje, limite.Token);
        }
        catch (TaskCanceledException)
        {
            // Pasó su plazo (o el tope del cliente): la caja principal está, pero no contestó a tiempo.
            throw new ErrorCaja("La caja principal no contestó a tiempo.", "sin-conexion") { PorPlazo = true };
        }
        catch (HttpRequestException)
        {
            throw new ErrorCaja("No hay conexión con el servicio de la caja principal.", "sin-conexion");
        }
    }

    /// <summary>
    /// Abre el aviso de la caja principal (SSE, entrega 5, parte 2): regresa en cuanto llegan sus encabezados, y lo que manda
    /// se lee del cuerpo mientras siga abierto. Vacío si el servicio no lo tiene (uno de antes: 404).
    /// </summary>
    public async Task<HttpResponseMessage?> AbrirAvisoAsync(string url, TimeSpan espera, CancellationToken ct)
    {
        using var mensaje = new HttpRequestMessage(HttpMethod.Get, new Uri(conexion.Servicio, url));
        if (conexion.Llave is { } llave)
            mensaje.Headers.Add(ClienteCaja.EncabezadoLlave, llave);
        mensaje.Headers.Accept.ParseAdd("text/event-stream");
        using var cabeceras = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cabeceras.CancelAfter(espera);
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await http.SendAsync(mensaje, HttpCompletionOption.ResponseHeadersRead, cabeceras.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ErrorCaja("No hay conexión con el servicio de la caja principal.", "sin-conexion") { PorPlazo = ex is TaskCanceledException };
        }
        if (respuesta.StatusCode == HttpStatusCode.NotFound)
        {
            respuesta.Dispose();
            return null;
        }
        try
        {
            await RevisarAsync(respuesta);
        }
        catch
        {
            respuesta.Dispose();
            throw;
        }
        return respuesta;
    }

    private static async Task RevisarAsync(HttpResponseMessage respuesta)
    {
        if (respuesta.IsSuccessStatusCode)
            return;
        ErrorDto? error = null;
        PreciosCambiadosDto? precios = null;
        try
        {
            var cuerpo = await respuesta.Content.ReadAsStringAsync();
            error = JsonSerializer.Deserialize<ErrorDto>(cuerpo, JsonSerializerOptions.Web);
            // El cobro no se hizo porque cambió un precio (entrega 5): trae los de ahora.
            if (error?.Codigo == PreciosCambiadosDto.CodigoDelError)
                precios = JsonSerializer.Deserialize<PreciosCambiadosDto>(cuerpo, JsonSerializerOptions.Web);
        }
        catch (Exception)
        {
            // Sin mensaje del servicio: se usa el genérico.
        }
        throw new ErrorCaja(error?.Mensaje ?? $"El servicio respondió con un error ({(int)respuesta.StatusCode}).", error?.Codigo)
        {
            PreciosCambiados = precios,
        };
    }
}
