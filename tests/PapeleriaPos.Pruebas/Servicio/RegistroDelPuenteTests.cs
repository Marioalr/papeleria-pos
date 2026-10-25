using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que el puente con eleventa deja en el registro del servicio (el piloto alfa, 2026-10-25: juntar datos): cada envío con lo
/// que tardó y cómo quedó (y su error, aunque después entre), lo que esperó el cajero al cobrar y con qué folio salió, cada
/// lectura con lo que tardó y lo que cambió, el respaldo del día, y los turnos que adopta y cierra. Con un eleventa que no
/// contesta, un gbak de mentiras y un registro en memoria; lo que necesita que eleventa conteste, contra el Firebird de prueba.
/// </summary>
public sealed class RegistroDelPuenteTests
{
    /// <summary>Una clave que no debe aparecer en ningún renglón.</summary>
    private const string Clave = "clave-del-puente-4321";

    private static Task<ServicioDePrueba> ServicioAsync(RegistroEnMemoria registro, (string, string)[] ajustes, GbakDePrueba? gbak = null) =>
        ServicioDePrueba.CrearAsync(s =>
        {
            s.AddSingleton<ILoggerProvider>(registro);
            if (gbak is not null)
                s.AddSingleton<IEjecutorGbak>(gbak);
        }, ajustes);

    [Fact]
    public async Task Cada_envio_dice_lo_que_espero_el_cajero_y_su_error_aunque_despues_entre()
    {
        var registro = new RegistroEnMemoria();
        await using var servicio = await ServicioAsync(registro, [.. Ajustes(leer: true), ("Eleventa:Clave", Clave)]);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        // La lectura del arranque: eleventa no contesta (nadie escucha en ese puerto). Sin la traza: el texto ya lo dice.
        var lectura = await registro.EsperarAsync("Lectura de eleventa · no se pudo (", "No contesta el eleventa de 127.0.0.1");
        Assert.Equal(LogLevel.Warning, lectura.Nivel);
        Assert.Null(lectura.Error);

        // El cobro: el intento de cuando se cobró (el cajero lo esperó), con lo que tardó, y lo que esperó el cajero.
        var ticket = await CobrarAsync(c);
        var envio = await registro.EsperarAsync($"Envío a eleventa · Venta {ticket.Folio} · intento 1 al cobrar · ");
        Assert.Matches($@"^Envío a eleventa · Venta {ticket.Folio} · intento 1 al cobrar · [\d,]+ ms · pendiente, se vuelve a intentar: No contesta el eleventa "
                       + @"de 127\.0\.0\.1: revisa que la caja principal esté prendida y en la red\. · envío [0-9a-f-]{36}$", envio.Texto);
        Assert.Equal(LogLevel.Warning, envio.Nivel);
        var cobro = await registro.EsperarAsync($"Cobro con eleventa · Venta {ticket.Folio} · ");
        Assert.Matches(@"· el cajero esperó \d+\.\d\d s · salió con el folio de la caja: no entró \(No contesta el eleventa de 127\.0\.0\.1: .+\): se manda en la vuelta$",
            cobro.Texto);
        Assert.Null(ticket.FolioEleventa);
        // Lo que esperó también le llega a la caja, para su registro.
        Assert.NotNull(ticket.EsperaEleventaMs);

        // El siguiente cobro ya no espera: eleventa no contestó hace poco.
        var otro = await CobrarAsync(c);
        await registro.EsperarAsync($"Cobro con eleventa · Venta {otro.Folio} · el cajero esperó ", "eleventa no contestó hace poco: no se esperó y se manda en la vuelta");

        // La vuelta: el segundo intento de la primera venta, con su error (en la bandeja solo queda el último, y se borra al entrar).
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        await registro.EsperarAsync($"Envío a eleventa · Venta {ticket.Folio} · intento 2 en la vuelta · ", "pendiente, se vuelve a intentar: No contesta el eleventa");

        Assert.DoesNotContain(registro.Lineas, l => l.Texto.Contains(Clave) || (l.Error?.ToString().Contains(Clave) ?? false));
    }

