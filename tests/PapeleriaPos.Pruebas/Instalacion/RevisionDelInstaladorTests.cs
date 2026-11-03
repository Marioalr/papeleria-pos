using System.Text;
using System.Text.Json.Nodes;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// De la revisión independiente del instalador (rama instalacion/desde-cero, fc0b026): las dos primeras probaban un hallazgo (ya
/// corregido) y las otras, algo que se revisó y estaba bien.
/// </summary>
public sealed class RevisionDelInstaladorTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();

    public void Dispose() => _carpeta.Dispose();

    private string Datos => Path.Combine(_carpeta.Ruta, "datos");

    private sealed class Pc(string datos, string programa, string? servicio) : IEquipo
    {
        public string Datos => datos;

        public string Programa => programa;

        public string? ExeDelServicio() => servicio;

        public string? Version(string exe) => "1.0.60";

        public IReadOnlyList<ImpresoraDeWindows> Impresoras() => [];

        public IReadOnlyList<string> PuertosCom() => [];

        public IReadOnlyList<string> Ips() => [];

        public IReadOnlyList<string> Gbak() => [];

        public string? IniDeEleventa() => null;
    }

    /// <summary>
    /// Lo que deja el asistente en respuestas.txt si nadie cambia nada (papeleria-pos.iss: Prellenar y GuardarRespuestas, 1264-1308): cada
    /// campo pasa tal cual (con Trim), y lo único que revisa antes (RevisarEleventa, 1187-1188) es que la caja y el cajero sean cifras.
    /// </summary>
    private static ArchivoDeClaves ComoLasPasaElAsistente(ArchivoDeClaves leido)
    {
        var r = new ArchivoDeClaves().Poner("rol", leido["rol"]).Poner("impresora", leido["ticket"]).Poner("cola", leido["cola"]).Poner("lector", "teclado");
        r.Poner("controlprint", leido["controlprint"]);
        if (leido["controlprint"] == "si")
            r.Poner("controlprint-servidor", leido["controlprint-servidor"]);
        r.Poner("eleventa", leido["eleventa"]);
        foreach (var clave in new[] { "eleventa-servidor", "eleventa-base", "eleventa-caja", "eleventa-cajero", "eleventa-gbak", "eleventa-respaldos" })
            r.Poner(clave, leido[clave]);
        return r;
    }

    /// <summary>El SoloCifras del asistente (papeleria-pos.iss:178-186): no vacío y solo de 0 a 9.</summary>
    private static bool SoloCifras(string texto) => texto.Length > 0 && texto.All(c => c is >= '0' and <= '9');

    /// <summary>
    /// Hallazgo (MEDIA), corregido. La plantilla (instalacion/plantilla-servicio.json) trae de ejemplo «"Caja": 0» y «"Cajero": 0». Con
    /// ese bloque descomentado tal cual, el asistente lo dejaba pasar y el ayudante lo rechazaba en <c>escribir</c>, que instalar-pc.ps1
    /// corre después de actualizar el programa: la corrida decía «Lo de antes sigue como estaba» con el programa ya actualizado. Ahora el
    /// 0 es «falta» para los dos (servicio.json se queda como está), y el asistente corre <c>revisar</c> antes de cambiar nada.
    /// </summary>
    [Fact]
    public void Lo_que_el_asistente_deja_pasar_de_la_pc_el_ayudante_lo_escribe()
    {
        Directory.CreateDirectory(Path.Combine(Datos, "Caja"));
        File.WriteAllText(Path.Combine(Datos, "servicio.json"), """
            // La plantilla con el bloque de eleventa descomentado: solo se puso la IP (para leer no hacen falta la caja ni el cajero).
            {
              "Eleventa": {
                "Activo": true,
                "Escribir": false,
                "Servidor": "192.0.2.10",
                "Puerto": 3050,
                "Base": "C:\\Program Files (x86)\\AbarrotesMultiCaja\\db\\PDVDATA.FDB",
                "Caja": 0,
                "Cajero": 0
              }
            }
            """);
        File.WriteAllText(CajaJson.Ruta(Datos), """{ "Servicio": "http://localhost:5080/", "CajaId": null, "Impresora": "", "ColaImpresion": null }""");

        var leido = LecturaDeLaPc.Leer(new Pc(Datos, Path.Combine(_carpeta.Ruta, "programa"), "servicio.exe"));
        Assert.Equal("", leido["error"]);
        Assert.Equal("0", leido["eleventa-caja"]);
        Assert.True(SoloCifras(leido["eleventa-caja"]!) && SoloCifras(leido["eleventa-cajero"]!), "el asistente lo deja pasar");

        var escrito = Ayudante.Escribir(ComoLasPasaElAsistente(leido), Datos);

        // Antes: «Una respuesta no se entiende: El número de la caja en eleventa va con cifras (está «0»).»
        Assert.Equal("", escrito["error"] ?? "");
    }

    /// <summary>
    /// Hallazgo (BAJA), corregido. «Otra caja» instalada sin la IP de la caja principal (se permite: «la caja la busca al abrirla»): el
    /// ayudante deja caja.json en http://localhost:5080. Al volver a correr el instalador antes de que la caja se dé de alta, <c>leer</c>
    /// no sabía que era «Otra caja». La regla: con la caja instalada, sin el servicio y sin servicio.json, es «Otra caja».
    /// </summary>
    [Fact]
    public void Otra_caja_sin_ip_se_reconoce_al_correrlo_otra_vez()
    {
        CajaJson.Escribir(Datos, new Respuestas { Rol = RolDeLaPc.Otra });
        var programa = Path.Combine(_carpeta.Ruta, "programa");
        Directory.CreateDirectory(Path.Combine(programa, "Caja"));
        File.WriteAllText(Path.Combine(programa, "Caja", "PapeleriaPos.Caja.exe"), "");

        var leido = LecturaDeLaPc.Leer(new Pc(Datos, programa, servicio: null));

        Assert.Equal("si", leido["caja-instalada"]);
        Assert.Equal("otra", leido["rol"]);
    }

    /// <summary>
    /// Revisado, bien: ningún camino de ServicioJson prende Escribir. Para cada servicio.json de partida y cada respuesta, Escribir queda
    /// igual que estaba, o en false si la sección es nueva; nunca pasa a true si no lo estaba.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "Eleventa": null }""")]
    [InlineData("""{ "Eleventa": "x" }""")]
    [InlineData("""{ "Eleventa": { "Activo": false, "Escribir": false, "Servidor": "192.0.2.10", "Base": "C:\\B.FDB" } }""")]
    [InlineData("""{ "eleventa": { "activo": true, "escribir": "false" } }""")]
    [InlineData("""{ "Eleventa": { "Activo": true } }""")]
    [InlineData("""{ "Eleventa": { "Activo": false, "Escribir": true, "Servidor": "192.0.2.10" } }""")]
    public void Ningun_camino_prende_Escribir(string antes)
    {
        foreach (var eleventa in new bool?[] { true, false, null })
        foreach (var rol in new[] { RolDeLaPc.Principal, RolDeLaPc.Unica })
        foreach (var controlPrint in new bool?[] { true, false, null })
        {
            var actual = (JsonObject)JsonNode.Parse(antes)!;
            var objetivo = ServicioJson.Mezclar(actual, new Respuestas
            {
                Rol = rol, Eleventa = eleventa, EleventaServidor = "192.0.2.10", EleventaBase = @"C:\B.FDB", EleventaCaja = 3, EleventaCajero = 1,
                ControlPrint = controlPrint, ControlPrintServidor = "192.0.2.10",
            });
            Assert.True(!Escribe(objetivo) || Escribe(actual), $"{antes} con eleventa={eleventa}, rol={rol}: prendió Escribir");
        }
    }

    private static bool Escribe(JsonObject servicio) =>
        Json.Valor(servicio, "Eleventa") is JsonObject e && Json.SiNo(Json.Valor(e, "Escribir"));

    /// <summary>
    /// Revisado, bien: servicio.json como lo deja el Bloc de notas en varias formas (tabuladores, CRLF y BOM, comas de más, comentarios de
    /// bloque y de renglón en cualquier lugar, todo en un renglón, mayúsculas distintas, acentos): los cuatro cambios que hace el
    /// instalador se aplican por partes (sin escribirlo entero) y los comentarios se quedan.
    /// </summary>
    [Theory]
    [InlineData("{\n\t\"Eleventa\": {\n\t\t\"Activo\": true,\n\t\t\"Servidor\": \"192.0.2.10\" // la principal\n\t},\n\t\"ControlPrint\": { \"Servidor\": \"192.0.2.10\" }\n}\n")]
    [InlineData("// Configuración «de la caja 2»\r\n{\r\n  \"Eleventa\": {\r\n    \"Activo\": true, // leer\r\n    \"Servidor\": \"192.0.2.10\",\r\n  },\r\n  \"ControlPrint\": { \"Servidor\": \"192.0.2.10\" }, // la principal\r\n}\r\n// fin\r\n")]
    [InlineData("{ \"Eleventa\": { \"Activo\": true, \"Servidor\": /* ip */ \"192.0.2.10\" }, \"ControlPrint\": { \"Servidor\": \"192.0.2.10\" } }")]
    [InlineData("{\n  \"eleventa\": {\n    \"activo\": true,\n    \"servidor\": \"192.0.2.10\" /* la principal */\n  },\n  /* Control Print */\n  \"controlprint\": {\n    // su IP\n    \"servidor\": \"192.0.2.10\"\n  }\n}")]
    [InlineData("{\n  \"ControlPrint\": { \"Servidor\": \"192.0.2.10\" },\n  \"Eleventa\": {\n    \"Activo\": true,\n    \"Servidor\": \"192.0.2.10\"\n  } // eleventa\n}")]
    [InlineData("\uFEFF{\r\n  \"Eleventa\": {}, \"ControlPrint\": { \"Servidor\": \"192.0.2.10\" }\r\n}")]
    public void El_bloc_de_notas_en_varias_formas_se_cambia_por_partes(string antes)
    {
        var ruta = Path.Combine(_carpeta.Ruta, "servicio.json");
        File.WriteAllBytes(ruta, Encoding.UTF8.GetBytes(antes));
        var comentarios = antes.Split('\n').Count(r => r.Contains("//") || r.Contains("/*"));

        // Cambiar el servidor de eleventa, agregar su gbak, quitar Control Print y pasar a «La única caja».
        var escrito = ServicioJson.Escribir(ruta, new Respuestas
        {
            Rol = RolDeLaPc.Unica, Eleventa = true, EleventaServidor = "192.0.2.20", EleventaBase = @"C:\B.FDB",
            EleventaGbak = @"C:\ProgramData\PapeleriaPos\firebird20\bin\gbak.exe", ControlPrint = false,
        });

        Assert.False(escrito.Completo, Encoding.UTF8.GetString(File.ReadAllBytes(ruta)));
        var quedo = Encoding.UTF8.GetString(File.ReadAllBytes(ruta));
        var objeto = new TextoJsonc(File.ReadAllBytes(ruta)).Objeto();
        Assert.Null(Json.Valor(objeto, "ControlPrint"));
        Assert.True(Json.SiNo(Json.Valor((JsonObject)Json.Valor(objeto, "Instalacion")!, "UnaSolaCaja")));
        Assert.Equal("192.0.2.20", Json.Texto(Json.Valor((JsonObject)Json.Valor(objeto, "Eleventa")!, "Servidor")));
        // Solo se pierden los comentarios de adentro de lo que se quitó (Control Print).
        Assert.True(quedo.Split('\n').Count(r => r.Contains("//") || r.Contains("/*")) >= comentarios - 3, quedo);
        Assert.Equal(antes.StartsWith('\uFEFF'), quedo.StartsWith('\uFEFF'));
    }
}
