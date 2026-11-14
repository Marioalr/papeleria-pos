using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// <c>escribir --mudada-a</c>: al terminar una mudanza, la caja de la PC de donde salió la base queda hablándole a la PC nueva
/// (docs/instalacion.md). En caja.json solo cambian <c>Servicio</c>, <c>DireccionDeConfianza</c> y <c>LlaveDelServicio</c>; su alta
/// (<c>CajaId</c>, <c>LlaveProtegida</c>) y todo lo demás se queda, salvo con <c>--alta otra-vez</c>. El de antes queda en
/// caja.json.anterior. Sin <c>--mudada-a</c>, como siempre: el alta nunca se toca.
/// </summary>
public sealed class MudanzaMudadaATests : IDisposable
{
    /// <summary>De documentación (TEST-NET): la PC a donde se llevó la base.</summary>
    private const string Principal = "192.0.2.50";

    private const string CajaId = "5a1d4c3e-0000-4000-8000-000000000002";

    /// <summary>Como una llave cifrada con DPAPI, en base64 (con sus «+» y «/»): aquí nadie la abre, solo tiene que quedar igual.</summary>
    private const string LlaveProtegida = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAAq1+/Zw3k0U+z9A/b7rK2nQQAAAACAAAAAAAQZgAAAAEAACAAAAD+/w==";

    private readonly CarpetaDePrueba _carpeta = new();
    private readonly PcDePrueba _pc;

    /// <summary>La llave pública del servicio de esta PC (la que la caja recuerda) y la del servicio de la PC nueva.</summary>
    private readonly string _deAntes = LlaveDeUnServicio();

    private readonly string _nueva = LlaveDeUnServicio();

    public MudanzaMudadaATests() => _pc = new PcDePrueba(_carpeta.Ruta) { NombreDeLaPc = "CAJA2" };

    public void Dispose() => _carpeta.Dispose();

    private string Salida => Path.Combine(_carpeta.Ruta, "salida.txt");

    private string Ruta => CajaJson.Ruta(_pc.Datos);

    private static string LlaveDeUnServicio()
    {
        using var llave = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(llave.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// El caja.json de la caja 2 antes de terminar la mudanza: dada de alta con el servicio de su misma PC, con su lector y sus colas, y
    /// con cosas que el ayudante no conoce (las que la caja agregue mañana). Escrito como lo deja el ayudante (dos espacios, sin escapar
    /// de más), para ver que al cambiarlo no se mueve ni un byte de lo demás.
    /// </summary>
    private string Antes => """
        {
          "Servicio": "http://localhost:5080/",
          "CajaId": "5a1d4c3e-0000-4000-8000-000000000002",
          "Impresora": "tcp://192.0.2.100:9100",
          "LlaveProtegida": "LA-LLAVE-PROTEGIDA",
          "ColaImpresion": "WF-6590",
          "QuitarImpresos": true,
          "Prueba": false,
          "ServicioLocal": null,
          "BaseInicial": null,
          "CarpetaDatos": null,
          "LlaveDelServicio": "LA-DE-ANTES",
          "DireccionDeConfianza": "http://localhost:5080/",
          "Red": {
            "DetalleWifi": true
          },
          "Lector": {
            "Puerto": "COM3",
            "Baudios": 9600,
            "Paridad": "Ninguna",
            "Fin": "CR"
          },
          "ColasImpresion": [
            {
              "Cola": "EPSON L5590 Series",
              "Impresora": 3
            }
          ],
          "AlgoQueElAyudanteNoConoce": {
            "Precio": 1.50,
            "Lista": [
              1,
              "dos",
              null
            ],
            "Texto": "con «comillas», ñ, + y \"otras\""
          },
          "OtroDeDespues": "se queda"
        }

        """.ReplaceLineEndings(Environment.NewLine).Replace("LA-LLAVE-PROTEGIDA", LlaveProtegida).Replace("LA-DE-ANTES", _deAntes);

    private void Empezar(string texto)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);
        File.WriteAllBytes(Ruta, Encoding.UTF8.GetBytes(texto));
    }

