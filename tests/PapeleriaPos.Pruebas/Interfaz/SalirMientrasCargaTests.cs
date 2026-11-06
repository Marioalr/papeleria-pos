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
    private static async Task<List<Exception>> ErroresFalsosAsync(string componente, Func<Task> cerrar)
    {
        var errores = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        void AlFallar(object? _, UnobservedTaskExceptionEventArgs e)
        {
            // Este aviso es de todo el proceso: el servicio de otra prueba que ya se cerró no es un error de esta pantalla.
            foreach (var error in e.Exception.InnerExceptions.Where(x => x is ObjectDisposedException { ObjectName: not "Microsoft.AspNetCore.TestHost.TestServer" } && x.StackTrace?.Contains(componente, StringComparison.Ordinal) == true))
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
}
