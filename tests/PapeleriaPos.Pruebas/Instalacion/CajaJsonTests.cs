using System.Text.Json;
using System.Text.Json.Nodes;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Pruebas.Interfaz;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// Lo que el instalador escribe en caja.json (el contrato con «arranque»): solo Servicio, Impresora, ColaImpresion y Lector, sobre lo que
/// ya tiene, sin quitar nunca su alta (CajaId, LlaveProtegida, LlaveDelServicio, DireccionDeConfianza).
/// </summary>
public sealed class CajaJsonTests : IDisposable
{
    private readonly CarpetaDePrueba _datos = new();

    public void Dispose() => _datos.Dispose();

    private string Ruta => CajaJson.Ruta(_datos.Ruta);

    private JsonObject Leida() => (JsonObject)JsonNode.Parse(File.ReadAllText(Ruta))!;

    /// <summary>Como la guarda la caja después de darse de alta (ConfiguracionCaja.Guardar), con el lector de eleventa ya puesto.</summary>
    private const string DadaDeAlta = """
        {
          "Servicio": "http://192.168.1.50:5080/",
          "CajaId": "5a1d4c3e-0000-4000-8000-000000000002",
          "Impresora": "tcp://192.168.1.100:9100",
          "LlaveProtegida": "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA",
          "ColaImpresion": "WF-6590",
          "QuitarImpresos": true,
          "Prueba": false,
          "ServicioLocal": null,
          "BaseInicial": null,
          "CarpetaDatos": null,
          "LlaveDelServicio": "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE",
          "DireccionDeConfianza": "http://192.168.1.50:5080/",
          "Red": { "DetalleWifi": true },
          "Lector": { "Puerto": "COM3", "Baudios": 9600, "Paridad": "Ninguna", "Fin": "CR" }
        }
        """;

