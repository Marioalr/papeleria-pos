using System.Net.Http.Json;
using System.Text.RegularExpressions;
using PapeleriaPos.Nucleo.Contratos;
using static PapeleriaPos.Pruebas.Servicio.PilotoDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que revisa el instructivo del ensayo contra una copia (docs/pruebas-en-tienda.md, el piloto alfa) tiene que distinguir lo
/// que importa. Revisión del PR #145: el regreso revisaba «origen» de api/eleventa, que es la base que se lee y dice lo mismo con
/// Escribir en true que en false; con Escribir en true por error, la siguiente venta iba a la base real sin el visto bueno del
/// dueño.
/// </summary>
public class EnsayoDelPilotoTests
{
    private const string BaseReal = @"C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB";

    private static (string, string)[] Con(bool activo, bool escribir) =>
    [
        .. Ajustes(leer: activo).Where(a => a.Item1 is not ("Eleventa:Escribir" or "Eleventa:Base")),
        ("Eleventa:Escribir", escribir ? "true" : "false"), ("Eleventa:Base", BaseReal),
    ];

    [Fact]
    public async Task Lo_que_revisa_el_paso_7_del_ensayo_dice_si_el_puente_sigue_escribiendo()
    {
        // Como queda al regresar (Activo y Escribir en false, la base real), y con Escribir en true por error.
        await using var apagado = await ServicioDePrueba.CrearAsync(Con(activo: false, escribir: false));
        await using var porError = await ServicioDePrueba.CrearAsync(Con(activo: true, escribir: true));
        var deApagado = (await apagado.Fabrica.CreateClient().GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;
        var dePorError = (await porError.Fabrica.CreateClient().GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;

        Assert.Equal((false, (string?)null, 0, 0), (deApagado.Activo, deApagado.Destino, deApagado.Pendientes, deApagado.PorRevisar));
        Assert.True(dePorError.Activo);
        Assert.Contains(@"PDVDATA.FDB, caja ", dePorError.Destino);

        // Y es lo que pide el paso 7: apagar los dos, revisar «activo» y «destino» de api/eleventa/envios, regresar al respaldo
        // anotado en el paso 1 (no al más reciente) y ver la bandeja vacía antes que nada más.
        var paso7 = Paso("- **7. Al terminar", "- **Después del ensayo**");
        Assert.Contains("`Activo` y `Escribir` en `false`, los dos, y `Base` otra vez con `PDVDATA.FDB`", paso7);
        Assert.Contains("`http://localhost:5080/api/eleventa/envios` tiene que decir `\"activo\": false` y `\"destino\": null`", paso7);
        Assert.Contains("**el que se anotó en el paso 1**", paso7);
        Assert.Contains("`\"pendientes\": 0`, `\"porRevisar\": 0` y otra vez `\"activo\": false`", paso7);
        // Escribir en la base real es otra decisión del dueño, con su respaldo y su regreso.
        Assert.Contains("**Escribir en la base real es otra decisión del dueño,** aparte del ensayo",
            Paso("- **Después del ensayo**", "### Empezar a escribir en la base real"));
    }

    [Fact]
    public void El_ensayo_nunca_pide_borrar_algo_junto_a_la_base_real()
    {
        var ensayo = Paso("### El ensayo contra una copia", "### Empezar a escribir en la base real");

        // Si la restauración se corta, otro nombre; la copia se queda al terminar.
        Assert.Contains("**No se borra nada en la caja principal:** se repite el segundo con otro nombre, `PDVDATA-ENSAYO-2.FDB`", ensayo);
        Assert.Contains("**La copia se queda en la caja principal:** no se borra a mano", ensayo);
        Assert.DoesNotContain("Borrar la copia", ensayo);
        // La restauración larga dice lo que va haciendo: nadie cierra la ventana.
        Assert.Contains("& $gbak -c -v ", ensayo);
        // Base primero, revisada, y Escribir después: dos cambios.
        Assert.Contains("**3. Apuntar la caja 2 a la copia, en dos cambios,**", ensayo);
    }

    /// <summary>El texto de docs/pruebas-en-tienda.md entre dos marcas, con los espacios y los saltos de línea juntos en uno.</summary>
    private static string Paso(string desde, string hasta)
    {
        var guia = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "docs", "pruebas-en-tienda.md"));
        var inicio = guia.IndexOf(desde, StringComparison.Ordinal);
        Assert.True(inicio >= 0, $"No está «{desde}» en la guía.");
        var fin = guia.IndexOf(hasta, inicio + desde.Length, StringComparison.Ordinal);
        Assert.True(fin > inicio, $"No está «{hasta}» después de «{desde}».");
        return Regex.Replace(guia[inicio..fin], @"\s+", " ");
    }

    private static string RaizDelRepositorio()
    {
        for (var carpeta = new DirectoryInfo(AppContext.BaseDirectory); carpeta is not null; carpeta = carpeta.Parent)
            if (File.Exists(Path.Combine(carpeta.FullName, "docs", "pruebas-en-tienda.md")))
                return carpeta.FullName;
        throw new DirectoryNotFoundException("No encontré la raíz del repositorio.");
    }
}
