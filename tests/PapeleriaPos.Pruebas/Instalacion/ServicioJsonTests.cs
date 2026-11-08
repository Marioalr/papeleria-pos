using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Servicio;
using ServicioDePrueba = PapeleriaPos.Pruebas.Servicio.ServicioDePrueba;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// Lo que el instalador escribe en servicio.json (docs/instalacion.md): «Eleventa» con Activo y sin prender nunca Escribir, «ControlPrint»
/// y «Instalacion:UnaSolaCaja», solo lo que cambió, y el servicio de verdad lo lee así.
/// </summary>
public sealed class ServicioJsonTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();

    public void Dispose() => _carpeta.Dispose();

    private string Ruta => Path.Combine(_carpeta.Ruta, "servicio.json");

    private static string Plantilla() => File.ReadAllText(Path.Combine(RaizDelRepositorio(), "instalacion", "plantilla-servicio.json"));

    internal static Respuestas ConEleventa(RolDeLaPc rol = RolDeLaPc.Principal) => new()
    {
        Rol = rol,
        Eleventa = true,
        // De documentación (TEST-NET): el servicio de las pruebas arranca con esto y nunca se conecta a la tienda.
        EleventaServidor = "192.0.2.10",
        EleventaBase = @"C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB",
        EleventaCaja = 3,
        EleventaCajero = 1,
        EleventaGbak = @"C:\ProgramData\PapeleriaPos\firebird20\bin\gbak.exe",
        EleventaRespaldos = @"C:\ProgramData\PapeleriaPos\respaldos-eleventa",
        ControlPrint = true,
        ControlPrintServidor = "192.0.2.10",
    };

    private JsonObject Leido() => new TextoJsonc(File.ReadAllBytes(Ruta)).Objeto();

    [Fact]
    public async Task En_la_plantilla_quedan_eleventa_y_control_print_con_sus_comentarios_y_el_servicio_de_verdad_los_lee()
    {
        File.WriteAllText(Ruta, Plantilla(), new UTF8Encoding(true));

        var escrito = ServicioJson.Escribir(Ruta, ConEleventa());

        Assert.Contains("Eleventa (se agrega)", escrito.Cambios);
        Assert.Contains("ControlPrint (se agrega)", escrito.Cambios);
        Assert.False(escrito.Completo);
        var texto = File.ReadAllText(Ruta);
        // Los comentarios de la plantilla se quedan, y nada de la sección «Instalacion» en la caja principal.
        Assert.StartsWith("// La configuración de ESTA PC para el servicio de Papelería POS", texto.TrimStart('\uFEFF'));
        Assert.Contains("La clave de eleventa NUNCA va aquí", texto);
        Assert.DoesNotContain("Instalacion", texto);
        Assert.Equal(File.ReadAllBytes(Ruta)[..3], [0xEF, 0xBB, 0xBF]);

        await using var servicio = await ServicioDePrueba.CrearAsync(("ConfiguracionDeLaPc", Ruta));
        var eleventa = servicio.Fabrica.Services.GetRequiredService<OpcionesEleventa>();
        var configuracion = servicio.Fabrica.Services.GetRequiredService<IConfiguration>();
        Assert.True(eleventa.Activo);
        Assert.False(eleventa.Escribir);
        Assert.Equal("192.0.2.10", eleventa.Servidor);
        Assert.Equal(3050, eleventa.Puerto);
        Assert.Equal(@"C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB", eleventa.Base);
        Assert.Equal(3, eleventa.Caja);
        Assert.Equal(1, eleventa.Cajero);
        Assert.Equal(@"C:\ProgramData\PapeleriaPos\firebird20\bin\gbak.exe", eleventa.Gbak);
        Assert.Equal(@"C:\ProgramData\PapeleriaPos\respaldos-eleventa", eleventa.CarpetaRespaldos);
        // Sin la clave (va solo en la variable del servicio), la lectura no se conecta a nada.
        Assert.Null(eleventa.Clave);
        Assert.Null(eleventa.Conexion());
        Assert.Equal("192.0.2.10", configuracion["ControlPrint:Servidor"]);
        Assert.False(configuracion.GetValue("Instalacion:UnaSolaCaja", false));
    }

    [Fact]
    public async Task La_unica_caja_lo_dice_en_instalacion_y_el_servicio_lo_lee()
    {
        File.WriteAllText(Ruta, Plantilla());

        ServicioJson.Escribir(Ruta, new Respuestas { Rol = RolDeLaPc.Unica, Eleventa = false, ControlPrint = false });

        await using var servicio = await ServicioDePrueba.CrearAsync(("ConfiguracionDeLaPc", Ruta));
        Assert.True(servicio.Fabrica.Services.GetRequiredService<IConfiguration>().GetValue("Instalacion:UnaSolaCaja", false));
        Assert.False(servicio.Fabrica.Services.GetRequiredService<OpcionesEleventa>().Piloto);
    }

    [Fact]
    public void De_la_unica_caja_a_la_principal_queda_en_false_y_la_principal_sin_la_seccion_no_la_agrega()
    {
        File.WriteAllText(Ruta, """{ "Instalacion": { "UnaSolaCaja": true } }""");

        ServicioJson.Escribir(Ruta, new Respuestas { Rol = RolDeLaPc.Principal });

        Assert.Equal("""{ "Instalacion": { "UnaSolaCaja": false } }""", File.ReadAllText(Ruta));
        File.WriteAllText(Ruta, """{ "Registro": { "Dias": 45 } }""");
        Assert.False(ServicioJson.Escribir(Ruta, new Respuestas { Rol = RolDeLaPc.Principal }).Cambio);
    }

    /// <summary>El instalador nunca prende Escribir; si el dueño ya lo prendió (el piloto escribiendo en eleventa), se queda prendido.</summary>
    [Fact]
    public void Escribir_nunca_se_prende_y_si_ya_estaba_prendido_se_queda()
    {
        File.WriteAllText(Ruta, """{ "Eleventa": { "Activo": true, "Escribir": true, "Servidor": "192.168.1.106", "SegundosMaximos": 45 } }""");

        ServicioJson.Escribir(Ruta, ConEleventa());

        var eleventa = (JsonObject)Leido()["Eleventa"]!;
        Assert.True((bool)eleventa["Escribir"]!);
        Assert.Equal(45, (int)eleventa["SegundosMaximos"]!);
        Assert.Equal(3, (int)eleventa["Caja"]!);
    }

    /// <summary>«No» en eleventa no apaga un puente que ya estaba: se apaga con el instructivo, revisando antes lo pendiente.</summary>
    [Fact]
    public void Eleventa_no_deja_la_seccion_como_estaba()
    {
        const string antes = """{ "Eleventa": { "Activo": true, "Escribir": true, "Servidor": "192.168.1.106" } }""";
        File.WriteAllText(Ruta, antes);

        var escrito = ServicioJson.Escribir(Ruta, new Respuestas { Rol = RolDeLaPc.Principal, Eleventa = false, ControlPrint = false });

        Assert.False(escrito.Cambio);
        Assert.Equal(antes, File.ReadAllText(Ruta));
    }

    [Fact]
    public void Control_print_no_quita_su_seccion_y_lo_demas_se_queda()
    {
        File.WriteAllText(Ruta, """
            {
              "Eleventa": { "Activo": true },
              "ControlPrint": { "Servidor": "192.168.1.106", "Huella": "7B:D8" },
              "Registro": { "Dias": 45 }
            }
            """);

        var escrito = ServicioJson.Escribir(Ruta, new Respuestas { Rol = RolDeLaPc.Principal, Eleventa = false, ControlPrint = false });

        Assert.Equal(["ControlPrint (se quita)"], escrito.Cambios);
        Assert.Equal("""
            {
              "Eleventa": { "Activo": true },
              "Registro": { "Dias": 45 }
            }
            """, File.ReadAllText(Ruta));
    }

    [Fact]
    public void Con_las_mismas_respuestas_otra_vez_no_se_toca_y_el_de_antes_queda_a_un_lado()
    {
        File.WriteAllText(Ruta, Plantilla());
        ServicioJson.Escribir(Ruta, ConEleventa());
        var despuesDeLaPrimera = File.ReadAllBytes(Ruta);
        Assert.Equal(Plantilla(), File.ReadAllText(Ruta + ".anterior"));
        File.Delete(Ruta + ".anterior");

        var otraVez = ServicioJson.Escribir(Ruta, ConEleventa());

        Assert.False(otraVez.Cambio);
        Assert.Equal(despuesDeLaPrimera, File.ReadAllBytes(Ruta));
        Assert.False(File.Exists(Ruta + ".anterior"));
    }

    [Fact]
    public void Lo_que_se_deja_vacio_se_quita_y_lo_que_el_instalador_no_conoce_se_queda()
    {
        File.WriteAllText(Ruta, """{ "Eleventa": { "Activo": true, "Gbak": "C:\\viejo\\gbak.exe", "Cajero": 7, "ProductoImpresiones": "IMP2" } }""");

        ServicioJson.Escribir(Ruta, ConEleventa() with { EleventaGbak = null, EleventaCajero = null, EleventaCaja = null });

        var eleventa = (JsonObject)Leido()["Eleventa"]!;
        Assert.Null(Json.Clave(eleventa, "Gbak"));
        Assert.Null(Json.Clave(eleventa, "Cajero"));
        Assert.Null(Json.Clave(eleventa, "Caja"));
        Assert.Equal("IMP2", (string?)eleventa["ProductoImpresiones"]);
    }

    [Fact]
    public void Sin_servicio_json_se_hace_uno()
    {
        var escrito = ServicioJson.Escribir(Ruta, new Respuestas { Rol = RolDeLaPc.Unica });

        Assert.True(escrito.Cambio);
        Assert.True((bool)Leido()["Instalacion"]!["UnaSolaCaja"]!);
    }

    [Fact]
    public void Mal_escrito_no_se_toca()
    {
        File.WriteAllText(Ruta, """{ "Eleventa": { "Activo": tru } }""");

        Assert.Throws<FormatException>(() => ServicioJson.Escribir(Ruta, ConEleventa()));

        Assert.Equal("""{ "Eleventa": { "Activo": tru } }""", File.ReadAllText(Ruta));
        Assert.False(File.Exists(Ruta + ".anterior"));
    }

    /// <summary>La sección escrita como texto («"Activo": "true"») cuenta como la lee el servicio: se pasa a true sin más.</summary>
    [Fact]
    public void Un_si_escrito_como_texto_cuenta_como_el_que_lee_el_servicio()
    {
        File.WriteAllText(Ruta, """{ "eleventa": { "activo": "true", "servidor": "192.168.1.106" } }""");

        ServicioJson.Escribir(Ruta, ConEleventa());

        var texto = File.ReadAllText(Ruta);
        Assert.Contains("\"activo\": true", texto);
        Assert.DoesNotContain("\"Eleventa\"", texto);
    }

    internal static string RaizDelRepositorio()
    {
        for (var carpeta = new DirectoryInfo(AppContext.BaseDirectory); carpeta is not null; carpeta = carpeta.Parent)
            if (File.Exists(Path.Combine(carpeta.FullName, "instalacion", "instalar-servicio.ps1")))
                return carpeta.FullName;
        throw new DirectoryNotFoundException("No encontré la raíz del repositorio.");
    }
}
