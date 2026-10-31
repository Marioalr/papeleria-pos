using System.Text;
using System.Text.Json.Nodes;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// PapeleriaPos.Instalacion.exe como lo corren el asistente y instalar-pc.ps1: cada orden deja lo suyo en su salida, y si algo no se entiende
/// lo dice y no cambia nada.
/// </summary>
public sealed class AyudanteTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();

    public void Dispose() => _carpeta.Dispose();

    private string Datos => Path.Combine(_carpeta.Ruta, "datos");

    private string Salida => Path.Combine(_carpeta.Ruta, "salida.txt");

    private IEquipo Equipo => new EquipoSinWindows(Datos, Path.Combine(_carpeta.Ruta, "programa"));

    private string Respuestas(params (string Clave, string Valor)[] datos)
    {
        var archivo = new ArchivoDeClaves();
        foreach (var (clave, valor) in datos)
            archivo.Poner(clave, valor);
        var ruta = Path.Combine(_carpeta.Ruta, "respuestas.txt");
        archivo.Guardar(ruta);
        return ruta;
    }

    [Fact]
    public async Task Escribir_deja_servicio_json_y_caja_json_y_dice_que_cambio()
    {
        Directory.CreateDirectory(Datos);
        File.WriteAllText(Path.Combine(Datos, "servicio.json"), "{\r\n  // la plantilla\r\n}\r\n", new UTF8Encoding(true));
        var respuestas = Respuestas(("rol", "unica"), ("impresora", ""), ("cola", ""), ("lector", "teclado"), ("controlprint", "no"), ("eleventa", "no"),
            ("eleventa-clave", "nueva"));

        var codigo = await Ayudante.CorrerAsync(["escribir", "--respuestas", respuestas, "--salida", Salida], Equipo);

        Assert.Equal(0, codigo);
        var salida = ArchivoDeClaves.Leer(Salida);
        Assert.Equal("cambio", salida["servicio-json"]);
        Assert.Equal("cambio", salida["caja-json"]);
        Assert.Equal("Instalacion (se agrega); caja.json (nuevo)", salida["cambios"]);
        Assert.Equal("{\r\n  // la plantilla\r\n  \"Instalacion\": {\r\n    \"UnaSolaCaja\": true\r\n  }\r\n}\r\n",
            Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Datos, "servicio.json"))[3..]));
        Assert.Equal(CajaJson.ServicioLocal, (string?)JsonNode.Parse(File.ReadAllText(CajaJson.Ruta(Datos)))!["Servicio"]);
    }

    /// <summary>Otra caja no tiene servicio: servicio.json no se toca, ni se hace.</summary>
    [Fact]
    public async Task En_otra_caja_servicio_json_no_aplica()
    {
        var respuestas = Respuestas(("rol", "otra"), ("principal", "192.168.1.50"), ("impresora", ""), ("cola", ""), ("lector", "teclado"));

        Assert.Equal(0, await Ayudante.CorrerAsync(["escribir", "--respuestas", respuestas, "--salida", Salida], Equipo));

        Assert.Equal("no-aplica", ArchivoDeClaves.Leer(Salida)["servicio-json"]);
        Assert.False(File.Exists(Path.Combine(Datos, "servicio.json")));
        Assert.Equal("http://192.168.1.50:5080/", (string?)JsonNode.Parse(File.ReadAllText(CajaJson.Ruta(Datos)))!["Servicio"]);
    }

    /// <summary>Con caja.json mal escrito tampoco se cambia servicio.json: no queda uno cambiado y el otro no.</summary>
    [Fact]
    public async Task Si_uno_no_se_entiende_no_se_cambia_ninguno()
    {
        Directory.CreateDirectory(Path.Combine(Datos, "Caja"));
        File.WriteAllText(Path.Combine(Datos, "servicio.json"), "{ }");
        File.WriteAllText(CajaJson.Ruta(Datos), "{ \"Servicio\": ");
        var respuestas = Respuestas(("rol", "unica"), ("lector", "teclado"));

        var codigo = await Ayudante.CorrerAsync(["escribir", "--respuestas", respuestas, "--salida", Salida], Equipo);

        Assert.Equal(2, codigo);
        Assert.Contains("caja.json está mal escrito", ArchivoDeClaves.Leer(Salida)["error"]);
        Assert.Equal("{ }", File.ReadAllText(Path.Combine(Datos, "servicio.json")));
    }

    [Fact]
    public async Task Una_respuesta_que_no_se_entiende_se_dice()
    {
        var respuestas = Respuestas(("rol", "otra"), ("lector", "com"), ("lector-puerto", "USB"));

        Assert.Equal(2, await Ayudante.CorrerAsync(["escribir", "--respuestas", respuestas, "--salida", Salida], Equipo));

        var salida = ArchivoDeClaves.Leer(Salida);
        Assert.StartsWith("Una respuesta no se puede usar: El puerto del lector", salida["error"]);
        Assert.Equal("lector-puerto", salida["clave"]);
        Assert.False(Directory.Exists(Datos));
    }

    [Fact]
    public async Task Leer_dice_lo_que_hay_y_sale_con_2_si_un_archivo_esta_mal_escrito()
    {
        Assert.Equal(0, await Ayudante.CorrerAsync(["leer", "--salida", Salida], Equipo));
        Assert.Equal("principal,unica,otra", ArchivoDeClaves.Leer(Salida)["roles"]);

        Directory.CreateDirectory(Datos);
        File.WriteAllText(Path.Combine(Datos, "servicio.json"), "{ \"Eleventa\": ");

        Assert.Equal(2, await Ayudante.CorrerAsync(["leer", "--salida", Salida], Equipo));
        Assert.Contains("servicio.json está mal escrito", ArchivoDeClaves.Leer(Salida)["error"]);
    }

    [Fact]
    public async Task Probar_una_direccion_que_no_es_dice_por_que()
    {
        Assert.Equal(0, await Ayudante.CorrerAsync(["probar", "--direccion", "la de enfrente", "--salida", Salida], Equipo));

        var salida = ArchivoDeClaves.Leer(Salida);
        Assert.Equal("no", salida["contesta"]);
        Assert.Contains("va la IP de la caja principal", salida["error"]);
    }

    [Theory]
    [InlineData]
    [InlineData("leer")]
    [InlineData("leer", "--salida")]
    [InlineData("otra-cosa", "--salida", "x.txt")]
    [InlineData("escribir", "--salida", "x.txt")]
    public async Task Mal_dada_la_orden_sale_con_1(params string[] argumentos) =>
        Assert.Equal(1, await Ayudante.CorrerAsync(argumentos, Equipo));
}