    /// <summary>Las respuestas del asistente al terminar la mudanza: «Otra caja» de esa principal, con la impresora, la cola y el lector que ya tiene.</summary>
    private static ArchivoDeClaves Respuestas(params (string Clave, string Valor)[] cambios)
    {
        var archivo = new ArchivoDeClaves().Poner("rol", "otra").Poner("mudanza", "terminar").Poner("principal", Principal)
            .Poner("impresora", "tcp://192.0.2.100:9100").Poner("cola", "WF-6590").Poner("lector", "com").Poner("lector-puerto", "COM3").Poner("lector-baudios", "9600");
        foreach (var (clave, valor) in cambios)
            archivo.Poner(clave, valor);
        return archivo;
    }

    /// <summary>Como lo corre instalar-pc.ps1: por su línea de órdenes. Ni <c>escribir</c> ni <c>revisar</c> preguntan nada por la red.</summary>
    private async Task<(int Codigo, ArchivoDeClaves Dice)> CorrerAsync(string orden, ArchivoDeClaves? respuestas, params string[] opciones)
    {
        var archivo = Path.Combine(_carpeta.Ruta, "respuestas.txt");
        (respuestas ?? Respuestas()).Guardar(archivo);
        var red = RedDePrueba.SinNadie();

        var codigo = await Ayudante.CorrerAsync([orden, "--respuestas", archivo, .. opciones, "--salida", Salida], _pc, red);

        Assert.Empty(red.Preguntadas);
        return (codigo, ArchivoDeClaves.Leer(Salida));
    }

    private async Task<ArchivoDeClaves> EscribirAsync(params string[] opciones)
    {
        var (codigo, dice) = await CorrerAsync("escribir", null, opciones);
        Assert.True(codigo == 0 && dice["error"] == "", dice["error"]);
        return dice;
    }

    private JsonObject Leida() => (JsonObject)JsonNode.Parse(File.ReadAllText(Ruta))!;

    /// <summary>Los renglones de <paramref name="despues"/> que no son los de <paramref name="antes"/> (los dos, con los mismos renglones).</summary>
    private static List<string> RenglonesQueCambiaron(string antes, string despues)
    {
        var (unos, otros) = (antes.Split('\n'), despues.Split('\n'));
        Assert.Equal(unos.Length, otros.Length);
        return unos.Zip(otros).Where(par => par.First != par.Second).Select(par => par.Second.Trim()).ToList();
    }

    // ── Lo que queda ──