    [Fact]
    public async Task Lo_que_no_pasa_solo_queda_por_revisar_en_el_registro()
    {
        var registro = new RegistroEnMemoria();
        await using var servicio = await ServicioAsync(registro, Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        // Lo cobrado con tarjeta (llegó de la contingencia antes de saber del piloto): a eleventa no pasa solo.
        var paquete = (await c.Http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{c.Datos.Id}/paquete"))!;
        var venta = new VentaContingenciaDto(Guid.NewGuid(), c.Datos.Id, c.Turno.Id, c.Sesion.UsuarioId, paquete.UltimoFolio + 1, DateTime.UtcNow.AddMinutes(-25),
            [new LineaContingenciaDto(null, null, "Recuerdo", 1000, 1500)], [new CobroSolicitado(FormaPago.Tarjeta, 1500, "4821")]);
        Assert.True((await c.Http.PostAsJsonAsync("api/bandeja/ventas", venta)).IsSuccessStatusCode);

        // La contingencia: cuánto tardó en subir lo que se vendió sin conexión.
        var subio = await registro.EsperarAsync("Contingencia · subió la venta ");
        Assert.Matches(@"^Contingencia · subió la venta \w+-\d{6} de «.+», cobrada sin conexión a las \d\d/\d\d \d\d:\d\d:\d\d · 25 min( \d+ s)? después$", subio.Texto);
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        var envio = await registro.EsperarAsync("Envío a eleventa · Venta ", "sin mandarla · por revisar: ");
        Assert.Equal(LogLevel.Warning, envio.Nivel);
    }

    [Fact]
    public async Task El_respaldo_de_eleventa_del_dia_dice_cuando_empezo_cuanto_tardo_y_cuanto_mide()
    {
        var registro = new RegistroEnMemoria();
        using var gbak = new GbakDePrueba();
        await using var servicio = await ServicioAsync(registro, [.. Ajustes(), ("Eleventa:Gbak", gbak.Ruta), ("Eleventa:CarpetaRespaldos", gbak.Respaldos),
            ("Eleventa:RespaldoObligatorio", "true"), ("Eleventa:Clave", Clave)], gbak);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        await CobrarAsync(c);

        await registro.EsperarAsync("Respaldo de eleventa del día · empieza: gbak de 127.0.0.1:1/", gbak.Respaldos);
        var listo = await registro.EsperarAsync("Respaldo de eleventa del día · listo en ");
        Assert.Matches(@"^Respaldo de eleventa del día · listo en \d+\.\d\d s · 20 B · .+eleventa-\d{4}-\d{2}-\d{2}\.fbk$", listo.Texto);
        // La clave va a gbak en su entorno, nunca al registro.
        Assert.DoesNotContain(registro.Lineas, l => l.Texto.Contains(Clave));
    }

    [Fact]
    public async Task El_respaldo_de_eleventa_que_no_se_pudo_dice_por_que_y_cuando_se_vuelve_a_intentar()
    {
        var registro = new RegistroEnMemoria();
        using var gbak = new GbakDePrueba { Bien = false };
        await using var servicio = await ServicioAsync(registro, [.. Ajustes(), ("Eleventa:Gbak", gbak.Ruta), ("Eleventa:CarpetaRespaldos", gbak.Respaldos),
            ("Eleventa:RespaldoObligatorio", "true")], gbak);
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));

        var ticket = await CobrarAsync(c);

        var fallo = await registro.EsperarAsync("Respaldo de eleventa del día · no se pudo (tardó ");
        Assert.Matches(@"\): gbak no pudo respaldar eleventa \(código 1\): gbak: ERROR:Unable to complete network request to host ""10\.0\.0\.10""\. "
                       + @"Se vuelve a intentar a las \d\d:\d\d\.$", fallo.Texto);
        Assert.Equal(LogLevel.Warning, fallo.Nivel);
        // Sin el respaldo, el ticket salió con el folio de la caja, y lo dice.
        await registro.EsperarAsync($"Cobro con eleventa · Venta {ticket.Folio} · ", "salió con el folio de la caja: falta el respaldo de eleventa del día");
    }

    // ── Contra un Firebird de prueba ──

    [FactConFirebird]
    public async Task Con_eleventa_el_envio_dice_su_folio_la_lectura_lo_que_cambio_y_los_turnos_lo_que_paso()
    {
        var (e, turnoEleventa) = await EleventaAsync(abierta: true);
        await using (e)
        {
            var registro = new RegistroEnMemoria();
            await using var servicio = await ServicioAsync(registro, Ajustes(e.Servidor, leer: true));
            var c = await AbrirAsync(servicio, await MapearAsync(servicio));
            await EsperarPrimeraLecturaAsync(c);
            var primera = await registro.EsperarAsync("Lectura de eleventa · ", " producto · ");
            Assert.Matches(@"^Lectura de eleventa · [\d,]+ ms · 1 producto · ", primera.Texto);

            var ticket = await CobrarAsync(c);
            Assert.Equal(42, ticket.FolioEleventa);
            var envio = await registro.EsperarAsync($"Envío a eleventa · Venta {ticket.Folio} · intento 1 al cobrar · ");
            Assert.Matches(@"· [\d,]+ ms · enviada con el folio 42 · envío ", envio.Texto);
            Assert.Equal(LogLevel.Information, envio.Nivel);
            await registro.EsperarAsync($"Cobro con eleventa · Venta {ticket.Folio} · el cajero esperó ", "salió con el folio 42 de eleventa");
            // El turno de la caja nueva tomó el de eleventa (al mandar lo primero, o en la lectura).
            await registro.EsperarAsync(l => l.Texto.StartsWith("Turno del piloto · ", StringComparison.Ordinal)
                                             && l.Texto.Contains($"adoptó el turno {turnoEleventa} de eleventa", StringComparison.Ordinal), "la adopción del turno");

            // Eleventa hace su corte: la lectura lo toma y lo dice.
            await e.CortarAsync(turnoEleventa!.Value, HoraLocal(0).AddSeconds(-1));
            await LeerAsync(c);
            await registro.EsperarAsync("Turno del piloto · se tomó el corte del turno " + turnoEleventa.Value.ToString(CultureInfo.InvariantCulture) + " de eleventa");
            Assert.DoesNotContain(registro.Lineas, l => l.Texto.Contains(e.Servidor.Clave));
        }
    }
}
