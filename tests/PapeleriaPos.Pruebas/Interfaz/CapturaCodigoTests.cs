using Microsoft.AspNetCore.Components;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El lector de códigos en la pantalla de venta: teclea cada código y un Enter más rápido de lo que contesta la caja
/// principal por un Wi-Fi lento, y Blazor atiende cada tecla sin esperar a que termine la consulta del Enter anterior.
/// </summary>
public class CapturaCodigoTests
{
    // El mismo de Blazor: una cosa a la vez, y mientras una consulta espera, pasa la siguiente tecla.
    private readonly Dispatcher _blazor = Dispatcher.CreateDefault();
    private readonly CapturaCodigo _captura = new();
    private readonly List<Task> _enCurso = [];

    /// <summary>Cada carácter llega como un oninput con el cuadro completo; el Enter, como un onkeydown que nadie espera.</summary>
    private async Task EscanearAsync(string codigo, Func<string, Task> procesar)
    {
        foreach (var caracter in codigo)
        {
            await _blazor.InvokeAsync(() => _captura.Texto += caracter);
            await Task.Delay(2);
        }
        await _blazor.InvokeAsync(() => _enCurso.Add(_captura.EnterAsync(procesar)));
    }

    [Fact]
    public async Task Una_rafaga_del_lector_con_la_red_lenta_deja_las_cinco_lineas_en_orden()
    {
        string[] codigos = ["7500000000011", "7500000000028", "7500000000035", "7500000000042", "7500000000059"];
        var lineas = new List<string>();
        async Task AgregarAsync(string codigo)
        {
            await Task.Delay(200);
            lineas.Add(codigo);
        }

        foreach (var codigo in codigos)
        {
            await EscanearAsync(codigo, AgregarAsync);
            await Task.Delay(30);
        }
        await Task.WhenAll(_enCurso);

        Assert.Equal(codigos, lineas);
        Assert.Equal("", _captura.Texto);
    }

    [Fact]
    public async Task Si_una_consulta_tarda_mas_que_la_siguiente_las_lineas_quedan_en_el_orden_del_lector()
    {
        var lineas = new List<string>();
        async Task AgregarAsync(string codigo)
        {
            // La primera se atora en el Wi-Fi; la segunda contesta enseguida.
            await Task.Delay(codigo == "7500000000011" ? 300 : 10);
            lineas.Add(codigo);
        }

        await EscanearAsync("7500000000011", AgregarAsync);
        await EscanearAsync("7500000000028", AgregarAsync);
        await Task.WhenAll(_enCurso);

        Assert.Equal(["7500000000011", "7500000000028"], lineas);
    }

    [Fact]
    public async Task Si_un_escaneo_falla_los_siguientes_se_procesan()
    {
        var lineas = new List<string>();
        async Task AgregarAsync(string codigo)
        {
            await Task.Delay(50);
            if (codigo == "7500000000011")
                throw new InvalidOperationException("Falla de prueba");
            lineas.Add(codigo);
        }

        await EscanearAsync("7500000000011", AgregarAsync);
        await EscanearAsync("7500000000028", AgregarAsync);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _enCurso[0]);
        await _enCurso[1];
        Assert.Equal(["7500000000028"], lineas);
    }
}