    /// <summary>
    /// La caja conserva su alta: en caja.json solo cambian la dirección (la del servicio y la de confianza) y la llave pública del
    /// servicio (la de la PC nueva, en lugar de la del servicio de esta PC). Todo lo demás queda byte por byte, también lo que el
    /// ayudante no conoce; y el de antes, entero, en caja.json.anterior.
    /// </summary>
    [Fact]
    public async Task Solo_cambian_la_direccion_y_la_llave_del_servicio_y_lo_demas_queda_byte_por_byte()
    {
        Empezar(Antes);

        var dice = await EscribirAsync("--mudada-a", Principal, "--llave-del-servicio", _nueva);

        var esperado = Antes
            .Replace("\"Servicio\": \"http://localhost:5080/\"", $"\"Servicio\": \"http://{Principal}:5080/\"")
            .Replace($"\"LlaveDelServicio\": \"{_deAntes}\"", $"\"LlaveDelServicio\": \"{_nueva}\"")
            .Replace("\"DireccionDeConfianza\": \"http://localhost:5080/\"", $"\"DireccionDeConfianza\": \"http://{Principal}:5080/\"");
        Assert.Equal(Encoding.UTF8.GetBytes(esperado), File.ReadAllBytes(Ruta));
        Assert.Equal([$"\"Servicio\": \"http://{Principal}:5080/\",", $"\"LlaveDelServicio\": \"{_nueva}\",", $"\"DireccionDeConfianza\": \"http://{Principal}:5080/\","],
            RenglonesQueCambiaron(Antes, File.ReadAllText(Ruta)));
        // Su alta, igual: la misma caja y la misma llave cifrada. Y la caja lo lee así al abrir.
        Assert.Equal(CajaId, (string?)Leida()["CajaId"]);
        Assert.Equal(LlaveProtegida, (string?)Leida()["LlaveProtegida"]);
        var comoLoLeeLaCaja = ConfiguracionDeLaCaja.Leer(Ruta);
        Assert.Equal(($"http://{Principal}:5080/", Guid.Parse(CajaId), LlaveProtegida, _nueva, $"http://{Principal}:5080/", "tcp://192.0.2.100:9100", "WF-6590"),
            (comoLoLeeLaCaja.Servicio, comoLoLeeLaCaja.CajaId, comoLoLeeLaCaja.LlaveProtegida, comoLoLeeLaCaja.LlaveDelServicio, comoLoLeeLaCaja.DireccionDeConfianza,
                comoLoLeeLaCaja.Impresora, comoLoLeeLaCaja.ColaImpresion));
        Assert.Equal(Encoding.UTF8.GetBytes(Antes), File.ReadAllBytes(Ruta + ".anterior"));
        // Dice qué cambió, por su nombre: nunca un valor.
        Assert.Equal("caja.json: Servicio; caja.json: LlaveDelServicio; caja.json: DireccionDeConfianza", dice["cambios"]);
        Assert.Equal("cambio", dice["caja-json"]);
        Assert.Equal("no-aplica", dice["servicio-json"]);
        Assert.DoesNotContain(LlaveProtegida, File.ReadAllText(Salida));
        // Sin nada más en la carpeta de la caja, ni servicio.json.
        Assert.Equal(["caja.json", "caja.json.anterior"], Directory.GetFiles(Path.GetDirectoryName(Ruta)!).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["Caja"], Directory.GetFileSystemEntries(_pc.Datos).Select(Path.GetFileName));
    }

    /// <summary>La dirección queda como la escribe la caja, se dé como se dé: sola (el 5080), con otro puerto o completa.</summary>
    [Theory]
    [InlineData("192.0.2.50", "http://192.0.2.50:5080/")]
    [InlineData(" 192.0.2.50 ", "http://192.0.2.50:5080/")]
    [InlineData("192.0.2.50:5090", "http://192.0.2.50:5090/")]
    [InlineData("http://192.0.2.50:5080/", "http://192.0.2.50:5080/")]
    [InlineData("PRINCIPAL", "http://principal:5080/")]
    public async Task La_direccion_queda_como_la_escribe_escribir_con_la_respuesta_principal(string direccion, string queda)
    {
        Empezar(Antes);

        var (codigo, dice) = await CorrerAsync("escribir", Respuestas(("principal", direccion)), "--mudada-a", direccion);

        Assert.True(codigo == 0, dice["error"]);
        Assert.Equal(queda, (string?)Leida()["Servicio"]);
        Assert.Equal(queda, (string?)Leida()["DireccionDeConfianza"]);
        // Lo mismo que deja «escribir» de siempre con esa respuesta «principal» (sin --mudada-a), en «Servicio».
        Empezar(Antes);
        Assert.Equal(0, (await CorrerAsync("escribir", Respuestas(("principal", direccion)))).Codigo);
        Assert.Equal(queda, (string?)Leida()["Servicio"]);
    }

