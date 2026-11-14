using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La base de una tienda como la deja el servicio de verdad, hecha una sola vez para todas las pruebas de la mudanza: sus datos de
/// prueba, la «Caja 1» (serie A) con un turno abierto y dos ventas cobradas por la API. Queda en un solo archivo (una copia consistente,
/// como un respaldo); cada prueba trabaja sobre su propia copia.
/// </summary>
public sealed class BaseParaMudar : IAsyncLifetime
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"mudanza-semilla-{Guid.NewGuid():N}");

    public string Archivo => Path.Combine(_carpeta, "semilla.db");

    /// <summary>Las dos ventas cobradas, en el orden en que se cobraron.</summary>
    public IReadOnlyList<Guid> Ventas { get; private set; } = [];

    /// <summary>Las sucursales como las contestó <c>/api/estado</c> con esa base.</summary>
    public IReadOnlyList<string> SucursalesDelEstado { get; private set; } = [];

    public async Task InitializeAsync()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        Ventas = await RestaurarRespaldoTests.DosVentasAsync(p);
        SucursalesDelEstado = (await p.EnMarcha!.CreateClient().GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.Sucursales;
        await p.DetenerAsync();
        Directory.CreateDirectory(_carpeta);
        File.Move(Respaldos.Hacer($"Data Source={p.Ruta};Pooling=False", _carpeta, new DateTime(2026, 11, 13), "semilla"), Archivo);
    }

    public Task DisposeAsync()
    {
        ArchivosDePrueba.BorrarCarpeta(_carpeta);
        return Task.CompletedTask;
    }
}

/// <summary>
/// La PC que entrega la base, en una carpeta temporal: sus datos (<c>pos.db</c>, <c>respaldos\</c>), sus «Archivos de programa» y la USB
/// con la carpeta del paquete. Lo que es de la PC (su nombre, sus IP, la hora, si su servicio contesta) lo pone la prueba: sin red.
/// </summary>
internal sealed class PcQueSeMuda : IDisposable
{
    public const string Equipo = "CAJA-2";

    /// <summary>De documentación (TEST-NET): nunca se les habla.</summary>
    public static readonly string[] Ips = ["192.0.2.72", "198.51.100.7"];

    public PcQueSeMuda(string? semilla)
    {
        Carpeta = Path.Combine(Path.GetTempPath(), $"mudanza-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Datos);
        if (semilla is not null)
            File.Copy(semilla, Ruta);
    }

    public string Carpeta { get; }
    public string Datos => Path.Combine(Carpeta, "ProgramData", "PapeleriaPos");
    public string Ruta => Path.Combine(Datos, "pos.db");
    public string Marca => Ruta + ".mudada";
    public string Respaldos => Path.Combine(Datos, "respaldos");
    public string ArchivosDePrograma => Path.Combine(Carpeta, "Program Files");
    public string Usb => Path.Combine(Carpeta, "usb", "mudanza");
    public string Resumen => Path.Combine(Usb, "mudanza.txt");
    public string Cadena => $"Data Source={Ruta};Pooling=False";

    /// <summary>Sábado 14 de noviembre de 2026, 08:15:07 en la tienda (14:15:07 UTC).</summary>
    public RelojDePrueba Reloj { get; } = new(new DateTime(2026, 11, 14, 14, 15, 7, 480, DateTimeKind.Utc));

    public const string ArchivoDeLasOchoYCuarto = "pos-2026-11-14-mudanza-0815.db";

    /// <summary>Lo que contesta «¿el servicio de esta PC sigue atendiendo?», y en qué puerto se preguntó.</summary>
    public bool ElServicioContesta { get; set; }

    public List<int> PuertosPreguntados { get; } = [];

    public PosDbContext Base() => new(BaseDeDatos.Opciones(Cadena));

    /// <param name="copiar">La copia a la USB es la de verdad, salvo que la prueba la haga fallar.</param>
    /// <param name="alLlegarA">Lo que pasa al llegar a cada momento del final (se llevan la USB); sin él, nada.</param>
    /// <param name="dejarEnDisco">Lo que deja en el disco el nombre de la marca: el de verdad, salvo que la prueba lo vigile o lo haga fallar.</param>
    public MudanzaDeLaBase Mudanza(OpcionesEleventa? eleventa = null, IEspacioEnDisco? disco = null,
        Func<Stream, Stream, CancellationToken, Task>? copiar = null, int puerto = 5080, string? controlPrint = null, string? respaldos = null,
        Action<MudanzaDeLaBase.Momento>? alLlegarA = null, Action<string>? dejarEnDisco = null) =>
        new(Cadena, respaldos ?? Respaldos, eleventa ?? new OpcionesEleventa())
        {
            Equipo = Equipo,
            Ips = () => Ips,
            Reloj = Reloj,
            Puerto = puerto,
            ControlPrint = controlPrint,
            Disco = disco ?? new EspacioEnDisco(),
            CarpetasDelPrograma = [ArchivosDePrograma],
            ElServicioContesta = SegunLaPrueba,
            Copiar = copiar,
            AlLlegarA = alLlegarA,
            DejarEnDisco = dejarEnDisco ?? MudanzaDeLaBase.DejarSuNombreEnDisco,
        };

    /// <summary>
    /// Se llevan la USB, o la traen de vuelta: la carpeta del paquete cambia de nombre. Con unos intentos: en Windows el antivirus puede
    /// tener abierto un momento lo que se acaba de escribir en ella, y así la carpeta no se deja mover.
    /// </summary>
    public static void MoverLaUsb(string de, string a)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                Directory.Move(de, a);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && intento < 30 && Directory.Exists(de))
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Se llevan la USB (a <paramref name="aDonde"/>) al llegar a ese momento del final.</summary>
    public Action<MudanzaDeLaBase.Momento> SeLlevanLaUsb(MudanzaDeLaBase.Momento cuando, string aDonde) => momento =>
    {
        if (momento == cuando)
            MoverLaUsb(Usb, aDonde);
    };

    public Task<bool> SegunLaPrueba(int puerto, CancellationToken ct)
    {
        PuertosPreguntados.Add(puerto);
        return Task.FromResult(ElServicioContesta);
    }

    /// <summary>Lo que hay en la carpeta del paquete (solo los nombres, ordenados); vacío si ni existe.</summary>
    public List<string> EnLaUsb() =>
        Directory.Exists(Usb) ? Directory.EnumerateFileSystemEntries(Usb).Select(e => Path.GetFileName(e)!).Order(StringComparer.Ordinal).ToList() : [];

    /// <summary>Los respaldos que hay en esta PC (solo los nombres).</summary>
    public List<string> RespaldosHechos() =>
        Directory.Exists(Respaldos) ? Directory.EnumerateFileSystemEntries(Respaldos).Select(e => Path.GetFileName(e)!).Order(StringComparer.Ordinal).ToList() : [];

    public void Dispose() => ArchivosDePrueba.BorrarCarpeta(Carpeta);
}

/// <summary>
/// El lector mínimo de «clave=valor» de las pruebas, escrito aparte del programa y solo con lo que dice el contrato de
/// <c>mudanza.txt</c>: UTF-8 (con o sin BOM), un renglón por clave, «#» es comentario. Con él se comprueba lo que va a leer el ayudante
/// del instalador en la otra PC: las claves, sus valores y el orden en que se escriben.
/// </summary>
internal sealed class ClavesEscritas
{
    private ClavesEscritas(List<(string Clave, string Valor)> enOrden) => EnOrden = enOrden;

    public IReadOnlyList<(string Clave, string Valor)> EnOrden { get; }

    public IEnumerable<string> Nombres => EnOrden.Select(c => c.Clave);

    /// <summary>El valor de esa clave, que tiene que estar una sola vez.</summary>
    public string this[string clave] => Assert.Single(EnOrden, c => c.Clave == clave).Valor;

    public static ClavesEscritas De(string ruta)
    {
        // Estricto: lo que no sea UTF-8 truena aquí, y no se pierde en un «?».
        var texto = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(File.ReadAllBytes(ruta));
        if (texto.StartsWith('\uFEFF'))
            texto = texto[1..];
        var claves = new List<(string, string)>();
        foreach (var renglon in texto.Split('\n').Select(r => r.TrimEnd('\r')))
        {
            if (renglon.Length == 0 || renglon.StartsWith('#'))
                continue;
            var igual = renglon.IndexOf('=');
            Assert.True(igual > 0, $"Un renglón que no es «clave=valor» ni comentario: «{renglon}»");
            claves.Add((renglon[..igual], renglon[(igual + 1)..]));
        }
        return new ClavesEscritas(claves);
    }
}

/// <summary>
/// <c>PapeleriaPos.Servicio.exe mudanza-sacar</c> (la mudanza de la base a otra PC, 2026-11-11): la PC que tiene la base deja en una
/// carpeta (la USB) el «paquete de mudanza» —un respaldo revisado de la base y su resumen, <c>mudanza.txt</c>— y junto a su base la marca
/// con la que su servicio ya no arranca. Aquí, contra una base de verdad (la que dejó el servicio vendiendo) y carpetas temporales: que
/// el paquete diga lo que tiene la base y cómo estaba el puente con eleventa, que nunca lleve la clave de eleventa, y que si algo falla no
/// queden ni la marca ni restos del paquete.
/// </summary>
public sealed class MudanzaDeLaBaseTests(BaseParaMudar semilla) : IClassFixture<BaseParaMudar>
{
    private const string BaseDeEleventa = @"C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB";

    /// <summary>Las claves de <c>mudanza.txt</c> que van siempre, en el orden del contrato (sección 3.1), antes y después de las cajas.</summary>
    private static readonly string[] AntesDeLasCajas =
    [
        "formato", "paquete", "hecho-utc", "hecho-local", "equipo", "ips", "puerto", "version", "archivo", "bytes", "sha256", "tienda", "productos",
        "ventas", "ultima-venta", "cajas",
    ];

    private static readonly string[] DeCadaCaja = ["id", "nombre", "serie", "equipo", "con-llave", "con-turnos", "turno-abierto", "eleventa-caja"];

    private static readonly string[] DespuesDeLasCajas =
    [
        "eleventa", "eleventa-escribir", "eleventa-servidor", "eleventa-puerto", "eleventa-base", "eleventa-base-de-la-bandeja", "eleventa-usuario",
        "eleventa-caja", "eleventa-cajero", "eleventa-cajas", "eleventa-ajustes", "eleventa-pendientes", "eleventa-por-revisar", "eleventa-detenido",
        "controlprint", "controlprint-servidor", "avisos",
    ];

    private PcQueSeMuda Pc() => new(semilla.Archivo);

    /// <summary>El puente como lo tiene hoy la caja 2: lee y escribe en eleventa como la caja 3, a nombre del cajero 7.</summary>
    private static OpcionesEleventa Escribiendo(Action<OpcionesEleventa>? ademas = null)
    {
        var opciones = new OpcionesEleventa { Activo = true, Escribir = true, Servidor = "192.0.2.106", Base = BaseDeEleventa, Caja = 3, Cajero = 7 };
        ademas?.Invoke(opciones);
        return opciones;
    }

