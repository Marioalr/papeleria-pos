using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Servicio;
using ServicioDePrueba = PapeleriaPos.Pruebas.Servicio.ServicioDePrueba;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// <c>escribir --mudanza</c> (y <c>revisar --mudanza</c>): el servicio.json de la PC a donde se trajo la base de un paquete de mudanza
/// (docs/instalacion.md). Sobre el servicio.json de ESTA PC y después de las respuestas de siempre, el puente con eleventa queda como
/// estaba en la otra PC: <c>Escribir</c>, el puerto y el usuario, el cajero, los ajustes y <c>Cajas</c> (cada caja que ya vendió, con
/// su número de allá). De allá nunca sale una ruta, el servidor, la caja de omisión ni la clave. Sin <c>--mudanza</c>, nada de esto.
/// </summary>
public sealed class MudanzaEscribirTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();
    private readonly PcDePrueba _pc;

    public MudanzaEscribirTests() => _pc = new PcDePrueba(_carpeta.Ruta);

    public void Dispose() => _carpeta.Dispose();

    private string Usb => Path.Combine(_carpeta.Ruta, "usb", "mudanza");

    private string Salida => Path.Combine(_carpeta.Ruta, "salida.txt");

    private string Ruta => Path.Combine(_pc.Datos, ServicioJson.Archivo);

    /// <summary>La caja 2 del piloto de hoy, escrita a mano con el Bloc de notas (con BOM y \r\n), ya en modo multicaja.</summary>
    private const string HechoAMano = """
        // A mano, con el Bloc de notas.
        {
          "Eleventa": {
            "Activo": true,
            "Escribir": false, // todavía no
            "Servidor": "192.0.2.106",
            "Base": "C:\\Program Files (x86)\\AbarrotesMultiCaja\\db\\PDVDATA.FDB",
            "Caja": 1,
            "Cajero": 1,
            // Cada caja del POS, por su serie.
            "Cajas": {
              "A": { "Caja": 1 }, // la principal
              "b": { "Caja": 9, "Cajero": 7 } // la caja 2, con su cajero
            },
            "SegundosMaximos": 45
          },
          "Registro": { "Dias": 45 } // el registro, más días
        }
        """;

    private static string Plantilla() => File.ReadAllText(Path.Combine(ServicioJsonTests.RaizDelRepositorio(), "instalacion", "plantilla-servicio.json"));

    private void Empezar(string servicioJson, bool bom = true)
    {
        Directory.CreateDirectory(_pc.Datos);
        File.WriteAllText(Ruta, servicioJson.ReplaceLineEndings("\r\n"), new UTF8Encoding(bom));
    }

    /// <summary>Las respuestas de siempre para traer ese paquete (con el gbak de esta PC), con sus cambios.</summary>
    private ArchivoDeClaves Respuestas(params (string Clave, string Valor)[] cambios) =>
        RespuestasDePrueba.ParaTraer(Usb, RespuestasDePrueba.Gbak(_carpeta.Ruta), cambios);

    /// <summary>
    /// Como lo corre instalar-pc.ps1 después de poner la base: <c>escribir --respuestas … --mudanza carpeta</c>. Ni <c>escribir</c> ni
    /// <c>revisar</c> preguntan nada por la red.
    /// </summary>
    private async Task<(int Codigo, ArchivoDeClaves Dice)> CorrerAsync(string orden, ArchivoDeClaves? respuestas = null, string? mudanza = "", PcDePrueba? pc = null)
    {
        var archivo = Path.Combine(_carpeta.Ruta, "respuestas.txt");
        (respuestas ?? Respuestas()).Guardar(archivo);
        string[] argumentos = mudanza is null
            ? [orden, "--respuestas", archivo, "--salida", Salida]
            : [orden, "--respuestas", archivo, "--mudanza", mudanza.Length == 0 ? Usb : mudanza, "--salida", Salida];
        var red = RedDePrueba.SinNadie();

        var codigo = await Ayudante.CorrerAsync(argumentos, pc ?? _pc, red);

        Assert.Empty(red.Preguntadas);
        return (codigo, ArchivoDeClaves.Leer(Salida));
    }

    private async Task<ArchivoDeClaves> EscribirAsync(ArchivoDeClaves? respuestas = null)
    {
        var (codigo, dice) = await CorrerAsync("escribir", respuestas);
        Assert.True(codigo == 0 && dice["error"] == "", dice["error"]);
        return dice;
    }

    private JsonObject Leido() => new TextoJsonc(File.ReadAllBytes(Ruta)).Objeto();

    private JsonObject Eleventa() => (JsonObject)Json.Valor(Leido(), "Eleventa")!;

    private static string? Valor(JsonObject seccion, params string[] ruta)
    {
        JsonNode? nodo = seccion;
        foreach (var nombre in ruta)
            nodo = nodo is JsonObject objeto ? Json.Valor(objeto, nombre) : null;
        return nodo is JsonObject or JsonArray ? nodo.ToJsonString() : Json.Texto(nodo);
    }

    private static int Comentarios(string texto) => texto.Split('\n').Count(r => r.Contains("//"));

    // ── Lo que queda ──

    /// <summary>
    /// La principal recién instalada (la plantilla, toda comentarios) a donde llega la base de la caja 2 con el puente escribiendo: el
    /// servicio de verdad lee de ahí que la caja de la serie B sigue escribiendo como la caja 3 de eleventa, y que una caja que no esté
    /// en Cajas (la de esta PC, cuando se dé de alta) no escribe como ninguna mientras no se ponga su número.
    /// </summary>
    [Fact]
    public async Task En_la_plantilla_queda_el_puente_como_estaba_alla_con_sus_comentarios_y_el_servicio_de_verdad_lo_lee()
    {
        PaqueteDePrueba.En(Usb, ("eleventa-puerto", "3051"), ("eleventa-usuario", "ELEVENTA"), ("eleventa-cajas", "B=3;C=4/9"),
            ("eleventa-ajustes", "Minutos=10;ProductoCopias=COPIA;RespaldoObligatorio=no;SegundosAlCobrar=2.5;SegundosEntreEnvios=45;SegundosMaximos=90;"
                + "DiasDeRespaldos=7;ProductoImpresiones=IMP2"));
        Empezar(Plantilla());
        var plantilla = File.ReadAllBytes(Ruta);
        // De documentación (TEST-NET), como en ServicioJsonTests: el servicio de las pruebas arranca con esto y nunca se conecta a nada.
        var respuestas = Respuestas(("eleventa-servidor", "192.0.2.10"), ("controlprint", "no"));

        var dice = await EscribirAsync(respuestas);

        Assert.Equal("cambio", dice["servicio-json"]);
        Assert.Equal("no", dice["servicio-json-completo"]);
        Assert.Contains("Eleventa (se agrega)", dice["cambios"]);
        // Los comentarios de la plantilla se quedan, con su BOM; y lo de antes, a un lado.
        var texto = File.ReadAllText(Ruta);
        Assert.StartsWith("// La configuración de ESTA PC para el servicio de Papelería POS", texto.TrimStart('\uFEFF'));
        Assert.Contains("La clave de eleventa NUNCA va aquí", texto);
        Assert.Equal(Comentarios(Plantilla()), Comentarios(texto));
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(Ruta)[..3]);
        Assert.Equal(plantilla, File.ReadAllBytes(Ruta + ".anterior"));
        // La sección, entera: lo de las respuestas (el servidor, la base, el gbak y la carpeta de ESTA PC) y lo de allá.
        Assert.Equal(JsonNode.Parse($$"""
            {
              "Activo": true,
              "Escribir": true,
              "Servidor": "192.0.2.10",
              "Puerto": 3051,
              "Base": {{JsonSerializer.Serialize(PaqueteDePrueba.BaseDeEleventa)}},
              "Cajero": 1,
              "Gbak": {{JsonSerializer.Serialize(RespuestasDePrueba.Gbak(_carpeta.Ruta))}},
              "CarpetaRespaldos": "C:\\ProgramData\\PapeleriaPos\\respaldos-eleventa",
              "Usuario": "ELEVENTA",
              "Cajas": { "B": { "Caja": 3 }, "C": { "Caja": 4, "Cajero": 9 } },
              "Minutos": 10,
              "ProductoCopias": "COPIA",
              "RespaldoObligatorio": false,
              "SegundosAlCobrar": 2.5,
              "SegundosEntreEnvios": 45,
              "SegundosMaximos": 90,
              "DiasDeRespaldos": 7,
              "ProductoImpresiones": "IMP2"
            }
            """)!.ToJsonString(), Eleventa().ToJsonString());

        await using var servicio = await ServicioDePrueba.CrearAsync(("ConfiguracionDeLaPc", Ruta));
        var eleventa = servicio.Fabrica.Services.GetRequiredService<OpcionesEleventa>();
        Assert.True(eleventa.Activo);
        Assert.True(eleventa.Escribir);
        Assert.Equal("192.0.2.10", eleventa.Servidor);
        Assert.Equal(3051, eleventa.Puerto);
        Assert.Equal("ELEVENTA", eleventa.Usuario);
        Assert.Equal(PaqueteDePrueba.BaseDeEleventa, eleventa.Base);
        // Cada caja que se mudó, con su número de allá (y su cajero, si traía el suyo); sin distinguir mayúsculas, como las series.
        Assert.Equal((3, 1), eleventa.DeLaSerie("B"));
        Assert.Equal((3, 1), eleventa.DeLaSerie("b"));
        Assert.Equal((4, 9), eleventa.DeLaSerie("C"));
        // La caja de omisión no se heredó: una caja que no está en Cajas no escribe como ninguna (espera en la bandeja).
        Assert.Null(eleventa.Caja);
        Assert.Null(eleventa.DeLaSerie("A"));
        Assert.Null(eleventa.DeLaSerie(null));
        Assert.Equal([3, 4], eleventa.CajasDeEleventa());
        Assert.Null(eleventa.ProblemaDeLasCajas());
        // Los ajustes del puente, con su tipo.
        Assert.Equal(10, eleventa.Minutos);
        Assert.Equal("COPIA", eleventa.ProductoCopias);
        Assert.Equal("IMP2", eleventa.ProductoImpresiones);
        Assert.False(eleventa.RespaldoObligatorio);
        Assert.Equal(2.5, eleventa.SegundosAlCobrar);
        Assert.Equal(45, eleventa.SegundosEntreEnvios);
        Assert.Equal(90, eleventa.SegundosMaximos);
        Assert.Equal(7, eleventa.DiasDeRespaldos);
        // La clave no viaja ni se escribe: va solo en la variable del servicio, y sin ella no se conecta a nada.
        Assert.Null(eleventa.Clave);
        Assert.Null(eleventa.ParaEscribir("B"));
    }

    /// <summary>
    /// El único camino por el que el instalador escribe Escribir: llega como estaba allá. Si ya escribía, sigue escribiendo (también
    /// sobre una sección de aquí que lo tenía apagado); si solo leía, solo lee (también sobre una que aquí lo tenía prendido).
    /// </summary>
    [Theory]
    [InlineData("{ }", "si", true)]
    [InlineData("{ }", "no", false)]
    [InlineData("""{ "Eleventa": { "Activo": true, "Escribir": false, "Servidor": "192.0.2.106" } }""", "si", true)]
    [InlineData("""{ "Eleventa": { "Activo": true, "Servidor": "192.0.2.106" } }""", "si", true)]
    [InlineData("""{ "Eleventa": { "Activo": true, "Escribir": true, "Servidor": "192.0.2.106" } }""", "no", false)]
    [InlineData("""{ "eleventa": { "activo": "true", "escribir": "true" } }""", "no", false)]
    public async Task Escribir_llega_como_estaba_alla(string antes, string alla, bool queda)
    {
        PaqueteDePrueba.En(Usb, ("eleventa-escribir", alla));
        Empezar(antes, bom: false);

        await EscribirAsync();

        var eleventa = Eleventa();
        Assert.Equal(queda ? "true" : "false", Valor(eleventa, "Escribir"));
        // Escrito como sí/no de JSON, no como texto: una sola clave, con el nombre que ya tenía.
        Assert.Single(eleventa, c => string.Equals(c.Key, "Escribir", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("true", Valor(eleventa, "Activo"));
    }

    /// <summary>
    /// Sin --mudanza el instalador sigue sin tocar Escribir, aunque el paquete esté ahí y las respuestas digan «traer»: en una sección
    /// nueva va en false, y la que ya lo tenía (prendido o apagado) se queda como estaba. Tampoco aparece Cajas.
    /// </summary>
    [Theory]
    [InlineData("{ }", "false")]
    [InlineData("""{ "Eleventa": { "Activo": true, "Escribir": true, "Servidor": "192.0.2.106" } }""", "true")]
    [InlineData("""{ "Eleventa": { "Activo": true, "Escribir": false, "Servidor": "192.0.2.106" } }""", "false")]
    [InlineData("""{ "Eleventa": { "Activo": true, "Servidor": "192.0.2.106" } }""", null)]
    public async Task Sin_mudanza_escribir_no_toca_Escribir_ni_pone_Cajas(string antes, string? queda)
    {
        PaqueteDePrueba.En(Usb);
        Empezar(antes, bom: false);

        var (codigo, dice) = await CorrerAsync("escribir", mudanza: null);

        Assert.True(codigo == 0, dice["error"]);
        Assert.Equal(queda, Valor(Eleventa(), "Escribir"));
        Assert.Null(Valor(Eleventa(), "Cajas"));
        Assert.DoesNotContain("Escribir", dice["cambios"]);
    }

    /// <summary>
    /// Sin --mudanza, ni un byte distinto de lo que el instalador escribía antes de que existiera la mudanza. Lo de abajo es, tal cual,
    /// lo que dejaba el ayudante de la versión anterior (93cc056) con este servicio.json, este caja.json y estas respuestas: se sacó
    /// corriéndolo, no de este código. Y sigue siendo eso con un paquete de mudanza junto y con las respuestas de la mudanza, mientras no
    /// se pida --mudanza: Escribir se queda como estaba, Cajas no se toca y la caja de omisión es la que se contesta. (Se contesta la 2:
    /// la 1 es la de la serie A en Cajas, y cambiar la de omisión a una de Cajas ya no se puede; lo de abajo se volvió a sacar así del
    /// ayudante de 93cc056.)
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_mudanza_no_cambia_ni_un_byte_de_lo_que_se_escribia(bool conLoDeLaMudanza)
    {
        const string servicioDeAntes = "// A mano, con el Bloc de notas.\r\n{\r\n  \"Eleventa\": {\r\n    \"Activo\": true,\r\n    \"Escribir\": true, // ya escribe\r\n"
            + "    \"Servidor\": \"192.0.2.106\",\r\n    \"Base\": \"C:\\\\Program Files (x86)\\\\AbarrotesMultiCaja\\\\db\\\\PDVDATA.FDB\",\r\n    \"Caja\": 3,\r\n"
            + "    \"Cajero\": 1,\r\n    \"Cajas\": { \"A\": { \"Caja\": 1 }, \"b\": { \"Caja\": 9, \"Cajero\": 7 } },\r\n    \"SegundosMaximos\": 45\r\n  },\r\n"
            + "  \"ControlPrint\": { \"Servidor\": \"192.0.2.106\" }, // la principal\r\n  \"Registro\": { \"Dias\": 45 }\r\n}\r\n";
        const string cajaDeAntes = "{ \"Servicio\": \"http://localhost:5080/\", \"CajaId\": \"5a1d4c3e-0000-4000-8000-000000000002\", \"Impresora\": \"tcp://192.0.2.100\", "
            + "\"LlaveProtegida\": \"AQAAANCMnd8B\", \"ColaImpresion\": \"WF-6590\", \"Lector\": { \"Puerto\": \"com3\", \"Baudios\": 9600 } }";
        Directory.CreateDirectory(Path.Combine(_pc.Datos, "Caja"));
        File.WriteAllText(Ruta, servicioDeAntes, new UTF8Encoding(true));
        File.WriteAllText(CajaJson.Ruta(_pc.Datos), cajaDeAntes, new UTF8Encoding(false));
        var respuestas = new ArchivoDeClaves().Poner("rol", "principal").Poner("impresora", "tcp://192.0.2.100:9100").Poner("cola", "WF-6590")
            .Poner("lector", "com").Poner("lector-puerto", "COM3").Poner("lector-baudios", "9600").Poner("controlprint", "si")
            .Poner("controlprint-servidor", "192.0.2.106").Poner("eleventa", "si").Poner("eleventa-servidor", "127.0.0.1")
            .Poner("eleventa-base", PaqueteDePrueba.BaseDeEleventa).Poner("eleventa-caja", "2").Poner("eleventa-cajero", "1")
            .Poner("eleventa-gbak", @"C:\ProgramData\PapeleriaPos\firebird20\bin\gbak.exe")
            .Poner("eleventa-respaldos", @"C:\ProgramData\PapeleriaPos\respaldos-eleventa");
        if (conLoDeLaMudanza)
        {
            // El paquete de la caja 2 (que allá escribía como la caja 3) está junto al instalador, y la página dice «traer».
            PaqueteDePrueba.En(Usb);
            respuestas.Poner("mudanza", "traer").Poner("mudanza-paquete", Usb).Poner("mudanza-encima", "si");
        }

        var (codigo, dice) = await CorrerAsync("escribir", respuestas, mudanza: null);

        Assert.Equal(0, codigo);
        Assert.Equal(
        [
            ("servicio-json", "cambio"), ("servicio-json-completo", "no"), ("caja-json", "cambio"), ("error", ""),
            ("cambios", "Eleventa:Servidor; Eleventa:Caja; Eleventa:Gbak (se agrega); Eleventa:CarpetaRespaldos (se agrega); caja.json: Impresora"),
        ], dice.Datos);
        Assert.Equal("\uFEFF// A mano, con el Bloc de notas.\r\n{\r\n  \"Eleventa\": {\r\n    \"Activo\": true,\r\n    \"Escribir\": true, // ya escribe\r\n"
            + "    \"Servidor\": \"127.0.0.1\",\r\n    \"Base\": \"C:\\\\Program Files (x86)\\\\AbarrotesMultiCaja\\\\db\\\\PDVDATA.FDB\",\r\n    \"Caja\": 2,\r\n"
            + "    \"Cajero\": 1,\r\n    \"Cajas\": { \"A\": { \"Caja\": 1 }, \"b\": { \"Caja\": 9, \"Cajero\": 7 } },\r\n    \"SegundosMaximos\": 45,\r\n"
            + "    \"Gbak\": \"C:\\\\ProgramData\\\\PapeleriaPos\\\\firebird20\\\\bin\\\\gbak.exe\",\r\n"
            + "    \"CarpetaRespaldos\": \"C:\\\\ProgramData\\\\PapeleriaPos\\\\respaldos-eleventa\"\r\n  },\r\n"
            + "  \"ControlPrint\": { \"Servidor\": \"192.0.2.106\" }, // la principal\r\n  \"Registro\": { \"Dias\": 45 }\r\n}\r\n",
            Encoding.UTF8.GetString(File.ReadAllBytes(Ruta)));
        // caja.json se escribe entero, con el salto de renglón del sistema.
        Assert.Equal(("{\n  \"Servicio\": \"http://localhost:5080/\",\n  \"CajaId\": \"5a1d4c3e-0000-4000-8000-000000000002\",\n"
            + "  \"Impresora\": \"tcp://192.0.2.100:9100\",\n  \"LlaveProtegida\": \"AQAAANCMnd8B\",\n  \"ColaImpresion\": \"WF-6590\",\n"
            + "  \"Lector\": {\n    \"Puerto\": \"com3\",\n    \"Baudios\": 9600\n  }\n}\n").Replace("\n", Environment.NewLine),
            Encoding.UTF8.GetString(File.ReadAllBytes(CajaJson.Ruta(_pc.Datos))));
        // Y lo de antes, a un lado, entero.
        Assert.Equal("\uFEFF" + servicioDeAntes, Encoding.UTF8.GetString(File.ReadAllBytes(Ruta + ".anterior")));
        Assert.Equal(cajaDeAntes, Encoding.UTF8.GetString(File.ReadAllBytes(CajaJson.Ruta(_pc.Datos) + ".anterior")));
    }

    /// <summary>
    /// Una Cajas hecha a mano se conserva: la serie que se muda queda con su número de allá dentro de la entrada que ya tenía (sin
    /// perder su cajero propio ni cambiarle el nombre), las otras series no se tocan, y los comentarios del archivo se quedan todos.
    /// </summary>
    [Fact]
    public async Task Una_Cajas_hecha_a_mano_se_conserva_con_su_cajero_sus_otras_series_y_sus_comentarios()
    {
        PaqueteDePrueba.En(Usb);
        Empezar(HechoAMano);

        var dice = await EscribirAsync(Respuestas(("eleventa-caja", "1"), ("eleventa-servidor", "192.0.2.106"), ("controlprint", "no")));

        Assert.Equal("no", dice["servicio-json-completo"]);
        Assert.Equal("Eleventa:Escribir; Eleventa:Cajas:b:Caja; Eleventa:Gbak (se agrega); Eleventa:CarpetaRespaldos (se agrega); caja.json (nuevo)",
            dice["cambios"]);
        var texto = File.ReadAllText(Ruta);
        var esperado = HechoAMano.ReplaceLineEndings("\r\n")
            .Replace("\"Escribir\": false, // todavía no", "\"Escribir\": true, // todavía no")
            .Replace("\"b\": { \"Caja\": 9, \"Cajero\": 7 }", "\"b\": { \"Caja\": 3, \"Cajero\": 7 }")
            .Replace("    \"SegundosMaximos\": 45\r\n", "    \"SegundosMaximos\": 45,\r\n"
                + $"    \"Gbak\": {Json.Escribir(JsonValue.Create(RespuestasDePrueba.Gbak(_carpeta.Ruta)))},\r\n"
                + "    \"CarpetaRespaldos\": \"C:\\\\ProgramData\\\\PapeleriaPos\\\\respaldos-eleventa\"\r\n");
        Assert.Equal(esperado, texto.TrimStart('\uFEFF'));
        Assert.Equal(HechoAMano.ReplaceLineEndings("\r\n"), File.ReadAllText(Ruta + ".anterior").TrimStart('\uFEFF'));
    }

    /// <summary>
    /// La caja de omisión de esta PC es SOLO la que aquí se conteste: vacía, se quita la que hubiera, y nunca se hereda la de allá (era
    /// la de la caja que se mudó: aquí la tomaría toda caja que no esté en Cajas, y escribiría en su turno).
    /// </summary>
    [Theory]
    [InlineData("{ }", "", null)]
    [InlineData("""{ "Eleventa": { "Activo": true, "Caja": 5, "Cajero": 2 } }""", "", null)]
    [InlineData("""{ "Eleventa": { "Activo": true, "Caja": 5 } }""", "1", "1")]
    [InlineData("{ }", "7", "7")]
    // El 0 de la plantilla es «falta»: se queda como está.
    [InlineData("""{ "Eleventa": { "Activo": true, "Caja": 0 } }""", "", "0")]
    public async Task La_caja_de_omision_es_la_que_se_contesta_aqui_y_vacia_no_hereda_la_de_alla(string antes, string contestada, string? queda)
    {
        // Allá la de omisión era la 3, la misma de la caja que se muda.
        var paquete = PaqueteDePrueba.En(Usb);
        Assert.Equal("3", paquete["eleventa-caja"]);
        Empezar(antes, bom: false);

        await EscribirAsync(Respuestas(("eleventa-caja", contestada)));

        Assert.Equal(queda, Valor(Eleventa(), "Caja"));
        Assert.Equal("3", Valor(Eleventa(), "Cajas", "B", "Caja"));
    }

    /// <summary>El cajero de omisión: el que aquí se contestó; si se dejó vacío, el de allá.</summary>
    [Theory]
    [InlineData("4", "1", "4")]
    [InlineData("", "1", "1")]
    [InlineData("4", "", "4")]
    public async Task El_cajero_es_el_que_se_contesta_aqui_y_si_no_el_de_alla(string contestado, string deAlla, string queda)
    {
        PaqueteDePrueba.En(Usb, ("eleventa-cajero", deAlla));
        Empezar("""{ "Eleventa": { "Activo": true, "Cajero": 8 } }""", bom: false);

        await EscribirAsync(Respuestas(("eleventa-cajero", contestado)));

        Assert.Equal(queda, Valor(Eleventa(), "Cajero"));
    }

    /// <summary>
    /// El puerto y el usuario de Firebird quedan como allá (es el mismo eleventa): se escriben si no son los de siempre, o si aquí había
    /// otros; los de siempre no se le agregan a una sección que ya estaba.
    /// </summary>
    [Theory]
    [InlineData("{ }", "3050", "SYSDBA", "3050", null)]
    [InlineData("{ }", "3051", "ELEVENTA", "3051", "ELEVENTA")]
    [InlineData("""{ "Eleventa": { "Activo": true } }""", "3050", "SYSDBA", null, null)]
    [InlineData("""{ "Eleventa": { "Activo": true } }""", "", "", null, null)]
    [InlineData("""{ "Eleventa": { "Activo": true, "Puerto": 3055, "Usuario": "OTRO" } }""", "3050", "SYSDBA", "3050", "SYSDBA")]
    [InlineData("""{ "Eleventa": { "Activo": true, "Puerto": "3051", "Usuario": "eleventa" } }""", "3051", "ELEVENTA", "3051", "eleventa")]
    public async Task El_puerto_y_el_usuario_quedan_como_alla(string antes, string puerto, string usuario, string? quedaPuerto, string? quedaUsuario)
    {
        PaqueteDePrueba.En(Usb, ("eleventa-puerto", puerto), ("eleventa-usuario", usuario));
        Empezar(antes, bom: false);

        await EscribirAsync();

        Assert.Equal(quedaPuerto, Valor(Eleventa(), "Puerto"));
        Assert.Equal(quedaUsuario, Valor(Eleventa(), "Usuario"));
    }

    /// <summary>
    /// De la otra PC nunca sale una ruta (el gbak, la carpeta de los respaldos), el servidor, la base, lo de Control Print ni la clave,
    /// aunque el mudanza.txt los traiga: todo eso es de ESTA PC, de sus respuestas.
    /// </summary>
    [Fact]
    public async Task De_alla_nunca_salen_rutas_ni_el_servidor_ni_la_clave_ni_nada_de_fuera_de_eleventa()
    {
        // Solo leía y sin nada pendiente: aquí la base y el servidor se pueden contestar distintos.
        PaqueteDePrueba.En(Usb, ("eleventa-escribir", "no"), ("eleventa-servidor", "192.0.2.199"), ("eleventa-base", @"D:\de-alla\OTRA.FDB"),
            ("eleventa-base-de-la-bandeja", ""), ("controlprint-servidor", "192.0.2.198"), ("eleventa-gbak", @"D:\de-alla\gbak.exe"),
            ("eleventa-respaldos", @"D:\de-alla\respaldos"), ("eleventa-clave", "masterkey"), ("Clave", "masterkey"), ("controlprint-codigo", "ABCD-1234"),
            ("Registro", "de-alla"));
        Empezar("{\r\n  \"Registro\": { \"Dias\": 45 }\r\n}\r\n", bom: false);

        var dice = await EscribirAsync(Respuestas(("eleventa-servidor", "127.0.0.1"), ("eleventa-base", @"C:\de-aqui\PDVDATA.FDB"), ("eleventa-gbak", ""),
            ("eleventa-respaldos", ""), ("controlprint", "no")));

        var texto = File.ReadAllText(Ruta);
        foreach (var deAlla in new[] { "de-alla", "192.0.2.199", "192.0.2.198", "masterkey", "ABCD-1234", "Clave", "Gbak", "CarpetaRespaldos", "ControlPrint" })
        {
            Assert.DoesNotContain(deAlla, texto);
            Assert.DoesNotContain(deAlla, File.ReadAllText(Salida));
        }
        Assert.Equal("127.0.0.1", Valor(Eleventa(), "Servidor"));
        Assert.Equal(@"C:\de-aqui\PDVDATA.FDB", Valor(Eleventa(), "Base"));
        Assert.Equal("false", Valor(Eleventa(), "Escribir"));
        Assert.Equal("""{"Dias":45}""", Valor(Leido(), "Registro"));
        Assert.Equal("cambio", dice["servicio-json"]);
    }

    /// <summary>Con «No» en eleventa (allá no había puente), la mudanza no escribe nada del puente: la sección ni se crea.</summary>
    [Fact]
    public async Task Sin_puente_alla_y_con_no_en_eleventa_no_se_escribe_nada_del_puente()
    {
        PaqueteDePrueba.En(Usb, ("eleventa", "no"), ("eleventa-escribir", "no"), ("eleventa-cajas", "B=3"));
        Empezar("{\r\n  // la plantilla\r\n}\r\n");
        var antes = File.ReadAllBytes(Ruta);

        var dice = await EscribirAsync(Respuestas(("eleventa", "no"), ("controlprint", "no")));

        Assert.Equal("igual", dice["servicio-json"]);
        Assert.Equal(antes, File.ReadAllBytes(Ruta));
    }

    // ── Solo lo que cambió ──

    [Fact]
    public async Task Otra_vez_con_lo_mismo_no_escribe_y_lo_de_antes_queda_en_anterior()
    {
        PaqueteDePrueba.En(Usb);
        Empezar(Plantilla());
        var plantilla = File.ReadAllBytes(Ruta);

        var primera = await EscribirAsync();

        Assert.Equal("cambio", primera["servicio-json"]);
        Assert.Equal("cambio", primera["caja-json"]);
        // Lo de antes quedó a un lado, entero.
        Assert.Equal(plantilla, File.ReadAllBytes(Ruta + ".anterior"));
        var escrito = File.ReadAllBytes(Ruta);
        var caja = File.ReadAllBytes(CajaJson.Ruta(_pc.Datos));
        File.Delete(Ruta + ".anterior");

        var segunda = await EscribirAsync();

        Assert.Equal("igual", segunda["servicio-json"]);
        Assert.Equal("igual", segunda["caja-json"]);
        Assert.Equal("", segunda["cambios"]);
        Assert.Equal(escrito, File.ReadAllBytes(Ruta));
        Assert.Equal(caja, File.ReadAllBytes(CajaJson.Ruta(_pc.Datos)));
        Assert.False(File.Exists(Ruta + ".anterior"));
        Assert.Equal(["Caja", "servicio.json"], Directory.GetFileSystemEntries(_pc.Datos).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>Revisar dice lo que escribir cambiaría, sin escribir nada (ni «.anterior» ni «.nuevo»).</summary>
    [Fact]
    public async Task Revisar_con_mudanza_dice_lo_que_cambiaria_y_no_escribe()
    {
        PaqueteDePrueba.En(Usb);
        Empezar(HechoAMano);
        var antes = File.ReadAllBytes(Ruta);
        var respuestas = Respuestas(("eleventa-caja", "1"), ("eleventa-servidor", "192.0.2.106"), ("controlprint", "no"));

        var (codigo, revisado) = await CorrerAsync("revisar", respuestas);

        Assert.Equal(0, codigo);
        Assert.Equal("", revisado["error"]);
        Assert.Equal("cambio", revisado["servicio-json"]);
        Assert.Equal(antes, File.ReadAllBytes(Ruta));
        Assert.Equal(["servicio.json"], Directory.GetFileSystemEntries(_pc.Datos).Select(Path.GetFileName));
        // Y es lo mismo que después dice escribir.
        var escrito = await EscribirAsync(respuestas);
        Assert.Equal(revisado["cambios"], escrito["cambios"]);
    }

    // ── Lo que no se puede, no se escribe ──

    /// <summary>
    /// Lo que mudanza-revisar revisó antes de traer la base se revisa otra vez al escribir, con el mismo código: si para entonces ya no
    /// se cumple (se movió el gbak, cambió una respuesta), no se escribe nada y se dice con su clave.
    /// </summary>
    [Theory]
    [InlineData("eleventa", "no", "eleventa", "no se puede contestar «No» a eleventa")]
    [InlineData("eleventa-base", @"C:\eleventa\PDVDATA-ENSAYO.FDB", "eleventa-base", "En una mudanza la base de eleventa no se cambia")]
    [InlineData("eleventa-gbak", "", "eleventa-gbak", "hace falta el gbak de Firebird 2.0 de esta PC")]
    [InlineData("eleventa-gbak", @"C:\no-esta\gbak.exe", "eleventa-gbak", "hace falta el gbak de Firebird 2.0 de esta PC")]
    [InlineData("eleventa-caja", "3", "eleventa-caja", "La caja 3 de eleventa es la de «Caja 2» (serie B)")]
    public async Task Lo_que_no_cumple_las_reglas_de_la_mudanza_no_se_escribe(string clave, string valor, string claveDelError, string dice)
    {
        PaqueteDePrueba.En(Usb);
        Empezar(HechoAMano);
        var antes = File.ReadAllBytes(Ruta);

        foreach (var orden in new[] { "revisar", "escribir" })
        {
            var (codigo, salida) = await CorrerAsync(orden, Respuestas((clave, valor)));

            Assert.Equal(2, codigo);
            Assert.Contains(dice, salida["error"]);
            Assert.Equal(claveDelError, salida["clave"]);
            Assert.Equal(antes, File.ReadAllBytes(Ruta));
            // Ni servicio.json ni caja.json: no queda uno cambiado y el otro no.
            Assert.Equal(["servicio.json"], Directory.GetFileSystemEntries(_pc.Datos).Select(Path.GetFileName));
        }
    }

    /// <summary>
    /// El número de ESTA PC en eleventa se deja vacío el día de la mudanza y se pone después, corriendo el instalador otra vez: esa
    /// corrida ya no es una mudanza (el paquete ya se trajo), y tampoco ahí puede ser el de una caja que se mudó (Eleventa:Cajas). Con
    /// él, la caja de esta PC, que no está en Cajas, escribiría en eleventa como la caja 2, en su turno. Se dice con su clave y no se
    /// escribe nada; otro número sí se pone. Y lo que ya estaba así (a mano, de antes) no detiene al instalador si no se cambia.
    /// </summary>
    [Fact]
    public async Task Despues_de_la_mudanza_el_numero_de_esta_pc_no_puede_ser_el_de_una_caja_que_se_mudo()
    {
        PaqueteDePrueba.En(Usb);
        Empezar(Plantilla());
        await EscribirAsync();
        Assert.Equal("3", Valor(Eleventa(), "Cajas", "B", "Caja"));
        Assert.Null(Valor(Eleventa(), "Caja"));
        var traida = File.ReadAllBytes(Ruta);
        // La corrida de después: «Sin mudanza», con el número de esta PC.
        ArchivoDeClaves Despues(string caja) => Respuestas(("mudanza", ""), ("mudanza-paquete", ""), ("eleventa-caja", caja));

        foreach (var orden in new[] { "revisar", "escribir" })
        {
            var (codigo, dice) = await CorrerAsync(orden, Despues("3"), mudanza: null);

            Assert.Equal(2, codigo);
            Assert.Equal("eleventa-caja", dice["clave"]);
            Assert.Equal("La caja 3 de eleventa ya es la de la caja del POS con la serie B (en Eleventa:Cajas): el número de ESTA PC en eleventa es otro. "
                + "Pon el suyo, o déjalo vacío si no lo sabes.", dice["error"]);
            Assert.Equal(traida, File.ReadAllBytes(Ruta));
        }

        await EscribirAsync(Despues("1"));
        Assert.Equal("1", Valor(Eleventa(), "Caja"));
        Assert.Equal("3", Valor(Eleventa(), "Cajas", "B", "Caja"));
        // Cambiarlo después a uno de Cajas tampoco: dice cuál tenía.
        var (otraVez, conElSuyo) = await CorrerAsync("escribir", Despues("3"), mudanza: null);
        Assert.Equal(2, otraVez);
        Assert.EndsWith("Pon el suyo (hasta hoy era la 1).", conElSuyo["error"]);

        // Una configuración hecha así a mano antes de todo esto, sin cambiarla, se queda como está.
        Empezar("""{ "Eleventa": { "Activo": true, "Escribir": true, "Caja": 3, "Cajero": 1, "Cajas": { "B": { "Caja": 3 } } } }""", bom: false);
        var aMano = File.ReadAllBytes(Ruta);
        var (codigoAMano, aManoDice) = await CorrerAsync("revisar", Despues("3"), mudanza: null);
        Assert.True(codigoAMano == 0, aManoDice["error"]);
        Assert.Equal(aMano, File.ReadAllBytes(Ruta));
    }

    [Fact]
    public async Task Con_el_puente_escribiendo_y_sin_cajero_para_la_caja_que_se_muda_no_se_escribe()
    {
        PaqueteDePrueba.En(Usb, ("eleventa-cajero", ""));
        Empezar("{ }", bom: false);

        var (codigo, salida) = await CorrerAsync("escribir", Respuestas(("eleventa-cajero", "")));

        Assert.Equal(2, codigo);
        Assert.Equal("eleventa-cajero", salida["clave"]);
        Assert.Equal("{ }", File.ReadAllText(Ruta));
    }

    /// <summary>Sin el mudanza.txt (se quitó la USB) o con uno que no se entiende, no hay con qué escribir: se dice, y no se cambia nada.</summary>
    [Fact]
    public async Task Sin_el_paquete_o_con_uno_que_no_se_entiende_no_se_escribe_nada()
    {
        Empezar("{ }", bom: false);

        var (codigo, salida) = await CorrerAsync("escribir");

        Assert.Equal(2, codigo);
        Assert.StartsWith($"No se pudo leer el paquete de mudanza de {Usb}: ", salida["error"]);
        Assert.EndsWith("No se cambió nada.", salida["error"]);
        Assert.Equal("mudanza-paquete", salida["clave"]);

        PaqueteDePrueba.En(Usb, ("eleventa-ajustes", @"Gbak=C:\de-alla\gbak.exe"));
        (codigo, salida) = await CorrerAsync("escribir");
        Assert.Equal(2, codigo);
        Assert.Equal($"El paquete de mudanza de {Usb} no se entiende (mudanza.txt: «eleventa-ajustes» trae «Gbak», que no es un ajuste del puente que viaje "
            + "en una mudanza). No se cambió nada.", salida["error"]);
        Assert.Equal("mudanza-paquete", salida["clave"]);
        Assert.Equal("{ }", File.ReadAllText(Ruta));
        Assert.Equal(["servicio.json"], Directory.GetFileSystemEntries(_pc.Datos).Select(Path.GetFileName));
    }

    /// <summary>
    /// De la carpeta solo hace falta su mudanza.txt: la base ya se puso en esta PC, y puede no estar ya en la USB. Y la carpeta puede
    /// llegar con la comilla que deja «"E:\mudanza\"» en una línea de órdenes de Windows.
    /// </summary>
    [Fact]
    public async Task De_la_carpeta_solo_se_lee_su_mudanza_txt()
    {
        var paquete = PaqueteDePrueba.En(Usb);
        File.Delete(paquete.RutaDeLaBase);
        Empezar("{ }", bom: false);

        var (codigo, salida) = await CorrerAsync("escribir", mudanza: Usb + "\"");

        Assert.True(codigo == 0, salida["error"]);
        Assert.Equal("3", Valor(Eleventa(), "Cajas", "B", "Caja"));
    }

    /// <summary>Con --mudanza y sin decir la carpeta no se busca un mudanza.txt en la carpeta desde donde se corrió: se dice que falta.</summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("\"\"")]
    public async Task Sin_decir_la_carpeta_de_la_mudanza_no_se_escribe_nada(string carpeta)
    {
        Empezar("{ }", bom: false);

        foreach (var orden in new[] { "revisar", "escribir" })
        {
            var (codigo, salida) = await CorrerAsync(orden, mudanza: carpeta);

            Assert.Equal(2, codigo);
            Assert.Equal("Falta la carpeta del paquete de mudanza. No se cambió nada.", salida["error"]);
            Assert.Equal("mudanza-paquete", salida["clave"]);
            Assert.Equal("{ }", File.ReadAllText(Ruta));
        }
    }

    /// <summary>La configuración de una mudanza es la del servicio: en «Otra caja» no hay servicio.json donde ponerla.</summary>
    [Fact]
    public async Task En_otra_caja_no_hay_donde_escribir_la_mudanza()
    {
        PaqueteDePrueba.En(Usb);
        var respuestas = new ArchivoDeClaves().Poner("rol", "otra").Poner("principal", "192.0.2.50").Poner("impresora", "").Poner("lector", "teclado");

        var (codigo, salida) = await CorrerAsync("escribir", respuestas);

        Assert.Equal(2, codigo);
        Assert.Contains("no con «Otra caja»", salida["error"]);
        Assert.Equal("rol", salida["clave"]);
        Assert.False(Directory.Exists(_pc.Datos));
    }

    // ── La base de eleventa, como la compara el puente ──

    /// <summary>
    /// La ruta de la base de eleventa se compara aquí como la compara el puente al arrancar (ServidorEleventa.RutaDe, sin distinguir
    /// mayúsculas): si aquí pasara una que allá es «otra base», el puente llegaría detenido.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB")]
    [InlineData(@"  C:\eleventa\PDVDATA.FDB  ")]
    [InlineData(@"C:\eleventa\PDVDATA.FDB.")]
    [InlineData(@"C:\eleventa\PDVDATA.FDB . .")]
    [InlineData(@"C:\eleventa\.oculta\PDVDATA.FDB")]
    [InlineData("")]
    public void La_ruta_de_la_base_de_eleventa_se_limpia_como_en_el_puente(string ruta)
    {
        Assert.Equal(ServidorEleventa.RutaDe(ruta), ServicioJson.RutaDe(ruta));
        Assert.True(ServicioJson.MismaRuta(ruta, ServidorEleventa.RutaDe(ruta).ToUpperInvariant()));
        Assert.False(ServicioJson.MismaRuta(ruta, ruta.Trim() + "x"));
    }
}
