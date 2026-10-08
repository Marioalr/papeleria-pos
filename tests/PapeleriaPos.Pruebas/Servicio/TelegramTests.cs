using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Servicio.Telegram;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Telegram sin internet: guarda lo que se manda y entrega lo que se le encola, como si el dueño escribiera.</summary>
internal sealed class TelegramFalso : ITelegram
{
    private long _siguiente = 100;

    public List<(long Chat, long Id, string Texto, IReadOnlyList<BotonTelegram>? Botones)> Enviados { get; } = [];
    public List<(long Chat, long Id, string Texto)> Editados { get; } = [];
    public List<string> Contestados { get; } = [];
    public List<ActualizacionTelegram> PorLeer { get; } = [];

    public bool Configurado => true;

    public Task<string?> NombreBotAsync(CancellationToken ct) => Task.FromResult<string?>("papeleria_prueba_bot");

    public Task<long> EnviarAsync(long chatId, string texto, IReadOnlyList<BotonTelegram>? botones, CancellationToken ct)
    {
        var id = ++_siguiente;
        Enviados.Add((chatId, id, texto, botones));
        return Task.FromResult(id);
    }

    public Task EditarAsync(long chatId, long mensajeId, string texto, CancellationToken ct)
    {
        Editados.Add((chatId, mensajeId, texto));
        return Task.CompletedTask;
    }

    public Task ContestarBotonAsync(string callbackId, string texto, CancellationToken ct)
    {
        Contestados.Add(texto);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActualizacionTelegram>> LeerAsync(long desde, int esperaSegundos, CancellationToken ct)
    {
        var listas = PorLeer.Where(a => a.Id >= desde).ToList();
        PorLeer.Clear();
        return Task.FromResult<IReadOnlyList<ActualizacionTelegram>>(listas);
    }
}

/// <summary>El servicio de prueba con el Telegram falso y el dueño ya vinculado en el chat 7001.</summary>
public abstract class PruebaTelegram(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    protected const long ChatDueno = 7001;
    private static long _actualizacion = 1;
    private WebApplicationFactory<Program>? _fabrica;

    internal TelegramFalso Telegram { get; } = new();

    protected WebApplicationFactory<Program> Fabrica =>
        _fabrica ??= servicio.Fabrica.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ITelegram>(Telegram)));

    protected MotorTelegram Motor => Fabrica.Services.GetRequiredService<MotorTelegram>();

    protected HttpClient Http() => Fabrica.CreateClient();

    protected async Task<CajaDto> CajaAsync() => (await Http().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First(c => c.Serie == "A");

    protected static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Lo que el dueño le escribe al bot (o el botón que aprieta).</summary>
    internal async Task EscribirAsync(long chat, string? texto, string? boton = null)
    {
        Telegram.PorLeer.Add(new ActualizacionTelegram(Interlocked.Increment(ref _actualizacion) + 1000, chat, "Mario", texto,
            boton is null ? null : $"cb{_actualizacion}", boton));
        await Motor.LeerAsync(0);
        await Motor.EnviarAsync();
    }

    protected async Task VincularAsync(long chat = ChatDueno)
    {
        var caja = await CajaAsync();
        var codigo = await Leer<CodigoVinculacionDto>(await Http().PostAsJsonAsync($"api/sucursales/{caja.SucursalId}/telegram/codigo",
            new SolicitudCredenciales("dueno", "3333")));
        await EscribirAsync(chat, $"/vincular {codigo.Codigo}");
    }

    protected async Task<(CajaDto Caja, SesionDto Cajero, TurnoDto Turno)> TurnoAsync(string usuario = "cajero", string pin = "1111")
    {
        var http = Http();
        var caja = await CajaAsync();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        return (caja, cajero, turno);
    }

    protected List<string> MensajesA(long chat) => Telegram.Enviados.Where(m => m.Chat == chat).Select(m => m.Texto).ToList();
}

public sealed class TelegramVinculoTests(ServicioDePrueba servicio) : PruebaTelegram(servicio)
{
    [Fact]
    public async Task Solo_el_dueno_vincula_su_chat_y_el_bot_ignora_a_los_demas()
    {
        var caja = await CajaAsync();
        var encargado = await Http().PostAsJsonAsync($"api/sucursales/{caja.SucursalId}/telegram/codigo", new SolicitudCredenciales("encargado", "2222"));
        await VincularAsync();
        await EscribirAsync(8002, "/vincular 000000");
        await EscribirAsync(8003, "hola");
        await EscribirAsync(ChatDueno, "/resumen");

        Assert.Equal(HttpStatusCode.BadRequest, encargado.StatusCode);
        Assert.StartsWith("Listo, Dueño de prueba", MensajesA(ChatDueno)[0]);
        Assert.Contains("Resumen del día", MensajesA(ChatDueno)[1]);
        Assert.StartsWith("Ese código no vale", MensajesA(8002).Single());
        Assert.Empty(MensajesA(8003));
        var estado = await Http().GetFromJsonAsync<EstadoTelegramDto>($"api/sucursales/{caja.SucursalId}/telegram");
        await Motor.IniciarAsync(default);
        Assert.Equal("papeleria_prueba_bot", Motor.NombreBot);
        Assert.Equal("Dueño de prueba", estado!.Chats.Single().Usuario);
    }
}