    private static string Sha256De(string archivo) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivo))).ToLowerInvariant();

    /// <summary>Como se ve un texto en la pantalla del verbo (la lee Windows PowerShell 5.1): las mismas letras, sin sus acentos ni la tilde de la ñ.</summary>
    private static string SinAcentos(string texto) =>
        string.Concat(texto.Normalize(NormalizationForm.FormD).Where(letra => CharUnicodeInfo.GetUnicodeCategory(letra) != UnicodeCategory.NonSpacingMark))
            .Normalize(NormalizationForm.FormC);

    /// <summary>Una consulta directa al archivo, sin pasar por el modelo del programa.</summary>
    private static async Task<object?> EscalarAsync(string ruta, string sql)
    {
        await using var conexion = new SqliteConnection($"Data Source={ruta};Pooling=False");
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        return await comando.ExecuteScalarAsync();
    }

    private static async Task<Caja> CajaUnoAsync(PosDbContext db) => await db.Cajas.SingleAsync(c => c.Serie == "A");

    /// <summary>Otra caja en la misma sucursal; con <paramref name="equipo"/>, dada de alta desde esa PC.</summary>
    private static async Task<Caja> OtraCajaAsync(PosDbContext db, string nombre, string serie, string? equipo = null)
    {
        var caja = new Caja { SucursalId = (await CajaUnoAsync(db)).SucursalId, Nombre = nombre, Serie = serie };
        if (equipo is not null)
            caja.DarDeAlta(equipo, DateTime.UtcNow);
        db.Cajas.Add(caja);
        return caja;
    }

    /// <summary>Un turno de esa caja, de ayer; cerrado, salvo que se diga.</summary>
    private static async Task TurnoAsync(PosDbContext db, Caja caja, bool abierto = false)
    {
        var cajero = await db.Usuarios.Where(u => u.NombreUsuario == "cajero").Select(u => u.Id).SingleAsync();
        var ayer = DateTime.UtcNow.AddDays(-1);
        db.Turnos.Add(new Turno { SucursalId = caja.SucursalId, CajaId = caja.Id, CajeroId = cajero, AbiertoUtc = ayer, CerradoUtc = abierto ? null : ayer.AddHours(8) });
    }

    private static void Tiene(string esperado, ClavesEscritas claves, string clave) => Assert.Equal(esperado, claves[clave]);

    /// <summary>Sin marca, sin nada del paquete en la carpeta y sin archivos a medias junto a la base: la PC sigue como estaba.</summary>
    private static void SinMarcaNiRestos(PcQueSeMuda pc, params string[] loQueYaEstabaEnLaUsb)
    {
        Assert.False(Path.Exists(pc.Marca), "Quedó la marca de la mudanza.");
        Assert.Equal(loQueYaEstabaEnLaUsb.Order(StringComparer.Ordinal), pc.EnLaUsb());
        Assert.DoesNotContain(Directory.EnumerateFiles(pc.Datos), a => a.EndsWith(".tmp", StringComparison.Ordinal));
        // Y el servicio de esta PC arranca como siempre: la guarda no tiene qué decir.
        MudanzaDeLaBase.NoArrancarConLaMarca(pc.Cadena, new RegistroEnMemoria().CreateLogger("prueba"));
    }

    // ── El paquete completo ──────────────────────────────────────────────────

    /// <summary>
    /// Como el sábado en la caja 2: su caja dada de alta en esa misma PC, con el turno abierto, dos ventas que todavía no llegan a
    /// eleventa y un celular dado de alta. El paquete lleva un respaldo que es la base (mismo SHA-256 en el respaldo de esta PC, en la
    /// USB, en mudanza.txt y en la marca) y el resumen dice lo que hay, clave por clave y en el orden del contrato.
    /// </summary>
    [Fact]
    public async Task Saca_el_paquete_completo_con_lo_que_dice_la_base_y_deja_la_marca()
    {
        using var pc = Pc();
        Guid cajaUno, celular;
        await using (var db = pc.Base())
        {
            var caja = await CajaUnoAsync(db);
            caja.DarDeAlta(PcQueSeMuda.Equipo, DateTime.UtcNow);
            cajaUno = caja.Id;
            celular = (await OtraCajaAsync(db, "Celular", "C", "Redmi de Mario")).Id;
            // La bandeja del puente: las dos ventas, sin mandar; y una de antes que ya llegó (esa no cuenta).
            db.EnviosAEleventa.AddRange(
                new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = semilla.Ventas[0], CreadoUtc = DateTime.UtcNow },
                new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = semilla.Ventas[1], CreadoUtc = DateTime.UtcNow, Intentos = 2 },
                new EnvioAEleventa { Tipo = TipoEnvioEleventa.CierreDeTurno, CreadoUtc = DateTime.UtcNow.AddDays(-1), Estado = EstadoEnvioEleventa.Enviada });
            db.Set<BaseDeLaBandeja>().Add(new BaseDeLaBandeja { Ruta = BaseDeEleventa.ToUpperInvariant() });
            await db.SaveChangesAsync();
        }
        var productos = Convert.ToInt32(await EscalarAsync(pc.Ruta, "SELECT COUNT(*) FROM Productos WHERE Activo = 1"));
        var ultimaVenta = Convert.ToString(await EscalarAsync(pc.Ruta, "SELECT MAX(PagadaUtc) FROM Ventas"), CultureInfo.InvariantCulture)!;
        var baseAntes = Sha256De(pc.Ruta);

        var resultado = await pc.Mudanza(Escribiendo(), puerto: 5090, controlPrint: "192.0.2.106").SacarAsync(pc.Usb);

        Assert.Null(resultado.Error);
        Assert.True(resultado.Bien);
        // En la USB, la base y su resumen, y nada más (ni temporales, ni el -wal de haberla abierto).
        Assert.Equal(["mudanza.txt", PcQueSeMuda.ArchivoDeLasOchoYCuarto], pc.EnLaUsb());
        // En esta PC, el respaldo de siempre (un respaldo más) y la marca; la base, sin tocar.
        Assert.Equal([PcQueSeMuda.ArchivoDeLasOchoYCuarto], pc.RespaldosHechos());
        Assert.Equal(TipoRespaldo.Otro, Respaldos.TipoDe(PcQueSeMuda.ArchivoDeLasOchoYCuarto));
        Assert.True(File.Exists(pc.Marca));
        Assert.Equal(baseAntes, Sha256De(pc.Ruta));
        Assert.Equal(["pos.db", "pos.db.mudada", "respaldos"], Directory.EnumerateFileSystemEntries(pc.Datos).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        var enLaUsb = Path.Combine(pc.Usb, PcQueSeMuda.ArchivoDeLasOchoYCuarto);
        var sha256 = Sha256De(enLaUsb);
        Assert.Equal(sha256, Sha256De(Path.Combine(pc.Respaldos, PcQueSeMuda.ArchivoDeLasOchoYCuarto)));
        // Lo que viaja es la base: sana, con sus ventas y con la llave de la caja (su huella), que es lo que la PC nueva tiene que conocer.
        Assert.Equal("ok", await EscalarAsync(enLaUsb, "PRAGMA integrity_check"));
        Assert.Equal(2L, await EscalarAsync(enLaUsb, "SELECT COUNT(*) FROM Ventas WHERE PagadaUtc IS NOT NULL"));
        const string huellaDeLaCaja = "SELECT LlaveHuella FROM Cajas WHERE Serie = 'A'";
        Assert.NotNull(await EscalarAsync(enLaUsb, huellaDeLaCaja));
        Assert.Equal(await EscalarAsync(pc.Ruta, huellaDeLaCaja), await EscalarAsync(enLaUsb, huellaDeLaCaja));

        var claves = ClavesEscritas.De(pc.Resumen);
        // El orden de escritura, al pie de la letra: lo de siempre, las cajas (por nombre), el puente y los avisos.
        Assert.Equal(
            [
                .. AntesDeLasCajas, .. DeCadaCaja.Select(c => $"caja.1.{c}"), .. DeCadaCaja.Select(c => $"caja.2.{c}"), .. DespuesDeLasCajas, "aviso.1",
                "aviso.2", "aviso.3",
            ],
            claves.Nombres);
        Tiene("1", claves, "formato");
        Assert.Matches("^[0-9a-f]{32}$", claves["paquete"]);
        Tiene("2026-11-14T14:15:07Z", claves, "hecho-utc");
        Tiene("2026-11-14 08:15", claves, "hecho-local");
        Tiene("CAJA-2", claves, "equipo");
        Tiene("192.0.2.72;198.51.100.7", claves, "ips");
        Tiene("5090", claves, "puerto");
        // La del programa, sin lo de después del «+» (la que dice /api/estado).
        Tiene(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0], claves, "version");
        Tiene(PcQueSeMuda.ArchivoDeLasOchoYCuarto, claves, "archivo");
        Tiene(new FileInfo(enLaUsb).Length.ToString(CultureInfo.InvariantCulture), claves, "bytes");
        Tiene(sha256, claves, "sha256");
        // La tienda, como la dice /api/estado.
        Tiene(string.Join("; ", semilla.SucursalesDelEstado), claves, "tienda");
        Tiene("Matriz", claves, "tienda");
        Tiene(productos.ToString(CultureInfo.InvariantCulture), claves, "productos");
        Assert.True(productos > 0);
        Tiene("2", claves, "ventas");
        Tiene(HoraMexico.ALocal(DateTime.Parse(ultimaVenta, CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), claves, "ultima-venta");
        Tiene("2", claves, "cajas");
        // La caja de esta PC: dada de alta aquí, con su turno abierto, y escribe en eleventa como la caja 3 (la de omisión: no hay «Cajas»).
        Tiene(cajaUno.ToString(), claves, "caja.1.id");
        Tiene("Caja 1", claves, "caja.1.nombre");
        Tiene("A", claves, "caja.1.serie");
        Tiene("CAJA-2", claves, "caja.1.equipo");
        Tiene("si", claves, "caja.1.con-llave");
        Tiene("si", claves, "caja.1.con-turnos");
        Tiene("si", claves, "caja.1.turno-abierto");
        Tiene("3", claves, "caja.1.eleventa-caja");
        // El celular: dado de alta, nunca ha abierto un turno. Su número efectivo sería el de omisión, pero no entra a eleventa-cajas.
        Tiene(celular.ToString(), claves, "caja.2.id");
        Tiene("Celular", claves, "caja.2.nombre");
        Tiene("C", claves, "caja.2.serie");
        Tiene("Redmi de Mario", claves, "caja.2.equipo");
        Tiene("si", claves, "caja.2.con-llave");
        Tiene("no", claves, "caja.2.con-turnos");
        Tiene("no", claves, "caja.2.turno-abierto");
        Tiene("3", claves, "caja.2.eleventa-caja");
        Tiene("si", claves, "eleventa");
        Tiene("si", claves, "eleventa-escribir");
        Tiene("192.0.2.106", claves, "eleventa-servidor");
        Tiene("3050", claves, "eleventa-puerto");
        Tiene(BaseDeEleventa, claves, "eleventa-base");
        Tiene(BaseDeEleventa.ToUpperInvariant(), claves, "eleventa-base-de-la-bandeja");
        Tiene("SYSDBA", claves, "eleventa-usuario");
        Tiene("3", claves, "eleventa-caja");
        Tiene("7", claves, "eleventa-cajero");
        Tiene("A=3", claves, "eleventa-cajas");
        Tiene("", claves, "eleventa-ajustes");
        Tiene("2", claves, "eleventa-pendientes");
        Tiene("0", claves, "eleventa-por-revisar");
        Tiene("no", claves, "eleventa-detenido");
        Tiene("si", claves, "controlprint");
        Tiene("192.0.2.106", claves, "controlprint-servidor");
        Tiene("3", claves, "avisos");
        Tiene("Hay 2 ventas que todavía no llegan a eleventa: viajan con la base y las manda la PC nueva, sin repetirse.", claves, "aviso.1");
        Tiene("La caja «Caja 1» (serie A) tiene un turno abierto: se queda abierto; no hagas el corte en eleventa hasta terminar la mudanza.", claves, "aviso.2");
        Tiene("La caja «Celular» (serie C) está dada de alta desde otro equipo («Redmi de Mario»): después de la mudanza se da de alta otra vez.", claves, "aviso.3");

        // La marca: JSON con sus siete campos en camelCase y en su orden; la tienda, el mismo texto de mudanza.txt.
        using var marca = JsonDocument.Parse(await File.ReadAllTextAsync(pc.Marca));
        Assert.Equal(["paquete", "tienda", "cuandoUtc", "carpeta", "archivo", "sha256", "equipo"], marca.RootElement.EnumerateObject().Select(p => p.Name));
        string Campo(string nombre) => marca.RootElement.GetProperty(nombre).GetString()!;
        Assert.Equal(claves["paquete"], Campo("paquete"));
        Assert.Equal(claves["tienda"], Campo("tienda"));
        Assert.Equal(new DateTime(2026, 11, 14, 14, 15, 7, DateTimeKind.Utc), marca.RootElement.GetProperty("cuandoUtc").GetDateTime().ToUniversalTime());
        Assert.Equal("2026-11-14T14:15:07Z", Campo("cuandoUtc"));
        Assert.Equal(pc.Usb, Campo("carpeta"));
        Assert.Equal(PcQueSeMuda.ArchivoDeLasOchoYCuarto, Campo("archivo"));
        Assert.Equal(sha256, Campo("sha256"));
        Assert.Equal("CAJA-2", Campo("equipo"));
        // Y como la lee el programa.
        Assert.Equal(new MarcaDeMudanza(claves["paquete"], "Matriz", new DateTime(2026, 11, 14, 14, 15, 7, DateTimeKind.Utc), pc.Usb,
            PcQueSeMuda.ArchivoDeLasOchoYCuarto, sha256, "CAJA-2"), MudanzaDeLaBase.LeerMarca(pc.Marca));

        // Lo que regresa, para --salida.
        Assert.Equal((claves["paquete"], pc.Usb, PcQueSeMuda.ArchivoDeLasOchoYCuarto, new FileInfo(enLaUsb).Length, sha256, "Matriz", pc.Marca),
            (resultado.Paquete, resultado.Carpeta, resultado.Archivo, resultado.Bytes, resultado.Sha256, resultado.Tienda, resultado.Marca));
        Assert.Equal(new DateTime(2026, 11, 14, 8, 15, 7), resultado.HechoLocal);
        Assert.Equal([claves["aviso.1"], claves["aviso.2"], claves["aviso.3"]], resultado.Avisos);
        // Se preguntó por el servicio de esta PC en su puerto, una vez.
        Assert.Equal([5090], pc.PuertosPreguntados);
    }

    /// <summary>El resumen se escribe como lo demás del instalador: UTF-8 con BOM, un comentario al principio y renglones de Windows.</summary>
    [Fact]
    public async Task El_resumen_va_en_utf8_con_su_comentario_y_un_renglon_por_clave()
    {
        using var pc = Pc();
        await using (var db = pc.Base())
        {
            (await CajaUnoAsync(db)).Nombre = "Caja de la señora Ñáñez";
            await db.SaveChangesAsync();
        }

        Assert.Null((await pc.Mudanza().SacarAsync(pc.Usb)).Error);

        var bytes = await File.ReadAllBytesAsync(pc.Resumen);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));
        var texto = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("# ", texto);
        Assert.EndsWith("\r\n", texto);
        Assert.DoesNotContain("\n", texto.Replace("\r\n", ""));
        var renglones = texto.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(renglones, r => r.StartsWith('#'));
        Assert.All(renglones.Skip(1), r => Assert.Matches("^[a-z0-9.-]+=", r));
        Assert.Contains("caja.1.nombre=Caja de la señora Ñáñez", renglones);
        // Sin puente ni Control Print: todas las claves siguen ahí, vacías o en «no».
        var claves = ClavesEscritas.De(pc.Resumen);
        Assert.Equal([.. AntesDeLasCajas, .. DeCadaCaja.Select(c => $"caja.1.{c}"), .. DespuesDeLasCajas, "aviso.1"], claves.Nombres);
        Assert.Equal(("no", "no", "", "3050", "", "", "SYSDBA", "", "", "", "", "0", "0", "no", "no", ""),
            (claves["eleventa"], claves["eleventa-escribir"], claves["eleventa-servidor"], claves["eleventa-puerto"], claves["eleventa-base"],
                claves["eleventa-base-de-la-bandeja"], claves["eleventa-usuario"], claves["eleventa-caja"], claves["eleventa-cajero"], claves["eleventa-cajas"],
                claves["eleventa-ajustes"], claves["eleventa-pendientes"], claves["eleventa-por-revisar"], claves["eleventa-detenido"], claves["controlprint"],
                claves["controlprint-servidor"]));
        // La caja de la semilla no está dada de alta en ningún equipo.
        Assert.Equal(("", "no", "si", "si", ""), (claves["caja.1.equipo"], claves["caja.1.con-llave"], claves["caja.1.con-turnos"], claves["caja.1.turno-abierto"],
            claves["caja.1.eleventa-caja"]));
        // Sin el puente escribiendo, el turno abierto no habla del corte de eleventa; y no hay más avisos.
        Tiene("1", claves, "avisos");
        Tiene("La caja «Caja de la señora Ñáñez» (serie A) tiene un turno abierto: se queda abierto y se sigue en él al terminar la mudanza.", claves, "aviso.1");
    }

    /// <summary>
    /// «La misma tienda» se reconoce por los nombres de las sucursales en el orden de <c>/api/estado</c>: lo que va en mudanza.txt y en la
    /// marca es lo que contesta el servicio con esa base, unido con «; ».
    /// </summary>
    [Fact]
    public async Task La_tienda_son_las_sucursales_en_el_orden_en_que_las_dice_el_estado_del_servicio()
    {
        await using var p = await PrincipalQueSeRestaura.CrearAsync();
        await using (var db = p.Servicio.Base())
        {
            db.Sucursales.AddRange(new Sucursal { Clave = "S2", Nombre = "anexo de la esquina" }, new Sucursal { Clave = "S3", Nombre = "Ánimas" },
                new Sucursal { Clave = "S4", Nombre = "Centro" });
            await db.SaveChangesAsync();
        }
        var estado = (await p.Arrancar().GetFromJsonAsync<EstadoServicioDto>("api/estado"))!;
        await p.DetenerAsync();
        using var pc = new PcQueSeMuda(null);
        File.Move(Respaldos.Hacer($"Data Source={p.Ruta};Pooling=False", pc.Datos, new DateTime(2026, 11, 13), "copia"), pc.Ruta);

        Assert.Null((await pc.Mudanza().SacarAsync(pc.Usb)).Error);

        Assert.Equal(4, estado.Sucursales.Count);
        Assert.Equal(string.Join("; ", estado.Sucursales), ClavesEscritas.De(pc.Resumen)["tienda"]);
        Assert.Equal(string.Join("; ", estado.Sucursales), MudanzaDeLaBase.LeerMarca(pc.Marca)!.Tienda);
        // La marca la leen también los scripts (Windows PowerShell 5.1): sus letras con acento van escritas con su número, y así se lee
        // igual con cualquier codificación.
        Assert.Contains(estado.Sucursales, s => s.Any(letra => letra > 127));
        Assert.DoesNotContain(await File.ReadAllBytesAsync(pc.Marca), b => b > 127);
    }

    /// <summary>
    /// Si el servicio se detuvo de golpe, lo último que se cobró puede seguir en el -wal y no en pos.db: el respaldo de la mudanza se
    /// hace con la copia de SQLite, que lo toma también. Nada se queda atrás.
    /// </summary>
    [Fact]
    public async Task Lo_ultimo_que_se_escribio_y_sigue_en_el_wal_tambien_viaja()
    {
        using var pc = Pc();
        await RestaurarRespaldoTests.MarcarAsync(pc.Ruta, "lo de ayer");
        await RestaurarRespaldoTests.MarcarSoloEnElWalAsync(pc.Ruta, "la última venta, todavía en el -wal");
        Assert.True(new FileInfo(pc.Ruta + "-wal").Length > 0);

        var resultado = await pc.Mudanza().SacarAsync(pc.Usb);

        Assert.Null(resultado.Error);
        Assert.Equal(["lo de ayer", "la última venta, todavía en el -wal"], await RestaurarRespaldoTests.MarcasAsync(Path.Combine(pc.Usb, resultado.Archivo)));
        // Abrirla para leer sus marcas dejó su -wal junto a ella un momento; ya cerrada, la del paquete sigue siendo un solo archivo.
        Assert.Equal(["mudanza.txt", resultado.Archivo], pc.EnLaUsb());
    }

    /// <summary>
    /// Dos índices con el contenido cambiado (como después de un apagón a media escritura): la base abre y hasta contesta, pero la
    /// revisión completa no pasa. Como en <c>RestaurarRespaldoAMediasTests</c>.
    /// </summary>
    private static async Task IndicesQueNoCuadranAsync(string ruta)
    {
        await using var conexion = new SqliteConnection($"Data Source={ruta};Pooling=False");
        await conexion.OpenAsync();
        async Task<object?> EjecutarAsync(string sql)
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = sql;
            return await comando.ExecuteScalarAsync();
        }
        await EjecutarAsync("CREATE TABLE PruebaA (x INTEGER); CREATE INDEX IX_PruebaA ON PruebaA (x); CREATE TABLE PruebaB (x INTEGER);"
            + " CREATE INDEX IX_PruebaB ON PruebaB (x); INSERT INTO PruebaA VALUES (1), (2), (3); INSERT INTO PruebaB VALUES (10), (20), (30);");
        var a = Convert.ToInt64(await EjecutarAsync("SELECT rootpage FROM sqlite_master WHERE name = 'IX_PruebaA'"));
        var b = Convert.ToInt64(await EjecutarAsync("SELECT rootpage FROM sqlite_master WHERE name = 'IX_PruebaB'"));
        await EjecutarAsync($"PRAGMA writable_schema = ON; UPDATE sqlite_master SET rootpage = {b} WHERE name = 'IX_PruebaA';"
            + $" UPDATE sqlite_master SET rootpage = {a} WHERE name = 'IX_PruebaB'; PRAGMA writable_schema = OFF;");
    }

    /// <summary>
    /// Una base dañada no se muda: el respaldo de la mudanza pasa la revisión completa, como cualquier respaldo, y si no la pasa no hay
    /// paquete ni marca (y ese respaldo tampoco se queda como uno bueno).
    /// </summary>
    [Fact]
    public async Task Una_base_que_no_pasa_la_revision_no_se_muda()
    {
        using var pc = Pc();
        await IndicesQueNoCuadranAsync(pc.Ruta);
        Assert.True(BaseDeDatos.EstaSana(BaseDeDatos.RevisionRapida(pc.Cadena)));

        var resultado = await pc.Mudanza().SacarAsync(pc.Usb);

        Assert.StartsWith("El respaldo de la base no pasó la revisión: la base de esta PC puede estar dañada y así no se muda. La copia no pasó la revisión: ",
            resultado.Error);
        Assert.Empty(pc.RespaldosHechos());
        SinMarcaNiRestos(pc);
    }

    // ── Eleventa:Cajas para la PC nueva ──────────────────────────────────────

    /// <summary>
    /// Con «Cajas» allá (el modo multicaja): cada caja con turnos lleva su número de ahí, sin distinguir mayúsculas en la serie; su cajero
    /// solo si su entrada trae el suyo. La que no está en «Cajas» lleva el de omisión.
    /// </summary>
    [Fact]
    public async Task Con_cajas_en_la_configuracion_cada_caja_con_turnos_lleva_su_numero_y_su_cajero_solo_si_tiene_el_suyo()
    {
        using var pc = Pc();
        await using (var db = pc.Base())
        {
            await TurnoAsync(db, await OtraCajaAsync(db, "Caja 2", "B", PcQueSeMuda.Equipo));
            await TurnoAsync(db, await OtraCajaAsync(db, "Caja 3", "D"));
            await db.SaveChangesAsync();
        }
        var eleventa = Escribiendo(o =>
        {
            o.Cajas["a"] = new CajaDelPuente { Caja = 5 };
            o.Cajas["B"] = new CajaDelPuente { Caja = 6, Cajero = 9 };
        });

        Assert.Null((await pc.Mudanza(eleventa).SacarAsync(pc.Usb)).Error);

        var claves = ClavesEscritas.De(pc.Resumen);
        Tiene("A=5;B=6/9;D=3", claves, "eleventa-cajas");
        Assert.Equal(("5", "6", "3"), (claves["caja.1.eleventa-caja"], claves["caja.2.eleventa-caja"], claves["caja.3.eleventa-caja"]));
        // La de omisión de allá y su cajero van aparte: la PC nueva no hereda esa caja.
        Assert.Equal(("3", "7"), (claves["eleventa-caja"], claves["eleventa-cajero"]));
        // Solo el turno abierto de la Caja 1: ni números repetidos ni cajas sin número.
        Tiene("1", claves, "avisos");
    }

    /// <summary>
    /// Una caja con turnos sin número efectivo (no hay caja de omisión, su entrada de «Cajas» está a medias, o no hay cajero) no entra a
    /// eleventa-cajas —en la PC nueva lo suyo espera en la bandeja, nunca escribe como otra— y se avisa.
    /// </summary>
    [Fact]
    public async Task Una_caja_con_turnos_sin_su_caja_de_eleventa_no_entra_y_se_avisa()
    {
        using var pc = Pc();
        await using (var db = pc.Base())
        {
            await TurnoAsync(db, await OtraCajaAsync(db, "Caja 2", "B"));
            await TurnoAsync(db, await OtraCajaAsync(db, "Caja 3", "D"));
            // Sin turnos: aunque tampoco tenga número, no se dice nada de ella.
            await OtraCajaAsync(db, "Celular", "C", "Redmi");
            await db.SaveChangesAsync();
        }
        // Sin caja de omisión: la A tiene la suya; la B está en «Cajas» a medias (sin su número) y la D ni está.
        var eleventa = Escribiendo(o =>
        {
            o.Caja = null;
            o.Cajas["A"] = new CajaDelPuente { Caja = 3 };
            o.Cajas["B"] = new CajaDelPuente { Caja = 0, Cajero = 9 };
        });

        Assert.Null((await pc.Mudanza(eleventa).SacarAsync(pc.Usb)).Error);

        var claves = ClavesEscritas.De(pc.Resumen);
        Tiene("A=3", claves, "eleventa-cajas");
        Assert.Equal(("3", "", "", ""), (claves["caja.1.eleventa-caja"], claves["caja.2.eleventa-caja"], claves["caja.3.eleventa-caja"], claves["caja.4.eleventa-caja"]));
        Tiene("", claves, "eleventa-caja");
        Assert.Equal(
            [
                "La caja «Caja 1» (serie A) tiene un turno abierto: se queda abierto; no hagas el corte en eleventa hasta terminar la mudanza.",
                "La caja «Celular» (serie C) está dada de alta desde otro equipo («Redmi»): después de la mudanza se da de alta otra vez.",
                "La caja «Caja 2» (serie B) no tiene caja de eleventa: lo suyo espera en la bandeja.",
                "La caja «Caja 3» (serie D) no tiene caja de eleventa: lo suyo espera en la bandeja.",
            ],
            Enumerable.Range(1, int.Parse(claves["avisos"], CultureInfo.InvariantCulture)).Select(n => claves[$"aviso.{n}"]));
    }

    /// <summary>Con número pero sin cajero no se puede escribir en eleventa: tampoco es un número efectivo (<c>OpcionesEleventa.DeLaSerie</c>).</summary>
    [Fact]
    public async Task Sin_cajero_la_caja_no_tiene_numero_efectivo()
    {
        using var pc = Pc();

        Assert.Null((await pc.Mudanza(Escribiendo(o => o.Cajero = null)).SacarAsync(pc.Usb)).Error);

        var claves = ClavesEscritas.De(pc.Resumen);
        Assert.Equal(("", "", "3", ""), (claves["eleventa-cajas"], claves["caja.1.eleventa-caja"], claves["eleventa-caja"], claves["eleventa-cajero"]));
        Tiene("2", claves, "avisos");
        Tiene("La caja «Caja 1» (serie A) no tiene caja de eleventa: lo suyo espera en la bandeja.", claves, "aviso.2");
    }

    /// <summary>Dos cajas que allá escriben como la misma caja de eleventa entran las dos (así estaban) y se avisa.</summary>
    [Fact]
    public async Task Dos_cajas_con_el_mismo_numero_entran_las_dos_y_se_avisa()
    {
        using var pc = Pc();
        await using (var db = pc.Base())
        {
            await TurnoAsync(db, await OtraCajaAsync(db, "Caja 2", "B"));
            await TurnoAsync(db, await OtraCajaAsync(db, "Caja 3", "D"));
            await db.SaveChangesAsync();
        }

        Assert.Null((await pc.Mudanza(Escribiendo(o => o.Cajas["D"] = new CajaDelPuente { Caja = 4 })).SacarAsync(pc.Usb)).Error);

        var claves = ClavesEscritas.De(pc.Resumen);
        Tiene("A=3;B=3;D=4", claves, "eleventa-cajas");
        Tiene("2", claves, "avisos");
        Tiene("Las cajas «Caja 1» (serie A) y «Caja 2» (serie B) tienen la misma caja de eleventa (3): así estaban en esta PC y así llegan a la PC nueva; "
            + "cada una debería llevar la suya.", claves, "aviso.2");
    }

    /// <summary>
    /// Si el puente solo leía (no escribe), las cajas conservan su número para cuando se prenda y no se promete mandar lo pendiente. De
    /// la caja con turnos que no tiene su número también se avisa, con otras palabras (nada suyo espera en la bandeja): no viaja en
    /// eleventa-cajas, y si el puente se prende en la PC nueva con la caja de allá, esa caja escribiría como ella.
    /// </summary>
    [Fact]
    public async Task Con_el_puente_sin_escribir_no_se_promete_mandar_lo_pendiente_y_de_la_caja_sin_numero_se_avisa_que_se_le_ponga_antes_de_prenderlo()
    {
        using var pc = Pc();
        await using (var db = pc.Base())
        {
            await TurnoAsync(db, await OtraCajaAsync(db, "Caja 2", "B"));
            // Sin turnos: aunque tampoco tenga número, no se dice nada de ella.
            await OtraCajaAsync(db, "Celular", "C");
            db.EnviosAEleventa.Add(new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = semilla.Ventas[0], CreadoUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var soloLee = new OpcionesEleventa { Activo = true, Servidor = "192.0.2.106", Base = BaseDeEleventa, Cajero = 7 };
        soloLee.Cajas["A"] = new CajaDelPuente { Caja = 3 };

        Assert.Null((await pc.Mudanza(soloLee).SacarAsync(pc.Usb)).Error);

        var claves = ClavesEscritas.De(pc.Resumen);
        Assert.Equal(("si", "no", "A=3", "1"), (claves["eleventa"], claves["eleventa-escribir"], claves["eleventa-cajas"], claves["eleventa-pendientes"]));
        Assert.Equal(
            [
                "Hay 1 venta que todavía no llega a eleventa: viaja con la base, pero el puente llega sin escribir y no se manda hasta que lo prendas en la PC nueva.",
                "La caja «Caja 1» (serie A) tiene un turno abierto: se queda abierto y se sigue en él al terminar la mudanza.",
                "La caja «Caja 2» (serie B) no tiene caja de eleventa: antes de prender el puente en la PC nueva, ponle la suya en Eleventa:Cajas.",
            ],
            Enumerable.Range(1, int.Parse(claves["avisos"], CultureInfo.InvariantCulture)).Select(n => claves[$"aviso.{n}"]));
    }

    /// <summary>
    /// El puente apagado pero con su caja de eleventa puesta (como queda después del ensayo: ya no lee ni escribe) también es un puente
    /// configurado: de la caja con turnos sin número efectivo (aquí falta el cajero) se avisa igual. Sin nada de eleventa en la
    /// configuración no hay de qué avisar.
    /// </summary>
    [Fact]
    public async Task Con_el_puente_apagado_pero_con_su_caja_tambien_se_avisa_y_sin_puente_no()
    {
        using var pc = Pc();

        Assert.Null((await pc.Mudanza(new OpcionesEleventa { Caja = 3 }).SacarAsync(pc.Usb)).Error);

        var claves = ClavesEscritas.De(pc.Resumen);
        Assert.Equal(("no", "no", "", "3", ""), (claves["eleventa"], claves["eleventa-escribir"], claves["eleventa-cajas"], claves["eleventa-caja"], claves["eleventa-cajero"]));
        Tiene("2", claves, "avisos");
        Tiene("La caja «Caja 1» (serie A) no tiene caja de eleventa: antes de prender el puente en la PC nueva, ponle la suya en Eleventa:Cajas.", claves, "aviso.2");

        // En otra PC igual, sin nada de eleventa: solo el aviso del turno abierto.
        using var sinPuente = Pc();
        Assert.Null((await sinPuente.Mudanza().SacarAsync(sinPuente.Usb)).Error);
        Assert.Equal("1", ClavesEscritas.De(sinPuente.Resumen)["avisos"]);
    }

    // ── Los avisos y los ajustes del puente ──────────────────────────────────

    /// <summary>
    /// Lo que quien hace la mudanza tiene que saber antes de seguir: lo pendiente de la bandeja (que no siempre son ventas), lo que quedó
    /// por revisar, el puente detenido y las cajas dadas de alta desde otros equipos, en ese orden.
    /// </summary>
    [Fact]
    public async Task Avisa_de_lo_pendiente_lo_por_revisar_el_puente_detenido_y_las_cajas_de_otros_equipos()
    {
        using var pc = Pc();
        var detenidoDesde = new DateTime(2026, 11, 13, 23, 40, 0, DateTimeKind.Utc);
        await using (var db = pc.Base())
        {
            var caja = await CajaUnoAsync(db);
            caja.DarDeAlta("caja-2", DateTime.UtcNow);
            await OtraCajaAsync(db, "Celular de Ana", "C", "Moto G");
            await OtraCajaAsync(db, "Mostrador", "M", "PC-MOSTRADOR");
            // Sin llave no es de ningún equipo.
            await OtraCajaAsync(db, "Sin equipo", "S");
            db.EnviosAEleventa.AddRange(
                new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = semilla.Ventas[0], CreadoUtc = DateTime.UtcNow },
                new EnvioAEleventa { Tipo = TipoEnvioEleventa.Cancelacion, VentaId = semilla.Ventas[0], CreadoUtc = DateTime.UtcNow },
                new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = semilla.Ventas[1], CreadoUtc = DateTime.UtcNow, Estado = EstadoEnvioEleventa.PorRevisar },
                // Lo que ya no se manda (descartado, o anotado a mano en eleventa) no cuenta como pendiente ni como por revisar.
                new EnvioAEleventa { Tipo = TipoEnvioEleventa.Cancelacion, VentaId = semilla.Ventas[1], CreadoUtc = DateTime.UtcNow, Estado = EstadoEnvioEleventa.Descartada },
                new EnvioAEleventa { Tipo = TipoEnvioEleventa.MovimientoDeCaja, CreadoUtc = DateTime.UtcNow, Estado = EstadoEnvioEleventa.AMano });
            db.Set<DetencionDelPuente>().AddRange(
                new DetencionDelPuente { DetectadaUtc = detenidoDesde, Pruebas = "Eleventa ya no tiene lo que se le mandó." },
                // Una de antes, ya reanudada: no cuenta.
                new DetencionDelPuente { DetectadaUtc = detenidoDesde.AddDays(-9), Pruebas = "De antes.", ReanudadoUtc = detenidoDesde.AddDays(-8) });
            await db.SaveChangesAsync();
        }

        Assert.Null((await pc.Mudanza(Escribiendo()).SacarAsync(pc.Usb)).Error);

        var claves = ClavesEscritas.De(pc.Resumen);
        Assert.Equal(("2", "1", "si"), (claves["eleventa-pendientes"], claves["eleventa-por-revisar"], claves["eleventa-detenido"]));
        // El equipo de la caja de esta PC, sin distinguir mayúsculas: no es «otro equipo».
        Assert.Equal(
            [
                "Hay 2 operaciones de la caja (ventas, devoluciones, entradas o salidas) que todavía no llegan a eleventa: viajan con la base y las manda la PC "
                    + "nueva, sin repetirse.",
                "Hay 1 cosa por revisar con eleventa (no pasó sola): viaja con la base y se atiende en la PC nueva, como siempre.",
                "El puente con eleventa está detenido desde el 13/11/2026 17:40: llega detenido a la PC nueva y no manda nada hasta que se reanude desde una caja.",
                "La caja «Caja 1» (serie A) tiene un turno abierto: se queda abierto; no hagas el corte en eleventa hasta terminar la mudanza.",
                "Hay 2 cajas dadas de alta desde otros equipos (Celular de Ana en «Moto G», Mostrador en «PC-MOSTRADOR»): después de la mudanza cada una se da "
                    + "de alta otra vez.",
            ],
            Enumerable.Range(1, int.Parse(claves["avisos"], CultureInfo.InvariantCulture)).Select(n => claves[$"aviso.{n}"]));
    }

    /// <summary>
    /// De la configuración de allá solo viajan los ajustes del puente de la lista cerrada que no están en su valor de siempre, escritos
    /// como van en servicio.json (el punto decimal, true o false). Nunca una ruta (Gbak, CarpetaRespaldos).
    /// </summary>
    [Fact]
    public async Task Los_ajustes_del_puente_que_cambiaron_viajan_como_se_escriben_en_la_configuracion()
    {
        using var pc = Pc();
        var eleventa = Escribiendo(o =>
        {
            o.ProductoImpresiones = "IMPRE";
            o.RespaldoObligatorio = false;
            o.SegundosAlCobrar = 2.5;
            o.Minutos = 10;
            o.DiasDeRespaldos = 7;
            // El de copias lleva «;»: no cabe en la lista, y se dice.
            o.ProductoCopias = "COP;1";
            // Lo que es de esa PC no viaja.
            o.Gbak = @"C:\Firebird\bin\gbak.exe";
            o.CarpetaRespaldos = @"D:\respaldos-eleventa";
        });
        // Con una región de Windows que escribe los decimales con coma: el punto no depende de eso.
        var cultura = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
        try
        {
            Assert.Null((await pc.Mudanza(eleventa).SacarAsync(pc.Usb)).Error);
        }
        finally
        {
            CultureInfo.CurrentCulture = cultura;
        }

        var claves = ClavesEscritas.De(pc.Resumen);
        Tiene("ProductoImpresiones=IMPRE;RespaldoObligatorio=false;SegundosAlCobrar=2.5;Minutos=10;DiasDeRespaldos=7", claves, "eleventa-ajustes");
        Tiene("El ajuste «ProductoCopias» del puente con eleventa de esta PC no cabe en el paquete (lleva «;»): ponlo a mano en la PC nueva.", claves, "aviso.2");
        var todo = await File.ReadAllTextAsync(pc.Resumen);
        Assert.DoesNotContain("gbak", todo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("respaldos-eleventa", todo, StringComparison.OrdinalIgnoreCase);
    }

    // ── Lo que se revisa antes de tocar nada ─────────────────────────────────

    [Fact]
    public async Task Sin_la_base_no_hay_nada_que_mudar()
    {
        using var pc = new PcQueSeMuda(null);

        var resultado = await pc.Mudanza().SacarAsync(pc.Usb);

        Assert.Equal($"No está la base de esta PC ({pc.Ruta}): no hay nada que mudar.", resultado.Error);
        Assert.False(resultado.Bien);
        // Ni se creó una vacía, ni se preguntó por el servicio, ni se tocó la USB.
        Assert.False(File.Exists(pc.Ruta));
        Assert.Empty(pc.PuertosPreguntados);
        Assert.False(Directory.Exists(pc.Usb));
        SinMarcaNiRestos(pc);
    }

    /// <summary>Una base de una versión anterior del programa: la pone al día el servicio al arrancar (con su respaldo), no esto.</summary>
    [Fact]
    public async Task Con_migraciones_pendientes_no_se_saca_la_pone_al_dia_el_servicio()
    {
        using var pc = new PcQueSeMuda(null);
        await using (var db = pc.Base())
        {
            var migraciones = db.Database.GetMigrations().ToList();
            await db.GetService<IMigrator>().MigrateAsync(migraciones[^2]);
        }
        var antes = Sha256De(pc.Ruta);

        var resultado = await pc.Mudanza().SacarAsync(pc.Usb);

        Assert.Equal($"A la base de esta PC ({pc.Ruta}) le faltan cambios de esta versión del programa: arranca el servicio para ponerla al día y saca el "
            + "paquete otra vez.", resultado.Error);
        // No la migró.
        Assert.Equal(antes, Sha256De(pc.Ruta));
        await using (var db = pc.Base())
            Assert.Single(await db.Database.GetPendingMigrationsAsync());
        Assert.Empty(pc.RespaldosHechos());
        SinMarcaNiRestos(pc);
    }

    /// <summary>La base que deja el servicio recién instalado (sin sucursal): «Empezar sin base» no tiene nada que mudar.</summary>
    [Fact]
    public async Task Sin_tienda_no_hay_nada_que_mudar()
    {
        using var pc = new PcQueSeMuda(null);
        await using (var db = pc.Base())
            await BaseDeDatos.PrepararAsync(db);

        var resultado = await pc.Mudanza().SacarAsync(pc.Usb);

        Assert.Equal($"La base de esta PC ({pc.Ruta}) no tiene tienda: no hay nada que mudar.", resultado.Error);
        Assert.Empty(pc.RespaldosHechos());
        Assert.False(Directory.Exists(pc.Usb));
        SinMarcaNiRestos(pc);
    }

    [Fact]
    public async Task Un_archivo_que_no_es_una_base_no_se_muda()
    {
        using var pc = new PcQueSeMuda(null);
        await File.WriteAllTextAsync(pc.Ruta, "esto no es una base de SQLite, aunque se llame pos.db");

        var resultado = await pc.Mudanza().SacarAsync(pc.Usb);

        Assert.StartsWith($"No se pudo leer la base de esta PC ({pc.Ruta}): ", resultado.Error);
        SinMarcaNiRestos(pc);
    }

    /// <summary>
    /// Ya se sacó (está la marca): no se saca otro paquete encima, que dejaría dos distintos de la misma base. Primero se cancela con el
    /// instalador. La marca y el paquete que había no se tocan.
    /// </summary>
    [Fact]
    public async Task Con_la_marca_no_se_saca_otra_vez_primero_se_cancela()
    {
        using var pc = Pc();
        var primero = await pc.Mudanza().SacarAsync(pc.Usb);
        Assert.Null(primero.Error);
        var (marca, resumen) = (await File.ReadAllBytesAsync(pc.Marca), await File.ReadAllBytesAsync(pc.Resumen));
        pc.Reloj.Avanzar(TimeSpan.FromMinutes(20));

        var otraVez = await pc.Mudanza().SacarAsync(pc.Usb);

        Assert.Equal("La base de esta PC ya se sacó para mudarla (el 14/11/2026 08:15): cancela la mudanza primero (corre el instalador en esta PC y elige "
            + "«Cancelar la mudanza») y sácala otra vez.", otraVez.Error);
        Assert.Equal(marca, await File.ReadAllBytesAsync(pc.Marca));
        Assert.Equal(resumen, await File.ReadAllBytesAsync(pc.Resumen));
        Assert.Equal(["mudanza.txt", PcQueSeMuda.ArchivoDeLasOchoYCuarto], pc.EnLaUsb());
        Assert.Equal([PcQueSeMuda.ArchivoDeLasOchoYCuarto], pc.RespaldosHechos());
        // A otra carpeta, tampoco.
        Assert.StartsWith("La base de esta PC ya se sacó para mudarla", (await pc.Mudanza().SacarAsync(Path.Combine(pc.Carpeta, "otra-usb"))).Error);
        Assert.False(Directory.Exists(Path.Combine(pc.Carpeta, "otra-usb")));
        // Una marca que no se entiende cuenta igual: con que exista.
        await File.WriteAllTextAsync(pc.Marca, "{ esto no es una marca");
        Assert.StartsWith("La base de esta PC ya se sacó para mudarla", (await pc.Mudanza().SacarAsync(pc.Usb)).Error);
        // Cancelada (el instalador le cambia el nombre), se saca de nuevo: es otro paquete.
        File.Move(pc.Marca, pc.Marca + ".cancelada");
        var despues = await pc.Mudanza().SacarAsync(pc.Usb);
        Assert.Null(despues.Error);
        Assert.NotEqual(primero.Paquete, despues.Paquete);
        Assert.Equal("pos-2026-11-14-mudanza-0835.db", despues.Archivo);
    }

    /// <summary>La base se saca con el servicio detenido (lo detiene el instalador): si contesta, lo que se cobre después no iría en el paquete.</summary>
    [Fact]
    public async Task Si_el_servicio_de_esta_pc_contesta_no_se_saca()
    {
        using var pc = Pc();
        pc.ElServicioContesta = true;

        var resultado = await pc.Mudanza(puerto: 5099).SacarAsync(pc.Usb);

        Assert.Equal("El servicio de esta PC está contestando (http://localhost:5099): la base se saca con el servicio detenido; lo hace el instalador.", resultado.Error);
        Assert.Equal([5099], pc.PuertosPreguntados);
        Assert.Empty(pc.RespaldosHechos());
        Assert.False(Directory.Exists(pc.Usb));
        SinMarcaNiRestos(pc);
    }

    /// <summary>
    /// Un servicio que sigue vivo sin contestar (todavía arranca: migra antes de atender; o se está deteniendo) pasaría la pregunta por
    /// HTTP, y con la marca recién puesta seguiría de largo hasta atender: lo que cobrara ya no iría en el paquete. Mientras vive tiene
    /// abierta la base, así que si otro programa la tiene abierta tampoco se saca. En Windows basta con que alguien la tenga abierta;
    /// aquí se comprueba también con una conexión de SQLite, como la del servicio (fuera de Windows SQLite no la aparta de esa forma).
    /// </summary>
    [Fact]
    public async Task Si_otro_programa_tiene_abierta_la_base_no_se_saca_aunque_el_servicio_no_conteste()
    {
        using var pc = Pc();
        var esperado = $"Otro programa tiene abierta la base de esta PC ({pc.Ruta}): si es el servicio, sigue vivo (arrancando o deteniéndose). La base se "
            + "saca con el servicio detenido; lo hace el instalador.";

        await using (new FileStream(pc.Ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var resultado = await pc.Mudanza().SacarAsync(pc.Usb);

            Assert.Equal(esperado, resultado.Error);
            // Se le preguntó al servicio, que no contesta; y no se hizo nada: ni el respaldo, ni la carpeta.
            Assert.Equal([5080], pc.PuertosPreguntados);
            Assert.Empty(pc.RespaldosHechos());
            Assert.False(Directory.Exists(pc.Usb));
            SinMarcaNiRestos(pc);
        }
        if (OperatingSystem.IsWindows())
        {
            await using var delServicio = new SqliteConnection(pc.Cadena);
            await delServicio.OpenAsync();
            await using (var comando = delServicio.CreateCommand())
            {
                comando.CommandText = "SELECT COUNT(*) FROM Sucursales";
                Assert.Equal(1L, await comando.ExecuteScalarAsync());
            }
            Assert.Equal(esperado, (await pc.Mudanza().SacarAsync(pc.Usb)).Error);
            SinMarcaNiRestos(pc);
        }
        // En cuanto la sueltan, sale.
        Assert.Null((await pc.Mudanza().SacarAsync(pc.Usb)).Error);
    }

    /// <summary>
    /// La carpeta del paquete va fuera de los datos de esta PC (al terminar la mudanza se apartan), del programa (desinstalar se lo
    /// lleva) y no es la carpeta de respaldos (el respaldo y su copia serían el mismo archivo).
    /// </summary>
    [Fact]
    public async Task La_carpeta_del_paquete_no_va_en_los_datos_ni_en_el_programa_ni_es_la_de_respaldos()
    {
        using var pc = Pc();
        var enLosDatos = Path.Combine(pc.Datos, "mudanza");
        var enElPrograma = Path.Combine(pc.ArchivosDePrograma, "PapeleriaPos", "mudanza");
        var respaldosEnLaUsb = Path.Combine(pc.Carpeta, "usb", "respaldos");

        Assert.Equal($"La carpeta del paquete ({enLosDatos}) está dentro de la carpeta de datos de esta PC ({pc.Datos}): elige otra (la USB).",
            (await pc.Mudanza().SacarAsync(enLosDatos)).Error);
        // La de datos misma, escrita con su diagonal al final.
        Assert.EndsWith($" está dentro de la carpeta de datos de esta PC ({pc.Datos}): elige otra (la USB).",
            (await pc.Mudanza().SacarAsync(pc.Datos + Path.DirectorySeparatorChar)).Error);
        Assert.Equal($"La carpeta del paquete ({enElPrograma}) está dentro de Archivos de programa ({pc.ArchivosDePrograma}): elige otra (la USB).",
            (await pc.Mudanza().SacarAsync(enElPrograma)).Error);
        Assert.Equal($"La carpeta del paquete ({respaldosEnLaUsb}) es la carpeta de respaldos de esta PC: elige otra (la USB).",
            (await pc.Mudanza(respaldos: respaldosEnLaUsb).SacarAsync(respaldosEnLaUsb)).Error);

        // Ninguna se creó, y no se hizo ningún respaldo.
        Assert.False(Directory.Exists(enLosDatos));
        Assert.False(Directory.Exists(enElPrograma));
        Assert.False(Directory.Exists(respaldosEnLaUsb));
        Assert.Empty(pc.RespaldosHechos());
        SinMarcaNiRestos(pc);
        // Una carpeta que solo empieza igual que la de datos no está dentro de ella.
        Assert.Null((await pc.Mudanza().SacarAsync(pc.Datos + "-mudanza")).Error);
    }

    /// <summary>
    /// La carpeta de respaldos escrita de otra forma (aquí, por un enlace; en la tienda sería una unidad de red o un acceso directo) es
    /// la misma carpeta, y ahí tampoco se saca: al dejar el paquete se quitan de su carpeta las bases de paquetes anteriores, y en esa
    /// serían los respaldos de las mudanzas de antes.
    /// </summary>
    [Fact]
    public async Task La_carpeta_de_respaldos_por_otro_camino_tampoco_sirve_y_sus_respaldos_no_se_tocan()
    {
        using var pc = Pc();
        Directory.CreateDirectory(pc.Respaldos);
        // El respaldo de una mudanza de antes, que se canceló: un respaldo más.
        await File.WriteAllBytesAsync(Path.Combine(pc.Respaldos, "pos-2026-11-07-mudanza-0900.db"), new byte[64]);
        var porOtroCamino = Path.Combine(pc.Carpeta, "usb", "respaldos-de-la-tienda");
        Directory.CreateDirectory(Path.GetDirectoryName(porOtroCamino)!);
        try
        {
            Directory.CreateSymbolicLink(porOtroCamino, pc.Respaldos);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // En Windows, sin permiso para hacer enlaces (hace falta ser administrador): aquí no se puede armar el caso.
            return;
        }

        var resultado = await pc.Mudanza().SacarAsync(porOtroCamino);

        Assert.Equal($"La carpeta del paquete ({porOtroCamino}) es la carpeta de respaldos de esta PC ({pc.Respaldos}): elige otra (la USB).", resultado.Error);
        Assert.Equal(["pos-2026-11-07-mudanza-0900.db"], pc.RespaldosHechos());
        Assert.False(Path.Exists(pc.Marca));
    }

    /// <summary>La carpeta no se puede crear ni escribir (la USB no está, o hay un archivo con ese nombre): se dice antes de hacer nada.</summary>
    [Fact]
    public async Task Si_la_carpeta_del_paquete_no_se_puede_escribir_no_se_hace_nada()
    {
        using var pc = Pc();
        Directory.CreateDirectory(Path.GetDirectoryName(pc.Usb)!);
        // Donde iría la carpeta hay un archivo.
        await File.WriteAllTextAsync(pc.Usb, "no soy una carpeta");

        var resultado = await pc.Mudanza().SacarAsync(pc.Usb);

        Assert.StartsWith($"No se puede escribir en la carpeta del paquete ({pc.Usb}): ", resultado.Error);
        Assert.EndsWith(" Revisa que la USB esté conectada y que no esté protegida contra escritura.", resultado.Error);
        Assert.Equal("no soy una carpeta", await File.ReadAllTextAsync(pc.Usb));
        Assert.Empty(pc.RespaldosHechos());
        Assert.False(Path.Exists(pc.Marca));
        // Y debajo de un archivo, tampoco.
        Assert.StartsWith("No se puede escribir en la carpeta del paquete", (await pc.Mudanza().SacarAsync(Path.Combine(pc.Usb, "adentro"))).Error);
        Assert.False(Path.Exists(pc.Marca));
    }

    /// <summary>El disco con tantos bytes libres en cada carpeta (las que no se dicen, de sobra).</summary>
    private sealed class DiscoCon(params (string Carpeta, long Libres)[] carpetas) : IEspacioEnDisco
    {
        public long? Libre(string carpeta) => carpetas.Where(c => c.Carpeta == carpeta).Select(c => (long?)c.Libres).FirstOrDefault() ?? long.MaxValue;
    }

    /// <summary>Tiene que caber la base y 32 MB más, en la carpeta del paquete y en la de respaldos; si no, ni se empieza.</summary>
    [Fact]
    public async Task Sin_espacio_en_la_usb_o_en_los_respaldos_ni_se_empieza()
    {
        using var pc = Pc();
        var necesario = new FileInfo(pc.Ruta).Length + 32L * 1024 * 1024;

        var sinEspacioEnLaUsb = await pc.Mudanza(disco: new DiscoCon((pc.Usb, necesario - 1))).SacarAsync(pc.Usb);
        var sinEspacioAqui = await pc.Mudanza(disco: new DiscoCon((pc.Respaldos, necesario - 1))).SacarAsync(pc.Usb);

        Assert.Equal($"No hay espacio en {pc.Usb} para el paquete de mudanza: hacen falta {RespaldoDto.TamanoDe(necesario)} libres y hay "
            + $"{RespaldoDto.TamanoDe(necesario - 1)}.", sinEspacioEnLaUsb.Error);
        Assert.Equal($"No hay espacio en el disco de esta PC para el respaldo de la mudanza ({pc.Respaldos}): hacen falta {RespaldoDto.TamanoDe(necesario)} "
            + $"libres y hay {RespaldoDto.TamanoDe(necesario - 1)}.", sinEspacioAqui.Error);
        Assert.Empty(pc.RespaldosHechos());
        SinMarcaNiRestos(pc);
        // Con lo justo, sí.
        Assert.Null((await pc.Mudanza(disco: new DiscoCon((pc.Usb, necesario), (pc.Respaldos, necesario))).SacarAsync(pc.Usb)).Error);
    }

    // ── Si algo falla a la mitad ─────────────────────────────────────────────

    /// <summary>
    /// La USB se llena (o la quitan) a media copia: no queda la marca, ni la copia a medias, ni mudanza.txt; el respaldo de esta PC sí
    /// (es un respaldo más) y la base sigue como estaba. Esta PC sigue trabajando.
    /// </summary>
    [Fact]
    public async Task Si_falla_a_media_copia_no_queda_la_marca_ni_restos_del_paquete()
    {
        using var pc = Pc();
        var baseAntes = Sha256De(pc.Ruta);
        long copiados = 0;
        async Task SeLlenaAMedias(Stream de, Stream a, CancellationToken ct)
        {
            var mitad = new byte[de.Length / 2];
            await de.ReadExactlyAsync(mitad, ct);
            await a.WriteAsync(mitad, ct);
            await a.FlushAsync(ct);
            copiados = mitad.Length;
            throw new IOException("No space left on device");
        }

        var resultado = await pc.Mudanza(Escribiendo(), copiar: SeLlenaAMedias).SacarAsync(pc.Usb);

        Assert.Equal($"No se pudo copiar la base a {pc.Usb}: No space left on device", resultado.Error);
        Assert.True(copiados > 0);
        SinMarcaNiRestos(pc);
        Assert.Equal([PcQueSeMuda.ArchivoDeLasOchoYCuarto], pc.RespaldosHechos());
        Assert.Equal(baseAntes, Sha256De(pc.Ruta));
        // Ya con lugar, sale: nada del intento anterior estorba.
        pc.Reloj.Avanzar(TimeSpan.FromMinutes(1));
        var despues = await pc.Mudanza(Escribiendo()).SacarAsync(pc.Usb);
        Assert.Null(despues.Error);
        Assert.Equal(["mudanza.txt", "pos-2026-11-14-mudanza-0816.db"], pc.EnLaUsb());
    }

    /// <summary>La copia llegó completa en tamaño pero no es la misma (una USB que falla): se vuelve a leer de ahí y no cuadra su SHA-256.</summary>
    [Fact]
    public async Task Si_la_copia_no_queda_igual_que_el_respaldo_no_se_da_por_buena()
    {
        using var pc = Pc();
        static async Task ConUnByteCambiado(Stream de, Stream a, CancellationToken ct)
        {
            var todo = new byte[de.Length];
            await de.ReadExactlyAsync(todo, ct);
            todo[^100] ^= 0xFF;
            await a.WriteAsync(todo, ct);
        }

        var resultado = await pc.Mudanza(copiar: ConUnByteCambiado).SacarAsync(pc.Usb);

        Assert.Equal($"La copia de la base en {pc.Usb} no quedó igual que el respaldo (¿la USB está fallando?): prueba con otra.", resultado.Error);
        SinMarcaNiRestos(pc);
    }

    /// <summary>Lo que no se deja escribir tiene, en su lugar, una carpeta con el nombre de su temporal.</summary>
    private static void QueNoSeDejeEscribir(string archivo) => Directory.CreateDirectory(archivo + ".tmp");

    /// <summary>No se puede dejar la marca junto a la base: tampoco queda el paquete, que sin la marca no debe servir.</summary>
    [Fact]
    public async Task Si_no_se_puede_dejar_la_marca_no_queda_el_paquete()
    {
        using var pc = Pc();
        QueNoSeDejeEscribir(pc.Marca);

        var resultado = await pc.Mudanza().SacarAsync(pc.Usb);

        Assert.StartsWith($"No se pudo dejar la marca de la mudanza junto a la base ({pc.Marca}): ", resultado.Error);
        Assert.False(Path.Exists(pc.Marca));
        Assert.Empty(pc.EnLaUsb());
        Assert.Equal([PcQueSeMuda.ArchivoDeLasOchoYCuarto], pc.RespaldosHechos());
    }

    /// <summary>
    /// Un apagón justo después de sacar no deja un paquete completo con esta PC sin marca: Windows escribe al disco el cambio de nombre
    /// unos segundos después, así que la marca se deja en el disco ya con su nombre antes de empezar a escribir mudanza.txt (en la USB, que
    /// escribe enseguida). Vaciarla no la cambia.
    /// </summary>
    [Fact]
    public async Task La_marca_queda_en_el_disco_con_su_nombre_antes_de_empezar_el_resumen()
    {
        using var pc = Pc();
        var pasos = new List<string>();
        void EnDisco(string archivo)
        {
            MudanzaDeLaBase.DejarSuNombreEnDisco(archivo);
            pasos.Add($"en el disco: {Path.GetFileName(archivo)}");
            Assert.False(File.Exists(archivo + ".tmp"), "La marca se vació a disco antes de tomar su nombre.");
            Assert.False(File.Exists(pc.Resumen) || File.Exists(pc.Resumen + ".tmp"), "El resumen ya se había empezado a escribir.");
        }

        var resultado = await pc.Mudanza(Escribiendo(), alLlegarA: momento => pasos.Add(momento.ToString()), dejarEnDisco: EnDisco).SacarAsync(pc.Usb);

        Assert.Null(resultado.Error);
        Assert.Equal(["AntesDeLaMarca", "en el disco: pos.db.mudada", "AntesDelResumen", "AntesDeNombrarElResumen"], pasos);
        Assert.Equal(resultado.Paquete, MudanzaDeLaBase.LeerMarca(pc.Marca)?.Paquete);
        Assert.Equal(["mudanza.txt", PcQueSeMuda.ArchivoDeLasOchoYCuarto], pc.EnLaUsb());
    }

    /// <summary>Si la marca no se deja en el disco con su nombre, no se escribe el resumen y se quita la marca: esta PC sigue trabajando.</summary>
    [Fact]
    public async Task Si_la_marca_no_queda_en_el_disco_no_se_escribe_el_resumen_y_no_queda_nada()
    {
        using var pc = Pc();
        static void NoSeDeja(string archivo) => throw new IOException("The request could not be performed because of an I/O device error.");

        var resultado = await pc.Mudanza(Escribiendo(), dejarEnDisco: NoSeDeja).SacarAsync(pc.Usb);

        Assert.Equal($"No se pudo dejar la marca de la mudanza junto a la base ({pc.Marca}): The request could not be performed because of an I/O device "
            + "error.", resultado.Error);
        SinMarcaNiRestos(pc);
        Assert.Equal([PcQueSeMuda.ArchivoDeLasOchoYCuarto], pc.RespaldosHechos());
    }

    /// <summary>
    /// La marca se deja antes que mudanza.txt (así nunca hay un paquete completo con esta PC sin marca). Si mudanza.txt ya no se pudo
    /// escribir, el paquete queda sin terminar y la marca se quita: esta PC sigue trabajando, y se puede sacar otra vez.
    /// </summary>
    [Fact]
    public async Task Si_el_resumen_no_se_puede_escribir_se_quita_la_marca_y_no_quedan_restos()
    {
        using var pc = Pc();
        Directory.CreateDirectory(pc.Usb);
        QueNoSeDejeEscribir(pc.Resumen);

        var resultado = await pc.Mudanza().SacarAsync(pc.Usb);

        Assert.StartsWith($"No se pudo escribir {pc.Resumen}: ", resultado.Error);
        Assert.DoesNotContain("marca", resultado.Error);
        SinMarcaNiRestos(pc, "mudanza.txt.tmp");
        // Quitado el estorbo, sale.
        Directory.Delete(pc.Resumen + ".tmp");
        Assert.Null((await pc.Mudanza().SacarAsync(pc.Usb)).Error);
        Assert.True(File.Exists(pc.Marca));
        Assert.Equal(["mudanza.txt", PcQueSeMuda.ArchivoDeLasOchoYCuarto], pc.EnLaUsb());
    }

    /// <summary>Lo que hay en una carpeta (solo los nombres, ordenados).</summary>
    private static List<string> En(string carpeta) =>
        Directory.EnumerateFileSystemEntries(carpeta).Select(e => Path.GetFileName(e)!).Order(StringComparer.Ordinal).ToList();

    /// <summary>Lo que se agrega al error cuando la copia pudo quedarse en una USB que ya no está al alcance: nadie más lo va a decir.</summary>
    private static string PudoQuedarLaCopia(PcQueSeMuda pc) =>
        $" En {pc.Usb} pudo quedar la copia de la base ({PcQueSeMuda.ArchivoDeLasOchoYCuarto}), sin terminar: si sigue ahí, bórrala a mano.";

    /// <summary>
    /// Se llevaron la USB con la copia ya comprobada y antes de dejar la marca: se nota antes de dejarla, y termina como cualquier falla
    /// de la copia. Sin marca, esta PC sigue trabajando como antes; y como ya no se puede borrar la copia de la USB, se dice.
    /// </summary>
    [Fact]
    public async Task Si_se_llevan_la_usb_antes_de_dejar_la_marca_no_se_deja_y_esta_pc_sigue_trabajando()
    {
        using var pc = Pc();
        var seFue = pc.Usb + "-ya-no-esta";

        var resultado = await pc.Mudanza(alLlegarA: pc.SeLlevanLaUsb(MudanzaDeLaBase.Momento.AntesDeLaMarca, seFue)).SacarAsync(pc.Usb);

        Assert.Equal($"La copia de la base ya no está en {pc.Usb} (¿quitaron la USB antes de terminar?): no se sacó la base." + PudoQuedarLaCopia(pc),
            resultado.Error);
        SinMarcaNiRestos(pc);
        // En la USB que se fue, la copia sin su resumen: no es un paquete. Y no se hizo otra carpeta en su lugar.
        Assert.Equal([PcQueSeMuda.ArchivoDeLasOchoYCuarto], En(seFue));
        Assert.False(Directory.Exists(pc.Usb));
    }

    /// <summary>
    /// Se llevaron la USB con la marca ya puesta y antes de empezar a escribir mudanza.txt: como ni se llegó a escribir aparte, consta
    /// que el paquete quedó sin terminar aunque la carpeta ya no esté al alcance, y la marca se quita. Esta PC sigue trabajando como
    /// antes, sin tener que cancelar nada con el instalador.
    /// </summary>
    [Fact]
    public async Task Si_se_llevan_la_usb_con_la_marca_puesta_y_sin_empezar_el_resumen_la_marca_se_quita()
    {
        using var pc = Pc();
        var seFue = pc.Usb + "-ya-no-esta";
        var conLaMarca = false;
        void YSeVaLaUsb(MudanzaDeLaBase.Momento momento)
        {
            if (momento != MudanzaDeLaBase.Momento.AntesDelResumen)
                return;
            conLaMarca = File.Exists(pc.Marca);
            PcQueSeMuda.MoverLaUsb(pc.Usb, seFue);
        }

        var resultado = await pc.Mudanza(alLlegarA: YSeVaLaUsb).SacarAsync(pc.Usb);

        Assert.True(conLaMarca, "La USB se fue antes de que se dejara la marca: la prueba no es de este caso.");
        Assert.StartsWith($"No se pudo escribir {pc.Resumen}: ", resultado.Error);
        Assert.EndsWith(PudoQuedarLaCopia(pc), resultado.Error);
        Assert.DoesNotContain("marca", resultado.Error);
        SinMarcaNiRestos(pc);
        Assert.Equal([PcQueSeMuda.ArchivoDeLasOchoYCuarto], En(seFue));
        Assert.False(Directory.Exists(pc.Usb));
        // La USB regresa y se saca otra vez a la misma carpeta: sale, y la copia que quedó suelta no se queda junto al paquete nuevo.
        PcQueSeMuda.MoverLaUsb(seFue, pc.Usb);
        pc.Reloj.Avanzar(TimeSpan.FromMinutes(25));
        Assert.Null((await pc.Mudanza().SacarAsync(pc.Usb)).Error);
        Assert.Equal(["mudanza.txt", "pos-2026-11-14-mudanza-0840.db"], pc.EnLaUsb());
    }

    /// <summary>
    /// Se llevaron la USB justo cuando mudanza.txt, ya escrito aparte, iba a tomar su nombre: es lo único en que no se puede saber si
    /// el paquete quedó completo, así que la marca se queda —nunca un paquete que pudiera servir con esta PC trabajando— y el error dice
    /// cómo seguir.
    /// </summary>
    [Fact]
    public async Task Si_se_llevan_la_usb_justo_al_nombrar_el_resumen_la_marca_se_queda_y_se_dice_como_seguir()
    {
        using var pc = Pc();
        var seFue = pc.Usb + "-ya-no-esta";

        var resultado = await pc.Mudanza(alLlegarA: pc.SeLlevanLaUsb(MudanzaDeLaBase.Momento.AntesDeNombrarElResumen, seFue)).SacarAsync(pc.Usb);

        Assert.StartsWith($"No se pudo escribir {pc.Resumen}: ", resultado.Error);
        Assert.EndsWith($" No se pudo comprobar que el paquete de {pc.Usb} quedó sin terminar, así que esta PC se queda con la marca de la mudanza ({pc.Marca}) "
            + "y su servicio no arranca: así ese paquete no se trae a otra PC con esta trabajando. Corre el instalador en esta PC y elige «Cancelar la "
            + "mudanza»; después saca la base otra vez.", resultado.Error);
        Assert.True(File.Exists(pc.Marca));
        Assert.Throws<InvalidOperationException>(() => MudanzaDeLaBase.NoArrancarConLaMarca(pc.Cadena, new RegistroEnMemoria().CreateLogger("prueba")));
        // En la USB que se fue no quedó un paquete completo (su resumen no llegó a llamarse mudanza.txt), y no se hizo otra carpeta con
        // un resumen sin su base.
        Assert.Equal(["mudanza.txt.tmp", PcQueSeMuda.ArchivoDeLasOchoYCuarto], En(seFue));
        Assert.False(Directory.Exists(pc.Usb));
    }

    // ── El paquete que ya estaba en la carpeta ───────────────────────────────

    /// <summary>
    /// Sacar a una carpeta que ya tiene un paquete lo reemplaza: se van su mudanza.txt, su base y su traida.txt, y nada más (lo demás que
    /// haya en la USB no es del paquete). Un mudanza.txt ajeno no hace borrar otra cosa.
    /// </summary>
    [Fact]
    public async Task Un_paquete_que_ya_estaba_en_la_carpeta_se_reemplaza_sin_tocar_lo_demas()
    {
        using var pc = Pc();
        Directory.CreateDirectory(pc.Usb);
        var deAntes = Path.Combine(pc.Usb, "pos-2026-11-07-mudanza-0900.db");
        await File.WriteAllBytesAsync(deAntes, new byte[4096]);
        await File.WriteAllTextAsync(pc.Resumen, "# de antes\r\nformato=1\r\npaquete=0f8fad5bd9cb469fa16570867728950e\r\narchivo=pos-2026-11-07-mudanza-0900.db\r\n");
        await File.WriteAllTextAsync(Path.Combine(pc.Usb, "traida.txt"), "paquete=0f8fad5bd9cb469fa16570867728950e\r\nips=192.0.2.10\r\n");
        await File.WriteAllTextAsync(Path.Combine(pc.Usb, "instrucciones.txt"), "no es del paquete");
        await File.WriteAllBytesAsync(Path.Combine(pc.Usb, "pos-2026-10-31.db"), new byte[16]);

        // Mientras no se llega a la carpeta (el servicio contesta), el paquete de antes sigue entero.
        pc.ElServicioContesta = true;
        Assert.NotNull((await pc.Mudanza().SacarAsync(pc.Usb)).Error);
        Assert.Equal(["instrucciones.txt", "mudanza.txt", "pos-2026-10-31.db", "pos-2026-11-07-mudanza-0900.db", "traida.txt"], pc.EnLaUsb());
        pc.ElServicioContesta = false;

        // En la USB falta justo lo que ocupa la base del paquete de antes (4096 bytes): como se va a quitar, cabe.
        var necesario = new FileInfo(pc.Ruta).Length + 32L * 1024 * 1024;
        Assert.StartsWith($"No hay espacio en {pc.Usb} para el paquete de mudanza", (await pc.Mudanza(disco: new DiscoCon((pc.Usb, necesario - 4097))).SacarAsync(pc.Usb)).Error);
        var resultado = await pc.Mudanza(disco: new DiscoCon((pc.Usb, necesario - 4096))).SacarAsync(pc.Usb);

        Assert.Null(resultado.Error);
        Assert.Equal(["instrucciones.txt", "mudanza.txt", "pos-2026-10-31.db", PcQueSeMuda.ArchivoDeLasOchoYCuarto], pc.EnLaUsb());
        Assert.Equal(resultado.Paquete, ClavesEscritas.De(pc.Resumen)["paquete"]);
        Assert.Equal("no es del paquete", await File.ReadAllTextAsync(Path.Combine(pc.Usb, "instrucciones.txt")));
    }

    [Theory]
    [InlineData("../fuera.db")]
    [InlineData("instrucciones.txt")]
    [InlineData("")]
    public async Task Un_resumen_ajeno_no_hace_borrar_lo_que_no_es_la_base_de_un_paquete(string archivo)
    {
        using var pc = Pc();
        Directory.CreateDirectory(pc.Usb);
        await File.WriteAllTextAsync(Path.Combine(pc.Usb, "instrucciones.txt"), "no es del paquete");
        await File.WriteAllTextAsync(Path.Combine(pc.Carpeta, "usb", "fuera.db"), "tampoco");
        await File.WriteAllTextAsync(pc.Resumen, $"archivo={archivo}\r\n");

        Assert.Null((await pc.Mudanza().SacarAsync(pc.Usb)).Error);

        Assert.Equal("no es del paquete", await File.ReadAllTextAsync(Path.Combine(pc.Usb, "instrucciones.txt")));
        Assert.Equal("tampoco", await File.ReadAllTextAsync(Path.Combine(pc.Carpeta, "usb", "fuera.db")));
        Assert.Equal(["instrucciones.txt", "mudanza.txt", PcQueSeMuda.ArchivoDeLasOchoYCuarto], pc.EnLaUsb());
    }

    /// <summary>
    /// Un intento que se cortó a media copia (se fue la luz, cerraron la ventana, se llevaron la USB) deja en la carpeta la copia de la
    /// base sin su mudanza.txt, y con el nombre de su minuto: el siguiente intento lleva otro. Al sacar otra vez también se quitan: son
    /// copias enteras de la base de la tienda (con sus PIN cifrados) que nadie más va a borrar. Solo las que llevan el nombre exacto que
    /// este programa le pone a la base de un paquete, y solo las de esa carpeta.
    /// </summary>
    [Fact]
    public async Task Las_copias_de_la_base_que_quedaron_sueltas_en_la_carpeta_se_quitan_al_sacar_otra_vez()
    {
        using var pc = Pc();
        Directory.CreateDirectory(Path.Combine(pc.Usb, "otra"));
        // De dos intentos que no terminaron: ninguna tiene su mudanza.txt. Y un resumen que se quedó a medio escribir.
        await File.WriteAllBytesAsync(Path.Combine(pc.Usb, "pos-2026-11-14-mudanza-0750.db"), new byte[4096]);
        await File.WriteAllBytesAsync(Path.Combine(pc.Usb, "pos-2026-11-07-mudanza-2359.db"), new byte[2048]);
        await File.WriteAllTextAsync(Path.Combine(pc.Usb, "mudanza.txt.tmp"), "# a medias\r\nformato=1\r\n");
        // Lo que no lleva ese nombre exacto no es la base de un paquete: no se toca. Lo de otra carpeta, tampoco.
        string[] ajenos =
        [
            "pos-2026-10-31.db", "pos-2026-11-14-z-0750.db", "pos-2026-11-14-mudanza-0750.db.bak", "copia de pos-2026-11-14-mudanza-0750.db",
            "pos-2026-11-14-mudanza-075.db", "pos-2026-11-14-mudanza-0750.db-wal", "instrucciones.txt",
        ];
        foreach (var ajeno in ajenos)
            await File.WriteAllBytesAsync(Path.Combine(pc.Usb, ajeno), new byte[16]);
        await File.WriteAllBytesAsync(Path.Combine(pc.Usb, "otra", "pos-2026-11-01-mudanza-0900.db"), new byte[16]);
        var antes = pc.EnLaUsb();

        // Lo que ocupan las sueltas cuenta como libre (se van a quitar); mientras no se llega a reemplazar el paquete, nada se borra.
        var necesario = new FileInfo(pc.Ruta).Length + 32L * 1024 * 1024;
        Assert.StartsWith($"No hay espacio en {pc.Usb} para el paquete de mudanza",
            (await pc.Mudanza(disco: new DiscoCon((pc.Usb, necesario - 4096 - 2048 - 1))).SacarAsync(pc.Usb)).Error);
        Assert.Equal(antes, pc.EnLaUsb());
        var resultado = await pc.Mudanza(disco: new DiscoCon((pc.Usb, necesario - 4096 - 2048))).SacarAsync(pc.Usb);

        Assert.Null(resultado.Error);
        Assert.Equal(ajenos.Concat(["otra", "mudanza.txt", PcQueSeMuda.ArchivoDeLasOchoYCuarto]).Order(StringComparer.Ordinal), pc.EnLaUsb());
        Assert.True(File.Exists(Path.Combine(pc.Usb, "otra", "pos-2026-11-01-mudanza-0900.db")));
        Assert.Equal(resultado.Paquete, ClavesEscritas.De(pc.Resumen)["paquete"]);
    }

    // ── El verbo, con la configuración de la PC ──────────────────────────────

    private const string ClaveDeEleventa = "Firebird-de-la-tienda-8842";
    private const string CodigoDeControlPrint = "CP-codigo-771-203";

    /// <summary>La configuración de la PC como la arma el servicio (servicio.json y las variables de entorno), con lo que diga la prueba.</summary>
    private static IConfiguration Configuracion(PcQueSeMuda pc, params (string Clave, string? Valor)[] ajustes) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Pos"] = $"Data Source={pc.Ruta}",
            ["Urls"] = "http://*:5080",
        }).AddInMemoryCollection(ajustes.Select(a => new KeyValuePair<string, string?>(a.Clave, a.Valor))).Build();

    /// <summary>
    /// Con la clave de eleventa y el código de Control Print en la configuración (aunque no deberían estar en un archivo): el paquete
    /// sale con lo del puente —«Cajas» de la configuración incluida— y ninguno de los dos aparece en mudanza.txt, en la marca, en
    /// --salida ni en la pantalla, ni en ningún otro archivo de la carpeta.
    /// </summary>
    [Fact]
    public async Task El_verbo_saca_el_paquete_con_la_configuracion_de_la_pc_y_nunca_escribe_la_clave_de_eleventa()
    {
        using var pc = Pc();
        await using (var db = pc.Base())
        {
            // Dada de alta en esta misma PC (aquí el nombre del equipo es el de verdad: no lo pone la prueba).
            await TurnoAsync(db, await OtraCajaAsync(db, "Caja 2", "B", Environment.MachineName));
            await db.SaveChangesAsync();
        }
        var archivoDeSalida = Path.Combine(pc.Carpeta, "salida", "sacar.txt");
        var configuracion = Configuracion(pc,
            ("Urls", "http://*:5099;http://localhost:6000"), ("Eleventa:Activo", "true"), ("Eleventa:Escribir", "true"), ("Eleventa:Servidor", "192.0.2.106"),
            ("Eleventa:Base", BaseDeEleventa), ("Eleventa:Usuario", "SYSDBA"), ("Eleventa:Clave", ClaveDeEleventa), ("Eleventa:Caja", "3"),
            ("Eleventa:Cajero", "7"), ("Eleventa:Cajas:b:Caja", "4"), ("Eleventa:Cajas:b:Cajero", "9"), ("Eleventa:Minutos", "10"),
            ("Eleventa:Gbak", @"C:\Firebird\bin\gbak.exe"), ("ControlPrint:Servidor", " 192.0.2.106 "), ("ControlPrint:Codigo", CodigoDeControlPrint));
        var (salida, errores) = (new StringWriter(), new StringWriter());

        var codigo = await MudanzaDeLaBase.CorrerAsync(configuracion, ["--carpeta", pc.Usb, "--salida", archivoDeSalida], salida, errores, pc.SegunLaPrueba);

        Assert.Equal("", errores.ToString());
        Assert.Equal(0, codigo);
        Assert.Equal([5099], pc.PuertosPreguntados);
        // Los respaldos, donde siempre: junto a la base.
        var archivo = Assert.Single(pc.RespaldosHechos());
        Assert.Matches(@"^pos-\d{4}-\d{2}-\d{2}-mudanza-\d{4}\.db$", archivo);
        Assert.Equal(["mudanza.txt", archivo], pc.EnLaUsb());
        var claves = ClavesEscritas.De(pc.Resumen);
        Assert.Equal((Environment.MachineName, "5099", archivo, "si", "si", "192.0.2.106", BaseDeEleventa, "SYSDBA", "3", "7", "A=3;B=4/9", "Minutos=10", "si", "192.0.2.106"),
            (claves["equipo"], claves["puerto"], claves["archivo"], claves["eleventa"], claves["eleventa-escribir"], claves["eleventa-servidor"],
                claves["eleventa-base"], claves["eleventa-usuario"], claves["eleventa-caja"], claves["eleventa-cajero"], claves["eleventa-cajas"],
                claves["eleventa-ajustes"], claves["controlprint"], claves["controlprint-servidor"]));

        // --salida: lo que lee instalar-pc.ps1, en su orden.
        var dicho = ClavesEscritas.De(archivoDeSalida);
        Assert.Equal(["bien", "paquete", "carpeta", "archivo", "bytes", "sha256", "tienda", "hecho-local", "avisos", "aviso.1"], dicho.Nombres);
        Assert.Equal(("si", claves["paquete"], pc.Usb, archivo, claves["bytes"], claves["sha256"], "Matriz", claves["hecho-local"], "1", claves["aviso.1"]),
            (dicho["bien"], dicho["paquete"], dicho["carpeta"], dicho["archivo"], dicho["bytes"], dicho["sha256"], dicho["tienda"], dicho["hecho-local"],
                dicho["avisos"], dicho["aviso.1"]));
        Assert.Equal([0xEF, 0xBB, 0xBF], (await File.ReadAllBytesAsync(archivoDeSalida)).Take(3));
        // En la pantalla, tres renglones sin acentos (los lee Windows PowerShell 5.1): tampoco en las rutas, por si la carpeta temporal
        // de esta PC los lleva (C:\Users\José\…).
        var renglones = salida.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, renglones.Length);
        Assert.StartsWith("Se saco la base de «Matriz» para mudarla: ", renglones[0]);
        Assert.EndsWith(" productos, 2 ventas, 2 cajas.", renglones[0]);
        Assert.StartsWith($"El paquete de mudanza quedo en {SinAcentos(pc.Usb)}: {archivo} (", renglones[1]);
        Assert.Equal($"El servicio de esta PC ya no arranca mientras exista {SinAcentos(pc.Marca)}. Hay 1 aviso.", renglones[2]);
        Assert.All(renglones, r => Assert.DoesNotMatch("[áéíóúñÁÉÍÓÚÑ]", r));
        // Las IP son las de esta PC de verdad (aquí no las pone la prueba).
        Assert.Equal(string.Join(';', MudanzaDeLaBase.IpsDeEstaPc()), claves["ips"]);

        // Ni la clave de eleventa ni el código de Control Print, en ningún lado: ni en la pantalla ni en ningún archivo de la PC o de la
        // USB (la base y sus copias incluidas).
        var escrito = new List<(string Donde, string Texto)> { ("la pantalla", salida.ToString()), ("los errores", errores.ToString()) };
        foreach (var ruta in Directory.EnumerateFiles(pc.Carpeta, "*", SearchOption.AllDirectories))
            escrito.Add((ruta, Encoding.Latin1.GetString(await File.ReadAllBytesAsync(ruta))));
        Assert.Contains(escrito, e => e.Donde == pc.Marca);
        Assert.Contains(escrito, e => e.Donde == pc.Resumen);
        Assert.Contains(escrito, e => e.Donde == archivoDeSalida);
        Assert.Contains(escrito, e => e.Donde == Path.Combine(pc.Usb, archivo));
        Assert.All(escrito, e =>
        {
            Assert.False(e.Texto.Contains(ClaveDeEleventa, StringComparison.OrdinalIgnoreCase), $"La clave de eleventa quedó en {e.Donde}.");
            Assert.False(e.Texto.Contains(CodigoDeControlPrint, StringComparison.OrdinalIgnoreCase), $"El código de Control Print quedó en {e.Donde}.");
            // Las rutas de esa PC (el gbak) tampoco viajan.
            Assert.False(e.Texto.Contains("gbak", StringComparison.OrdinalIgnoreCase) && !e.Donde.EndsWith(".db", StringComparison.Ordinal), $"La ruta de gbak quedó en {e.Donde}.");
        });
    }

    /// <summary>
    /// La pantalla va sin acentos de principio a fin (la lee Windows PowerShell 5.1, que no siempre los entiende), también en las rutas:
    /// una carpeta con acento o con ñ se ve ahí sin ellos. Lo que cuenta —la carpeta como es— va en --salida, en la marca y en la USB.
    /// </summary>
    [Fact]
    public async Task En_la_pantalla_las_rutas_van_sin_acentos_y_en_la_salida_y_en_la_marca_como_son()
    {
        using var pc = Pc();
        var usb = Path.Combine(pc.Carpeta, "usb", "Papelería Ñandú", "mudanza");
        var archivoDeSalida = Path.Combine(pc.Carpeta, "sacar.txt");
        var (salida, errores) = (new StringWriter(), new StringWriter());

        var codigo = await MudanzaDeLaBase.CorrerAsync(Configuracion(pc), ["--carpeta", usb, "--salida", archivoDeSalida], salida, errores, pc.SegunLaPrueba);

        Assert.Equal("", errores.ToString());
        Assert.Equal(0, codigo);
        var dicho = ClavesEscritas.De(archivoDeSalida);
        Assert.Equal(usb, dicho["carpeta"]);
        Assert.Equal(usb, MudanzaDeLaBase.LeerMarca(pc.Marca)!.Carpeta);
        Assert.True(File.Exists(Path.Combine(usb, "mudanza.txt")));
        Assert.True(File.Exists(Path.Combine(usb, dicho["archivo"])));
        var renglones = salida.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, renglones.Length);
        Assert.StartsWith($"El paquete de mudanza quedo en {SinAcentos(usb)}: {dicho["archivo"]} (", renglones[1]);
        Assert.Contains(Path.Combine("usb", "Papeleria Nandu", "mudanza") + ": ", renglones[1]);
        Assert.All(renglones, r => Assert.DoesNotMatch("[áéíóúñÁÉÍÓÚÑ]", r));
    }

    /// <summary>
    /// En «ControlPrint:Servidor» va la IP o el nombre de la PC de Control Print. Si ahí hay otra cosa —el código de conexión puesto por
    /// error, que es un secreto— no viaja ni se repite en ningún lado (como hace la caja, que tampoco lo repite): el paquete dice que
    /// allá se usaba Control Print, sin su PC, y avisa que se ponga en la PC nueva.
    /// </summary>
    [Theory]
    [InlineData("ABCD-EFGH-JKLM-NPQR")]
    [InlineData("abcdefghjklmnpqr")]
    [InlineData("ABCD EFGH JKLM NPQR")]
    [InlineData("192.0.2.106:5199")]
    public async Task Lo_que_no_es_la_pc_de_Control_Print_no_viaja_por_si_era_su_codigo(string enElServidor)
    {
        using var pc = Pc();
        var archivoDeSalida = Path.Combine(pc.Carpeta, "sacar.txt");
        var (salida, errores) = (new StringWriter(), new StringWriter());

        var codigo = await MudanzaDeLaBase.CorrerAsync(Configuracion(pc, ("ControlPrint:Servidor", $" {enElServidor} ")),
            ["--carpeta", pc.Usb, "--salida", archivoDeSalida], salida, errores, pc.SegunLaPrueba);

        Assert.Equal("", errores.ToString());
        Assert.Equal(0, codigo);
        var claves = ClavesEscritas.De(pc.Resumen);
        Assert.Equal(("si", ""), (claves["controlprint"], claves["controlprint-servidor"]));
        const string aviso = "En esta PC, «ControlPrint:Servidor» no es la IP ni el nombre de la PC de Control Print (no se repite aquí, por si era el código de "
            + "conexión): no viaja en el paquete. Ponla en la PC nueva.";
        Assert.Equal(("2", aviso), (claves["avisos"], claves["aviso.2"]));
        var dicho = ClavesEscritas.De(archivoDeSalida);
        Assert.Equal(("2", aviso), (dicho["avisos"], dicho["aviso.2"]));
        // En ningún lado: ni en la pantalla ni en ningún archivo de la PC o de la USB.
        var escrito = new List<(string Donde, string Texto)> { ("la pantalla", salida.ToString()), ("los errores", errores.ToString()) };
        foreach (var ruta in Directory.EnumerateFiles(pc.Carpeta, "*", SearchOption.AllDirectories))
            escrito.Add((ruta, Encoding.Latin1.GetString(await File.ReadAllBytesAsync(ruta))));
        Assert.Contains(escrito, e => e.Donde == pc.Marca);
        Assert.Contains(escrito, e => e.Donde == pc.Resumen);
        Assert.Contains(escrito, e => e.Donde == archivoDeSalida);
        Assert.All(escrito, e => Assert.False(e.Texto.Contains(enElServidor, StringComparison.OrdinalIgnoreCase), $"Lo que había en «ControlPrint:Servidor» quedó en {e.Donde}."));
    }

    /// <summary>Una fuente de configuración que anota cada clave que se le pide: para ver que la clave de eleventa ni se lee.</summary>
    private sealed class ConfiguracionQueAnota(Dictionary<string, string?> datos)
        : MemoryConfigurationProvider(new MemoryConfigurationSource { InitialData = datos }), IConfigurationSource
    {
        public List<string> Leidas { get; } = [];

        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        public override bool TryGet(string key, out string? value)
        {
            lock (Leidas)
                Leidas.Add(key);
            return base.TryGet(key, out value);
        }
    }

    /// <summary>La mudanza no necesita la clave de eleventa: de la sección «Eleventa» se enlaza todo lo demás y esa ni se pide.</summary>
    [Fact]
    public void La_clave_de_eleventa_ni_se_lee_de_la_configuracion()
    {
        var fuente = new ConfiguracionQueAnota(new()
        {
            ["Eleventa:Activo"] = "true", ["Eleventa:Escribir"] = "true", ["Eleventa:Servidor"] = "192.0.2.106", ["Eleventa:Base"] = BaseDeEleventa,
            ["Eleventa:Clave"] = ClaveDeEleventa, ["Eleventa:Caja"] = "3", ["Eleventa:Cajero"] = "7", ["Eleventa:Cajas:B:Caja"] = "4",
            ["Eleventa:Cajas:B:Cajero"] = "9", ["Eleventa:SegundosAlCobrar"] = "2.5", ["Eleventa:RespaldoObligatorio"] = "false",
        });
        var configuracion = new ConfigurationBuilder().Add(fuente).Build();

        var eleventa = MudanzaDeLaBase.EleventaSinSuClave(configuracion);

        Assert.DoesNotContain(fuente.Leidas, clave => clave.Equals("Eleventa:Clave", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Eleventa:Base", fuente.Leidas);
        Assert.Null(eleventa.Clave);
        // Lo demás, igual que como lo enlaza el servicio.
        var delServicio = configuracion.GetSection("Eleventa").Get<OpcionesEleventa>()!;
        Assert.Equal(ClaveDeEleventa, delServicio.Clave);
        Assert.Equal((delServicio.Activo, delServicio.Escribir, delServicio.Servidor, delServicio.Base, delServicio.Caja, delServicio.Cajero, delServicio.SegundosAlCobrar,
                delServicio.RespaldoObligatorio, delServicio.Puerto, delServicio.Usuario),
            (eleventa.Activo, eleventa.Escribir, eleventa.Servidor, eleventa.Base, eleventa.Caja, eleventa.Cajero, eleventa.SegundosAlCobrar,
                eleventa.RespaldoObligatorio, eleventa.Puerto, eleventa.Usuario));
        Assert.Equal((4, (int?)9), (eleventa.Cajas["b"].Caja, eleventa.Cajas["b"].Cajero));
        Assert.Equal(delServicio.DeLaSerie("B"), eleventa.DeLaSerie("B"));
        // Sin la sección, las opciones de siempre.
        Assert.False(MudanzaDeLaBase.EleventaSinSuClave(new ConfigurationBuilder().Build()).Activo);
    }

    /// <summary>Si no se saca, --salida lo dice (<c>bien=no</c> y el porqué, para enseñarlo), el programa termina con 1 y tampoco ahí va la clave.</summary>
    [Fact]
    public async Task Si_no_se_saca_el_verbo_lo_dice_en_la_salida_y_termina_con_uno()
    {
        using var pc = Pc();
        pc.ElServicioContesta = true;
        var archivoDeSalida = Path.Combine(pc.Carpeta, "sacar.txt");
        var (salida, errores) = (new StringWriter(), new StringWriter());

        var codigo = await MudanzaDeLaBase.CorrerAsync(Configuracion(pc, ("Eleventa:Clave", ClaveDeEleventa), ("Eleventa:Caja", "tres")),
            ["--salida", archivoDeSalida, "--carpeta", pc.Usb], salida, errores, pc.SegunLaPrueba);

        // La configuración no se entiende (la caja de eleventa no es un número): se dice, sin sacar nada.
        Assert.Equal(1, codigo);
        var dicho = ClavesEscritas.De(archivoDeSalida);
        Assert.Equal(["bien", "error"], dicho.Nombres);
        Assert.Equal("no", dicho["bien"]);
        Assert.StartsWith("No se pudo sacar la base: ", dicho["error"]);
        Assert.Contains("Eleventa:Caja", dicho["error"]);
        Assert.DoesNotContain(ClaveDeEleventa, await File.ReadAllTextAsync(archivoDeSalida));
        Assert.DoesNotContain(ClaveDeEleventa, errores.ToString());
        Assert.Equal("", salida.ToString());
        SinMarcaNiRestos(pc);

        // Ya con la configuración bien, pero el servicio contesta.
        (salida, errores) = (new StringWriter(), new StringWriter());
        codigo = await MudanzaDeLaBase.CorrerAsync(Configuracion(pc, ("Eleventa:Clave", ClaveDeEleventa)), ["--salida", archivoDeSalida, "--carpeta", pc.Usb],
            salida, errores, pc.SegunLaPrueba);

        Assert.Equal(1, codigo);
        dicho = ClavesEscritas.De(archivoDeSalida);
        Assert.Equal(["bien", "error"], dicho.Nombres);
        Assert.Equal(("no", "El servicio de esta PC está contestando (http://localhost:5080): la base se saca con el servicio detenido; lo hace el instalador."),
            (dicho["bien"], dicho["error"]));
        Assert.Equal("No se saco la base: El servicio de esta PC esta contestando (http://localhost:5080): la base se saca con el servicio detenido; lo hace el "
            + "instalador." + Environment.NewLine, errores.ToString());
        Assert.Equal("", salida.ToString());
        SinMarcaNiRestos(pc);
    }

    /// <summary>Mal dada la orden: dice cómo se usa, termina con 1 y no toca nada (ni --salida).</summary>
    [Theory]
    [InlineData]
    [InlineData("--carpeta")]
    [InlineData("--salida", "salida.txt")]
    [InlineData("--carpeta", "", "--salida", "salida.txt")]
    [InlineData("--carpeta", "usb", "--base", "otra.db")]
    [InlineData("--carpeta", "usb", "--carpeta", "otra")]
    [InlineData("usb")]
    public async Task Mal_dada_la_orden_dice_como_se_usa_y_no_toca_nada(params string[] args)
    {
        using var pc = Pc();
        var (salida, errores) = (new StringWriter(), new StringWriter());

        var codigo = await MudanzaDeLaBase.CorrerAsync(Configuracion(pc), args, salida, errores, pc.SegunLaPrueba);

        Assert.Equal(1, codigo);
        Assert.Equal(MudanzaDeLaBase.Uso + Environment.NewLine, errores.ToString());
        Assert.Equal("", salida.ToString());
        Assert.Empty(pc.PuertosPreguntados);
        Assert.Empty(pc.RespaldosHechos());
        Assert.False(File.Exists("salida.txt"));
        Assert.False(Directory.Exists("usb"));
        SinMarcaNiRestos(pc);
    }

    /// <summary>
    /// Antes de empezar, --salida ya dice que no quedó: si cierran el programa a la mitad, el instalador no se queda sin saber. Y si ni
    /// eso se puede escribir, no se empieza.
    /// </summary>
    [Fact]
    public async Task La_salida_dice_que_no_quedo_desde_antes_de_empezar_y_si_no_se_puede_escribir_no_se_empieza()
    {
        using var pc = Pc();
        var archivoDeSalida = Path.Combine(pc.Carpeta, "sacar.txt");
        string? alPreguntar = null;
        Task<bool> MirandoLaSalida(int puerto, CancellationToken ct)
        {
            alPreguntar = File.ReadAllText(archivoDeSalida);
            return Task.FromResult(false);
        }

        Assert.Equal(0, await MudanzaDeLaBase.CorrerAsync(Configuracion(pc), ["--carpeta", pc.Usb, "--salida", archivoDeSalida], new StringWriter(), new StringWriter(),
            MirandoLaSalida));

        Assert.Contains("bien=no\r\n", alPreguntar);
        Assert.Contains($"error={MudanzaDeLaBase.SeCerroAntesDeTerminar}\r\n", alPreguntar);
        Assert.Equal("si", ClavesEscritas.De(archivoDeSalida)["bien"]);

        // En otra PC igual, la salida no se puede escribir: ni se pregunta por el servicio.
        using var otra = Pc();
        var (salida, errores) = (new StringWriter(), new StringWriter());
        QueNoSeDejeEscribir(Path.Combine(otra.Carpeta, "sacar.txt"));
        Assert.Equal(1, await MudanzaDeLaBase.CorrerAsync(Configuracion(otra), ["--carpeta", otra.Usb, "--salida", Path.Combine(otra.Carpeta, "sacar.txt")], salida,
            errores, otra.SegunLaPrueba));
        Assert.StartsWith("No se saco la base: no se puede escribir el archivo de salida (", errores.ToString());
        Assert.Empty(otra.PuertosPreguntados);
        SinMarcaNiRestos(otra);
    }

    /// <summary>
    /// El archivo de salida nunca contradice al código con que termina el programa. Si quedó sacada (0) y al final el archivo ya no se
    /// deja escribir, el de antes de empezar («no quedó») no se queda: instalar-pc.ps1 lo leería como que no se sacó, con el paquete
    /// bueno en la USB y esta PC ya sin atender. Sin archivo, decide por el 0 y la marca; y los avisos salen en la pantalla.
    /// </summary>
    [Fact]
    public async Task Si_quedo_sacada_y_la_salida_ya_no_se_deja_escribir_no_se_queda_diciendo_que_no_quedo()
    {
        using var pc = Pc();
        var archivoDeSalida = Path.Combine(pc.Carpeta, "sacar.txt");
        string? alPreguntar = null;
        // Al empezar se pudo escribir; a media corrida deja de poderse (donde va su temporal hay una carpeta).
        Task<bool> YLaSalidaYaNoSeDeja(int puerto, CancellationToken ct)
        {
            alPreguntar = File.ReadAllText(archivoDeSalida);
            QueNoSeDejeEscribir(archivoDeSalida);
            return Task.FromResult(pc.ElServicioContesta);
        }
        var (salida, errores) = (new StringWriter(), new StringWriter());

        var codigo = await MudanzaDeLaBase.CorrerAsync(Configuracion(pc), ["--carpeta", pc.Usb, "--salida", archivoDeSalida], salida, errores, YLaSalidaYaNoSeDeja);

        Assert.Contains("bien=no\r\n", alPreguntar);
        // Quedó sacada: el paquete completo, la marca y el 0.
        Assert.Equal(0, codigo);
        Assert.True(File.Exists(pc.Marca));
        Assert.Equal("1", ClavesEscritas.De(pc.Resumen)["avisos"]);
        Assert.False(File.Exists(archivoDeSalida), $"Con el programa terminando en 0, {archivoDeSalida} sigue diciendo: {(File.Exists(archivoDeSalida) ? File.ReadAllText(archivoDeSalida) : "")}");
        Assert.StartsWith($"No se pudo escribir el archivo de salida ({SinAcentos(archivoDeSalida)}): ", errores.ToString());
        var renglones = salida.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, renglones.Length);
        Assert.StartsWith("Aviso: La caja «Caja 1» (serie A) tiene un turno abierto: se queda abierto", renglones[3]);

        // Si no se sacó (1), el archivo de antes sigue diciendo que no quedó: eso no contradice al código.
        using var otra = Pc();
        otra.ElServicioContesta = true;
        archivoDeSalida = Path.Combine(otra.Carpeta, "sacar.txt");
        Task<bool> TampocoSeDeja(int puerto, CancellationToken ct)
        {
            QueNoSeDejeEscribir(archivoDeSalida);
            return Task.FromResult(true);
        }
        Assert.Equal(1, await MudanzaDeLaBase.CorrerAsync(Configuracion(otra), ["--carpeta", otra.Usb, "--salida", archivoDeSalida], new StringWriter(),
            new StringWriter(), TampocoSeDeja));
        Assert.Equal("no", ClavesEscritas.De(archivoDeSalida)["bien"]);
        SinMarcaNiRestos(otra);
    }

    [Theory]
    [InlineData(null, 5080)]
    [InlineData("", 5080)]
    [InlineData("http://*:5080", 5080)]
    [InlineData("http://+:6001;https://*:6002", 6001)]
    [InlineData("  http://localhost:7000  ", 7000)]
    [InlineData("esto no es una dirección;http://0.0.0.0:7100", 7100)]
    public void El_puerto_del_servicio_sale_de_sus_direcciones(string? urls, int puerto) => Assert.Equal(puerto, MudanzaDeLaBase.PuertoDe(urls));

    // ── Lo de esta PC, de verdad (sin lo que ponen las demás pruebas) ────────

    /// <summary>
    /// Un servicio de verdad (Kestrel) en un puerto libre de esta PC, atendiendo solo por esa dirección: contesta <c>/api/estado</c> y
    /// anota por dónde le llegó cada pregunta.
    /// </summary>
    private static async Task<(WebApplication Servicio, int Puerto, ConcurrentQueue<string> Pedidas)> ServicioDeVerdadEnAsync(IPAddress direccion)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ContentRootPath = AppContext.BaseDirectory,
            // Sin vigilar archivos de configuración: cada servidor de prueba gastaría un vigilante del sistema.
            Args = ["--hostBuilder:reloadConfigOnChange=false"],
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(direccion, 0));
        var servicio = builder.Build();
        var pedidas = new ConcurrentQueue<string>();
        servicio.MapGet("/api/estado", (HttpContext contexto) =>
        {
            pedidas.Enqueue($"{contexto.Request.Path} por {contexto.Connection.LocalIpAddress}");
            return Results.Json(new { tiendaNueva = false });
        });
        try
        {
            await servicio.StartAsync();
        }
        catch
        {
            await servicio.DisposeAsync();
            throw;
        }
        var puerto = new Uri(servicio.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        return (servicio, puerto, pedidas);
    }

    /// <summary>
    /// La pregunta de verdad al servicio de esta PC (en las demás pruebas la contesta la prueba): lo encuentra atienda solo por IPv4
    /// (<c>Urls</c> con 0.0.0.0 o con 127.0.0.1) o solo por IPv6, y con el puerto cerrado dice que no contesta sin tardar más que su
    /// espera. Si dijera siempre que no, la base se sacaría con el servicio atendiendo.
    /// </summary>
    [Fact]
    public async Task La_pregunta_de_verdad_encuentra_al_servicio_de_esta_pc_por_ipv4_o_por_ipv6_y_con_el_puerto_cerrado_dice_que_no()
    {
        var (soloPorIpv4, puerto, pedidas) = await ServicioDeVerdadEnAsync(IPAddress.Loopback);
        await using (soloPorIpv4)
        {
            Assert.True(await MudanzaDeLaBase.ElServicioContestaAsync(puerto));
            Assert.Equal(["/api/estado por 127.0.0.1"], pedidas);
        }
        // Ya detenido, su puerto quedó cerrado: no contesta, y se sabe pronto (no a los 21 s de un intento de conexión, ni a los 100 de
        // HttpClient).
        var reloj = Stopwatch.StartNew();
        Assert.False(await MudanzaDeLaBase.ElServicioContestaAsync(puerto));
        Assert.True(reloj.Elapsed < MudanzaDeLaBase.EsperaDelServicio + TimeSpan.FromSeconds(8), $"Tardó {reloj.Elapsed} en decir que no contesta.");

        (WebApplication Servicio, int Puerto, ConcurrentQueue<string> Pedidas) porIpv6;
        try
        {
            porIpv6 = await ServicioDeVerdadEnAsync(IPAddress.IPv6Loopback);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            // Esta PC no tiene IPv6 (un contenedor): no hay nada que atienda por ahí.
            return;
        }
        await using (porIpv6.Servicio)
        {
            Assert.True(await MudanzaDeLaBase.ElServicioContestaAsync(porIpv6.Puerto));
            Assert.Equal(["/api/estado por ::1"], porIpv6.Pedidas);
        }
    }

    /// <summary>
    /// Las IP que van en el paquete (con ellas la PC nueva nota si esta volvió a trabajar) son IPv4 de esta PC, ninguna de loopback y
    /// cada una una sola vez. El verbo las escribe tal cual (<see cref="El_verbo_saca_el_paquete_con_la_configuracion_de_la_pc_y_nunca_escribe_la_clave_de_eleventa"/>).
    /// </summary>
    [Fact]
    public void Las_ip_de_esta_pc_son_ipv4_y_ninguna_es_de_loopback()
    {
        var ips = MudanzaDeLaBase.IpsDeEstaPc();

        Assert.All(ips, ip =>
        {
            Assert.True(IPAddress.TryParse(ip, out var direccion), $"«{ip}» no es una IP.");
            Assert.Equal(AddressFamily.InterNetwork, direccion!.AddressFamily);
            Assert.False(IPAddress.IsLoopback(direccion), $"{ip} es de loopback.");
            // Escrita con sus cuatro números, como la lee el ayudante del instalador.
            Assert.Matches(@"^[0-9]{1,3}(\.[0-9]{1,3}){3}$", ip);
        });
        Assert.Equal(ips.Distinct(), ips);
    }

    /// <summary>
    /// Solo en Windows, con lo de la PC de verdad (en las demás pruebas «Archivos de programa» es una carpeta de la prueba): la carpeta
    /// del paquete no va en Archivos de programa de esta PC, ni en la carpeta de datos aunque se escriba con otras mayúsculas.
    /// </summary>
    [Fact]
    public async Task En_Windows_la_carpeta_del_paquete_no_va_en_Archivos_de_programa_ni_en_los_datos_escritos_con_otras_mayusculas()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var pc = Pc();
        var mudanza = new MudanzaDeLaBase(pc.Cadena, pc.Respaldos, new OpcionesEleventa()) { ElServicioContesta = pc.SegunLaPrueba, Reloj = pc.Reloj };
        var enElPrograma = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), $"PapeleriaPos-prueba-{Guid.NewGuid():N}", "mudanza");
        var enLosDatos = Path.Combine(pc.Datos.ToUpperInvariant(), "Mudanza");

        var errores = new[]
        {
            (await mudanza.SacarAsync(enElPrograma)).Error, (await mudanza.SacarAsync(enElPrograma.ToUpperInvariant())).Error,
            (await mudanza.SacarAsync(enLosDatos)).Error,
        };

        Assert.StartsWith($"La carpeta del paquete ({enElPrograma}) está dentro de Archivos de programa (", errores[0]);
        Assert.Contains(") está dentro de Archivos de programa (", errores[1]);
        Assert.StartsWith($"La carpeta del paquete ({enLosDatos}) está dentro de la carpeta de datos de esta PC (", errores[2]);
        Assert.False(Directory.Exists(Path.GetDirectoryName(enElPrograma)));
        Assert.False(Directory.Exists(Path.Combine(pc.Datos, "Mudanza")));
        Assert.Empty(pc.RespaldosHechos());
        SinMarcaNiRestos(pc);
    }

    // ── El programa de verdad, en otro proceso ───────────────────────────────

    /// <summary>Lo que dejó una corrida del programa del servicio: su código de salida y lo que escribió.</summary>
    private sealed record Corrida(int Codigo, string Salida, string Errores)
    {
        public string Todo => $"Terminó con {Codigo}.\n{Salida}\n{Errores}";
    }

    /// <summary>El <c>dotnet</c> con que corren estas pruebas: con ese mismo se corre el programa del servicio.</summary>
    private static string Dotnet() =>
        Environment.ProcessPath is { } proceso && Path.GetFileNameWithoutExtension(proceso).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? proceso
        : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } anfitrion && File.Exists(anfitrion) ? anfitrion
        : "dotnet";

    /// <summary>Un puerto de esta PC en el que ahora no atiende nadie.</summary>
    private static int PuertoLibre()
    {
        var escucha = new TcpListener(IPAddress.Loopback, 0);
        escucha.Start();
        try
        {
            return ((IPEndPoint)escucha.LocalEndpoint).Port;
        }
        finally
        {
            escucha.Stop();
        }
    }

    /// <summary>
    /// Corre el programa del servicio ya compilado (el que queda junto a las pruebas) en otro proceso, con esa orden, como lo corren
    /// el instalador y Windows: su configuración es la de esa PC (por variables de entorno: su base, su puerto y ningún servicio.json
    /// de la PC donde corren las pruebas). Por si llegara a arrancar el servicio entero —es justo lo que no debe pasar—, sin nada hacia
    /// afuera y solo para esta PC; y si no termina, se le cierra y la prueba falla.
    /// </summary>
    private static async Task<Corrida> ProgramaDelServicioAsync(PcQueSeMuda pc, int puerto, string[] orden, params (string Variable, string Valor)[] ademas)
    {
        var programa = Path.Combine(AppContext.BaseDirectory, "PapeleriaPos.Servicio.dll");
        Assert.True(File.Exists(programa), $"No está el programa del servicio junto a las pruebas ({programa}).");
        var sinConfiguracion = Path.Combine(pc.Carpeta, "servicio-de-la-prueba.json");
        await File.WriteAllTextAsync(sinConfiguracion, "{}");
        var info = new ProcessStartInfo(Dotnet())
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = pc.Carpeta,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add(programa);
        foreach (var argumento in orden)
            info.ArgumentList.Add(argumento);
        foreach (var (variable, valor) in new[]
                 {
                     ("ASPNETCORE_ENVIRONMENT", "Production"), ("DOTNET_ENVIRONMENT", "Production"), ("ConnectionStrings__Pos", $"Data Source={pc.Ruta}"),
                     ("ConfiguracionDeLaPc", sinConfiguracion), ("Urls", $"http://127.0.0.1:{puerto.ToString(CultureInfo.InvariantCulture)}"),
                     ("Telegram__Activo", "false"), ("Descubrimiento__Activo", "false"), ("Red__Vigilar", "false"), ("Respaldos__Activo", "false"),
                     ("Impresora__Vigilar", "false"), ("Cobro__Calentar", "false"), ("Eleventa__Activo", "false"), ("Eleventa__Escribir", "false"),
                 }.Concat(ademas))
            info.Environment[variable] = valor;
        using var proceso = Process.Start(info)!;
        proceso.StandardInput.Close();
        var (salida, errores) = (proceso.StandardOutput.ReadToEndAsync(), proceso.StandardError.ReadToEndAsync());
        using var limite = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await proceso.WaitForExitAsync(limite.Token);
        }
        catch (OperationCanceledException)
        {
            proceso.Kill(entireProcessTree: true);
            Assert.Fail($"El programa del servicio no terminó con «{string.Join(' ', orden)}»: ¿arrancó el servicio entero?");
        }
        return new Corrida(proceso.ExitCode, await salida, await errores);
    }

    /// <summary>
    /// Lo que depende del orden de Program.cs, con el programa de verdad y no con sus piezas sueltas: <c>mudanza-sacar</c> es una orden
    /// suya (saca el paquete y termina, sin arrancar el servicio); con la marca, el programa sin orden —como lo arranca Windows— termina
    /// con error enseguida, lo deja dicho en su registro y no toca la base, ni hace una vacía si ya la apartaron; y <c>pin-usuario</c>
    /// sigue sirviendo con la marca (va antes que ella).
    /// </summary>
    [Fact]
    public async Task El_programa_de_verdad_saca_con_su_orden_no_arranca_con_la_marca_y_pin_usuario_sigue_sirviendo()
    {
        using var pc = Pc();
        var puerto = PuertoLibre();
        var archivoDeSalida = Path.Combine(pc.Carpeta, "sacar.txt");

        // 1. PapeleriaPos.Servicio.exe mudanza-sacar --carpeta … --salida …
        var sacar = await ProgramaDelServicioAsync(pc, puerto, [MudanzaDeLaBase.Verbo, "--carpeta", pc.Usb, "--salida", archivoDeSalida]);

        Assert.True(sacar.Codigo == 0, sacar.Todo);
        var dicho = ClavesEscritas.De(archivoDeSalida);
        Assert.Equal(("si", pc.Usb, "Matriz"), (dicho["bien"], dicho["carpeta"], dicho["tienda"]));
        var claves = ClavesEscritas.De(pc.Resumen);
        Assert.Equal((dicho["paquete"], dicho["archivo"], puerto.ToString(CultureInfo.InvariantCulture), Environment.MachineName),
            (claves["paquete"], claves["archivo"], claves["puerto"], claves["equipo"]));
        Assert.Equal(["mudanza.txt", dicho["archivo"]], pc.EnLaUsb());
        Assert.Equal(dicho["sha256"], Sha256De(Path.Combine(pc.Usb, dicho["archivo"])));
        Assert.Equal(dicho["paquete"], MudanzaDeLaBase.LeerMarca(pc.Marca)!.Paquete);
        Assert.Contains("Se saco la base de ", sacar.Salida);
        Assert.Contains("El servicio de esta PC ya no arranca mientras exista ", sacar.Salida);

        // 2. Sin orden y con la marca, como lo arranca Windows aunque alguien lo habilite: termina con error, y la base ni se abre.
        var (baseAntes, junto) = (Sha256De(pc.Ruta), En(pc.Datos));
        var conLaMarca = await ProgramaDelServicioAsync(pc, puerto, []);

        Assert.True(conLaMarca.Codigo != 0, conLaMarca.Todo);
        Assert.Contains("para mudarla a otra PC", conLaMarca.Todo);
        Assert.Contains("pos.db.mudada", conLaMarca.Todo);
        Assert.Equal(baseAntes, Sha256De(pc.Ruta));
        // Junto a la base solo aparece su registro del día, donde quedó dicho: ni la marca de «en uso», ni el -wal.
        Assert.Equal(junto.Append("registros").Order(StringComparer.Ordinal), En(pc.Datos));
        var delDia = string.Concat(Directory.EnumerateFiles(Path.Combine(pc.Datos, "registros"), "servicio-*.log").Select(File.ReadAllText));
        Assert.Contains("  Grave  MudanzaDeLaBase  La base de esta PC se sacó para mudarla a otra PC el ", delDia);

        // 3. pin-usuario con la marca: contesta lo suyo (le cambia el PIN al dueño), no lo de la marca.
        var pin = await ProgramaDelServicioAsync(pc, puerto, [PinUsuario.Verbo, "--usuario", "dueno"], (PinUsuario.VariablePin, "2468"));

        Assert.True(pin.Codigo == 0, pin.Todo);
        Assert.Contains("Listo: ", pin.Salida);
        Assert.DoesNotContain("para mudarla", pin.Todo);
        await using (var db = pc.Base())
            Assert.Equal(ResultadoPin.Correcto, (await db.Usuarios.SingleAsync(u => u.NombreUsuario == "dueno")).VerificarPin("2468", DateTime.UtcNow));

        // 4. «Terminar la mudanza» aparta la base y deja la marca para el final: si se corta ahí, el programa tampoco arranca, y no hace
        // una base vacía en su lugar.
        File.Move(pc.Ruta, Path.Combine(pc.Carpeta, "pos.db.apartada"));
        junto = En(pc.Datos);
        var sinLaBase = await ProgramaDelServicioAsync(pc, puerto, []);

        Assert.True(sinLaBase.Codigo != 0, sinLaBase.Todo);
        Assert.Contains("para mudarla a otra PC", sinLaBase.Todo);
        Assert.False(File.Exists(pc.Ruta));
        Assert.Equal(junto, En(pc.Datos));
    }
}
