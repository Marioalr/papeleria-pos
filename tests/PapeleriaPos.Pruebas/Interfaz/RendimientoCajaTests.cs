using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Lo que mide la caja de su rendimiento (entrega 5, docs/propuesta-interfaz.md, «Cómo se mide»): lo que tardan los
/// últimos escaneos (la mitad, el 95 % y el 99 %) y el vigía del hilo de la pantalla, que anota en el registro del día si
/// se traba durante una ráfaga, con lo que pasaba.
/// </summary>
public sealed class RendimientoCajaTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"rendimiento-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
            Directory.Delete(_carpeta, recursive: true);
    }

    [Fact]
    public void La_mitad_el_95_y_el_99_son_de_los_ultimos_escaneos()
    {
        var rendimiento = new RendimientoCaja();
        Assert.Equal("Todavía no hay escaneos medidos: se miden en la venta, del Enter a la línea en pantalla.", rendimiento.Escaneos.Texto);

        // Del 1 al 100 ms, revueltos: la mitad en 50, el 95 % en 95 y el 99 % en 99 (valores que sí se midieron).
        foreach (var ms in Enumerable.Range(1, 100).OrderBy(x => (x * 37) % 101))
            rendimiento.AnotarEscaneo(TimeSpan.FromMilliseconds(ms));
        var medida = rendimiento.Escaneos;

        Assert.Equal((100, 50.0, 95.0, 99.0), (medida.Cuantos, medida.Mitad, medida.P95, medida.P99));
        Assert.Equal("Del Enter a la línea en pantalla, los últimos 100 escaneos: la mitad en 50 ms, el 95 % en 95 ms y el 99 % en 99 ms.", medida.Texto);

        // Solo se recuerdan los últimos: 500 escaneos lentos después ya no dejan ver los de antes.
        for (var i = 0; i < RendimientoCaja.Recordados; i++)
            rendimiento.AnotarEscaneo(TimeSpan.FromMilliseconds(400));
        Assert.Equal((RendimientoCaja.Recordados, 400.0, 400.0), (rendimiento.Escaneos.Cuantos, rendimiento.Escaneos.Mitad, rendimiento.Escaneos.P99));
        // Lo que no puede ser (un reloj que se fue para atrás) no cuenta.
        rendimiento.AnotarEscaneo(TimeSpan.FromMilliseconds(-3));
        Assert.Equal(400.0, rendimiento.Escaneos.Mitad);
    }

    [Fact]
    public void Lo_que_paso_y_tardo_se_encuentra_por_cuando_paso()
    {
        var rendimiento = new RendimientoCaja();
        var antes = Stopwatch.GetTimestamp();
        rendimiento.Actividad("la base de la caja (los tickets en espera)", TimeSpan.FromMilliseconds(80));
        var despues = Stopwatch.GetTimestamp();

        Assert.Equal(["la base de la caja (los tickets en espera), 80 ms"], rendimiento.Entre(antes, despues));
        Assert.Empty(rendimiento.Entre(despues + Stopwatch.Frequency, despues + 2 * Stopwatch.Frequency));
    }

    /// <summary>
    /// En una ráfaga, si el hilo de la pantalla se traba más de 30 ms, queda una línea en el registro del día con lo que
    /// pasaba. La pantalla se traba aquí a propósito (hasta que el vigía la vio trabada), sin suponer cuánto tarda la PC.
    /// </summary>
    [Fact]
    public async Task En_una_rafaga_lo_que_traba_la_pantalla_queda_en_el_registro_con_lo_que_pasaba()
    {
        var pantalla = Dispatcher.CreateDefault();
        var rendimiento = new RendimientoCaja();
        var registro = new RegistroDiario(_carpeta, "caja");
        await using var vigia = new VigiaDeLaPantalla(a => pantalla.InvokeAsync(a), rendimiento, registro);

        var limite = DateTime.UtcNow.AddSeconds(20);
        while (vigia.Trabadas == 0)
        {
            Assert.True(DateTime.UtcNow < limite, "El vigía no vio la pantalla trabada.");
            vigia.Escaneo();
            // La sincronización escribe la copia mientras la pantalla está ocupada 120 ms.
            await pantalla.InvokeAsync(() =>
            {
                Thread.Sleep(120);
                rendimiento.Actividad("la sincronización (armar la copia y su foto)", TimeSpan.FromMilliseconds(100));
            });
        }

        var texto = await File.ReadAllTextAsync(registro.ArchivoDeHoy);
        Assert.Matches(@"La pantalla se trabó \d+ ms durante una ráfaga de escaneos; mientras: la sincronización \(armar la copia y su foto\), 100 ms\.", texto);
    }

    [Fact]
    public async Task Fuera_de_una_rafaga_el_vigia_no_mira_ni_anota()
    {
        var pantalla = Dispatcher.CreateDefault();
        var registro = new RegistroDiario(_carpeta, "caja");
        await using var vigia = new VigiaDeLaPantalla(a => pantalla.InvokeAsync(a), new RendimientoCaja(), registro);

        await pantalla.InvokeAsync(() => Thread.Sleep(120));
        await pantalla.InvokeAsync(() => Thread.Sleep(120));

        Assert.Equal(0, vigia.Trabadas);
        Assert.False(File.Exists(registro.ArchivoDeHoy));
    }
}