    /// <summary>
    /// El caja.json como lo escribe la caja de verdad (JsonSerializer: «+» como +, sin salto al final): cambia de forma al
    /// escribirlo, pero cada dato que no es de la conversión queda con el mismo valor, letra por letra, y en su orden.
    /// </summary>
    [Fact]
    public async Task Sobre_el_caja_json_que_escribe_la_caja_cada_dato_que_no_se_cambia_queda_igual()
    {
        static JsonElement Dato(string json) => JsonSerializer.Deserialize<JsonElement>(json);
        new ConfiguracionDeLaCaja("http://localhost:5080/", Guid.Parse(CajaId), "tcp://192.0.2.100:9100", LlaveProtegida, "WF-6590", LlaveDelServicio: _deAntes,
            DireccionDeConfianza: "http://localhost:5080/", Red: Dato("""{ "DetalleWifi": true }"""),
            Lector: Dato("""{ "Puerto": "COM3", "Baudios": 9600, "Paridad": "Ninguna", "Fin": "CR" }"""),
            ColasImpresion: Dato("""[ { "Cola": "EPSON L5590 Series", "Impresora": 3 } ]""")).Guardar(Ruta);
        var comoLaCaja = File.ReadAllText(Ruta);
        // Como escribe la caja: el «+» de una llave en base64, escapado, y sin salto de renglón al final.
        Assert.Contains(@"\u002B", comoLaCaja);
        Assert.DoesNotContain(LlaveProtegida, comoLaCaja);
        Assert.False(comoLaCaja.EndsWith('}' + Environment.NewLine));
        var antes = (JsonObject)JsonNode.Parse(comoLaCaja)!;

        await EscribirAsync("--mudada-a", Principal, "--llave-del-servicio", _nueva);

        var caja = Leida();
        Assert.Equal(antes.Select(dato => dato.Key), caja.Select(dato => dato.Key));
        foreach (var (clave, valor) in antes)
        {
            if (clave is "Servicio" or "DireccionDeConfianza" or "LlaveDelServicio")
                continue;
            Assert.True(Json.Iguales(valor, caja[clave]), clave);
            Assert.Equal(valor?.ToJsonString(), caja[clave]?.ToJsonString());
        }
        Assert.Equal(LlaveProtegida, (string?)caja["LlaveProtegida"]);
        Assert.Equal($"http://{Principal}:5080/", (string?)caja["Servicio"]);
        Assert.Equal($"http://{Principal}:5080/", (string?)caja["DireccionDeConfianza"]);
        Assert.Equal(_nueva, (string?)caja["LlaveDelServicio"]);
        Assert.Equal(comoLaCaja, File.ReadAllText(Ruta + ".anterior"));
    }

    /// <summary>
    /// Sin la llave del servicio (no se pudo saber si la PC nueva conoce a esta caja), la de antes se quita: con ella guardada la caja
    /// rechazaría a la PC nueva; vacía, la aprende al abrirla en su dirección de confianza.
    /// </summary>
    [Fact]
    public async Task Sin_la_llave_del_servicio_la_de_antes_se_quita_para_que_la_caja_aprenda_la_nueva()
    {
        Empezar(Antes);

        var dice = await EscribirAsync("--mudada-a", Principal);

        Assert.Equal([$"\"Servicio\": \"http://{Principal}:5080/\",", "\"LlaveDelServicio\": null,", $"\"DireccionDeConfianza\": \"http://{Principal}:5080/\","],
            RenglonesQueCambiaron(Antes, File.ReadAllText(Ruta)));
        Assert.Equal("caja.json: Servicio; caja.json: LlaveDelServicio; caja.json: DireccionDeConfianza", dice["cambios"]);
        Assert.Equal(CajaId, (string?)Leida()["CajaId"]);
        Assert.Equal(LlaveProtegida, (string?)Leida()["LlaveProtegida"]);
    }

