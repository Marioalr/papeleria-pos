using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La auditoría del instructivo del piloto (docs/pruebas-en-tienda.md, 2026-11-04): lo que la caja y el servicio tienen que decir para que
/// sus pasos se puedan seguir, y lo que la guía no puede volver a pedir.
/// </summary>
public sealed class InstructivoDelPilotoTests
{
    private static readonly (string, string)[] DeLaCaja2 =
    [
        ("Eleventa:Servidor", "127.0.0.1"), ("Eleventa:Puerto", "1"), ("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"), ("Eleventa:Usuario", "SYSDBA"),
        ("Eleventa:Clave", "masterkey"), ("Eleventa:SegundosEntreEnvios", "3600"), ("Eleventa:Activo", "false"),
    ];

    private static async Task<string?> AvisoConAsync(params (string, string)[] ajustes)
    {
        await using var servicio = await ServicioDePrueba.CrearAsync([.. DeLaCaja2, .. ajustes]);
        return (await servicio.Fabrica.CreateClient().GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.AvisoEleventa;
    }

    /// <summary>
    /// Después del ensayo o de «Regresar» pueden pasar semanas con el puente apagado, y el acceso sigue en el escritorio: lo que se vende
    /// ahí nunca llega a eleventa. La franja lo dice y no detiene el cobro. Sin caja de eleventa (una tienda sin eleventa), nada.
    /// </summary>
    [Fact]
    public async Task Con_la_caja_de_eleventa_y_sin_escribir_la_franja_dice_que_no_le_manda()
    {
        Assert.Equal(OpcionesEleventa.AvisoSinEscribir, await AvisoConAsync(("Eleventa:Caja", "3"), ("Eleventa:Cajero", "7"), ("Eleventa:Escribir", "false")));
        Assert.Null(await AvisoConAsync(("Eleventa:Caja", "3"), ("Eleventa:Cajero", "7"), ("Eleventa:Escribir", "true")));
        Assert.Null(await AvisoConAsync(("Eleventa:Escribir", "false")));
        // El 0 de la plantilla es que falta: esa PC no es una caja de eleventa.
        Assert.Null(await AvisoConAsync(("Eleventa:Caja", "0"), ("Eleventa:Escribir", "false")));

        await using var servicio = await ServicioDePrueba.CrearAsync([.. DeLaCaja2, ("Eleventa:Caja", "3"), ("Eleventa:Cajero", "7"), ("Eleventa:Escribir", "false")]);
        var http = servicio.Fabrica.CreateClient();
        var carpeta = Path.Combine(Path.GetTempPath(), $"aviso-eleventa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(carpeta);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First().Id };
        var contingencia = new Contingencia(new AlmacenLocal(Path.Combine(carpeta, "caja-local.db")), new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion);
        var servicios = new ServiceCollection().AddSingleton(conexion).AddSingleton(contingencia).BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        try
        {
            await contingencia.LatidoAsync();
            await pantalla.AbrirAsync<FranjaContingencia>([]);
            await pantalla.EsperarAsync(t => t.Contains(OpcionesEleventa.AvisoSinEscribir, StringComparison.Ordinal), "el aviso en la franja");
        }
        finally
        {
            await ((IAsyncDisposable)pantalla).DisposeAsync();
            contingencia.Cerrar(TimeSpan.FromSeconds(20));
            await servicios.DisposeAsync();
            ArchivosDePrueba.BorrarCarpeta(carpeta);
        }
    }

    /// <summary>
    /// La plantilla de servicio.json trae «"Caja": 0» y «"Cajero": 0»: quitándole las // sin cambiarlos, /envios decía «activo» y sin
    /// problema, y la revisión del 3.2 del ensayo pasaba. Un 0 es que falta, como en el asistente.
    /// </summary>
    [Theory]
    [InlineData("0", "7")]
    [InlineData("3", "0")]
    public async Task Un_cero_en_Caja_o_Cajero_es_que_falta(string caja, string cajero)
    {
        await using var servicio = await ServicioDePrueba.CrearAsync([.. DeLaCaja2, ("Eleventa:Escribir", "true"), ("Eleventa:Caja", caja), ("Eleventa:Cajero", cajero)]);

        var envios = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;

        Assert.False(envios.Activo);
        Assert.Null(envios.Destino);
        Assert.Equal(EnvioVentasEleventa.FaltaConfiguracion, envios.Problema);
    }

    /// <summary>
    /// El paso 7.4 del ensayo: si se restauró el respaldo equivocado (el de juntar los datos), lo del ensayo ya está enviado, `pendientes` y
    /// `porRevisar` dan 0 y el día de escribir en la real el puente se detiene. `enviadas` lo nota.
    /// </summary>
    [Fact]
    public async Task La_bandeja_dice_cuanto_ya_se_mando()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var http = servicio.Fabrica.CreateClient();
        Assert.Equal(0, (await http.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!.Enviadas);

        await using (var db = servicio.Base())
        {
            db.EnviosAEleventa.Add(new EnvioAEleventa { Tipo = TipoEnvioEleventa.CierreDeTurno, Estado = EstadoEnvioEleventa.Enviada, CreadoUtc = DateTime.UtcNow, EnviadaUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var estado = await http.GetFromJsonAsync<System.Text.Json.JsonElement>("api/eleventa/envios");
        Assert.Equal(1, estado.GetProperty("enviadas").GetInt32());
        Assert.Equal(0, estado.GetProperty("pendientes").GetInt32());
    }

    /// <summary>
    /// Lo que la guía ya no puede pedir: nada se instala en la caja principal; el segundo usuario del ensayo no se puede hacer (ve «tiene
    /// abierto el turno de…» y solo «Volver»); y con el puerto 3059 no hay intentos, solo el respaldo que no se pudo.
    /// </summary>
    [Fact]
    public void La_guia_no_pide_lo_que_no_se_puede_hacer()
    {
        var diaPico = Paso("## El día pico en la caja principal real", "## Con el POS en la caja 2: las fotos");
        Assert.DoesNotContain("dotnet run", diaPico);
        Assert.DoesNotContain("repositorio al día", diaPico);
        Assert.Contains("--iniciar-servicio", diaPico);
        // Lo que el .exe de un solo archivo desempaca se queda en la carpeta temporal: «no queda nada» no sería cierto.
        Assert.DoesNotContain("no queda nada", diaPico);
        Assert.Contains("%TEMP%\\.net", diaPico);

        var ensayo = Paso("### El ensayo contra una copia", "### Empezar a escribir en la base real");
        Assert.DoesNotContain("cobrar con dos usuarios", ensayo);
        Assert.DoesNotContain("`3059`", ensayo);
        Assert.DoesNotContain("con mayoreo (F11)", ensayo);

        Assert.DoesNotContain("ELEVENTA_RUTA", Paso("### Instalar la caja 2 para el piloto", "### La clave de Firebird"));
        // El respaldo de eleventa, en sus renglones: el bloque de la instalación trae un gbak -b de la base que se va a reemplazar.
        var restaurarEleventa = Paso("### Restaurar un respaldo de eleventa (.fbk)", "## Actualizar la caja 2 del piloto");
        Assert.DoesNotContain("primeros bloques", restaurarEleventa);
        Assert.DoesNotContain("& $gbak -b", restaurarEleventa);
    }

    /// <summary>Los pasos que evitan los tropiezos que la auditoría trazó en el código.</summary>
    [Fact]
    public void La_guia_regresa_del_ensayo_sin_dejar_nada_abierto()
    {
        var regreso = Paso("- **7. Al terminar, regresar,**", "### Empezar a escribir en la base real");
        // El respaldo equivocado se nota, y el turno del ensayo no se queda abierto para el primer día de escribir.
        Assert.Contains("`\"enviadas\": 0`", regreso);
        Assert.Contains("hasta que la caja pida abrir turno", regreso);
        // El corte se puede seguir: qué se escribe y con qué botón termina.
        Assert.Contains("`0` en «Efectivo contado»", regreso);
        Assert.Contains("«Listo: abrir el siguiente turno»", regreso);
        // La cola: sin la caja abierta nadie quita lo impreso, y al abrirla saldría todo en F2.
        Assert.Contains("-KeepPrintedJobs $false", regreso);
        // El lector, de regreso en eleventa.
        Assert.Contains("escanear algo en eleventa", regreso);
        Assert.Contains("hasta que la caja pida abrir turno", Paso("### Empezar a escribir en la base real", "### Juntar los datos del piloto"));
        Assert.Contains("`0` en «Efectivo contado»", Paso("### Empezar a escribir en la base real", "### Juntar los datos del piloto"));
        // Con el asistente y el cajero vacío, servicio.json se queda sin ese renglón: hay que agregarlo, no buscarlo.
        Assert.Contains("agregar el renglón `\"Cajero\"", Paso("### Instalar la caja 2 para el piloto", "### La clave de Firebird"));
        Assert.Contains("-KeepPrintedJobs $true", Paso("### Empezar a escribir en la base real", "### Juntar los datos del piloto"));

        var apagar = Paso("- **Para apagar el puente**", "- **Seguir después:**");
        Assert.Contains("`\"pendientes\": 0`", apagar);
        Assert.Contains("los pasos 8 y 9", apagar);

        // El orden de verdad: la lista de lo perdido sale al reanudar.
        var reanudar = Paso("5. **La caja nueva:** `Start-Service PapeleriaPos`", "## Actualizar la caja 2 del piloto");
        Assert.True(reanudar.IndexOf("reanudar", StringComparison.Ordinal) < reanudar.IndexOf("por revisar", StringComparison.Ordinal), reanudar);
    }

    /// <summary>
    /// Después de escribir en la real, el turno que siga abierto tiene ventas de verdad: un corte con 0 aquí anotaría de faltante todo su
    /// efectivo y dejaría por revisar su cierre. Se deja abierto: al prender Escribir, la caja toma el corte de eleventa (TurnosDelPiloto).
    /// </summary>
    [Fact]
    public void La_guia_solo_corta_con_cero_el_turno_del_ensayo()
    {
        var empezar = Paso("### Empezar a escribir en la base real", "### Juntar los datos del piloto");
        Assert.Contains("Si «Ventas del turno» dice más de 0, no se corta", empezar);

        var apagar = Paso("- **Para apagar el puente**", "- **Seguir después:**");
        Assert.DoesNotContain("el Corte del turno que siga abierto", apagar);
        Assert.Contains("El turno que siga abierto se deja así, sin Corte", apagar);

        var seguir = Paso("- **Seguir después:**", "- **Quitar el POS de la caja 2:**");
        Assert.Contains("sin el corte de su paso 2", seguir);
        Assert.Contains("toma el corte de eleventa", seguir);
    }

    /// <summary>
    /// La instalación y el ensayo pueden ser en días distintos: con la cola como en el paso 11 y la caja cerrada, lo que se imprime en la
    /// caja 2 no lo mide Control Print y al abrirla saldría todo en F2 como de hoy.
    /// </summary>
    [Fact]
    public void La_cola_no_se_queda_puesta_entre_la_instalacion_y_el_ensayo()
    {
        var instalar = Paso("  11. **Control Print,** si el paquete lo trae", "- **Sale bien si:**");
        Assert.Contains("Si el ensayo no se hace hoy", instalar);
        Assert.Contains("el puerto anotado", instalar);
        Assert.Contains("-KeepPrintedJobs $false", instalar);

        var copia = Paso("- **2. La copia:**", "- **3. Apuntar la caja 2 a la copia");
        Assert.Contains("-KeepPrintedJobs $true", copia);
    }

    /// <summary>En la caja principal no se instala nada, y el .cmd compila: el README tampoco puede mandar a correrlo en la tienda.</summary>
    [Fact]
    public void El_README_no_manda_el_cmd_del_dia_pico_a_la_tienda()
    {
        var readme = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "README.md"));
        Assert.DoesNotContain("En la tienda, `scripts\\simular-dia-pico.cmd`", readme);
        Assert.Contains("desde la memoria USB", readme);
    }

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