public sealed class TelegramAutorizacionTests(ServicioDePrueba servicio) : PruebaTelegram(servicio)
{
    [Fact]
    public async Task El_dueno_aprueba_desde_telegram_y_la_caja_sigue_con_esa_autorizacion()
    {
        await VincularAsync();
        var (caja, cajero, turno) = await TurnoAsync();
        var http = Http();
        var producto = (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=pluma%20punto%20fino&sucursalId={caja.SucursalId}"))!
            .First(r => r.Codigo is not null);
        var pluma = (await http.GetFromJsonAsync<ProductoVentaDto>($"api/productos/{producto.VarianteId}?sucursalId={caja.SucursalId}"))!;

        var solicitud = await Leer<SolicitudRemotaDto>(await http.PostAsJsonAsync("api/autorizaciones/remotas",
            new SolicitudAutorizacionRemota(AccionControlada.CambiarPrecio, caja.SucursalId, caja.Id, cajero.UsuarioId, 500, "Precio de la pluma a $5.00")));
        await Motor.EnviarAsync();
        var mensaje = Telegram.Enviados.Last(m => m.Chat == ChatDueno);
        var pendiente = await http.GetFromJsonAsync<EstadoSolicitudRemotaDto>($"api/autorizaciones/remotas/{solicitud.Id}");

        await EscribirAsync(ChatDueno, null, mensaje.Botones![0].Datos);
        var aprobada = await http.GetFromJsonAsync<EstadoSolicitudRemotaDto>($"api/autorizaciones/remotas/{solicitud.Id}");
        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(pluma.VarianteId, pluma.Presentacion.Id, 1000, null, 500, aprobada!.Autorizacion!.Id)], [new CobroSolicitado(FormaPago.Efectivo, 500)])));

        Assert.Contains("Autorización · Caja 1", mensaje.Texto);
        Assert.Equal(["Aprobar", "Rechazar"], mensaje.Botones.Select(b => b.Texto));
        Assert.Equal(EstadoSolicitudRemota.Pendiente, pendiente!.Estado);
        Assert.Equal("Aprobada: la caja ya puede seguir.", Telegram.Contestados.Single());
        Assert.Equal(EstadoSolicitudRemota.Aprobada, aprobada.Estado);
        Assert.Contains(Telegram.Editados, e => e.Id == mensaje.Id && e.Texto.EndsWith("Aprobada por Dueño de prueba."));
        Assert.Equal(500, ticket.TotalCentavos);
        // El cambio de precio también le avisa, y dice que se autorizó por Telegram.
        await Motor.EnviarAsync();
        Assert.Contains(MensajesA(ChatDueno), t => t.StartsWith("Cambio de precio") && t.Contains("autorizado por Telegram"));
    }

    [Fact]
    public async Task Si_la_rechaza_la_caja_se_entera_y_no_le_llega_una_alerta_de_lo_que_el_mismo_rechazo()
    {
        await VincularAsync(7101);
        var (caja, cajero, _) = await TurnoAsync();
        var http = Http();
        var solicitud = await Leer<SolicitudRemotaDto>(await http.PostAsJsonAsync("api/autorizaciones/remotas",
            new SolicitudAutorizacionRemota(AccionControlada.CancelarTicket, caja.SucursalId, caja.Id, cajero.UsuarioId, 24500, "Cancelar el ticket A-000123")));
        await Motor.EnviarAsync();
        var antes = Telegram.Enviados.Count;

        await EscribirAsync(7101, null, $"aut:{solicitud.Id:N}:0");
        var estado = await http.GetFromJsonAsync<EstadoSolicitudRemotaDto>($"api/autorizaciones/remotas/{solicitud.Id}");
        await EscribirAsync(7101, null, $"aut:{solicitud.Id:N}:1");

        Assert.Equal(EstadoSolicitudRemota.Rechazada, estado!.Estado);
        Assert.Null(estado.Autorizacion);
        Assert.Equal(["Rechazada.", "Ya estaba rechazada."], Telegram.Contestados.TakeLast(2));
        Assert.Equal(antes, Telegram.Enviados.Count);
    }
}

