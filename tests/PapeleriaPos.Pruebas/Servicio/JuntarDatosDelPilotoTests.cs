using System.Globalization;
using System.Text;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Servicio;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que saca el programa del servicio para <c>instalacion\juntar-datos-piloto.ps1</c> (el piloto alfa, 2026-10-25): de una COPIA
/// de la base, la bandeja hacia eleventa y la bitácora de los días del piloto en CSV; y la configuración de la PC sin secretos.
/// </summary>
public sealed class JuntarDatosDelPilotoTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"datos-piloto-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    private static DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));

    /// <summary>
    /// Una caja 2 del piloto con dos ventas que no han entrado a eleventa (no contesta), y un respaldo de su base como el que hace
    /// el servicio: la copia que lee el programa.
    /// </summary>
    private async Task<(string Copia, List<TicketDto> Tickets)> CopiaDelPilotoAsync()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var tickets = new List<TicketDto> { await CobrarAsync(c), await CobrarAsync(c, plumas: 2, comun: true) };
        // La vuelta: la primera vuelve a no entrar (y lo demás espera detrás de ella), con su error.
        Assert.Equal(System.Net.HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        await EsperarAsync(() => EnviosAsync(c), x => x.PorAtender.Count == 2 && x.PorAtender[0].Error is not null);
        var copia = Respaldos.Hacer($"Data Source={servicio.Ruta};Pooling=False", Path.Combine(_carpeta, "respaldos"), HoraMexico.ALocal(DateTime.UtcNow), "ahora-0000");
        return (copia, tickets);
    }

    /// <summary>Lo que se cobró, como lo escribe el CSV: «45.50».</summary>
    private static string Total(TicketDto t) => ((t.TotalCentavos + t.RedondeoCentavos) / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    [Fact]
    public async Task De_la_copia_salen_la_bandeja_y_la_bitacora_de_los_dias_del_piloto()
    {
        var (copia, tickets) = await CopiaDelPilotoAsync();
        var salida = Path.Combine(_carpeta, "csv");

        var periodo = await JuntarDatosDelPiloto.ExportarAsync(copia, null, Hoy, salida);

        // Sin «desde», desde el primer día del piloto: lo primero que se mandó a eleventa, hoy.
        Assert.Equal((Hoy, Hoy, (DateOnly?)Hoy, 2), (periodo.Desde, periodo.Hasta, periodo.PrimerDia, periodo.Envios));
        Assert.Equal($"desde={Hoy:yyyy-MM-dd}\nhasta={Hoy:yyyy-MM-dd}\nprimer-dia={Hoy:yyyy-MM-dd}\n", File.ReadAllText(Path.Combine(salida, "periodo.txt")));

        var bytes = File.ReadAllBytes(Path.Combine(salida, "envios-a-eleventa.csv"));
        // Con su marca de UTF-8, para que Excel lea los acentos.
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var envios = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Envío,Tipo,Qué,Venta,Total,Estado,Creado,Intentos,Último intento,Enviado,Segundos hasta enviarse,Folio de eleventa,Ticket de eleventa,"
                     + "Número en eleventa,Folio repetido,Por revisar porque,Por revisar desde,Último error", envios[0]);
        Assert.Equal(3, envios.Length);
        Assert.Contains($",Venta,Venta {tickets[0].Folio},{tickets[0].Folio},{Total(tickets[0])},Pendiente,", envios[1]);
        Assert.Contains($",Venta,Venta {tickets[1].Folio},{tickets[1].Folio},{Total(tickets[1])},Pendiente,", envios[2]);
        // El error de su último intento.
        Assert.EndsWith(",No contesta el eleventa de 127.0.0.1: revisa que la caja principal esté prendida y en la red.", envios[1]);

        var bitacora = File.ReadAllText(Path.Combine(salida, "bitacora.csv"), Encoding.UTF8).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Fecha,Caja,Usuario,Acción,Resultado,Detalle,Monto,Venta,Autorizó,Sin conexión", bitacora[0].TrimStart('﻿'));
    }

    [Fact]
    public async Task Con_desde_se_toman_esos_dias_y_lo_que_sigue_pendiente_aunque_sea_de_antes()
    {
        var (copia, _) = await CopiaDelPilotoAsync();
        var salida = Path.Combine(_carpeta, "csv");

        // Desde mañana: de lo creado hoy no entra nada… salvo lo que sigue pendiente (lo que hay que ver).
        var periodo = await JuntarDatosDelPiloto.ExportarAsync(copia, Hoy.AddDays(1), Hoy.AddDays(1), salida);

        Assert.Equal(2, periodo.Envios);
        Assert.Equal(0, periodo.Bitacora);
    }

    [Fact]
    public async Task Sin_piloto_toma_los_ultimos_30_dias()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var copia = Respaldos.Hacer($"Data Source={servicio.Ruta};Pooling=False", Path.Combine(_carpeta, "respaldos"), HoraMexico.ALocal(DateTime.UtcNow));

        var periodo = await JuntarDatosDelPiloto.ExportarAsync(copia, null, Hoy, Path.Combine(_carpeta, "csv"));

        Assert.Equal((Hoy.AddDays(-29), (DateOnly?)null, 0), (periodo.Desde, periodo.PrimerDia, periodo.Envios));
    }

    [Fact]
    public async Task Nunca_abre_la_base_que_usa_el_servicio()
    {
        // Con la marca de la base contra apagones junto a ella, es la que está usando un servicio.
        Directory.CreateDirectory(_carpeta);
        var enUso = Path.Combine(_carpeta, "pos.db");
        await File.WriteAllTextAsync(enUso, "no es una base");
        await File.WriteAllTextAsync(enUso + ".en-uso", "");
        var salida = new StringWriter();

        var codigo = await JuntarDatosDelPiloto.CorrerAsync(["--copia", enUso, "--salida", Path.Combine(_carpeta, "csv")], salida);

        Assert.Equal(2, codigo);
        Assert.Contains("es la base que usa el servicio", salida.ToString());
        Assert.False(Directory.Exists(Path.Combine(_carpeta, "csv")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => JuntarDatosDelPiloto.ExportarAsync(enUso, null, Hoy, Path.Combine(_carpeta, "csv")));
    }

    [Fact]
    public async Task Desde_la_linea_de_ordenes_deja_los_csv_y_la_configuracion_sin_secretos()
    {
        var (copia, _) = await CopiaDelPilotoAsync();
        var servicioJson = Path.Combine(_carpeta, "servicio.json");
        await File.WriteAllTextAsync(servicioJson, """
            // La configuración de esta PC.
            {
              "Eleventa": {
                "Activo": true,
                "Escribir": true,
                "Servidor": "192.168.0.10",
                // "Clave": "una-clave-comentada",
                "Clave": "clave-que-no-va-9876",
                "Caja": 3,
              },
              "Telegram": { "Token": "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw" },
              "ConnectionStrings": { "Otra": "Data Source=otra.db;Password=clave-de-la-cadena;Pooling=False" },
            }
            """);
        var cajaJson = Path.Combine(_carpeta, "caja.json");
        await File.WriteAllTextAsync(cajaJson, """{ "Servicio": "http://192.168.0.10:5080/", "CajaId": "9b6c8a4e-0000-4000-8000-000000000001", "LlaveProtegida": "AQAAANCMnd8BFdERjHoAwE", "Impresora": "tcp://192.168.0.50:9100" }""");
        var salida = new StringWriter();
        var carpeta = Path.Combine(_carpeta, "datos");

        var codigo = await JuntarDatosDelPiloto.CorrerAsync(["--copia", copia, "--salida", carpeta, "--hasta", Hoy.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "--configuracion", servicioJson, "--caja", cajaJson], salida);

        Assert.Equal(0, codigo);
        Assert.Contains("2 renglones de la bandeja hacia eleventa", salida.ToString());
        Assert.True(File.Exists(Path.Combine(carpeta, "envios-a-eleventa.csv")));
        var servicio = File.ReadAllText(Path.Combine(carpeta, "servicio.json"));
        foreach (var secreto in new[] { "clave-que-no-va-9876", "una-clave-comentada", "AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", "clave-de-la-cadena" })
            Assert.DoesNotContain(secreto, servicio);
        Assert.Contains("\"Servidor\": \"192.168.0.10\"", servicio);
        Assert.Contains("\"Clave\": \"(quitado)\"", servicio);
        Assert.Contains("\"Token\": \"(quitado)\"", servicio);
        Assert.Contains("Data Source=otra.db;Password=(quitado);Pooling=False", servicio);
        var caja = File.ReadAllText(Path.Combine(carpeta, "caja.json"));
        Assert.DoesNotContain("AQAAANCMnd8BFdERjHoAwE", caja);
        Assert.Contains("\"Impresora\": \"tcp://192.168.0.50:9100\"", caja);
    }

    [Fact]
    public void Lo_que_es_un_secreto_por_su_nombre()
    {
        foreach (var secreto in new[] { "Clave", "clave", "Eleventa__Clave", "Password", "ISC_PASSWORD", "Token", "ApiToken", "APIToken", "Pin", "PinHash",
                     "LlaveProtegida", "llaveDelServicio", "Secreto", "Contraseña" })
            Assert.True(JuntarDatosDelPiloto.EsSecreto(secreto), secreto);
        foreach (var normal in new[] { "Servidor", "Base", "Pintar", "Spinner", "Opinion", "Impresora", "Caja", "Cajero", "Gbak", "Usuario" })
            Assert.False(JuntarDatosDelPiloto.EsSecreto(normal), normal);
        // Un JSON que no se lee no se copia tal cual (podría traer una clave): lo dice quien lo usa.
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => JuntarDatosDelPiloto.SinSecretos("{ \"Clave\": "));
    }

    [Fact]
    public void Un_campo_del_csv_va_entre_comillas_solo_si_hace_falta()
    {
        Assert.Equal("Venta B-000123", JuntarDatosDelPiloto.Campo("Venta B-000123"));
        Assert.Equal("\"No contesta, revisa\"", JuntarDatosDelPiloto.Campo("No contesta, revisa"));
        Assert.Equal("\"dijo \"\"hola\"\"\"", JuntarDatosDelPiloto.Campo("dijo \"hola\""));
        Assert.Equal("\"dos\nrenglones\"", JuntarDatosDelPiloto.Campo("dos\nrenglones"));
        Assert.Equal("\" con espacio\"", JuntarDatosDelPiloto.Campo(" con espacio"));
    }
}