    /// <summary>
    /// La PC nueva ya no reconoce la llave de esta caja: con <c>--alta otra-vez</c> la caja queda sin alta (pide darla de alta al
    /// abrirla) y todo lo demás igual. El alta de antes no se pierde: queda en caja.json.anterior.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Con_alta_otra_vez_la_caja_queda_sin_alta_y_la_de_antes_en_el_anterior(bool conLaLlaveDelServicio)
    {
        Empezar(Antes);
        string[] opciones = conLaLlaveDelServicio
            ? ["--mudada-a", Principal, "--llave-del-servicio", _nueva, "--alta", "otra-vez"]
            : ["--mudada-a", Principal, "--alta", "otra-vez"];

        var dice = await EscribirAsync(opciones);

        Assert.Equal(
            [
                $"\"Servicio\": \"http://{Principal}:5080/\",", "\"CajaId\": null,", "\"LlaveProtegida\": null,",
                conLaLlaveDelServicio ? $"\"LlaveDelServicio\": \"{_nueva}\"," : "\"LlaveDelServicio\": null,", $"\"DireccionDeConfianza\": \"http://{Principal}:5080/\",",
            ],
            RenglonesQueCambiaron(Antes, File.ReadAllText(Ruta)));
        var caja = Leida();
        Assert.True(caja.ContainsKey("CajaId") && caja["CajaId"] is null);
        Assert.True(caja.ContainsKey("LlaveProtegida") && caja["LlaveProtegida"] is null);
        // Para la caja eso es no tener alta (sin su caja y sin su llave, pide darla de alta): así lo lee ella, así lo dice «leer» y así
        // lo ve la prueba de identidad.
        var comoLoLeeLaCaja = ConfiguracionDeLaCaja.Leer(Ruta);
        Assert.Equal(($"http://{Principal}:5080/", null, null), (comoLoLeeLaCaja.Servicio, comoLoLeeLaCaja.CajaId, comoLoLeeLaCaja.LlaveProtegida));
        Assert.Equal("no", LecturaDeLaPc.Leer(_pc)["caja-dada-de-alta"]);
        Assert.Null(AltaDeLaCaja.DeEstaPc(_pc.Datos));
        Assert.Equal(Encoding.UTF8.GetBytes(Antes), File.ReadAllBytes(Ruta + ".anterior"));
        Assert.Equal("caja.json: Servicio; caja.json: CajaId; caja.json: LlaveProtegida; caja.json: LlaveDelServicio; caja.json: DireccionDeConfianza", dice["cambios"]);
        Assert.DoesNotContain(LlaveProtegida, File.ReadAllText(Salida));
    }

    /// <summary>Sin <c>--mudada-a</c>, como siempre: con la respuesta «principal» solo cambia «Servicio», y el alta, la llave del servicio y la dirección de confianza se quedan.</summary>
    [Fact]
    public async Task Sin_mudada_a_el_alta_la_llave_del_servicio_y_la_direccion_de_confianza_no_se_tocan()
    {
        Empezar(Antes);

        var dice = await EscribirAsync();

        Assert.Equal([$"\"Servicio\": \"http://{Principal}:5080/\","], RenglonesQueCambiaron(Antes, File.ReadAllText(Ruta)));
        Assert.Equal("caja.json: Servicio", dice["cambios"]);
        // Y es lo mismo que deja CajaJson con esas respuestas, sin nada de la mudanza.
        var conLaMudanza = File.ReadAllBytes(Ruta);
        Empezar(Antes);
        CajaJson.Escribir(_pc.Datos, PapeleriaPos.Instalacion.Respuestas.De(Respuestas()));
        Assert.Equal(File.ReadAllBytes(Ruta), conLaMudanza);
    }

    /// <summary>Volver a correrlo (una corrida que se cortó después de escribir caja.json) no cambia nada ni pisa el caja.json.anterior.</summary>
    [Fact]
    public async Task Lo_que_ya_esta_convertido_no_se_escribe_otra_vez()
    {
        Empezar(Antes);
        await EscribirAsync("--mudada-a", Principal, "--llave-del-servicio", _nueva);
        var convertido = File.ReadAllBytes(Ruta);

        var dice = await EscribirAsync("--mudada-a", Principal, "--llave-del-servicio", _nueva);

        Assert.Equal("igual", dice["caja-json"]);
        Assert.Equal("", dice["cambios"]);
        Assert.Equal(convertido, File.ReadAllBytes(Ruta));
        Assert.Equal(Encoding.UTF8.GetBytes(Antes), File.ReadAllBytes(Ruta + ".anterior"));
        // Y el «escribir» de siempre que corre después (sin --mudada-a) ya no cambia nada de la dirección.
        dice = await EscribirAsync();
        Assert.Equal("igual", dice["caja-json"]);
        Assert.Equal(convertido, File.ReadAllBytes(Ruta));
    }

