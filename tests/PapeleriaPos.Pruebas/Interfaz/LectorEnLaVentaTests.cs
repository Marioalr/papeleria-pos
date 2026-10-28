using PapeleriaPos.Interfaz;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El lector por puerto COM y lo que se teclea después de escanear (revisión del PR #142). Con el lector de teclado, el escaneo y
/// las teclas que siguen (F12 y Enter, «5*» y Enter, F6) van por la misma fila de la página: el escaneo llega a .NET antes, y el cobro
/// (EnTurnoDeCobroAsync → CapturaCodigo.ApartarAsync), «5*» y F6 esperan lo que ya estaba en la cola de la captura. El del puerto va
/// por otro camino (el hilo del lector → EntregaDelLector → el hilo de la ventana → caja.js → la venta), y antes nada del cobro sabía
/// que el puerto ya había leído un código: con la entrega lenta, se cobraba sin él y caía en el ticket del siguiente cliente, «5*»
/// cambiaba el renglón de antes, F6 ponía en espera el ticket sin él. Ahora lo esperan (LectorPorPuerto.EsperarLoLeidoAsync), hasta
/// que caja.js dice que la venta lo recibió (LectorRecibido).
/// <para>
/// Aquí la página «ocupada» detiene la entrega; la página de mentira hace lo que caja.js: le da el código a la venta y, en el mismo
/// turno, le dice que lo recibió. La tecla empieza antes de soltar la página (como en la caja, donde las dos van a la vez) y se espera
/// después.
/// </para>
/// </summary>
public sealed class LectorEnLaVentaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"lector-venta-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    /// <summary>La venta con su lector por puerto, y su entrega como la hace caja.js, detenida hasta <paramref name="paginaLibre"/>.</summary>
    private async Task<(VentaConCopia Venta, LectorPorPuerto Lector, PuertosDeMentira Puertos, EntregaDelLector Entrega)> VentaConLectorAsync(
        Task paginaLibre, Func<VentaConCopia, string, Task> comoCajaJs, string resultado)
    {
        var puertos = new PuertosDeMentira();
        var lector = new LectorPorPuerto(new AjustesLector("COM3"), null, puertos, reintento: TimeSpan.FromMilliseconds(30));
        var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, lector: lector);
        var entrega = new EntregaDelLector(lector, async (codigo, id) =>
        {
            await paginaLibre;
            // Como caja.js: a la venta y, después, que ya lo recibió (los dos van a .NET en orden).
            await v.Pantalla.Dispatcher.InvokeAsync(() =>
            {
                _ = comoCajaJs(v, codigo);
                v.Venta.LectorRecibido(id);
            });
            return resultado;
        });
        entrega.Iniciar();
        lector.Iniciar();
        await VentaConCopia.EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Listo, "el puerto abierto");
        return (v, lector, puertos, entrega);
    }

    /// <summary>
    /// Escanear lo último y cobrar exacto enseguida (F12, Enter): el cobro espera lo que el puerto ya leyó, no cobra sin ello y dice
    /// «Llegó lo último que escaneaste», como con el lector de teclado; al volver a cobrar, se cobra con todo.
    /// </summary>
    [Fact]
    public async Task Lo_que_el_lector_por_puerto_ya_leyo_antes_de_cobrar_no_queda_fuera_del_cobro()
    {
        var paginaLibre = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // caja.js con el cursor en «Pagó con» (después de F12): escaneoExterno → rafagaEnCampo → EscaneoEnPago.
        var (v, lector, puertos, entrega) = await VentaConLectorAsync(paginaLibre.Task, (venta, codigo) => venta.Venta.EscaneoEnPago(codigo), "cobro");
        await using (v)
        await using (lector)
        await using (entrega)
        {
            var (a, b) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);
            var antes = await UltimoAsync(v);

            puertos.Abierto!.Mandar(b.Codigo + "\r");
            await VentaConCopia.EsperarAsync(() => lector.Codigos == 1, "que el puerto lea el código de B");
            // F12 y Enter: cobrar exacto, mientras la entrega de B sigue en camino.
            var cobro = v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
            await Task.Delay(150);
            Assert.False(cobro.IsCompleted, "El cobro no esperó lo que el puerto ya leyó.");
            paginaLibre.SetResult();
            await cobro.WaitAsync(TimeSpan.FromSeconds(20));

            // No se cobró sin B: B entró al mismo ticket y la caja dice que llegó lo último.
            Assert.Equal(antes?.Folio, (await UltimoAsync(v))?.Folio);
            var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
            Assert.Equal(2, renglones.Count);
            Assert.StartsWith($"1 {a.Descripcion}", renglones[0]);
            Assert.StartsWith($"1 {b.Descripcion}", renglones[1]);
            Assert.Contains("Llegó lo último que escaneaste", await v.Pantalla.TextoAsync());

            // Al volver a cobrar, se cobra con los dos.
            await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
            var cobrado = await UltimoAsync(v);
            Assert.NotEqual(antes?.Folio, cobrado?.Folio);
            Assert.Equal(2, cobrado!.Lineas.Count);
            Assert.Contains(cobrado.Lineas, l => l.Descripcion.Contains(b.Descripcion, StringComparison.Ordinal));
            Assert.Empty(v.Pantalla.Errores);
        }
    }

    /// <summary>El último ticket cobrado en esta caja; vacío si todavía no tiene ventas.</summary>
    private static async Task<PapeleriaPos.Nucleo.Contratos.TicketDto?> UltimoAsync(VentaConCopia v)
    {
        try
        {
            return await v.Cliente.UltimoTicketAsync(v.Caja.Id);
        }
        catch (ErrorCaja)
        {
            return null;
        }
    }

    /// <summary>Escanear y luego «5*» y Enter: cinco de lo último que leyó el puerto, no del renglón de antes.</summary>
    [Fact]
    public async Task La_cantidad_con_asterisco_es_para_lo_ultimo_que_leyo_el_lector_por_puerto()
    {
        var paginaLibre = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // caja.js con el cursor en el cuadro de captura: escaneoExterno → enterDeLaCaptura → Escaneado.
        var (v, lector, puertos, entrega) = await VentaConLectorAsync(paginaLibre.Task, (venta, codigo) => venta.Venta.Escaneado(codigo), "captura");
        await using (v)
        await using (lector)
        await using (entrega)
        {
            var (a, b) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);

            puertos.Abierto!.Mandar(b.Codigo + "\r");
            await VentaConCopia.EsperarAsync(() => lector.Codigos == 1, "que el puerto lea el código de B");
            // «5*» y Enter, tecleados después de escanear B.
            var cinco = v.EscanearAsync("5*");
            await Task.Delay(150);
            paginaLibre.SetResult();
            await cinco.WaitAsync(TimeSpan.FromSeconds(20));
            await VentaConCopia.EsperarAsync(() => !lector.HayEnCamino, "que la venta reciba B");
            await v.Pantalla.Dispatcher.InvokeAsync(() => { });

            var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
            Assert.Equal(2, renglones.Count);
            Assert.StartsWith($"1 {a.Descripcion}", renglones[0]);
            Assert.StartsWith($"5 {b.Descripcion}", renglones[1]);
            Assert.Empty(v.Pantalla.Errores);
        }
    }

    /// <summary>Escanear y poner el ticket en espera (F6) enseguida: lo que leyó el puerto se va con el ticket en espera.</summary>
    [Fact]
    public async Task F6_pone_en_espera_el_ticket_con_lo_que_el_puerto_ya_leyo()
    {
        var paginaLibre = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (v, lector, puertos, entrega) = await VentaConLectorAsync(paginaLibre.Task, (venta, codigo) => venta.Venta.Escaneado(codigo), "captura");
        await using (v)
        await using (lector)
        await using (entrega)
        {
            var (a, b) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);

            puertos.Abierto!.Mandar(b.Codigo + "\r");
            await VentaConCopia.EsperarAsync(() => lector.Codigos == 1, "que el puerto lea el código de B");
            var enEspera = v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F6", null));
            await Task.Delay(150);
            paginaLibre.SetResult();
            await enEspera.WaitAsync(TimeSpan.FromSeconds(20));

            // El ticket nuevo quedó vacío: los dos renglones se fueron con el de la espera.
            Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
            await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F5", null));
            var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
            Assert.Equal(2, renglones.Count);
            Assert.Contains(renglones, r => r.StartsWith($"1 {b.Descripcion}", StringComparison.Ordinal));
            Assert.Empty(v.Pantalla.Errores);
        }
    }
}