public sealed class TelegramAlertasTests(ServicioDePrueba servicio) : PruebaTelegram(servicio)
{
    [Fact]
    public async Task Avisa_lo_que_pasa_del_umbral_y_al_ultimo_corte_manda_el_resumen()
    {
        await VincularAsync();
        var (caja, cajero, turno) = await TurnoAsync();
        var http = Http();

        await Leer<ComprobanteMovimientoDto>(await http.PostAsJsonAsync($"api/turnos/{turno.Id}/movimientos",
            new SolicitudMovimiento(TipoMovimientoCaja.Entrada, 100000, "Cambio del banco", cajero.UsuarioId)));
        await Leer<ComprobanteMovimientoDto>(await http.PostAsJsonAsync($"api/turnos/{turno.Id}/movimientos",
            new SolicitudMovimiento(TipoMovimientoCaja.Salida, 10000, "Garrafón", cajero.UsuarioId)));
        await Leer<ComprobanteMovimientoDto>(await http.PostAsJsonAsync($"api/turnos/{turno.Id}/movimientos",
            new SolicitudMovimiento(TipoMovimientoCaja.Salida, 60000, "Pago al proveedor", cajero.UsuarioId)));
        await Leer<CorteDto>(await http.PostAsJsonAsync($"api/turnos/{turno.Id}/corte-z",
            new SolicitudCorteZ("encargado", "2222", new Dictionary<long, int> { [10000] = 1 })));
        await Motor.EnviarAsync();
        var mensajes = MensajesA(ChatDueno);

        Assert.Single(mensajes, t => t.StartsWith("Salida de efectivo · Caja 1") && t.Contains("$600.00"));
        Assert.DoesNotContain(mensajes, t => t.Contains("Garrafón"));
        Assert.Single(mensajes, t => t.StartsWith("Diferencia en el corte · Caja 1") && t.Contains("Faltante"));
        var resumen = mensajes.Single(t => t.StartsWith("Resumen del día"));
        Assert.Contains("Cortes:", resumen);
        Assert.Contains("Salidas de efectivo: $700.00", resumen);
    }

}

public sealed class TelegramTiempoTests(ServicioDePrueba servicio) : PruebaTelegram(servicio)
{
    [Fact]
    public async Task Las_alertas_apagadas_no_llegan_y_las_de_tiempo_avisan_una_vez()
    {
        await VincularAsync(7201);
        var caja = await CajaAsync();
        var http = Http();
        var ajustes = await Leer<AjustesAlertasDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/telegram/alertas",
            new SolicitudAlertas("dueno", "3333", new AjustesAlertasDto([TipoAlerta.SalidaEfectivo], 5000, 20000, 50000, 10, new TimeOnly(23, 58), new TimeOnly(23, 59)))));
        var (_, cajero, turno) = await TurnoAsync();
        await Leer<ComprobanteMovimientoDto>(await http.PostAsJsonAsync($"api/turnos/{turno.Id}/movimientos",
            new SolicitudMovimiento(TipoMovimientoCaja.Salida, 90000, "Retiro grande", cajero.UsuarioId)));

        // Una caja dada de alta que dejó de mandar su latido hace 20 minutos, con turno abierto.
        var alta = await Leer<AltaCajaDto>(await http.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, null, "Caja 9", "Z", "PC-9")));
        await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(alta.Caja.Id, cajero.UsuarioId, 0)));
        Fabrica.Services.GetRequiredService<PresenciaCajas>().Visto(alta.Caja.Id, DateTime.UtcNow.AddMinutes(-20));

        await Motor.RevisarAsync();
        await Motor.RevisarAsync();
        await Motor.EnviarAsync();
        var mensajes = MensajesA(7201);

        Assert.Equal([TipoAlerta.SalidaEfectivo], ajustes.Apagadas);
        Assert.DoesNotContain(mensajes, t => t.Contains("Retiro grande"));
        Assert.Single(mensajes, t => t.StartsWith("Caja sin conexión · Caja 9"));
        Assert.Contains(mensajes, t => t.StartsWith("Caja abierta fuera de horario · Caja 1"));
        Assert.Single(mensajes, t => t.StartsWith("Caja abierta fuera de horario · Caja 9"));
    }
}

public sealed class TelegramClientesTests(ServicioDePrueba servicio) : PruebaTelegram(servicio)
{
    [Fact]
    public async Task Al_dueno_le_llegan_los_privilegios_que_da_el_encargado_y_no_los_suyos()
    {
        await VincularAsync();
        var http = Http();
        var caja = await CajaAsync();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var antes = MensajesA(ChatDueno).Count;

        var cliente = await Leer<ClienteRegistradoDto>(await http.PostAsJsonAsync("api/clientes", new SolicitudGuardarCliente(encargado.UsuarioId,
            caja.SucursalId, new ClienteRegistradoDto(Guid.Empty, "Cliente de Telegram", 1500, null, true))));
        await Leer<ClienteRegistradoDto>(await http.PostAsJsonAsync("api/clientes", new SolicitudGuardarCliente(dueno.UsuarioId, caja.SucursalId,
            cliente with { DescuentoCentesimas = 500 })));
        await Leer<ClienteRegistradoDto>(await http.PostAsJsonAsync("api/clientes", new SolicitudGuardarCliente(encargado.UsuarioId, caja.SucursalId,
            cliente with { DescuentoCentesimas = 500, Nota = "Solo cambia la nota" })));
        await Motor.EnviarAsync();

        var aviso = Assert.Single(MensajesA(ChatDueno).Skip(antes));
        Assert.Contains("Privilegios de un cliente", aviso);
        Assert.Contains("Privilegios de «Cliente de Telegram» (alta): descuento 15 %", aviso);
    }
}