    /// <summary>La dirección que ya es esa (escrita de otra forma) se queda como está escrita, como hace «escribir» con «Servicio».</summary>
    [Fact]
    public async Task La_direccion_que_ya_es_esa_se_queda_como_esta_escrita()
    {
        Empezar(Antes.Replace("http://localhost:5080/", $"HTTP://{Principal}:5080"));

        var dice = await EscribirAsync("--mudada-a", Principal, "--llave-del-servicio", _nueva);

        Assert.Equal("caja.json: LlaveDelServicio", dice["cambios"]);
        Assert.Equal($"HTTP://{Principal}:5080", (string?)Leida()["Servicio"]);
        Assert.Equal($"HTTP://{Principal}:5080", (string?)Leida()["DireccionDeConfianza"]);
    }

    /// <summary>
    /// Una caja de antes, que todavía no guardaba ni la dirección de confianza ni la llave del servicio: la dirección de confianza se
    /// pone (es donde la caja aprende la llave), y la llave solo si se dio (vacía y sin la clave es lo mismo para la caja).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task En_una_caja_de_antes_se_agrega_la_direccion_de_confianza_y_la_llave_solo_si_se_dio(bool conLlave)
    {
        Empezar($$"""{ "Servicio": "http://localhost:5080", "CajaId": "{{CajaId}}", "Impresora": "", "LlaveProtegida": "{{LlaveProtegida}}" }""");

        var (codigo, dice) = await CorrerAsync("escribir", Respuestas(("impresora", ""), ("cola", ""), ("lector", "teclado")),
            conLlave ? ["--mudada-a", Principal, "--llave-del-servicio", _nueva] : ["--mudada-a", Principal]);

        Assert.True(codigo == 0, dice["error"]);
        var caja = Leida();
        string[] quedan = conLlave
            ? ["Servicio", "CajaId", "Impresora", "LlaveProtegida", "DireccionDeConfianza", "LlaveDelServicio"]
            : ["Servicio", "CajaId", "Impresora", "LlaveProtegida", "DireccionDeConfianza"];
        Assert.Equal(quedan, caja.Select(dato => dato.Key));
        Assert.Equal($"http://{Principal}:5080/", (string?)caja["Servicio"]);
        Assert.Equal($"http://{Principal}:5080/", (string?)caja["DireccionDeConfianza"]);
        Assert.Equal(conLlave ? _nueva : null, (string?)caja["LlaveDelServicio"]);
        Assert.Equal(CajaId, (string?)caja["CajaId"]);
        Assert.Equal(LlaveProtegida, (string?)caja["LlaveProtegida"]);
    }

    /// <summary>La caja que guardaba su caja.json en la carpeta de antes: se parte de ese, con su alta, y queda en la carpeta Caja.</summary>
    [Fact]
    public async Task El_caja_json_de_la_carpeta_de_antes_se_convierte_con_su_alta()
    {
        Directory.CreateDirectory(_pc.Datos);
        File.WriteAllText(CajaJson.RutaDeAntes(_pc.Datos), Antes);

        var dice = await EscribirAsync("--mudada-a", Principal, "--llave-del-servicio", _nueva);

        Assert.StartsWith("caja.json (pasa a la carpeta Caja); caja.json: Servicio", dice["cambios"]);
        Assert.Equal(CajaId, (string?)Leida()["CajaId"]);
        Assert.Equal(LlaveProtegida, (string?)Leida()["LlaveProtegida"]);
        Assert.Equal(_nueva, (string?)Leida()["LlaveDelServicio"]);
        Assert.Equal(Antes, File.ReadAllText(CajaJson.RutaDeAntes(_pc.Datos)));
    }

