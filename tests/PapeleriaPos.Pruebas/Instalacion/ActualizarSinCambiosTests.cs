using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// Correr el instalador otra vez sin cambiar nada (actualizar la caja 2 de hoy, instalada con los scripts): las páginas empiezan con lo
/// que tiene la PC (<c>leer</c>) y, con esas mismas respuestas, servicio.json y caja.json no se tocan, ni un byte.
/// </summary>
public sealed class ActualizarSinCambiosTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();

    public void Dispose() => _carpeta.Dispose();

    /// <summary>Lo que el asistente pone en respuestas.txt con lo que dijo <c>leer</c>, sin que nadie cambie nada (GuardarRespuestas).</summary>
    private static ArchivoDeClaves ComoLasDejaElAsistente(ArchivoDeClaves leido)
    {
        var respuestas = new ArchivoDeClaves()
            .Poner("rol", leido["rol"])
            .Poner("principal", leido["principal"])
            .Poner("impresora", leido["ticket"])
            .Poner("cola", leido["cola"]);
        if (leido["lector-puerto"] is { Length: > 0 } puerto)
            respuestas.Poner("lector", "com").Poner("lector-puerto", puerto).Poner("lector-baudios", leido["lector-baudios"] is { Length: > 0 } b ? b : "9600");
        else
            respuestas.Poner("lector", "teclado");
        respuestas.Poner("controlprint", leido["controlprint"]).Poner("controlprint-servidor", leido["controlprint-servidor"]);
        respuestas.Poner("eleventa", leido["eleventa"]);
        foreach (var clave in new[] { "eleventa-servidor", "eleventa-base", "eleventa-caja", "eleventa-cajero", "eleventa-gbak", "eleventa-respaldos" })
            respuestas.Poner(clave, leido[clave]);
        return respuestas;
    }

    private sealed class Equipo(string datos, string programa, string? servicio) : IEquipo
    {
        public string Datos => datos;

        public string Programa => programa;

        public string? ExeDelServicio() => servicio;

        public string? Version(string exe) => "1.0.25";

        public IReadOnlyList<ImpresoraDeWindows> Impresoras() => [new("WF-6590", "IP_192.168.1.20", false, false)];

        public IReadOnlyList<string> PuertosCom() => ["COM3"];

        public IReadOnlyList<string> Ips() => [];

        public IReadOnlyList<string> Gbak() => [@"C:\FirebirdSQL20\bin\gbak.exe"];

        public string? IniDeEleventa() => "[EnRed]\nIPServidor=192.168.1.106\n";
    }

    [Theory]
    // La caja 2 del piloto, como la dejó el instructivo: servicio.json a mano, sin carpeta de respaldos ni gbak, y con el puente escribiendo.
    [InlineData("""
        // A mano, con el Bloc de notas.
        {
          "Eleventa": {
            "Activo": true,
            "Escribir": true,
            "Servidor": "192.168.1.106",
            "Base": "C:\\Program Files (x86)\\AbarrotesMultiCaja\\db\\PDVDATA.FDB",
            "Caja": 3,
            "Cajero": 1
          },
          "ControlPrint": { "Servidor": "192.168.1.106" }, // la caja principal
          "Registro": { "Dias": 45 }
        }
        """, """
        { "Servicio": "http://localhost:5080/", "CajaId": "5a1d4c3e-0000-4000-8000-000000000002", "Impresora": "tcp://192.168.1.100",
          "LlaveProtegida": "AQAAANCMnd8B", "ColaImpresion": "WF-6590", "Lector": { "Puerto": "com3", "Baudios": 9600, "Paridad": "Ninguna" } }
        """)]
    // Una principal recién instalada con la plantilla, sin eleventa ni Control Print.
    [InlineData("{\n  // la plantilla\n}\n", """{ "Servicio": "http://localhost:5080", "CajaId": null, "Impresora": "", "ColaImpresion": null }""")]
    // Con el puente apagado (Activo y Escribir en false): «No» y sus datos se quedan como están.
    [InlineData("""{ "Eleventa": { "Activo": false, "Escribir": false, "Servidor": "192.168.1.106" } }""",
        """{ "Servicio": "http://127.0.0.1:5080/", "CajaId": null, "Impresora": "Tickets", "ColaImpresion": null, "Lector": null }""")]
    public void Con_lo_que_ya_tiene_no_se_toca_ni_un_byte(string servicioJson, string cajaJson)
    {
        var datos = Path.Combine(_carpeta.Ruta, "datos");
        Directory.CreateDirectory(Path.Combine(datos, "Caja"));
        File.WriteAllText(Path.Combine(datos, "servicio.json"), servicioJson);
        File.WriteAllText(CajaJson.Ruta(datos), cajaJson);
        var antesServicio = File.ReadAllBytes(Path.Combine(datos, "servicio.json"));
        var antesCaja = File.ReadAllBytes(CajaJson.Ruta(datos));
        var leido = LecturaDeLaPc.Leer(new Equipo(datos, Path.Combine(_carpeta.Ruta, "programa"), "servicio.exe"));
        Assert.Equal("", leido["error"]);

        var escrito = Ayudante.Escribir(ComoLasDejaElAsistente(leido), datos);

        Assert.Equal("", escrito["error"] ?? "");
        Assert.Equal("igual", escrito["servicio-json"]);
        Assert.Equal("igual", escrito["caja-json"]);
        Assert.Equal(antesServicio, File.ReadAllBytes(Path.Combine(datos, "servicio.json")));
        Assert.Equal(antesCaja, File.ReadAllBytes(CajaJson.Ruta(datos)));
        Assert.False(File.Exists(Path.Combine(datos, "servicio.json.anterior")));
        Assert.False(File.Exists(CajaJson.Ruta(datos) + ".anterior"));
    }

    /// <summary>A una sección de eleventa que ya está no se le agrega lo de siempre (Escribir, el puerto); a una nueva, sí, a la vista.</summary>
    [Fact]
    public void A_una_seccion_que_ya_esta_no_se_le_agrega_lo_de_siempre()
    {
        var ruta = Path.Combine(_carpeta.Ruta, "servicio.json");
        File.WriteAllText(ruta, """{ "Eleventa": { "Activo": true, "Servidor": "192.168.1.106", "Base": "C:\\PDVDATA.FDB" } }""");

        var escrito = ServicioJson.Escribir(ruta, new Respuestas
        {
            Rol = RolDeLaPc.Principal, Eleventa = true, EleventaServidor = "192.168.1.50", EleventaBase = @"C:\PDVDATA.FDB",
        });

        Assert.Equal(["Eleventa:Servidor"], escrito.Cambios);
        File.Delete(ruta);
        ServicioJson.Escribir(ruta, new Respuestas { Rol = RolDeLaPc.Principal, Eleventa = true, EleventaServidor = "192.168.1.50", EleventaBase = @"C:\PDVDATA.FDB" });
        var nueva = File.ReadAllText(ruta);
        Assert.Contains("\"Escribir\": false", nueva);
        Assert.Contains("\"Puerto\": 3050", nueva);
    }
}
