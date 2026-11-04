using System.Reflection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
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
    /// Su consulta termina con la pantalla ya cerrada: el ciclo pedía el Token de una fuente ya desechada y la tarea, que nadie espera,
    /// tronaba; en el registro de la caja quedaba «Error en una tarea que nadie esperaba».
    /// </summary>
    [Fact]
    public async Task Cerrar_el_estado_de_la_caja_con_su_consulta_en_camino_no_deja_un_error_falso()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var errores = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        void AlFallar(object? _, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var error in e.Exception.InnerExceptions.Where(x => x is ObjectDisposedException && x.StackTrace?.Contains(nameof(EstadoCaja), StringComparison.Ordinal) == true))
                errores.Add(error);
        }
        TaskScheduler.UnobservedTaskException += AlFallar;
        try
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

        Assert.Empty(errores);
    }
}
