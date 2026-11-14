using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Pruebas.Servicio;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// El paquete de mudanza DE VERDAD, el que escribe el servicio en la PC de donde sale la base (<see cref="MudanzaDeLaBase"/>, el verbo
/// <c>mudanza-sacar</c>), leído por el ayudante del instalador como lo lee en la PC a donde se trae: <c>mudanza-ver</c> (lo que enseña el
/// asistente, con cada clave que el .iss lee del paquete), <c>mudanza-revisar</c> (el SHA-256 de la base, a quién le pregunta, la tienda
/// y las claves que el .iss lee de su salida) y <c>escribir --mudanza</c> (el puente que queda, leído como lo lee el servicio al
/// arrancar); y la marca <c>pos.db.mudada</c> que deja junto a la base, como la lee <c>leer</c>. El que escribe y el que lee están
/// escritos aparte (el ayudante no usa los proyectos del servicio): si uno cambia el nombre, la forma o el orden de algo, aquí falla.
/// </summary>
public sealed class MudanzaConElPaqueteDelServicioTests(BaseParaMudar semilla) : IClassFixture<BaseParaMudar>, IDisposable
{
    private const string BaseDeEleventa = PaqueteDePrueba.BaseDeEleventa;

    /// <summary>De documentación (TEST-NET): la principal, donde viven eleventa y Control Print; nunca se le habla de verdad.</summary>
    private const string IpDeLaPrincipal = "192.0.2.106";

    /// <summary>El puerto del servicio de la PC de donde sale la base: el ayudante le pregunta ahí, no en el de siempre.</summary>
    private const int PuertoDeAlla = 5090;

    private readonly CarpetaDePrueba _carpeta = new();

    public void Dispose() => _carpeta.Dispose();

    private string Salida => Path.Combine(_carpeta.Ruta, "salida.txt");

    /// <summary>
    /// El puente de la caja 2 con todo lo que puede viajar distinto de lo de siempre: escribiendo, con otro puerto y otro usuario de
    /// Firebird, su caja y su cajero de omisión, una caja en Cajas con su propio cajero (con la serie en minúsculas, como la deja quien la
    /// escribe a mano) y cada ajuste de la lista cerrada; y lo que es de esa PC, que no viaja (el gbak, la carpeta de respaldos, la clave).
    /// </summary>
    private static OpcionesEleventa ComoEnLaCaja2()
    {
        var opciones = new OpcionesEleventa
        {
            Activo = true, Escribir = true, Servidor = IpDeLaPrincipal, Puerto = 3051, Usuario = "ELEVENTA", Base = BaseDeEleventa, Caja = 3, Cajero = 7,
            ProductoImpresiones = "IMPRE", ProductoCopias = "COPIA", RespaldoObligatorio = false, SegundosAlCobrar = 2.5, SegundosEntreEnvios = 45, Minutos = 10,
            SegundosMaximos = 5, DiasDeRespaldos = 7,
            Gbak = @"C:\Firebird\bin\gbak.exe", CarpetaRespaldos = @"D:\respaldos-eleventa", Clave = "masterkey",
        };
        opciones.Cajas["b"] = new CajaDelPuente { Caja = 4, Cajero = 9 };
        return opciones;
    }

