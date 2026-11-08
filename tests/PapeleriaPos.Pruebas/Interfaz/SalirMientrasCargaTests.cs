using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>Lo que sigue en camino cuando una pantalla se cierra (Ctrl+U, volver a cargar) no la deja viva ni truena después.</summary>
public sealed class SalirMientrasCargaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"salir-cargando-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static int Suscritos(Contingencia contingencia) =>
        (typeof(Contingencia).GetField(nameof(Contingencia.Cambio), BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(contingencia) as Delegate)?.GetInvocationList().Length ?? 0;

    [Fact]
    public async Task Salir_de_la_venta_mientras_carga_no_la_deja_suscrita_a_la_contingencia()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var antes = Suscritos(v.Contingencia);
        var detenida = v.Red.TodoDetenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otra = new PantallaSinNavegador(v.Servicios);

        await otra.AbrirAsync<PantallaVenta>(new()
        {
            [nameof(PantallaVenta.Caja)] = v.Caja,
            [nameof(PantallaVenta.Sesion)] = v.Cajero,
            [nameof(PantallaVenta.Turno)] = v.Venta.Turno,
        });
        await ((IAsyncDisposable)otra).DisposeAsync();
        v.Red.TodoDetenido = null;
        detenida.SetResult();
        // Lo que estaba en camino termina: sin el arreglo, aquí se suscribía.
        await Task.Delay(1000);

        Assert.Equal(antes, Suscritos(v.Contingencia));
    }

    /// <summary>
    /// Las tareas que nadie espera y tronaron por una fuente ya desechada, con <paramref name="componente"/> en su pila: en el registro de
    /// la caja quedaba «Error en una tarea que nadie esperaba». <paramref name="cerrar"/> cierra la pantalla y suelta lo que estaba en camino.
    /// </summary>
    /// <remarks>
    /// Este aviso es de todo el proceso, y forzar al recolector saca también lo que otras pruebas dejaron: una pantalla que siguió abierta
    /// cuando ya se habían cerrado su servicio o sus servicios truena al darle su vuelta, con el mismo componente en su pila. Por eso solo
    /// cuenta la fuente que la pantalla desecha al cerrarse: venga de la prueba que venga, esa sí es este error.
    /// </remarks>
    private static async Task<List<Exception>> ErroresFalsosAsync(string componente, Func<Task> cerrar)
    {
        var errores = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        void AlFallar(object? _, UnobservedTaskExceptionEventArgs e)
        {
            // Por su mensaje y no por la pila: ahí su nombre sale también en otros errores (es el tipo de un parámetro de HttpClient).
            foreach (var error in e.Exception.InnerExceptions.Where(x => x is ObjectDisposedException && x.Message.Contains(nameof(CancellationTokenSource), StringComparison.Ordinal)
                         && x.StackTrace?.Contains(componente, StringComparison.Ordinal) == true))
                errores.Add(error);
        }
        TaskScheduler.UnobservedTaskException += AlFallar;
        try
        {
            await cerrar();
            await Task.Delay(1000);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= AlFallar;
        }
        return [.. errores];
    }

    /// <summary>Su consulta termina con la pantalla ya cerrada: el ciclo pedía el Token de una fuente ya desechada.</summary>
    [Fact]
    public async Task Cerrar_el_estado_de_la_caja_con_su_consulta_en_camino_no_deja_un_error_falso()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);

        var errores = await ErroresFalsosAsync(nameof(EstadoCaja), async () =>
        {
            var detenida = v.Red.TodoDetenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var otra = new PantallaSinNavegador(v.Servicios);
            await otra.AbrirAsync<EstadoCaja>(new()
            {
                [nameof(EstadoCaja.Caja)] = v.Caja,
                [nameof(EstadoCaja.Sesion)] = v.Cajero,
            });
            await ((IAsyncDisposable)otra).DisposeAsync();
            v.Red.TodoDetenido = null;
            detenida.SetResult();
        });

        Assert.Empty(errores);
    }

    /// <summary>Se cierra mientras carga el conteo la primera vez: el ciclo arranca ya con la fuente desechada.</summary>
    [Fact]
    public async Task Cerrar_el_conteo_mientras_carga_la_primera_vez_no_deja_un_error_falso()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);

        var errores = await ErroresFalsosAsync(nameof(ContarDepartamento), async () =>
        {
            var detenida = v.Red.TodoDetenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var otra = new PantallaSinNavegador(v.Servicios);
            // Abrir no termina hasta que cargue: aquí se cierra antes.
            _ = otra.AbrirAsync<ContarDepartamento>(new()
            {
                [nameof(ContarDepartamento.ConteoId)] = Guid.NewGuid(),
                [nameof(ContarDepartamento.Sesion)] = v.Cajero,
            });
            await ((IAsyncDisposable)otra).DisposeAsync();
            v.Red.TodoDetenido = null;
            detenida.SetResult();
        });

        Assert.Empty(errores);
    }

    /// <summary>En el navegador (sin contingencia) le pregunta al servicio si es el piloto: se cierra antes de que conteste.</summary>
    [Fact]
    public async Task Cerrar_el_chip_de_eleventa_antes_de_saber_si_es_el_piloto_no_deja_un_error_falso()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = Guid.NewGuid() };
        var suelta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var red = new DiceQueEsElPiloto(suelta.Task);
        await using var servicios = new ServiceCollection()
            .AddSingleton(new ClienteCaja(new HttpClient(red), conexion))
            .AddSingleton(new ClientePuente(new HttpClient(red), conexion))
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .BuildServiceProvider();

        var errores = await ErroresFalsosAsync(nameof(PuenteEleventa), async () =>
        {
            var otra = new PantallaSinNavegador(servicios);
            await otra.AbrirAsync<PuenteEleventa>(new()
            {
                [nameof(PuenteEleventa.Caja)] = new CajaDto(Guid.NewGuid(), "Caja 2", "B", Guid.NewGuid(), "Matriz"),
                [nameof(PuenteEleventa.Sesion)] = new SesionDto(Guid.NewGuid(), "Cajero", Rol.Cajero),
            });
            await ((IAsyncDisposable)otra).DisposeAsync();
            suelta.SetResult();
        });

        Assert.Empty(errores);
    }

    /// <summary>Contesta hasta que la suelten, y dice que sí es el piloto.</summary>
    private sealed class DiceQueEsElPiloto(Task suelta) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await suelta.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"pilotoEleventa":true}""", Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>F2 se cierra mientras carga: su ciclo arrancaba ya con la fuente desechada y le pedía su Token.</summary>
    [Fact]
    public async Task Cerrar_F2_mientras_carga_no_deja_un_error_falso()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);

        var errores = await ErroresFalsosAsync(nameof(ImpresionesPorCobrar), async () =>
        {
            var detenida = v.Red.TodoDetenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var otra = new PantallaSinNavegador(v.Servicios);
            // Abrir no termina hasta que cargue: aquí se cierra antes.
            var abriendo = otra.AbrirAsync<ImpresionesPorCobrar>(new()
            {
                [nameof(ImpresionesPorCobrar.Caja)] = v.Caja,
                [nameof(ImpresionesPorCobrar.Sesion)] = v.Cajero,
            });
            await ((IAsyncDisposable)otra).DisposeAsync();
            v.Red.TodoDetenido = null;
            detenida.SetResult();
            // Al terminar de cargar arranca su ciclo: se espera a eso, aunque la PC cargada tarde.
            await Task.WhenAny(abriendo, Task.Delay(TimeSpan.FromSeconds(30)));
        });

        Assert.Empty(errores);
    }

    /// <summary>
    /// Como al volver a cargar la página: se quita la pantalla y después se cierran sus servicios, con la consulta del chip en camino. Al
    /// contestar, su ciclo seguía y les pedía algo ya cerrados: el puente si preguntaba si es el piloto (solo en el navegador; la caja lo
    /// sabe por su contingencia), o la contingencia si leía el puente.
    /// </summary>
    [Theory]
    [InlineData("/api/estado")]
    [InlineData("/api/eleventa/pantalla")]
    public async Task Volver_a_cargar_la_pagina_con_la_consulta_del_chip_en_camino_no_deja_un_error_falso(string enCamino)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = Guid.NewGuid() };
        var suelta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var red = new ContestaElPuente(enCamino, suelta.Task);
        await using var raiz = new ServiceCollection()
            .AddSingleton(new ClienteCaja(new HttpClient(red), conexion))
            .AddSingleton(new ClientePuente(new HttpClient(red), conexion))
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .BuildServiceProvider();
        var pagina = new ServiciosDeLaPagina(raiz);

        var errores = await SueltosAsync(x => x is ObjectDisposedException o && o.ObjectName == pagina.Nombre, async () =>
        {
            var otra = new PantallaSinNavegador(pagina);
            await otra.AbrirAsync<PuenteEleventa>(new()
            {
                [nameof(PuenteEleventa.Caja)] = new CajaDto(Guid.NewGuid(), "Caja 2", "B", Guid.NewGuid(), "Matriz"),
                [nameof(PuenteEleventa.Sesion)] = new SesionDto(Guid.NewGuid(), "Cajero", Rol.Cajero),
            });
            await red.Llego.WaitAsync(TimeSpan.FromSeconds(30));
            await ((IAsyncDisposable)otra).DisposeAsync();
            await pagina.DisposeAsync();
            suelta.SetResult();
            // Hasta que el ciclo les pida algo (sin el arreglo, ahí truena), aunque la PC cargada tarde.
            await Task.WhenAny(pagina.PedidoYaCerrados, Task.Delay(TimeSpan.FromSeconds(10)));
        });

        Assert.Empty(errores);
    }

    /// <summary>
    /// Las tareas que nadie espera y tronaron con lo que <paramref name="cuenta"/> dice que es de esta prueba, mientras <paramref name="cerrar"/>
    /// cierra la pantalla y suelta lo que estaba en camino: como <see cref="ErroresFalsosAsync"/>, con el filtro que se le pida.
    /// </summary>
    private static async Task<List<Exception>> SueltosAsync(Func<Exception, bool> cuenta, Func<Task> cerrar)
    {
        var errores = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        void AlFallar(object? _, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var error in e.Exception.InnerExceptions.Where(cuenta))
                errores.Add(error);
        }
        TaskScheduler.UnobservedTaskException += AlFallar;
        try
        {
            await cerrar();
            await Task.Delay(1000);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= AlFallar;
        }
        return [.. errores];
    }

    /// <summary>
    /// Los servicios de la página, como en la caja: al volver a cargarla, primero se quita la pantalla y después se cierran ellos. Ya
    /// cerrados, pedirles algo truena como allá, pero con su propio nombre: el aviso de errores sueltos es de todo el proceso, y así solo
    /// cuenta lo de esta pantalla (no lo que dejó otra prueba).
    /// </summary>
    private sealed class ServiciosDeLaPagina(IServiceProvider servicios) : IServiceProvider, IAsyncDisposable
    {
        private readonly TaskCompletionSource _pedidoYaCerrados = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _cerrados;

        public string Nombre { get; } = $"Los servicios de la página {Guid.NewGuid():N}";

        /// <summary>Ya cerrados, alguien les pidió algo.</summary>
        public Task PedidoYaCerrados => _pedidoYaCerrados.Task;

        public object? GetService(Type tipo)
        {
            if (_cerrados)
            {
                _pedidoYaCerrados.TrySetResult();
                throw new ObjectDisposedException(Nombre);
            }
            // La pantalla se los pide a ellos mismos (@inject IServiceProvider): son estos, no los de adentro.
            return tipo == typeof(IServiceProvider) ? this : servicios.GetService(tipo);
        }

        public ValueTask DisposeAsync()
        {
            _cerrados = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>El servicio, en el navegador: sí es el piloto, y el puente sin nada. La consulta a <paramref name="detenida"/> espera a que la suelten.</summary>
    private sealed class ContestaElPuente(string detenida, Task suelta) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _llego = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Ya llegó la consulta que espera.</summary>
        public Task Llego => _llego.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ruta = request.RequestUri!.AbsolutePath;
            if (ruta == detenida)
            {
                _llego.TrySetResult();
                await suelta.WaitAsync(cancellationToken);
            }
            var json = ruta == "/api/estado" ? """{"pilotoEleventa":true}""" : """{"porRevisar":[],"avisos":[]}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
