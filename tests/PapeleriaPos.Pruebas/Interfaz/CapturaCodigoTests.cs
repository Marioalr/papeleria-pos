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

    /// <summary>Un escaneo completo de una vez, desde el hilo de Blazor (a media operación).</summary>
    private Task Enter(string codigo, Func<string, Task> procesar)
    {
        _captura.Texto = codigo;
        return _captura.EnterAsync(procesar);
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

    [Fact]
    public async Task F12_justo_despues_del_ultimo_escaneo_espera_a_que_llegue_su_linea()
    {
        var lineas = new List<string>();
        async Task AgregarAsync(string codigo)
        {
            await Task.Delay(150);
            lineas.Add(codigo);
        }

        await EscanearAsync("7500000000011", AgregarAsync);
        await EscanearAsync("7500000000028", AgregarAsync);
        // La tecla llega mientras las dos consultas siguen en vuelo: espera, y al abrir el cobro ya están las dos líneas.
        await _blazor.InvokeAsync(() => _captura.EsperarAsync());

        Assert.Equal(["7500000000011", "7500000000028"], lineas);
    }

    [Fact]
    public async Task El_cobro_espera_el_ultimo_escaneo_y_sabe_que_llego()
    {
        var lineas = new List<string>();
        async Task AgregarAsync(string codigo)
        {
            await Task.Delay(150);
            lineas.Add(codigo);
        }

        // Enter en «Pagó con» justo después de escanear, con el Wi-Fi lento: la última línea sigue en camino.
        await EscanearAsync("7500000000011", AgregarAsync);
        var apartado = await _blazor.InvokeAsync(() => _captura.ApartarAsync(() => lineas.Count));
        apartado.Dispose();

        Assert.Equal(["7500000000011"], lineas);
        Assert.True(apartado.LlegoAlgo);
    }

    [Fact]
    public async Task Sin_escaneos_en_camino_el_cobro_sigue_sin_esperar()
    {
        var lineas = new List<string> { "7500000000011" };

        var apartado = await _blazor.InvokeAsync(() => _captura.ApartarAsync(() => lineas.Count));
        apartado.Dispose();

        Assert.False(apartado.LlegoAlgo);
    }

    [Fact]
    public async Task Lo_que_se_escanea_mientras_se_cobra_cae_en_el_ticket_siguiente()
    {
        var cobrado = new List<string> { "7500000000011" };
        var siguiente = new List<string>();
        var actual = cobrado;
        async Task AgregarAsync(string codigo)
        {
            await Task.Delay(20);
            actual.Add(codigo);
        }

        await _blazor.InvokeAsync(async () =>
        {
            using var apartado = await _captura.ApartarAsync(() => actual.Count);
            // Mientras el cobro va y viene por el Wi-Fi, el lector ya escanea lo del siguiente cliente.
            _enCurso.Add(Enter("7500000000028", AgregarAsync));
            await Task.Delay(100);
            Assert.Single(cobrado);
            // Se cobró: queda el ticket del siguiente cliente y se suelta la cola.
            actual = siguiente;
        });
        await Task.WhenAll(_enCurso);

        Assert.Equal(["7500000000011"], cobrado);
        Assert.Equal(["7500000000028"], siguiente);
    }

    [Fact]
    public async Task Si_el_cobro_falla_lo_escaneado_mientras_tanto_va_al_mismo_ticket()
    {
        var lineas = new List<string> { "7500000000011" };
        async Task AgregarAsync(string codigo)
        {
            await Task.Delay(20);
            lineas.Add(codigo);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => _blazor.InvokeAsync(async () =>
        {
            using var apartado = await _captura.ApartarAsync(() => lineas.Count);
            _enCurso.Add(Enter("7500000000028", AgregarAsync));
            await Task.Delay(50);
            throw new InvalidOperationException("La caja principal no contestó");
        }));
        await Task.WhenAll(_enCurso);

        Assert.Equal(["7500000000011", "7500000000028"], lineas);
    }

    [Fact]
    public async Task Las_teclas_esperan_a_que_termine_el_cobro()
    {
        var pasos = new List<string>();
        var soltar = new TaskCompletionSource();

        await _blazor.InvokeAsync(async () =>
        {
            var apartado = await _captura.ApartarAsync(() => 0);
            _enCurso.Add(Task.Run(async () =>
            {
                await soltar.Task;
                await _blazor.InvokeAsync(() =>
                {
                    pasos.Add("cobrado");
                    apartado.Dispose();
                });
            }));
        });
        // F6 llega a medio cobro: no aparta el ticket que se está cobrando.
        var f6 = _blazor.InvokeAsync(async () =>
        {
            await _captura.EsperarAsync();
            pasos.Add("F6");
        });
        await Task.Delay(50);
        Assert.Empty(pasos);
        soltar.SetResult();
        await f6;
        await Task.WhenAll(_enCurso);

        Assert.Equal(["cobrado", "F6"], pasos);
    }

    [Fact]
    public async Task Un_renglon_de_la_lista_queda_en_orden_con_los_escaneos()
    {
        var lineas = new List<string>();
        async Task AgregarAsync(string codigo)
        {
            await Task.Delay(codigo == "7500000000011" ? 150 : 10);
            lineas.Add(codigo);
        }

        await EscanearAsync("7500000000011", AgregarAsync);
        // Un clic en la lista de resultados mientras el escaneo anterior sigue en camino.
        await _blazor.InvokeAsync(() => _enCurso.Add(_captura.EnTurnoAsync(() => AgregarAsync("Cuaderno elegido de la lista"))));
        await Task.WhenAll(_enCurso);

        Assert.Equal(["7500000000011", "Cuaderno elegido de la lista"], lineas);
    }

    [Fact]
    public async Task Esperar_no_falla_si_un_escaneo_fallo()
    {
        async Task FallarAsync(string _)
        {
            await Task.Delay(20);
            throw new InvalidOperationException("Falla de prueba");
        }

        await EscanearAsync("7500000000011", FallarAsync);
        await _blazor.InvokeAsync(() => _captura.EsperarAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _enCurso[0]);
    }

    /// <summary>
    /// Un código que no está en la copia (entrega 5) se pregunta aparte: la cola no lo espera y los escaneos que siguen entran
    /// enseguida; las teclas del ticket y el cobro sí lo esperan, y el cobro sabe que llegó algo.
    /// </summary>
    [Fact]
    public async Task Lo_que_se_pregunta_aparte_no_detiene_la_cola_pero_las_teclas_y_el_cobro_lo_esperan()
    {
        var lineas = new List<string>();
        var contesta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task PreguntarAsync()
        {
            await contesta.Task;
            await _blazor.InvokeAsync(() => lineas.Add("nuevo"));
        }
        Task ProcesarAsync(string codigo)
        {
            if (codigo == "nuevo")
                _captura.EnVuelo(PreguntarAsync());
            else
                lineas.Add(codigo);
            return Task.CompletedTask;
        }

        await _blazor.InvokeAsync(() => Enter("7500000000011", ProcesarAsync));
        await _blazor.InvokeAsync(() => Enter("nuevo", ProcesarAsync));
        await _blazor.InvokeAsync(() => Enter("7500000000028", ProcesarAsync));
        Assert.Equal(["7500000000011", "7500000000028"], lineas);
        Assert.Equal(1, _captura.Buscando);

        var f6 = _blazor.InvokeAsync(() => _captura.EsperarAsync());
        var cobro = _blazor.InvokeAsync(() => _captura.ApartarAsync(() => lineas.Count));
        Assert.False(f6.IsCompleted);
        Assert.False(cobro.IsCompleted);
        contesta.SetResult();
        await f6;
        using var apartado = await cobro;

        Assert.Equal(["7500000000011", "7500000000028", "nuevo"], lineas);
        Assert.True(apartado.LlegoAlgo);
    }
}
