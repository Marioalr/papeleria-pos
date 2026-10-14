using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Lo que falla en la caja queda anotado (registros\caja-….log) en lugar de perderse, y un error de la pantalla dice
/// «Algo falló» con «Volver a cargar» en lugar de cerrar la caja. La ventana de Windows (App, MainWindow) no corre aquí.
/// </summary>
public sealed class RegistroDiarioTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"registros-{Guid.NewGuid():N}");

    /// <summary>Al mediodía en UTC: el mismo día en la hora de la PC (la tienda está en UTC-6).</summary>
    private readonly RelojFijo _reloj = new(new DateTimeOffset(2026, 10, 17, 18, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    private RegistroDiario Registro(string nombre = "caja", long tope = RegistroDiario.TopePorOmision, long reserva = RegistroDiario.ReservaPorOmision) =>
        new(_carpeta, nombre, tope: tope, reloj: _reloj, reserva: reserva);

    private string Dia(int masDias = 0) =>
        DateOnly.FromDateTime(_reloj.GetLocalNow().DateTime).AddDays(masDias).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public void Anota_con_fecha_y_hora_en_el_archivo_del_dia_y_al_dia_siguiente_en_otro()
    {
        var registro = Registro();

        registro.Anotar("Se abrió la caja.");
        _reloj.Ahora = _reloj.Ahora.AddDays(1);
        registro.Anotar("Se abrió la caja otra vez.");

        var hoy = File.ReadAllText(Path.Combine(_carpeta, $"caja-{Dia(-1)}.log"));
        var hora = _reloj.GetLocalNow().AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Equal($"{hora}  Se abrió la caja.{Environment.NewLine}", hoy);
        Assert.Contains("Se abrió la caja otra vez.", File.ReadAllText(Path.Combine(_carpeta, $"caja-{Dia()}.log")));
        Assert.Equal(Path.Combine(_carpeta, $"caja-{Dia()}.log"), registro.ArchivoDeHoy);
    }

    [Fact]
    public void Un_error_se_anota_completo_y_una_sola_vez_aunque_avisen_dos()
    {
        var registro = Registro();
        Exception error;
        try
        {
            throw new InvalidOperationException("Se rompió a propósito", new IOException("El de adentro"));
        }
        catch (InvalidOperationException ex)
        {
            error = ex;
        }

        Assert.True(registro.AnotarError("Error en la ventana", error));
        // WPF lo vuelve a lanzar y la tarea que falló avisa después con el mismo error adentro.
        Assert.False(registro.AnotarError("Error en la ventana", error));
        Assert.False(registro.AnotarError("Error en una tarea que nadie esperaba", new AggregateException(error)));
        Assert.True(registro.AnotarError("Error en la ventana", new InvalidOperationException("Otro")));

        var texto = File.ReadAllText(registro.ArchivoDeHoy);
        Assert.Equal(1, Ocurrencias(texto, "Se rompió a propósito"));
        Assert.Contains("Error en la ventana:", texto);
        Assert.Contains("System.InvalidOperationException: Se rompió a propósito", texto);
        Assert.Contains("El de adentro", texto);
        Assert.Contains(nameof(Un_error_se_anota_completo_y_una_sola_vez_aunque_avisen_dos), texto);
        Assert.DoesNotContain("Error en una tarea", texto);
    }

    [Fact]
    public void Guarda_los_ultimos_catorce_dias_y_no_toca_lo_demas()
    {
        Directory.CreateDirectory(_carpeta);
        var archivos = new[]
        {
            $"caja-{Dia(-14)}.log", $"caja-{Dia(-13)}.log", $"caja-{Dia()}.log", $"servicio-{Dia(-30)}.log", "caja-copia.log",
            "respuestas-prueba.json",
        };
        foreach (var archivo in archivos)
            File.WriteAllText(Path.Combine(_carpeta, archivo), "algo");

        Registro().Limpiar();

        Assert.Equal(archivos.Skip(1).Order(), Directory.EnumerateFiles(_carpeta).Select(f => Path.GetFileName(f)).Order());
        Registro("servicio").Limpiar();
        Assert.False(File.Exists(Path.Combine(_carpeta, $"servicio-{Dia(-30)}.log")));
    }

    /// <summary>
    /// Un error que se repite no se lleva el final del día (revisión del 2026-10-14): pasado el tope, de cada cosa queda
    /// una línea, y así todavía se ve que se cerró la caja; pasada la reserva ya no se anota nada.
    /// </summary>
    [Fact]
    public void Pasado_el_tope_queda_una_linea_de_cada_cosa_hasta_la_reserva()
    {
        const long tope = 2000, reserva = 1000;
        var registro = Registro(tope: tope, reserva: reserva);
        string Texto() => File.Exists(registro.ArchivoDeHoy) ? File.ReadAllText(registro.ArchivoDeHoy) : "";

        for (var i = 0; !Texto().Contains("(sin el detalle"); i++)
        {
            Assert.InRange(i, 0, 100);
            registro.AnotarError("Error en la ventana", Lanzado($"El mismo error otra vez ({i})"));
        }
        registro.Anotar("Se cerró la caja.");

        var texto = Texto();
        // Antes del tope, completo (con dónde pasó); después, el tipo y el mensaje en una línea.
        Assert.Contains($"   at {typeof(RegistroDiarioTests).FullName}.{nameof(Lanzado)}", texto);
        Assert.Contains("Error en la ventana: System.InvalidOperationException: El mismo error otra vez", texto);
        var hora = _reloj.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Contains($"{Environment.NewLine}{hora}  Se cerró la caja.{Environment.NewLine}", texto);
        Assert.DoesNotContain("Se cerró la caja. (sin el detalle", texto);
        // Con todo el detalle mientras cabía en el tope (la que ya no cupo salió en una línea, así que puede quedar un poco
        // abajo de él: depende del largo de la traza, distinto en Windows y en Linux), y nunca más allá de la reserva.
        Assert.InRange(new FileInfo(registro.ArchivoDeHoy).Length, tope / 2, tope + reserva);

        for (var i = 0; !Texto().Contains("se llenó"); i++)
        {
            Assert.InRange(i, 0, 100);
            registro.AnotarError("Error en la ventana", Lanzado($"Y otra vez ({i})"));
        }
        registro.Anotar("Después de llenarse.");
        // Otra vez abierta la caja, el mismo día: tampoco.
        Registro(tope: tope, reserva: reserva).Anotar("Al volver a abrir.");

        var lineas = File.ReadAllLines(registro.ArchivoDeHoy);
        Assert.Contains("El registro de hoy se llenó", lineas[^1]);
        Assert.Equal(1, lineas.Count(l => l.Contains("se llenó")));
        Assert.InRange(new FileInfo(registro.ArchivoDeHoy).Length, tope, tope + reserva + 200);
    }

    /// <summary>Un error de JavaScript en una tecla llega cada vez que se aprieta: completo una vez, y después cuántas van.</summary>
    [Fact]
    public void Lo_que_se_repite_se_anota_completo_una_vez_y_despues_cuantas_van()
    {
        var registro = Registro();
        const string error = "TypeError: no hay ticket (caja.js:10:5)\n    at tecla (caja.js:10:5)";

        for (var i = 0; i < 120; i++)
            registro.AnotarSinRepetir("Error en la página", error);
        registro.AnotarSinRepetir("Error en la página", "ReferenceError: otro");

        var texto = File.ReadAllText(registro.ArchivoDeHoy);
        Assert.Equal(1, Ocurrencias(texto, "at tecla"));
        Assert.Contains($"Error en la página:{Environment.NewLine}{error}", texto);
        Assert.Equal(["2", "10", "100"],
            texto.Split(Environment.NewLine).Where(l => l.Contains("el mismo otra vez")).Select(l => l.Split("ya van ")[1].Split(')')[0]));
        Assert.Contains("Error en la página, el mismo otra vez (ya van 10): TypeError: no hay ticket (caja.js:10:5)", texto);
        Assert.Contains("ReferenceError: otro", texto);
    }

    [Fact]
    public void Si_no_puede_escribir_la_caja_sigue_sin_error()
    {
        // La carpeta del registro no se puede crear: en su lugar hay un archivo.
        Directory.CreateDirectory(_carpeta);
        var ocupado = Path.Combine(_carpeta, "ocupado");
        File.WriteAllText(ocupado, "");
        var registro = new RegistroDiario(Path.Combine(ocupado, "registros"), "caja", reloj: _reloj);

        registro.Anotar("Se abrió la caja.");
        Assert.True(registro.AnotarError("Error en la ventana", new InvalidOperationException("No se anota")));
        registro.Limpiar();

        Assert.False(Directory.Exists(Path.Combine(ocupado, "registros")));
    }

    [Fact]
    public void En_la_prueba_los_registros_van_junto_a_sus_datos_y_si_no_en_la_carpeta_del_usuario()
    {
        var datos = Path.Combine(_carpeta, "PapeleriaPos-Prueba");

        Assert.Equal(Path.Combine(datos, "registros"), RegistroDiario.CarpetaDeLaCaja(datos));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PapeleriaPos", "registros"),
            RegistroDiario.CarpetaDeLaCaja(null));
    }

    [Fact]
    public void La_caja_sigue_tras_un_error_suelto_y_se_cierra_si_se_repiten_sin_parar_o_no_hay_memoria()
    {
        var seguidos = new ErroresSeguidos(_reloj);

        for (var i = 0; i < ErroresSeguidos.Tope; i++)
            Assert.True(seguidos.SePuedeSeguir(new NullReferenceException()));
        Assert.False(seguidos.SePuedeSeguir(new NullReferenceException()));
        _reloj.Ahora = _reloj.Ahora.AddMinutes(1);
        Assert.True(seguidos.SePuedeSeguir(new NullReferenceException()));
        Assert.False(seguidos.SePuedeSeguir(new OutOfMemoryException()));
    }

    [Fact]
    public async Task Lo_que_truena_en_la_pantalla_dice_algo_fallo_con_volver_a_cargar_y_queda_anotado()
    {
        var registro = Registro();
        await using var proveedor = new ServiceCollection().AddSingleton(registro).BuildServiceProvider();
        await using var dibujo = new HtmlRenderer(proveedor, NullLoggerFactory.Instance);

        var bien = await Dibujar<Vende>(dibujo);
        var mal = await Dibujar<Truena>(dibujo);

        Assert.Contains("Vendiendo", bien);
        Assert.DoesNotContain("Algo falló", bien);
        Assert.Contains("Algo falló", mal);
        Assert.Contains("La caja no se cerró", mal);
        // Vuelve a cargar sin pasar por .NET, aunque lo que falló sea la conexión con la página.
        Assert.Contains("onclick=\"location.reload()\"", mal);
        Assert.Contains("Volver a cargar", mal);
        Assert.Contains("InvalidOperationException: Se rompió a propósito", mal);
        var texto = File.ReadAllText(registro.ArchivoDeHoy);
        Assert.Contains("Algo falló en la pantalla:", texto);
        Assert.Contains("System.InvalidOperationException: Se rompió a propósito", texto);
        Assert.Contains(nameof(Truena), texto);
    }

    private static Task<string> Dibujar<T>(HtmlRenderer dibujo) where T : IComponent =>
        dibujo.Dispatcher.InvokeAsync(async () =>
        {
            var parametros = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(AlgoFallo.ChildContent)] = (RenderFragment)(b =>
                {
                    b.OpenComponent<T>(0);
                    b.CloseComponent();
                }),
            });
            // Sin los acentos escapados («fall&#xF3;»): como se ve en la pantalla.
            return WebUtility.HtmlDecode((await dibujo.RenderComponentAsync<AlgoFallo>(parametros)).ToHtmlString());
        });

    private static int Ocurrencias(string texto, string buscado) => texto.Split(buscado).Length - 1;

    /// <summary>Un error que se lanzó de verdad: trae dónde pasó.</summary>
    private static InvalidOperationException Lanzado(string mensaje)
    {
        try
        {
            throw new InvalidOperationException(mensaje);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private sealed class Vende : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
            builder.AddContent(0, "Vendiendo");
    }

    private sealed class Truena : ComponentBase
    {
        protected override void OnInitialized() => throw new InvalidOperationException("Se rompió a propósito");
    }
}