    private void Escribir(string texto)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);
        File.WriteAllText(Ruta, texto);
    }

    [Fact]
    public void Otra_caja_nueva_apunta_a_la_principal_que_se_dio_y_lo_demas_como_el_del_programa()
    {
        var escrito = CajaJson.Escribir(_datos.Ruta, new Respuestas
        {
            Rol = RolDeLaPc.Otra, Principal = "192.168.1.50", Impresora = "tcp://192.168.1.100:9100", Cola = "WF-6590", LectorPuerto = "COM3", LectorBaudios = 19200,
        });

        Assert.Equal("caja.json (nuevo)", escrito.Cambios[0]);
        var caja = Leida();
        Assert.Equal("http://192.168.1.50:5080/", (string?)caja["Servicio"]);
        Assert.True(caja.ContainsKey("CajaId"));
        Assert.Null(caja["CajaId"]);
        Assert.Equal("tcp://192.168.1.100:9100", (string?)caja["Impresora"]);
        Assert.Equal("WF-6590", (string?)caja["ColaImpresion"]);
        // El lector, como lo lee la caja.
        var (ajustes, problema) = AjustesLector.Leer(JsonDocument.Parse(caja["Lector"]!.ToJsonString()).RootElement);
        Assert.Null(problema);
        Assert.Equal("COM3", ajustes!.Puerto);
        Assert.Equal(19200, ajustes.Baudios);
    }

    [Fact]
    public void Sobre_una_caja_dada_de_alta_se_queda_su_alta_y_todo_lo_demas()
    {
        Escribir(DadaDeAlta);

        var escrito = CajaJson.Escribir(_datos.Ruta, new Respuestas
        {
            Rol = RolDeLaPc.Otra, Principal = "192.168.1.51", Impresora = "Tickets", Cola = null, LectorPuerto = "COM4", LectorBaudios = 9600,
        });

        Assert.Equal(["caja.json: Servicio", "caja.json: Impresora", "caja.json: ColaImpresion", "caja.json: Lector:Puerto"], escrito.Cambios);
        var antes = (JsonObject)JsonNode.Parse(DadaDeAlta)!;
        var caja = Leida();
        foreach (var clave in new[] { "CajaId", "LlaveProtegida", "LlaveDelServicio", "DireccionDeConfianza", "QuitarImpresos", "Prueba", "Red" })
            Assert.True(Json.Iguales(antes[clave], caja[clave]), clave);
        Assert.Equal("http://192.168.1.51:5080/", (string?)caja["Servicio"]);
        Assert.Equal("Tickets", (string?)caja["Impresora"]);
        Assert.Null(caja["ColaImpresion"]);
        // Del lector, solo el puerto y la velocidad: la paridad y el fin que se copiaron de eleventa se quedan.
        Assert.Equal("""{"Puerto":"COM4","Baudios":9600,"Paridad":"Ninguna","Fin":"CR"}""", caja["Lector"]!.ToJsonString());
        Assert.Equal(DadaDeAlta, File.ReadAllText(Ruta + ".anterior"));
    }

    [Fact]
    public void Con_las_respuestas_que_ya_tiene_no_se_toca()
    {
        Escribir(DadaDeAlta);

        var escrito = CajaJson.Escribir(_datos.Ruta, new Respuestas
        {
            Rol = RolDeLaPc.Otra, Principal = "192.168.1.50", Impresora = "tcp://192.168.1.100:9100", Cola = "WF-6590", LectorPuerto = "COM3", LectorBaudios = 9600,
        });

        Assert.False(escrito.Cambio);
        Assert.Equal(DadaDeAlta, File.ReadAllText(Ruta));
        Assert.False(File.Exists(Ruta + ".anterior"));
    }

    [Fact]
    public void Otra_caja_sin_la_ip_de_la_principal_se_queda_con_la_que_tenia()
    {
        Escribir(DadaDeAlta);

        CajaJson.Escribir(_datos.Ruta, new Respuestas { Rol = RolDeLaPc.Otra, Impresora = "tcp://192.168.1.100:9100", Cola = "WF-6590", LectorPuerto = "COM3" });

        Assert.Equal("http://192.168.1.50:5080/", (string?)Leida()["Servicio"]);
    }

    [Theory]
    [InlineData("http://localhost:5080/", "http://localhost:5080/")]
    [InlineData("http://127.0.0.1:5080/", "http://127.0.0.1:5080/")]
    [InlineData("http://192.168.1.50:5080/", CajaJson.ServicioLocal)]
    public void En_la_principal_la_caja_va_al_servicio_de_la_misma_pc(string tenia, string queda)
    {
        Escribir($$"""{ "Servicio": "{{tenia}}", "CajaId": null, "Impresora": "" }""");

        CajaJson.Escribir(_datos.Ruta, new Respuestas { Rol = RolDeLaPc.Principal });

        Assert.Equal(queda, (string?)Leida()["Servicio"]);
    }

    [Fact]
    public void Como_teclado_el_lector_queda_en_null()
    {
        Escribir(DadaDeAlta);

        CajaJson.Escribir(_datos.Ruta, new Respuestas { Rol = RolDeLaPc.Otra, Impresora = "tcp://192.168.1.100:9100", Cola = "WF-6590" });

        var caja = Leida();
        Assert.True(caja.ContainsKey("Lector"));
        Assert.Null(caja["Lector"]);
        // Como lo lee la caja: sin lector por puerto y sin problema.
        var (ajustes, problema) = AjustesLector.Leer(JsonDocument.Parse("null").RootElement);
        Assert.Null(ajustes);
        Assert.Null(problema);
    }

    /// <summary>Una caja de antes guardaba su caja.json en C:\ProgramData\PapeleriaPos: se parte de ese y se escribe en la carpeta Caja.</summary>
    [Fact]
    public void La_de_la_carpeta_de_antes_pasa_a_la_carpeta_caja_con_su_alta()
    {
        File.WriteAllText(CajaJson.RutaDeAntes(_datos.Ruta), DadaDeAlta);

        var escrito = CajaJson.Escribir(_datos.Ruta, new Respuestas
        {
            Rol = RolDeLaPc.Otra, Impresora = "tcp://192.168.1.100:9100", Cola = "WF-6590", LectorPuerto = "COM3",
        });

        Assert.Equal(["caja.json (pasa a la carpeta Caja)"], escrito.Cambios);
        Assert.True(Json.Iguales(JsonNode.Parse(DadaDeAlta), Leida()));
        Assert.True(File.Exists(CajaJson.RutaDeAntes(_datos.Ruta)));
    }

    [Fact]
    public void Mal_escrita_no_se_toca()
    {
        Escribir("{ \"Servicio\": \"http://localhost:5080\", // comentario\n}");

        Assert.Throws<FormatException>(() => CajaJson.Escribir(_datos.Ruta, new Respuestas { Rol = RolDeLaPc.Principal }));

        Assert.StartsWith("{ \"Servicio\"", File.ReadAllText(Ruta));
    }
}
