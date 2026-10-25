using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El registro del servicio en archivos (el piloto alfa, 2026-10-25: juntar datos): uno por día, con 30 días de historia, lo del
/// POS desde «Información» y lo demás desde «Aviso», con un tope por día y nunca un secreto (la clave de eleventa, un token, un
/// PIN o la contraseña de una cadena de conexión).
/// </summary>
public sealed class RegistroDelServicioTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"registro-servicio-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    private string Archivo(DateTime dia) => Path.Combine(_carpeta, $"servicio-{dia:yyyy-MM-dd}.log");

    [Fact]
    public async Task Un_archivo_por_dia_con_30_dias_de_historia()
    {
        // La hora local de este reloj es la UTC: el día no depende de la zona de la PC que corre las pruebas.
        var reloj = RelojDeLaRed.Nuevo();
        var hoy = reloj.GetLocalNow().DateTime.Date;
        Directory.CreateDirectory(_carpeta);
        foreach (var hace in new[] { 29, 30, 31, 45 })
            File.WriteAllText(Archivo(hoy.AddDays(-hace)), "de antes\n");
        File.WriteAllText(Path.Combine(_carpeta, $"red-servicio-{hoy.AddDays(-45):yyyy-MM-dd}.log"), "la bitácora de red\n");
        File.WriteAllText(Path.Combine(_carpeta, "servicio-notas.log"), "otra cosa\n");

        var registro = new RegistroDelServicio(_carpeta, [], reloj, juntar: TimeSpan.Zero);
        var log = registro.CreateLogger("PapeleriaPos.Servicio.EnvioVentasEleventa");
        log.LogInformation("Envío a eleventa · Venta B-000001 · intento 1 al cobrar · 412 ms · enviada con el folio 4521");
        await registro.AlDiaAsync();

        var lineas = File.ReadAllLines(Archivo(hoy));
        Assert.StartsWith("2026-10-17 14:00:00.000  Info   RegistroDelServicio  Empieza el registro del servicio: Papelería POS ", lineas[0]);
        Assert.Equal("2026-10-17 14:00:00.000  Info   EnvioVentasEleventa  Envío a eleventa · Venta B-000001 · intento 1 al cobrar · 412 ms · enviada con el folio 4521",
            lineas[1]);
        // Se quedan los de los últimos 30 días (con hoy) y lo que no es de este registro.
        Assert.True(File.Exists(Archivo(hoy.AddDays(-29))));
        Assert.False(File.Exists(Archivo(hoy.AddDays(-30))));
        Assert.False(File.Exists(Archivo(hoy.AddDays(-31))));
        Assert.False(File.Exists(Archivo(hoy.AddDays(-45))));
        Assert.True(File.Exists(Path.Combine(_carpeta, $"red-servicio-{hoy.AddDays(-45):yyyy-MM-dd}.log")));
        Assert.True(File.Exists(Path.Combine(_carpeta, "servicio-notas.log")));

        // El servicio corre días sin detenerse: al otro día, otro archivo, y se quita el que ya pasó de los 30 días.
        reloj.Avanzar(TimeSpan.FromDays(1));
        log.LogWarning("Envío a eleventa · Venta B-000002 · intento 1 en la vuelta · 2,003 ms · pendiente, se vuelve a intentar: No contesta");
        await registro.AlDiaAsync();
        Assert.Contains("Aviso  EnvioVentasEleventa  Envío a eleventa · Venta B-000002", File.ReadAllText(Archivo(hoy.AddDays(1))));
        Assert.DoesNotContain("B-000002", File.ReadAllText(Archivo(hoy)));
        Assert.False(File.Exists(Archivo(hoy.AddDays(-29))));

        // Al detenerse el servicio lo dice: un hueco sin «Termina» es que se apagó de golpe.
        registro.Dispose();
        Assert.EndsWith("Termina el registro del servicio.", File.ReadAllLines(Archivo(hoy.AddDays(1)))[^1]);
    }

    [Fact]
    public async Task Nunca_escribe_un_secreto()
    {
        const string clave = "clave-de-eleventa-9876";
        const string token = "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw-de-mentiras";
        using var registro = new RegistroDelServicio(_carpeta, [clave, token, "abc", null], RelojDeLaRed.Nuevo(), juntar: TimeSpan.Zero);
        var log = registro.CreateLogger("PapeleriaPos.Servicio.LecturaEleventa");

        log.LogWarning("La clave es {Clave}", clave);
        log.LogError(new InvalidOperationException($"No se pudo con data source=caja-principal;port=3050;user id=SYSDBA;password={clave};charset=WIN1252"),
            "Algo falló al leer eleventa");
        log.LogInformation("Otra cadena: Data Source=pos.db;Password=otra-clave-1;Pooling=False");
        log.LogInformation("Llegó {Cuerpo}", """{"usuario":"dueno","pin":"3333"}""");
        log.LogInformation("Telegram: https://api.telegram.org/bot{Token}/getUpdates", token);
        log.LogInformation("Otro bot: 987654321:BBHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw-otro");
        log.LogInformation("El entorno de gbak: ISC_PASSWORD=masterkey-de-prueba");
        log.LogInformation("Sin secretos: la clave de Firebird no se ve; abc es corto y se queda");
        await registro.AlDiaAsync();

        var texto = File.ReadAllText(registro.ArchivoDeHoy);
        foreach (var secreto in new[] { clave, token, "otra-clave-1", "3333", "987654321:BBH", "masterkey-de-prueba" })
            Assert.DoesNotContain(secreto, texto);
        Assert.Contains("La clave es ***", texto);
        Assert.Contains("user id=SYSDBA;password=***;charset=WIN1252", texto);
        Assert.Contains("Password=***;Pooling=False", texto);
        Assert.Contains("""{"usuario":"dueno","pin":***}""", texto);
        Assert.Contains("https://api.telegram.org/bot***/getUpdates", texto);
        Assert.Contains("ISC_PASSWORD=***", texto);
        Assert.Contains("Sin secretos: la clave de Firebird no se ve; abc es corto y se queda", texto);
        // El error va con su detalle, ya sin la clave.
        Assert.Contains("Error  LecturaEleventa  Algo falló al leer eleventa", texto);
        Assert.Contains("System.InvalidOperationException: No se pudo con data source=caja-principal", texto);
    }

    [Fact]
    public async Task Pasado_el_tope_del_dia_solo_los_errores()
    {
        using var registro = new RegistroDelServicio(_carpeta, [], RelojDeLaRed.Nuevo(), tope: 4096, juntar: TimeSpan.Zero);
        var log = registro.CreateLogger("PapeleriaPos.Servicio.LecturaEleventa");
        for (var i = 0; i < 200; i++)
            log.LogInformation("Lectura de eleventa · {Ms} ms · 1,950 productos · sin cambios", i);
        log.LogError("Un error después del tope");
        log.LogInformation("Una lectura después del tope");
        await registro.AlDiaAsync();

        var texto = File.ReadAllText(registro.ArchivoDeHoy);
        Assert.Contains("El registro de hoy llegó a su tope", texto);
        Assert.Contains("Un error después del tope", texto);
        Assert.DoesNotContain("Una lectura después del tope", texto);
        Assert.True(new FileInfo(registro.ArchivoDeHoy).Length < 4096 + 1024, $"Mide {new FileInfo(registro.ArchivoDeHoy).Length} bytes.");
    }

    [Fact]
    public void Lo_que_no_cabe_en_la_fila_se_cuenta_y_se_dice()
    {
        // Mientras junta (30 s) llegan 100 líneas a una fila de 16: las que no caben se cuentan. Al detenerse escribe lo que hay.
        var registro = new RegistroDelServicio(_carpeta, [], RelojDeLaRed.Nuevo(), juntar: TimeSpan.FromSeconds(30), capacidad: 16);
        var log = registro.CreateLogger("PapeleriaPos.Prueba");
        for (var i = 0; i < 100; i++)
            log.LogInformation("Línea {N}", i);
        registro.Dispose();

        var texto = File.ReadAllText(registro.ArchivoDeHoy);
        Assert.Matches(@"Llegaron demasiadas líneas a la vez: \d+ no cupieron en la fila y no se anotaron\.", texto);
        Assert.Contains("Línea 0", texto);
        Assert.DoesNotContain("Línea 99", texto);
    }

    [Fact]
    public async Task El_servicio_anota_lo_del_pos_desde_informacion_y_lo_demas_desde_aviso_sin_la_clave()
    {
        const string clave = "clave-del-piloto-4321";
        await using var servicio = await ServicioDePrueba.CrearAsync(("Registro:Activo", "true"), ("Registro:Carpeta", _carpeta), ("Eleventa:Clave", clave));
        var registros = servicio.Fabrica.Services.GetRequiredService<ILoggerFactory>();

        registros.CreateLogger("PapeleriaPos.Servicio.Prueba").LogInformation("Información del POS con la clave {Clave}", clave);
        registros.CreateLogger("Microsoft.AspNetCore.Prueba").LogInformation("Información de Microsoft");
        registros.CreateLogger("Microsoft.AspNetCore.Prueba").LogWarning("Aviso de Microsoft");
        registros.CreateLogger("Otra.Cosa").LogInformation("Información de otra cosa");
        var registro = servicio.Fabrica.Services.GetServices<ILoggerProvider>().OfType<RegistroDelServicio>().Single();
        await registro.AlDiaAsync();

        Assert.Equal(_carpeta, registro.Carpeta);
        // Mientras el servicio sigue escribiendo: como lo lee el script que junta los datos (en Windows, sin chocar con quien escribe).
        var texto = LeerCompartido(registro.ArchivoDeHoy);
        Assert.Contains("Empieza el registro del servicio", texto);
        Assert.Contains("Info   Prueba  Información del POS con la clave ***", texto);
        Assert.Contains("Aviso  Microsoft.AspNetCore.Prueba  Aviso de Microsoft", texto);
        Assert.DoesNotContain("Información de Microsoft", texto);
        Assert.DoesNotContain("Información de otra cosa", texto);
        Assert.DoesNotContain(clave, texto);
    }

    /// <summary>Un archivo que otro hilo puede estar escribiendo en ese momento.</summary>
    internal static string LeerCompartido(string ruta)
    {
        using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var lector = new StreamReader(flujo);
        return lector.ReadToEnd();
    }

    [Fact]
    public void Va_en_registros_junto_a_la_base_y_no_en_la_prueba_con_una_persona()
    {
        static IConfiguration Configuracion(params (string Clave, string Valor)[] valores) => new ConfigurationBuilder()
            .AddInMemoryCollection(valores.Select(v => new KeyValuePair<string, string?>(v.Clave, v.Valor))).Build();
        var cadena = $"Data Source={Path.Combine(Path.GetTempPath(), "tienda", "pos.db")}";

        Assert.Equal(Path.Combine(Path.GetTempPath(), "tienda", "registros"), RegistroDelServicio.CarpetaDeRegistros(Configuracion(), cadena));
        Assert.Equal(_carpeta, RegistroDelServicio.CarpetaDeRegistros(Configuracion(("Registro:Carpeta", _carpeta)), cadena));
        Assert.True(RegistroDelServicio.Activo(Configuracion()));
        Assert.False(RegistroDelServicio.Activo(Configuracion(("Registro:Activo", "false"))));
        // La caja de la prueba con una persona ya guarda lo que escribe su servicio en registros\servicio-….log.
        Assert.False(RegistroDelServicio.Activo(Configuracion(("Prueba:Activa", "true"))));
        Assert.False(RegistroDelServicio.Activo(Configuracion(("Prueba:DetenerAlCerrarEntrada", "true"))));
    }
}