    /// <summary><c>revisar</c> con las mismas opciones dice lo que cambiaría, sin escribir nada.</summary>
    [Fact]
    public async Task Revisar_dice_lo_que_cambiaria_sin_escribir()
    {
        Empezar(Antes);

        var (codigo, dice) = await CorrerAsync("revisar", null, "--mudada-a", Principal, "--llave-del-servicio", _nueva, "--alta", "otra-vez");

        Assert.Equal(0, codigo);
        Assert.Equal("cambio", dice["caja-json"]);
        Assert.Equal("caja.json: Servicio; caja.json: CajaId; caja.json: LlaveProtegida; caja.json: LlaveDelServicio; caja.json: DireccionDeConfianza", dice["cambios"]);
        Assert.Equal(Encoding.UTF8.GetBytes(Antes), File.ReadAllBytes(Ruta));
        Assert.False(File.Exists(Ruta + ".anterior"));
    }

    // ── Lo que no se puede: no se cambia nada ──

    private async Task<ArchivoDeClaves> NoSePuedeAsync(string clave, ArchivoDeClaves? respuestas, params string[] opciones)
    {
        Empezar(Antes);

        var (codigo, dice) = await CorrerAsync("escribir", respuestas, opciones);

        Assert.Equal(2, codigo);
        Assert.Equal(clave, dice["clave"]);
        Assert.EndsWith(" No se cambió nada.", dice["error"]);
        Assert.Equal(Encoding.UTF8.GetBytes(Antes), File.ReadAllBytes(Ruta));
        Assert.False(File.Exists(Ruta + ".anterior"));
        // Lo mismo dice «revisar», también sin tocar nada.
        var (alRevisar, revisado) = await CorrerAsync("revisar", respuestas, opciones);
        Assert.Equal((2, dice["error"], clave), (alRevisar, revisado["error"], revisado["clave"]));
        return dice;
    }

