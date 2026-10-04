using System.Net.Http.Json;
using System.Text.Json;

namespace PapeleriaPos.Servicio.Telegram;

public sealed record BotonTelegram(string Texto, string Datos);

/// <summary>Un mensaje o un botón que alguien apretó, tal como llega de Telegram.</summary>
public sealed record ActualizacionTelegram(long Id, long ChatId, string? Nombre, string? Texto, string? CallbackId = null, string? Datos = null);

/// <param name="Permanente">Telegram lo rechazó (chat bloqueado, mensaje inválido): reintentarlo no sirve.</param>
public sealed class TelegramException(string mensaje, bool permanente) : Exception(mensaje)
{
    public bool Permanente { get; } = permanente;
}

/// <summary>Lo que el servicio usa de Telegram. En las pruebas se cambia por uno falso, sin internet.</summary>
public interface ITelegram
{
    bool Configurado { get; }

    Task<string?> NombreBotAsync(CancellationToken ct);

    /// <summary>Devuelve el número del mensaje, para cambiarle el texto después.</summary>
    Task<long> EnviarAsync(long chatId, string texto, IReadOnlyList<BotonTelegram>? botones, CancellationToken ct);

    /// <summary>Cambia el texto de un mensaje ya enviado y le quita los botones.</summary>
    Task EditarAsync(long chatId, long mensajeId, string texto, CancellationToken ct);

    Task ContestarBotonAsync(string callbackId, string texto, CancellationToken ct);

    /// <summary>Lo que llegó desde <paramref name="desde"/>; espera hasta <paramref name="esperaSegundos"/> si no hay nada.</summary>
    Task<IReadOnlyList<ActualizacionTelegram>> LeerAsync(long desde, int esperaSegundos, CancellationToken ct);
}

/// <summary>
/// El bot de verdad, por la API de Telegram (https://core.telegram.org/bots/api). Solo conexiones de salida: los
/// mensajes nuevos se leen preguntando (getUpdates), así que la caja principal no necesita IP pública.
/// </summary>
public sealed class TelegramBot : ITelegram
{
    private readonly HttpClient _http;
    private readonly string? _token;

    public TelegramBot(string? token)
    {
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        // El tiempo de espera es mayor al de la lectura larga (getUpdates espera hasta 25 s).
        _http = new HttpClient { BaseAddress = new Uri($"https://api.telegram.org/bot{_token}/"), Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>El token va en la configuración (Telegram:Token) o en un archivo fuera del repositorio.</summary>
    public static string? LeerToken(IConfiguration configuracion)
    {
        if (configuracion["Telegram:Token"] is { Length: > 0 } token)
            return token;
        var archivo = configuracion["Telegram:ArchivoToken"] is { Length: > 0 } ruta
            ? ruta
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PapeleriaPos", "telegram-token.txt");
        return File.Exists(archivo) ? File.ReadAllText(archivo).Trim() : null;
    }

    public bool Configurado => _token is not null;

    public async Task<string?> NombreBotAsync(CancellationToken ct)
    {
        var bot = await LlamarAsync("getMe", new { }, ct);
        return bot.TryGetProperty("username", out var nombre) ? nombre.GetString() : null;
    }

    public async Task<long> EnviarAsync(long chatId, string texto, IReadOnlyList<BotonTelegram>? botones, CancellationToken ct)
    {
        object cuerpo = botones is { Count: > 0 }
            ? new { chat_id = chatId, text = texto, reply_markup = new { inline_keyboard = new[] { botones.Select(b => new { text = b.Texto, callback_data = b.Datos }).ToArray() } } }
            : new { chat_id = chatId, text = texto };
        var mensaje = await LlamarAsync("sendMessage", cuerpo, ct);
        return mensaje.GetProperty("message_id").GetInt64();
    }

    public Task EditarAsync(long chatId, long mensajeId, string texto, CancellationToken ct) =>
        LlamarAsync("editMessageText", new { chat_id = chatId, message_id = mensajeId, text = texto }, ct);

    public Task ContestarBotonAsync(string callbackId, string texto, CancellationToken ct) =>
        LlamarAsync("answerCallbackQuery", new { callback_query_id = callbackId, text = texto }, ct);

    public async Task<IReadOnlyList<ActualizacionTelegram>> LeerAsync(long desde, int esperaSegundos, CancellationToken ct)
    {
        var resultado = await LlamarAsync("getUpdates",
            new { offset = desde, timeout = esperaSegundos, allowed_updates = new[] { "message", "callback_query" } }, ct);
        var actualizaciones = new List<ActualizacionTelegram>();
        foreach (var u in resultado.EnumerateArray())
        {
            var id = u.GetProperty("update_id").GetInt64();
            if (u.TryGetProperty("callback_query", out var boton))
            {
                actualizaciones.Add(new ActualizacionTelegram(id, boton.GetProperty("message").GetProperty("chat").GetProperty("id").GetInt64(),
                    Nombre(boton), null, boton.GetProperty("id").GetString(), boton.TryGetProperty("data", out var datos) ? datos.GetString() : null));
            }
            else if (u.TryGetProperty("message", out var mensaje))
            {
                actualizaciones.Add(new ActualizacionTelegram(id, mensaje.GetProperty("chat").GetProperty("id").GetInt64(), Nombre(mensaje),
                    mensaje.TryGetProperty("text", out var texto) ? texto.GetString() : null));
            }
            else
            {
                actualizaciones.Add(new ActualizacionTelegram(id, 0, null, null));
            }
        }
        return actualizaciones;
    }

    private static string? Nombre(JsonElement e) =>
        e.TryGetProperty("from", out var de) && de.TryGetProperty("first_name", out var nombre) ? nombre.GetString() : null;

    private async Task<JsonElement> LlamarAsync(string metodo, object cuerpo, CancellationToken ct)
    {
        if (_token is null)
            throw new TelegramException("Falta el token del bot.", permanente: true);
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await _http.PostAsJsonAsync(metodo, cuerpo, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new TelegramException($"Sin conexión con Telegram: {ex.Message}", permanente: false);
        }
        using (respuesta)
        {
            using var documento = await JsonDocument.ParseAsync(await respuesta.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var raiz = documento.RootElement;
            if (raiz.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                return raiz.GetProperty("result").Clone();
            var codigo = raiz.TryGetProperty("error_code", out var c) ? c.GetInt32() : (int)respuesta.StatusCode;
            var descripcion = raiz.TryGetProperty("description", out var d) ? d.GetString() : respuesta.ReasonPhrase;
            // 429 es "espera un poco": se reintenta. Los demás 4xx no se arreglan solos.
            throw new TelegramException($"Telegram: {descripcion}", permanente: codigo is >= 400 and < 500 and not 429);
        }
    }
}