    /// <summary>
    /// La caja 2 del sábado, en la base de verdad: su «Caja 1» (serie A, con su turno abierto y dada de alta en esa PC), otra caja que ya
    /// vendió (serie B, con un nombre con acentos y eñe), un celular dado de alta desde otro equipo que nunca vendió, una venta que no ha
    /// llegado a eleventa, otra por revisar, y la bandeja anotada con la base de eleventa. Saca el paquete con el servicio de verdad.
    /// </summary>
    private async Task<(ClavesEscritas Claves, OpcionesEleventa Alla)> SacarAsync(PcQueSeMuda pc)
    {
        await using (var db = pc.Base())
        {
            var cajaUno = await db.Cajas.SingleAsync(c => c.Serie == "A");
            cajaUno.DarDeAlta(PcQueSeMuda.Equipo, DateTime.UtcNow);
            var mostrador = new Caja { SucursalId = cajaUno.SucursalId, Nombre = "Mostrador Ñandú", Serie = "B" };
            var celular = new Caja { SucursalId = cajaUno.SucursalId, Nombre = "Celular", Serie = "C" };
            celular.DarDeAlta("Redmi de Mario", DateTime.UtcNow);
            db.Cajas.AddRange(mostrador, celular);
            var cajero = await db.Usuarios.Where(u => u.NombreUsuario == "cajero").Select(u => u.Id).SingleAsync();
            var ayer = DateTime.UtcNow.AddDays(-1);
            db.Turnos.Add(new Turno { SucursalId = cajaUno.SucursalId, CajaId = mostrador.Id, CajeroId = cajero, AbiertoUtc = ayer, CerradoUtc = ayer.AddHours(8) });
            db.EnviosAEleventa.AddRange(
                new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = semilla.Ventas[0], CreadoUtc = DateTime.UtcNow },
                new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = semilla.Ventas[1], CreadoUtc = DateTime.UtcNow, Estado = EstadoEnvioEleventa.PorRevisar });
            db.Set<BaseDeLaBandeja>().Add(new BaseDeLaBandeja { Ruta = BaseDeEleventa.ToUpperInvariant() });
            await db.SaveChangesAsync();
        }
        var alla = ComoEnLaCaja2();

        var resultado = await pc.Mudanza(alla, puerto: PuertoDeAlla, controlPrint: IpDeLaPrincipal).SacarAsync(pc.Usb);

        Assert.Null(resultado.Error);
        return (ClavesEscritas.De(pc.Resumen), alla);
    }

    /// <summary>La PC a donde se trae la base: la principal, sin el servicio todavía, donde viven eleventa y Control Print.</summary>
    private PcDePrueba LaPrincipal()
    {
        var principal = new PcDePrueba(Path.Combine(_carpeta.Ruta, "principal")) { NombreDeLaPc = "PRINCIPAL" };
        principal.IpsDeLaTienda.Clear();
        principal.IpsDeLaTienda.Add(IpDeLaPrincipal);
        return principal;
    }

    private async Task<ArchivoDeClaves> CorrerAsync(IEquipo pc, HttpMessageHandler red, params string[] argumentos)
    {
        var codigo = await Ayudante.CorrerAsync([.. argumentos, "--salida", Salida], pc, red, RespuestasDePrueba.SinEsperar);
        var dice = ArchivoDeClaves.Leer(Salida);
        Assert.True(codigo == 0, $"{argumentos[0]} terminó con {codigo}: {dice["error"]}");
        return dice;
    }

    /// <summary>Lo de [Code] del asistente, sin los comentarios { … }, como lo lee ContratoDelInstaladorTests.</summary>
    private static string CodigoDelAsistente()
    {
        var asistente = File.ReadAllText(Path.Combine(ServicioJsonTests.RaizDelRepositorio(), "instalacion", "papeleria-pos.iss"));
        return Regex.Replace(asistente[asistente.IndexOf("[Code]", StringComparison.Ordinal)..], @"\{[^}]*\}", "");
    }

    private static List<string> Claves(string texto, string patron) => Regex.Matches(texto, patron).Select(m => m.Groups[1].Value).Distinct().ToList();

    private static void Faltan(IEnumerable<string> faltan)
    {
        var lista = faltan.ToList();
        Assert.True(lista.Count == 0, $"Faltan: {string.Join(", ", lista)}");
    }

    /// <summary>
    /// De punta a punta, del lado del ayudante: lo que escribe el servicio es lo que el asistente enseña y prellena, la revisión de antes
    /// de traerlo pasa con esas respuestas, y el puente queda en la principal como estaba en la caja 2: cada caja que ya vendió escribe en
    /// eleventa como la misma caja de allá, con los mismos ajustes, leído por el servicio de verdad.
    /// </summary>
    [Fact]
    public async Task Lo_que_saca_el_servicio_lo_lee_el_ayudante_y_el_puente_llega_como_estaba()
    {
        using var pc = new PcQueSeMuda(semilla.Archivo);
        var (claves, alla) = await SacarAsync(pc);
        var principal = LaPrincipal();
        // Lo que el servicio dice que escribe, para no probar contra un paquete al que le falte algo.
        Assert.Equal("A=3;B=4/9", claves["eleventa-cajas"]);
        Assert.Equal("ProductoImpresiones=IMPRE;ProductoCopias=COPIA;RespaldoObligatorio=false;SegundosAlCobrar=2.5;SegundosEntreEnvios=45;Minutos=10;"
            + "SegundosMaximos=5;DiasDeRespaldos=7", claves["eleventa-ajustes"]);

        // ── mudanza-ver: la página «La base de la tienda» ──
        var visto = await CorrerAsync(principal, RedDePrueba.SinNadie(), "mudanza-ver", "--paquete", pc.Usb, "--version", claves["version"]);

        Assert.Equal("bien", visto["paquete"]);
        Assert.Equal("", visto["error"]);
        Assert.Equal(claves["paquete"], visto["paquete-id"]);
        // Cada clave que escribió el servicio, repetida tal cual (su identificador, como «paquete-id»): ninguna se pierde ni cambia.
        foreach (var (clave, valor) in claves.EnOrden)
            Assert.True(visto[clave == "paquete" ? "paquete-id" : clave] == valor, $"«{clave}»: el servicio escribió «{valor}» y mudanza-ver dice «{visto[clave]}»");
        // Lo que el asistente lee del paquete (Valor(Paquete, …)), también lo de cada caja y cada aviso: todo está en lo que dice.
        var codigo = CodigoDelAsistente();
        var dice = visto.Datos.Select(d => d.Clave).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var delPaquete = Claves(codigo, @"Valor\(Paquete, '([a-z0-9.-]+)'\)");
        Assert.True(delPaquete.Count >= 10, string.Join(", ", delPaquete));
        Faltan(delPaquete.Where(c => !dice.Contains(c)));
        var deCadaCaja = Claves(codigo, @"Valor\(Paquete, 'caja\.' \+ IntToStr\(I\) \+ '\.([a-z-]+)'\)");
        Assert.NotEmpty(deCadaCaja);
        Assert.Equal("3", claves["cajas"]);
        Faltan(Enumerable.Range(1, 3).SelectMany(n => deCadaCaja.Select(dato => $"caja.{n}.{dato}")).Where(c => !dice.Contains(c)));
        Assert.Contains("Valor(Paquete, 'aviso.' + IntToStr(I))", codigo);
        var avisos = int.Parse(claves["avisos"], CultureInfo.InvariantCulture);
        Assert.True(avisos >= 3, claves["avisos"]);
        Faltan(Enumerable.Range(1, avisos).Select(n => $"aviso.{n}").Where(c => !dice.Contains(c)));
        // Lo que propone para esta PC: eleventa y Control Print vivían en la principal, que es esta.
        Assert.Equal("127.0.0.1", visto["propone-eleventa-servidor"]);
        Assert.Equal("127.0.0.1", visto["propone-controlprint-servidor"]);
        Assert.StartsWith($"La base de {PcQueSeMuda.Equipo}, sacada el 2026-11-14 08:15. Tienda: Matriz.", visto["texto"]);
        Assert.Contains("Cajas: Caja 1 (serie A), Celular (serie C), Mostrador Ñandú (serie B).", visto["texto"]);
        Assert.EndsWith($"El puente llega ESCRIBIENDO en {BaseDeEleventa}: lo que cobre «Caja 1» (serie A) entra a eleventa como la caja 3; lo que cobre "
            + "«Mostrador Ñandú» (serie B) entra a eleventa como la caja 4.", visto["texto"]);

        // ── mudanza-revisar: antes de cambiar nada, con lo que el asistente prellena del paquete ──
        var respuestas = Path.Combine(_carpeta.Ruta, "respuestas.txt");
        var gbak = RespuestasDePrueba.Gbak(_carpeta.Ruta);
        RespuestasDePrueba.ParaTraer(pc.Usb, gbak, ("eleventa-servidor", visto["propone-eleventa-servidor"]!), ("eleventa-base", visto["eleventa-base"]!),
            ("eleventa-cajero", visto["eleventa-cajero"]!), ("controlprint-servidor", visto["propone-controlprint-servidor"]!)).Guardar(respuestas);
        var red = RedDePrueba.SinNadie();

        var revisado = await CorrerAsync(principal, red, "mudanza-revisar", "--paquete", pc.Usb, "--respuestas", respuestas, "--version", claves["version"]);

        Assert.Equal(
            [
                ("bien", "si"), ("ya-estaba", "no"), ("aparta-base", "no"), ("archivo", claves["archivo"]), ("equipo", PcQueSeMuda.Equipo),
                ("paquete", claves["paquete"]), ("paquete-id", claves["paquete"]), ("tienda", "Matriz"), ("error", ""), ("clave", ""),
            ],
            revisado.Datos);
        // Le preguntó a la PC de donde salió si volvió a trabajar: en sus IP, en el puerto de su servicio (lo normal es que no conteste).
        Assert.Equal(PcQueSeMuda.Ips.Select(ip => $"{ip}:{PuertoDeAlla}").Order(), red.Preguntadas.Order());
        // Lo que el asistente lee de esa salida (RevisarElPaquete).
        var revisar = codigo[codigo.IndexOf("function RevisarElPaquete", StringComparison.Ordinal)..];
        revisar = revisar[..revisar.IndexOf("\nend;", StringComparison.Ordinal)];
        var deRevisar = Claves(revisar, @"Valor\(Salida, '([a-z-]+)'\)");
        Assert.Contains("ya-estaba", deRevisar);
        Faltan(deRevisar.Where(c => revisado[c] is null));

        // ── escribir --mudanza: sobre la plantilla que deja instalar-servicio.ps1, ya con la base puesta ──
        var ruta = Path.Combine(principal.Datos, ServicioJson.Archivo);
        Directory.CreateDirectory(principal.Datos);
        File.Copy(Path.Combine(ServicioJsonTests.RaizDelRepositorio(), "instalacion", "plantilla-servicio.json"), ruta);

        var escrito = await CorrerAsync(principal, RedDePrueba.SinNadie(), "escribir", "--respuestas", respuestas, "--mudanza", pc.Usb);

        Assert.Equal("", escrito["error"]);
        Assert.Equal("cambio", escrito["servicio-json"]);
        Assert.DoesNotContain("masterkey", File.ReadAllText(ruta));
        // Como lo lee el servicio al arrancar (Program.cs): servicio.json en su configuración, y de ahí la sección Eleventa.
        var configuracion = new ConfigurationManager();
        ConfiguracionDeLaPc.Agregar(configuracion, ruta);
        var aqui = configuracion.GetSection("Eleventa").Get<OpcionesEleventa>()!;
        Assert.Equal((alla.Activo, alla.Escribir, alla.Puerto, alla.Usuario, alla.Base, alla.Cajero),
            (aqui.Activo, aqui.Escribir, aqui.Puerto, aqui.Usuario, aqui.Base, aqui.Cajero));
        Assert.Equal((alla.ProductoImpresiones, alla.ProductoCopias, alla.RespaldoObligatorio, alla.SegundosAlCobrar, alla.SegundosEntreEnvios, alla.Minutos,
                alla.SegundosMaximos, alla.DiasDeRespaldos),
            (aqui.ProductoImpresiones, aqui.ProductoCopias, aqui.RespaldoObligatorio, aqui.SegundosAlCobrar, aqui.SegundosEntreEnvios, aqui.Minutos,
                aqui.SegundosMaximos, aqui.DiasDeRespaldos));
        // Cada caja que ya vendió escribe en eleventa como la misma caja de allá, con el mismo cajero.
        foreach (var serie in new[] { "A", "B" })
            Assert.Equal(alla.DeLaSerie(serie), aqui.DeLaSerie(serie));
        Assert.Equal((3, 7), aqui.DeLaSerie("A"));
        Assert.Equal((4, 9), aqui.DeLaSerie("B"));
        // La caja de omisión de allá no se hereda: el celular (nunca vendió) y la caja de esta PC, cuando se dé de alta, esperan su número.
        Assert.Null(aqui.Caja);
        Assert.Null(aqui.DeLaSerie("C"));
        Assert.Null(aqui.ProblemaDeLasCajas());
        // Lo de esta PC, de sus respuestas: el servidor, el gbak; la clave, nunca.
        Assert.Equal(("127.0.0.1", gbak, (string?)null), (aqui.Servidor, aqui.Gbak, aqui.Clave));
    }

    /// <summary>
    /// La base del paquete se revisa entera antes de traerla: con un byte cambiado (la misma medida), mudanza-ver todavía la da por buena
    /// (no la lee), y mudanza-revisar no, porque su SHA-256 no es el que escribió el servicio. Y si la PC de donde salió contesta con su
    /// tienda (como la dice /api/estado del servicio de verdad), «volvió a trabajar»: el paquete ya no sirve.
    /// </summary>
    [Fact]
    public async Task La_revision_compara_el_sha256_y_la_tienda_como_los_escribe_el_servicio()
    {
        using var pc = new PcQueSeMuda(semilla.Archivo);
        var (claves, _) = await SacarAsync(pc);
        var principal = LaPrincipal();
        var respuestas = Path.Combine(_carpeta.Ruta, "respuestas.txt");
        RespuestasDePrueba.ParaTraer(pc.Usb, RespuestasDePrueba.Gbak(_carpeta.Ruta), ("eleventa-cajero", "7")).Guardar(respuestas);
        string[] revisar = ["mudanza-revisar", "--paquete", pc.Usb, "--respuestas", respuestas, "--version", claves["version"], "--salida", Salida];
        Assert.Equal(0, await Ayudante.CorrerAsync(revisar, principal, RedDePrueba.SinNadie(), RespuestasDePrueba.SinEsperar));

        // Un byte cambiado en la base de la USB.
        var laBase = Path.Combine(pc.Usb, claves["archivo"]);
        var bytes = File.ReadAllBytes(laBase);
        bytes[bytes.Length / 2] ^= 0x5A;
        File.WriteAllBytes(laBase, bytes);

        Assert.Equal("bien", (await CorrerAsync(principal, RedDePrueba.SinNadie(), "mudanza-ver", "--paquete", pc.Usb, "--version", claves["version"]))["paquete"]);
        Assert.Equal(2, await Ayudante.CorrerAsync(revisar, principal, RedDePrueba.SinNadie(), RespuestasDePrueba.SinEsperar));
        var danada = ArchivoDeClaves.Leer(Salida);
        Assert.Equal(("no", "mudanza-paquete"), (danada["bien"], danada["clave"]));
        Assert.Contains("su SHA-256 no cuadra", danada["error"]);

        // Con la base buena otra vez, la PC de donde salió contesta con su tienda en la IP de su paquete.
        bytes[bytes.Length / 2] ^= 0x5A;
        File.WriteAllBytes(laBase, bytes);
        var red = new RedDePrueba().Contesta($"{PcQueSeMuda.Ips[0]}:{PuertoDeAlla}", RedDePrueba.Estado([.. semilla.SucursalesDelEstado]));

        Assert.Equal(2, await Ayudante.CorrerAsync(revisar, principal, red, RespuestasDePrueba.SinEsperar));
        var volvio = ArchivoDeClaves.Leer(Salida);
        Assert.Equal("mudanza-paquete", volvio["clave"]);
        Assert.StartsWith($"{PcQueSeMuda.Equipo} ({PcQueSeMuda.Ips[0]}) volvió a trabajar con su base", volvio["error"]);
    }

    /// <summary>
    /// La marca que deja el servicio junto a la base de la PC de donde salió, como la lee «leer» en esa PC: la mudanza está a medias, de qué
    /// paquete, de qué tienda (la misma de mudanza.txt), en qué carpeta y desde cuándo; esa PC ya solo puede ser «Otra caja», y su rol sigue
    /// siendo el de hoy (para cancelar).
    /// </summary>
    [Fact]
    public async Task Leer_entiende_la_marca_que_deja_el_servicio()
    {
        using var pc = new PcQueSeMuda(semilla.Archivo);
        var (claves, _) = await SacarAsync(pc);
        // La PC de donde salió, vista por el ayudante: sus datos son los de la PC que sacó el paquete, con su servicio todavía registrado.
        var vieja = new PcDePrueba(pc.Carpeta).ConServicio();
        Assert.Equal(pc.Datos, vieja.Datos);

        var leido = await CorrerAsync(vieja, RedDePrueba.SinNadie(), "leer");

        Assert.Equal("", leido["error"]);
        Assert.Equal("sacada", leido["mudanza"]);
        Assert.Equal(claves["paquete"], leido["mudanza-paquete"]);
        Assert.Equal(claves["tienda"], leido["mudanza-tienda"]);
        Assert.Equal(pc.Usb, leido["mudanza-carpeta"]);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 14, 15, 7, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            leido["mudanza-cuando"]);
        Assert.Equal(("otra", "principal", ""), (leido["roles"], leido["rol"], leido["mudanza-puesta"]));
        Assert.StartsWith("La base de esta PC se sacó para mudarla a otra PC (", leido["roles-motivo"]);
    }
}
