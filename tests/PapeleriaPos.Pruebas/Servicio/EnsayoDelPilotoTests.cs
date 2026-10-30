using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
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
        Assert.Contains("`Activo` y `Escribir` en `false`, los dos, **sin tocar `Base`**", paso7);
        Assert.Contains("`http://localhost:5080/api/eleventa/envios` tiene que decir `\"activo\": false` y `\"destino\": null`", paso7);
        Assert.Contains("**el que se anotó en el paso 1**", paso7);
        Assert.Contains("`\"pendientes\": 0`, `\"porRevisar\": 0` y otra vez `\"activo\": false`", paso7);
        // Escribir en la base real es otra decisión del dueño, con su respaldo y su regreso.
        Assert.Contains("**Escribir en la base real es otra decisión del dueño,** aparte del ensayo",
            Paso("- **Después del ensayo**", "### Empezar a escribir en la base real"));
    }

    /// <summary>
    /// Segunda revisión del PR #145. Un ensayo que no llegó a escribir en la copia (sin su respaldo del día, sin turno abierto en la
    /// copia, o la copia que no contesta) deja las ventas de prueba en la bandeja, y la bandeja no sabe para qué base se encoló cada
    /// cosa. Si el regreso cambiara <c>Base</c> a la real en el mismo cambio que apaga los dos, con <c>Escribir</c> olvidado en true
    /// esas ventas irían al turno abierto de la caja 2 de la base REAL. El paso 7.2 apaga los dos con <c>Base</c> todavía en la
    /// copia, revisa <c>"activo": false</c>, y solo entonces regresa <c>Base</c>: con el descuido, la revisión lo ve mientras el
    /// destino es la copia, y la real no recibe nada. Contra el Firebird de prueba (un eleventa de mentiras).
    /// </summary>
    [FactConFirebird]
    public async Task Lo_que_quedo_en_la_bandeja_del_ensayo_no_se_escribe_en_la_base_real_al_regresar_Base()
    {
        // «La real»: un eleventa de mentiras con el turno de la caja 2 abierto, como en la tienda mientras la cajera vende en eleventa.
        var (e, _) = await EleventaAsync();
        await using var eleventa = e;
        // El ensayo: Escribir en true y Base en la copia, que no recibe nada (aquí, porque no contesta).
        var copia = e.Servidor with { Base = @"C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA-ENSAYO.FDB", Puerto = 1 };
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(copia));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        await CobrarAsync(c);
        var antes = await EsperarAsync(() => EnviosAsync(c), x => x.PorAtender.Any(p => p.Intentos > 0));
        Assert.Equal(EstadoEnvioEleventa.Pendiente, antes.PorAtender.Single().Estado);
        async Task<int> EnLaRealAsync() =>
            Convert.ToInt32(await e.ValorAsync($"SELECT COUNT(*) FROM VENTATICKETS WHERE CAJA_ID = {CajaEleventa} AND ESTA_ABIERTO = 'f'"));
        Assert.Equal(0, await EnLaRealAsync());

        // 7.2.1 con un descuido: Activo en false pero Escribir se quedó en true; Base sigue en la copia. La revisión del 7.2.2 lo ve
        // («activo»: true), y lo que mande va a la copia, nunca a la real.
        using (var conDescuido = Reiniciado(servicio, Ajustes(copia), ("Eleventa:Activo", "false")))
        {
            var http = conDescuido.CreateClient();
            var envios = (await http.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;
            Assert.True(envios.Activo);
            Assert.Contains("PDVDATA-ENSAYO.FDB", envios.Destino);
            await http.PostAsync("api/eleventa/enviar", null);
        }
        // 7.2.1 bien: los dos en false, Base todavía en la copia; y 7.2.3, solo entonces Base en la real, con los dos en false.
        using (var apagado = Reiniciado(servicio, Ajustes(copia), ("Eleventa:Activo", "false"), ("Eleventa:Escribir", "false")))
            Assert.False((await apagado.CreateClient().GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!.Activo);
        using var regreso = Reiniciado(servicio, Ajustes(e.Servidor), ("Eleventa:Activo", "false"), ("Eleventa:Escribir", "false"));
        var httpRegreso = regreso.CreateClient();
        await httpRegreso.PostAsync("api/eleventa/enviar", null);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var alFinal = (await httpRegreso.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;

        Assert.Equal(0, await EnLaRealAsync());
        Assert.Equal((false, (string?)null), (alFinal.Activo, alFinal.Destino));
        // Y es el orden que pide el paso 7.2.
        var paso72 = Paso("  2. **El puente apagado y revisado, con `Base` todavía en la copia;**", "  3. **La caja 2, a su punto de partida:**");
        var apagar = paso72.IndexOf("`Activo` y `Escribir` en `false`, los dos, **sin tocar `Base`**", StringComparison.Ordinal);
        var revisar = paso72.IndexOf("tiene que decir `\"activo\": false`", StringComparison.Ordinal);
        var regresarBase = paso72.IndexOf("Solo entonces, `Base` otra vez con `PDVDATA.FDB`", StringComparison.Ordinal);
        Assert.True(apagar >= 0 && revisar > apagar && regresarBase > revisar, paso72);
        // El 7.1 dice que lo que no se mandó se queda: el 7.3 lo quita.
        Assert.Contains("se dejan así: el paso 3 las quita", Paso("  1. En la caja: cobrar o cancelar lo que esté abierto", "  2. **El puente apagado"));
    }

    /// <summary>El servicio otra vez, con la misma base y otra configuración: lo que hace <c>Restart-Service</c> después de editar servicio.json.</summary>
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Reiniciado(ServicioDePrueba servicio, (string, string)[] ajustes,
        params (string Clave, string Valor)[] encima) =>
        servicio.Fabrica.WithWebHostBuilder(b =>
        {
            foreach (var (clave, valor) in ajustes.Concat(encima.Select(a => (a.Clave, a.Valor))))
                b.UseSetting(clave, valor);
        });

    [Fact]
    public void El_ensayo_nunca_pide_borrar_algo_junto_a_la_base_real()
    {
        // La copia se hace al instalar (de ella sale la base de la caja 2) y el ensayo la usa ese mismo día.
        var instalarYEnsayo = Paso("### Instalar la caja 2 para el piloto", "### Empezar a escribir en la base real");

        // Si la restauración se corta, otro nombre; la copia se queda al terminar.
        Assert.Contains("**No se borra nada en la caja principal:** se repite el tercero con otro nombre, `PDVDATA-ENSAYO-2.FDB`", instalarYEnsayo);
        Assert.Contains("**La copia se queda en la caja principal:** no se borra a mano", instalarYEnsayo);
        Assert.DoesNotContain("Borrar la copia", instalarYEnsayo);
        // La restauración larga dice lo que va haciendo: nadie cierra la ventana.
        Assert.Contains("& $gbak -c -v ", instalarYEnsayo);
        // Base primero, revisada, y Escribir después: dos cambios.
        Assert.Contains("**3. Apuntar la caja 2 a la copia, en dos cambios,**", instalarYEnsayo);
    }

    /// <summary>
    /// Con la revisión del PR #144: la base de la caja 2 se arma en la tienda, desde la caja 2, leyendo por la red la copia del
    /// ensayo (`importar-eleventa --servidor`), así que la copia va antes; la clave se pide al correrlo (nada en el historial). Un
    /// PIN olvidado, con el programa del servicio, que es lo que queda instalado en la caja 2.
    /// </summary>
    [Fact]
    public void La_base_de_la_caja_2_sale_de_la_copia_del_ensayo_hecha_antes()
    {
        var instalar = Paso("### Instalar la caja 2 para el piloto", "### La clave de Firebird");

        var copia = instalar.IndexOf("& $gbak -c -v ", StringComparison.Ordinal);
        var deLaCopia = instalar.IndexOf(
            @"importar-eleventa --servidor $principal --ruta ""C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA-ENSAYO.FDB"" --usuario SYSDBA",
            StringComparison.Ordinal);
        Assert.True(copia >= 0 && deLaCopia > copia, "La copia del ensayo va antes que la base que sale de ella.");
        Assert.DoesNotContain("ELEVENTA_CLAVE", instalar);
        Assert.Contains(@"& ""C:\Program Files\PapeleriaPos\Servicio\PapeleriaPos.Servicio.exe"" pin-usuario --usuario",
            Paso("### Los usuarios en el día a día", "### El ensayo contra una copia"));
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
