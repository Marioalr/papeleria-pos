using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La configuración de esta PC (<c>C:\ProgramData\PapeleriaPos\servicio.json</c>), fuera de la carpeta del programa que reemplaza
/// actualizar.ps1: lo que trae llega al servicio, manda sobre appsettings.json, y las variables de entorno (la clave de eleventa) y
/// la línea de órdenes mandan sobre ella. La prueba con una persona real no la lee.
/// </summary>
public sealed class ConfiguracionDeLaPcTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();

    /// <summary>Una sección que solo usa esta prueba: las variables de entorno son de todo el proceso, y las pruebas corren a la par.</summary>
    private readonly string _seccion = $"PruebaDeLaPc{Guid.NewGuid():N}";

    private readonly List<string> _variables = [];

    public void Dispose()
    {
        foreach (var variable in _variables)
            Environment.SetEnvironmentVariable(variable, null);
        _carpeta.Dispose();
    }

    private void Variable(string clave, string valor)
    {
        var nombre = $"{_seccion}__{clave}";
        _variables.Add(nombre);
        Environment.SetEnvironmentVariable(nombre, valor);
    }

    private string Escribir(string nombre, string json)
    {
        var ruta = Path.Combine(_carpeta.Ruta, nombre);
        File.WriteAllText(ruta, json);
        return ruta;
    }

    [Fact]
    public async Task Lo_de_servicio_json_llega_al_servicio_y_una_variable_de_entorno_manda_sobre_el()
    {
        var ruta = Escribir("servicio.json", $$"""
            // La configuración de esta PC, con comentarios como la plantilla.
            {
              /* El puente con eleventa: lo que no trae appsettings.json. */
              "Eleventa": { "SegundosAlCobrar": 7, "SegundosMaximos": 45 },
              // appsettings.json trae http://localhost:5080.
              "Caja": { "Servicio": "http://localhost:5099" },
              "{{_seccion}}": { "DelArchivo": "servicio.json", "LaVariableManda": "servicio.json" }
            }
            """);
        Variable("LaVariableManda", "la variable de entorno");

        await using var servicio = await ServicioDePrueba.CrearAsync(("ConfiguracionDeLaPc", ruta));
        var opciones = servicio.Fabrica.Services.GetRequiredService<OpcionesEleventa>();
        var configuracion = servicio.Fabrica.Services.GetRequiredService<IConfiguration>();

        Assert.Equal(7, opciones.SegundosAlCobrar);
        Assert.Equal(45, opciones.SegundosMaximos);
        Assert.Equal("http://localhost:5099", configuracion["Caja:Servicio"]);
        Assert.Equal("servicio.json", configuracion[$"{_seccion}:DelArchivo"]);
        Assert.Equal("la variable de entorno", configuracion[$"{_seccion}:LaVariableManda"]);
        Assert.Single(DeServicioJson((IConfigurationRoot)configuracion));
    }

    /// <summary>
    /// Las pruebas no leen el servicio.json de la PC donde corren (<see cref="FabricaDePrueba"/>): en una caja de verdad traería el
    /// puente con eleventa, y una prueba escribiría en el eleventa de la tienda.
    /// </summary>
    [Fact]
    public async Task Las_pruebas_no_leen_el_servicio_json_de_la_pc_donde_corren()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();

        Assert.Empty(DeServicioJson((IConfigurationRoot)servicio.Fabrica.Services.GetRequiredService<IConfiguration>()));
    }

    private static IEnumerable<IConfigurationProvider> DeServicioJson(IConfigurationRoot configuracion) =>
        configuracion.Providers.OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider>()
            .Where(p => p.Source.Path?.EndsWith("servicio.json", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// Como las arma el servicio (WebApplication.CreateBuilder): appsettings.json, las variables de entorno y la línea de órdenes; el
    /// archivo de la PC entra justo antes de las variables.
    /// </summary>
    [Fact]
    public void Va_despues_de_appsettings_y_antes_de_las_variables_de_entorno_y_la_linea_de_ordenes()
    {
        var appsettings = Escribir("appsettings.json", $$"""
            { "{{_seccion}}": { "SoloAppsettings": "appsettings.json", "ElArchivoManda": "appsettings.json" } }
            """);
        var ruta = Escribir("servicio.json", $$"""
            { "{{_seccion}}": { "ElArchivoManda": "servicio.json", "LaVariableManda": "servicio.json", "LaLineaManda": "servicio.json" } }
            """);
        Variable("LaVariableManda", "la variable de entorno");
        Variable("LaLineaManda", "la variable de entorno");
        string[] argumentos = [$"--{_seccion}:LaLineaManda=la línea de órdenes"];
        using var configuracion = new ConfigurationManager();
        configuracion.AddJsonFile(appsettings, optional: false).AddEnvironmentVariables().AddCommandLine(argumentos);
        var fuentes = configuracion.Sources.Count;

        ConfiguracionDeLaPc.Agregar(configuracion, ruta, argumentos);

        Assert.Equal("appsettings.json", configuracion[$"{_seccion}:SoloAppsettings"]);
        Assert.Equal("servicio.json", configuracion[$"{_seccion}:ElArchivoManda"]);
        Assert.Equal("la variable de entorno", configuracion[$"{_seccion}:LaVariableManda"]);
        Assert.Equal("la línea de órdenes", configuracion[$"{_seccion}:LaLineaManda"]);
        // El archivo se metió entre ellas: las variables y la línea de órdenes no se volvieron a poner.
        Assert.Equal(fuentes + 1, configuracion.Sources.Count);
    }

    /// <summary>Sin las variables de entorno en la configuración (no es la del servicio), se ponen otra vez después del archivo.</summary>
    [Fact]
    public void Si_no_estan_las_variables_de_entorno_se_ponen_despues_del_archivo()
    {
        var ruta = Escribir("servicio.json", $$"""{ "{{_seccion}}": { "LaVariableManda": "servicio.json", "LaLineaManda": "servicio.json" } }""");
        Variable("LaVariableManda", "la variable de entorno");
        using var configuracion = new ConfigurationManager();

        ConfiguracionDeLaPc.Agregar(configuracion, ruta, [$"--{_seccion}:LaLineaManda=la línea de órdenes"]);

        Assert.Equal("la variable de entorno", configuracion[$"{_seccion}:LaVariableManda"]);
        Assert.Equal("la línea de órdenes", configuracion[$"{_seccion}:LaLineaManda"]);
    }

    /// <summary>En una PC recién instalada todavía no hay archivo, ni siquiera su carpeta: se arranca con lo de siempre.</summary>
    [Fact]
    public void Sin_el_archivo_ni_su_carpeta_no_cambia_nada()
    {
        using var configuracion = new ConfigurationManager();
        configuracion.AddInMemoryCollection(new Dictionary<string, string?> { [$"{_seccion}:Valor"] = "el de siempre" }).AddEnvironmentVariables();

        ConfiguracionDeLaPc.Agregar(configuracion, Path.Combine(_carpeta.Ruta, "todavia-no", "servicio.json"));

        Assert.Equal("el de siempre", configuracion[$"{_seccion}:Valor"]);
        Assert.Empty(DelArchivo(configuracion));
    }

    /// <summary>Lo que trae el archivo de la PC: lo que leyó su proveedor, sin lo de las demás fuentes.</summary>
    private static IEnumerable<string> DelArchivo(IConfigurationRoot configuracion) =>
        configuracion.Providers.OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider>().Single()
            .GetChildKeys([], null);

    /// <summary>Mal escrito, el servicio no arranca (no sigue sin el puente sin decirlo), y el error dice qué archivo es.</summary>
    [Fact]
    public void Mal_escrito_el_servicio_no_arranca_y_dice_cual_es()
    {
        var ruta = Escribir("servicio.json", """{ "Eleventa": { "Activo": true, } """);
        using var configuracion = new ConfigurationManager();
        configuracion.AddEnvironmentVariables();

        var error = Assert.Throws<InvalidOperationException>(() => ConfiguracionDeLaPc.Agregar(configuracion, ruta));

        Assert.StartsWith($"No se pudo leer la configuración de esta PC ({ruta}):", error.Message);
    }

    /// <summary>La plantilla que ponen los scripts de instalación (instalacion/plantilla-servicio.json) se lee y no cambia nada.</summary>
    [Fact]
    public void La_plantilla_se_lee_y_no_cambia_nada()
    {
        var plantilla = Path.Combine(RaizDelRepositorio(), "instalacion", "plantilla-servicio.json");
        using var configuracion = new ConfigurationManager();
        configuracion.AddEnvironmentVariables();

        ConfiguracionDeLaPc.Agregar(configuracion, plantilla);

        Assert.True(File.Exists(plantilla));
        Assert.Empty(DelArchivo(configuracion));
        Assert.Contains("Eleventa__Clave", File.ReadAllText(plantilla));
    }

    /// <summary>
    /// La prueba con una persona real (Prueba:Activa) tiene su propia carpeta de datos y puede estar en la misma PC que el servicio de
    /// verdad: no lee su servicio.json (traería el puente con eleventa). Las pruebas cambian la ruta; vacía, ninguna.
    /// </summary>
    [Fact]
    public void La_prueba_con_una_persona_real_no_lee_la_configuracion_de_la_pc()
    {
        static IConfiguration Con(params (string Clave, string Valor)[] valores) =>
            new ConfigurationBuilder().AddInMemoryCollection(valores.Select(v => KeyValuePair.Create(v.Clave, (string?)v.Valor))).Build();

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PapeleriaPos", "servicio.json"),
            RutasServicio.ConfiguracionDeLaPc(Con()));
        Assert.Null(RutasServicio.ConfiguracionDeLaPc(Con(("Prueba:Activa", "true"))));
        Assert.Null(RutasServicio.ConfiguracionDeLaPc(Con(("Prueba:Activa", "true"), ("ConfiguracionDeLaPc", "/otra/servicio.json"))));
        Assert.Equal("/otra/servicio.json", RutasServicio.ConfiguracionDeLaPc(Con(("ConfiguracionDeLaPc", "/otra/servicio.json"))));
        Assert.Null(RutasServicio.ConfiguracionDeLaPc(Con(("ConfiguracionDeLaPc", ""))));
    }

    /// <summary>Lo mismo con el servicio de verdad: arrancado como lo arranca la caja de la prueba, lo del archivo no llega.</summary>
    [Fact]
    public async Task El_servicio_de_la_prueba_con_una_persona_real_no_toma_lo_de_servicio_json()
    {
        var ruta = Escribir("servicio.json", """{ "Eleventa": { "Escribir": true, "SegundosAlCobrar": 7 } }""");

        await using var servicio = await ServicioDePrueba.CrearAsync(("ConfiguracionDeLaPc", ruta), ("Prueba:Activa", "true"));
        var opciones = servicio.Fabrica.Services.GetRequiredService<OpcionesEleventa>();

        Assert.False(opciones.Escribir);
        Assert.Equal(2, opciones.SegundosAlCobrar);
    }

    private static string RaizDelRepositorio()
    {
        for (var carpeta = new DirectoryInfo(AppContext.BaseDirectory); carpeta is not null; carpeta = carpeta.Parent)
            if (File.Exists(Path.Combine(carpeta.FullName, "instalacion", "instalar-servicio.ps1")))
                return carpeta.FullName;
        throw new DirectoryNotFoundException("No encontré la raíz del repositorio.");
    }
}
