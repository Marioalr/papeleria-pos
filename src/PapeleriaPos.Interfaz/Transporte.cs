using System.Net;
using System.Net.Http.Json;
using System.Text;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>Las llamadas HTTP a la caja principal: cada una lleva la llave de esta caja y se convierte en <see cref="ErrorCaja"/> si falla.</summary>
public sealed class Transporte(HttpClient http, ConexionCaja conexion)
{
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
    public async Task PostJsonAsync(string url, string json)
    {
        using var respuesta = await EnviarAsync(HttpMethod.Post, url, contenido: new StringContent(json, Encoding.UTF8, "application/json"));
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
        using var limite = new CancellationTokenSource(espera ?? Timeout.InfiniteTimeSpan);
        try
        {
            return await http.SendAsync(mensaje, limite.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ErrorCaja("No hay conexión con el servicio de la caja principal.", "sin-conexion");
        }
    }

    private static async Task RevisarAsync(HttpResponseMessage respuesta)
    {
        if (respuesta.IsSuccessStatusCode)
            return;
        ErrorDto? error = null;
        try
        {
            error = await respuesta.Content.ReadFromJsonAsync<ErrorDto>();
        }
        catch (Exception)
        {
            // Sin mensaje del servicio: se usa el genérico.
        }
        throw new ErrorCaja(error?.Mensaje ?? $"El servicio respondió con un error ({(int)respuesta.StatusCode}).", error?.Codigo);
    }
}