    /// <summary>La conversión es de la PC de donde salió la base, que queda como «Otra caja»: con el servicio en esta PC no hay a dónde mudar la caja.</summary>
    [Theory]
    [InlineData("principal")]
    [InlineData("unica")]
    public async Task Solo_va_con_otra_caja(string rol)
    {
        var respuestas = new ArchivoDeClaves().Poner("rol", rol).Poner("impresora", "").Poner("lector", "teclado").Poner("controlprint", "no").Poner("eleventa", "no");

        var dice = await NoSePuedeAsync("rol", respuestas, "--mudada-a", Principal);

        Assert.Contains("queda como «Otra caja»", dice["error"]);
        Assert.False(File.Exists(Path.Combine(_pc.Datos, "servicio.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("la de enfrente")]
    [InlineData("ftp://192.0.2.50")]
    public async Task Una_direccion_que_no_se_entiende_no_se_escribe(string direccion)
    {
        var dice = await NoSePuedeAsync("principal", Respuestas(), "--mudada-a", direccion);

        Assert.StartsWith("La dirección de la caja principal no se entiende", dice["error"]);
    }

    /// <summary>
    /// «Servicio» lo pone en cada corrida la respuesta «principal»: si no es la misma PC que <c>--mudada-a</c>, la caja quedaría
    /// hablándole a una y confiando en otra.
    /// </summary>
    [Fact]
    public async Task Si_la_respuesta_principal_dice_otra_pc_no_se_escribe()
    {
        var dice = await NoSePuedeAsync("principal", Respuestas(("principal", "192.0.2.51")), "--mudada-a", Principal);

        Assert.StartsWith("La caja se muda a «192.0.2.50», pero la dirección de la caja principal que se contestó es «192.0.2.51»", dice["error"]);
    }

    /// <summary>
    /// La llave del servicio queda como la que la caja compara, letra por letra, con la que presenta su caja principal: una cortada, con
    /// espacios en medio o que no es una llave pública la dejaría sin poder comprobarla nunca. No se escribe.
    /// </summary>
    [Theory]
    [InlineData("recortada")]
    [InlineData("con-espacios")]
    [InlineData("sin-relleno")]
    [InlineData("otra-cosa")]
    [InlineData("no-es-base64")]
    public async Task Una_llave_del_servicio_que_no_es_la_que_guarda_la_caja_no_se_escribe(string caso)
    {
        var llave = caso switch
        {
            "recortada" => _nueva[..^8],
            "con-espacios" => _nueva.Insert(20, " "),
            "sin-relleno" => _nueva.TrimEnd('='),
            "otra-cosa" => Convert.ToBase64String("esto no es una llave"u8),
            _ => "¡no!",
        };
        Assert.NotEqual(_nueva, llave);

        var dice = await NoSePuedeAsync("", Respuestas(), "--mudada-a", Principal, "--llave-del-servicio", llave);

        Assert.StartsWith("«--llave-del-servicio» no es la llave pública de un servicio", dice["error"]);
        Assert.False(IdentidadDeLaPrincipal.EsLlaveDeServicio(llave));
        Assert.True(IdentidadDeLaPrincipal.EsLlaveDeServicio(_nueva));
    }

    [Theory]
    [InlineData("si")]
    [InlineData("otra")]
    [InlineData("quitar")]
    public async Task El_alta_solo_se_quita_diciendo_otra_vez(string alta)
    {
        var dice = await NoSePuedeAsync("", Respuestas(), "--mudada-a", Principal, "--alta", alta);

        Assert.StartsWith($"«--alta» solo va como otra-vez (está «{alta}»)", dice["error"]);
    }

    /// <summary>Sin <c>--mudada-a</c> el alta no se toca: pedir quitarla (o dar la llave del servicio) sin decir a dónde no se entiende, y no se adivina.</summary>
    [Theory]
    [InlineData("--alta", "otra-vez")]
    [InlineData("--llave-del-servicio", null)]
    public async Task La_llave_del_servicio_y_el_alta_van_con_mudada_a(string opcion, string? valor)
    {
        var dice = await NoSePuedeAsync("", Respuestas(), opcion, valor ?? _nueva);

        Assert.StartsWith("«--llave-del-servicio» y «--alta» son de la conversión de la caja al terminar una mudanza: van con «--mudada-a»", dice["error"]);
    }

    /// <summary>Con caja.json mal escrito (la caja tampoco abriría) no se escribe nada, como siempre.</summary>
    [Fact]
    public async Task Con_caja_json_mal_escrito_no_se_cambia_nada()
    {
        Empezar("{ \"Servicio\": ");

        var (codigo, dice) = await CorrerAsync("escribir", null, "--mudada-a", Principal, "--llave-del-servicio", _nueva);

        Assert.Equal(2, codigo);
        Assert.Contains("caja.json está mal escrito", dice["error"]);
        Assert.Equal("{ \"Servicio\": ", File.ReadAllText(Ruta));
    }

    /// <summary>Sin caja.json (la caja de esta PC nunca se abrió) queda uno nuevo apuntando a la PC nueva, sin alta: se da de alta al abrirla.</summary>
    [Fact]
    public async Task Sin_caja_json_queda_uno_nuevo_apuntando_a_la_pc_nueva()
    {
        var (codigo, dice) = await CorrerAsync("escribir", Respuestas(("impresora", ""), ("cola", ""), ("lector", "teclado")), "--mudada-a", Principal);

        Assert.True(codigo == 0, dice["error"]);
        var caja = Leida();
        Assert.Equal($"http://{Principal}:5080/", (string?)caja["Servicio"]);
        Assert.Equal($"http://{Principal}:5080/", (string?)caja["DireccionDeConfianza"]);
        Assert.Null(caja["CajaId"]);
        Assert.False(caja.ContainsKey("LlaveDelServicio"));
        Assert.False(File.Exists(Ruta + ".anterior"));
    }
}
