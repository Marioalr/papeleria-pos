using System.Diagnostics;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El lector por puerto COM y lo que se teclea antes y después de escanear (revisión del PR #142 y su segunda revisión). Con el lector
/// de teclado, el escaneo y las teclas van por la misma fila de la página, en el orden en que pasaron: el escaneo de antes llega a .NET
/// antes que la tecla, y el de después, después. El cobro (EnTurnoDeCobroAsync → CapturaCodigo.ApartarAsync), «5*», F6 y las flechas
/// esperan lo que ya estaba en la cola de la captura. El del puerto va por otro camino (el hilo del lector → EntregaDelLector → el hilo
/// de la ventana → caja.js → la venta):
/// <list type="bullet">
/// <item>Antes nada del cobro sabía que el puerto ya había leído un código: con la entrega lenta, se cobraba sin él y caía en el ticket
/// del siguiente cliente, «5*» cambiaba el renglón de antes, F6 ponía en espera el ticket sin él. Ahora lo esperan
/// (LectorPorPuerto.EsperarLoLeidoAsync), hasta que caja.js dice que la venta lo recibió (LectorRecibido).</item>
/// <item>Y la espera no sabía cuándo se oprimió la tecla: con la pantalla ocupada, la tecla se atendía después de que el puerto leía lo
/// que se escaneó DESPUÉS de ella, y se lo llevaba (el primer producto del siguiente cliente se cobraba con este). Ahora caja.js manda
/// la hora de la PC de cada tecla, y se espera solo lo que el puerto empezó a leer antes (segunda revisión).</item>
/// </list>
/// <para>
/// Aquí la página «ocupada» detiene la entrega (o la pantalla entera); la página de mentira hace lo que caja.js: le da el código a la
/// venta y, en el mismo turno, le dice que lo recibió. La tecla empieza antes de soltar la página (como en la caja, donde las dos van a
/// la vez) y se espera después. Su hora es la de la PC al oprimirla, como Date.now() en caja.js.
/// </para>
/// </summary>
public sealed class LectorEnLaVentaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"lector-venta-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    /// <summary>
    /// La venta con su lector por puerto, y su entrega como la hace caja.js, detenida hasta <paramref name="paginaLibre"/> (sin ella,
    /// enseguida). La entrega va al hilo de la pantalla, como en la caja: si la pantalla está ocupada, espera su turno detrás de lo que
    /// ya estaba formado.
    /// </summary>
    /// <param name="pagina">La página (caja.js); sin ella, una que contesta todo al momento.</param>
    private async Task<(VentaConCopia Venta, LectorPorPuerto Lector, PuertosDeMentira Puertos, EntregaDelLector Entrega)> VentaConLectorAsync(
        Func<VentaConCopia, string, Task> comoCajaJs, string resultado, Task? paginaLibre = null, IJSRuntime? pagina = null)
    {
        var puertos = new PuertosDeMentira();
        var lector = new LectorPorPuerto(new AjustesLector("COM3"), null, puertos, reintento: TimeSpan.FromMilliseconds(30));
        var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina, lector: lector);
        var entrega = new EntregaDelLector(lector, async (codigo, id) =>
        {
            if (paginaLibre is not null)
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

    /// <summary>La hora de la PC, como la manda caja.js con cada tecla (Date.now(): milisegundos desde 1970).</summary>
    private static double HoraDeLaPagina() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// La pantalla ocupada (un dibujo largo en la PC de la caja 2): lo que llega después espera su turno detrás. Desde otro hilo y
    /// esperando a que empiece: el despachador corre en el mismo hilo lo que se le pide si no tiene nada.
    /// </summary>
    private static async Task<(Task Ocupada, ManualResetEventSlim Soltar)> PantallaOcupadaAsync(VentaConCopia v)
    {
        var soltar = new ManualResetEventSlim();
        var empezo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocupada = Task.Run(() => v.Pantalla.Dispatcher.InvokeAsync(() =>
        {
            empezo.TrySetResult();
            soltar.Wait(TimeSpan.FromSeconds(15));
        }));
        await empezo.Task;
        return (ocupada, soltar);
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

    // ── Lo que el puerto leyó ANTES de la tecla se espera (revisión del PR #142) ──

    /// <summary>
    /// Escanear lo último y cobrar exacto enseguida (F12 y Enter, o el botón): el cobro espera lo que el puerto ya leyó, no cobra sin
    /// ello y dice «Llegó lo último que escaneaste», como con el lector de teclado; al volver a cobrar, se cobra con todo. El botón no
    /// trae hora: espera todo lo que el puerto ya leyó, como antes.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lo_que_el_lector_por_puerto_ya_leyo_antes_de_cobrar_no_queda_fuera_del_cobro(bool conEnter)
    {
        var paginaLibre = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // caja.js con el cursor en «Pagó con» (después de F12): escaneoExterno → rafagaEnCampo → EscaneoEnPago.
        var (v, lector, puertos, entrega) = await VentaConLectorAsync((venta, codigo) => venta.Venta.EscaneoEnPago(codigo), "cobro", paginaLibre.Task);
        await using (v)
        await using (lector)
        await using (entrega)
        {
            var (a, b) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);
            var antes = await UltimoAsync(v);

            puertos.Abierto!.Mandar(b.Codigo + "\r");
            await VentaConCopia.EsperarAsync(() => lector.Codigos == 1, "que el puerto lea el código de B");
            // Cobrar exacto, mientras la entrega de B sigue en camino: Enter en «Pagó con» (caja.js lo manda con su hora), o el botón.
            var cobro = conEnter
                ? v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("Enter-cobro", null, HoraDeLaPagina()))
                : v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
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

    /// <summary>Escanear y luego «5*» y Enter: cinco de lo último que leyó el puerto, no del renglón de antes.</summary>
    [Fact]
    public async Task La_cantidad_con_asterisco_es_para_lo_ultimo_que_leyo_el_lector_por_puerto()
    {
        var paginaLibre = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // caja.js con el cursor en el cuadro de captura: escaneoExterno → enterDeLaCaptura → Escaneado.
        var (v, lector, puertos, entrega) = await VentaConLectorAsync((venta, codigo) => venta.Venta.Escaneado(codigo), "captura", paginaLibre.Task);
        await using (v)
        await using (lector)
        await using (entrega)
        {
            var (a, b) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);

            puertos.Abierto!.Mandar(b.Codigo + "\r");
            await VentaConCopia.EsperarAsync(() => lector.Codigos == 1, "que el puerto lea el código de B");
            // «5*» y Enter, tecleados después de escanear B.
            var cuando = HoraDeLaPagina();
            var cinco = v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escaneado("5*", null, cuando));
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

    /// <summary>
    /// Escanear y poner el ticket en espera (F6) enseguida: lo que leyó el puerto se va con el ticket en espera. También con el botón
    /// de la barra de teclas, sin hora.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task F6_pone_en_espera_el_ticket_con_lo_que_el_puerto_ya_leyo(bool conHora)
    {
        var paginaLibre = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (v, lector, puertos, entrega) = await VentaConLectorAsync((venta, codigo) => venta.Venta.Escaneado(codigo), "captura", paginaLibre.Task);
        await using (v)
        await using (lector)
        await using (entrega)
        {
            var (a, b) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);

            puertos.Abierto!.Mandar(b.Codigo + "\r");
            await VentaConCopia.EsperarAsync(() => lector.Codigos == 1, "que el puerto lea el código de B");
            double? cuando = conHora ? HoraDeLaPagina() : null;
            var enEspera = v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F6", null, cuando));
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

    // ── Lo que el puerto leyó DESPUÉS de la tecla no se espera (segunda revisión del PR #142) ──
    //
    // La tecla se oprime con la pantalla ocupada (espera su turno en el hilo de la pantalla) y DESPUÉS se escanea B: el puerto lo lee
    // mientras la pantalla sigue ocupada. Con el lector de teclado, la tecla va antes que el escaneo. En 8af93f0 la tecla esperaba «lo que
    // el puerto ya leyó» (B, escaneado después) y lo ponía antes de ella.

    /// <summary>La tecla, con su hora, con la pantalla ocupada; un rato después (el cajero toma el siguiente producto), B por el puerto.</summary>
    private async Task<VentaConCopia> TeclaYDespuesEscanearAsync(Func<VentaConCopia, double, Task> tecla, Func<VentaConCopia, string, Task> comoCajaJs,
        string resultado, Func<VentaConCopia, Task>? antes = null)
    {
        var (v, lector, puertos, entrega) = await VentaConLectorAsync(comoCajaJs, resultado);
        try
        {
            var (a, b) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);
            if (antes is not null)
                await antes(v);

            var (ocupada, soltar) = await PantallaOcupadaAsync(v);
            var oprimida = tecla(v, HoraDeLaPagina());
            await Task.Delay(LectorPorPuerto.Holgura * 4);
            puertos.Abierto!.Mandar(b.Codigo + "\r");
            await VentaConCopia.EsperarAsync(() => lector.Codigos == 1, "que el puerto lea B");
            soltar.Set();
            await ocupada;
            await oprimida.WaitAsync(TimeSpan.FromSeconds(20));
            await VentaConCopia.EsperarAsync(() => lector.Ultimas.All(l => l.Entrega is not null), "la entrega de B");
            await VentaConCopia.EsperarAsync(() => !lector.HayEnCamino, "que la venta reciba B");
            await Task.Delay(300);
            await v.Pantalla.Dispatcher.InvokeAsync(() => { });
            return v;
        }
        catch
        {
            await v.DisposeAsync();
            throw;
        }
        finally
        {
            await entrega.DisposeAsync();
            await lector.DisposeAsync();
        }
    }

    /// <summary>F6, y enseguida el primer producto del siguiente cliente: B queda en el ticket nuevo, no en el de la espera.</summary>
    [Fact]
    public async Task F6_oprimido_antes_de_escanear_no_se_lleva_lo_que_se_escaneo_despues()
    {
        await using var v = await TeclaYDespuesEscanearAsync((v, cuando) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F6", null, cuando)),
            (venta, codigo) => venta.Venta.Escaneado(codigo), "captura");
        var (a, b) = v.DosDeLaCopia();
        var nuevo = await VentaConCopia.RenglonesAsync(v.Pantalla);
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F5", null));
        var retomado = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.True(nuevo.Count == 1 && nuevo[0].StartsWith($"1 {b.Descripcion}", StringComparison.Ordinal),
            $"B se escaneó después de F6 y debía quedar en el ticket nuevo. Ticket nuevo: [{string.Join(" | ", nuevo)}]; "
            + $"el que se puso en espera (F5): [{string.Join(" | ", retomado)}]");
        Assert.Contains(retomado, r => r.StartsWith($"1 {a.Descripcion}", StringComparison.Ordinal));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Cobrar exacto (Enter en «Pagó con») y enseguida el primer producto del siguiente cliente: se cobra A y B queda para el siguiente.
    /// En 8af93f0, B entraba al ticket que se cobraba, no se cobraba y la caja decía «Llegó lo último que escaneaste: el total ahora
    /// es…»; si la cajera le creía y volvía a cobrar, este cliente pagaba lo del siguiente.
    /// </summary>
    [Fact]
    public async Task Cobrar_antes_de_escanear_al_siguiente_cliente_cobra_sin_lo_del_siguiente()
    {
        PapeleriaPos.Nucleo.Contratos.TicketDto? antes = null;
        // Con el cursor en el cobro, caja.js lleva el código a EscaneoEnPago.
        await using var v = await TeclaYDespuesEscanearAsync((v, cuando) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("Enter-cobro", null, cuando)),
            (venta, codigo) => venta.Venta.EscaneoEnPago(codigo), "cobro",
            antes: async v => antes = await UltimoAsync(v));
        var (a, b) = v.DosDeLaCopia();
        var cobrado = await UltimoAsync(v);
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        var aviso = (await v.Pantalla.TextoAsync()).Split('\n').FirstOrDefault(l => l.Contains("Llegó lo último", StringComparison.Ordinal))?.Trim();
        Assert.True(cobrado?.Folio != antes?.Folio && cobrado!.Lineas.Count == 1,
            $"Se debía cobrar A (el cobro se pidió antes de escanear B). Cobrado: {(cobrado?.Folio == antes?.Folio ? "nada" : cobrado?.Folio)}; "
            + $"en pantalla: [{string.Join(" | ", renglones)}]; aviso: «{aviso}»");
        Assert.Contains(cobrado!.Lineas, l => l.Descripcion.Contains(a.Descripcion, StringComparison.Ordinal));
        // B, en el ticket del siguiente cliente.
        Assert.True(renglones.Count == 1 && renglones[0].StartsWith($"1 {b.Descripcion}", StringComparison.Ordinal), string.Join(" | ", renglones));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>«5*» y Enter (cinco del último, A), y enseguida se escanea B: son cinco de A y uno de B.</summary>
    [Fact]
    public async Task La_cantidad_con_asterisco_tecleada_antes_de_escanear_es_para_lo_de_antes()
    {
        await using var v = await TeclaYDespuesEscanearAsync((v, cuando) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escaneado("5*", null, cuando)),
            (venta, codigo) => venta.Venta.Escaneado(codigo), "captura");
        var (a, b) = v.DosDeLaCopia();
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.True(renglones.Count == 2 && renglones[0].StartsWith($"5 {a.Descripcion}", StringComparison.Ordinal)
                    && renglones[1].StartsWith($"1 {b.Descripcion}", StringComparison.Ordinal),
            $"«5*» se tecleó antes de escanear B: eran cinco de A. Quedó: [{string.Join(" | ", renglones)}]");
        Assert.Empty(v.Pantalla.Errores);
    }

    // ── Las flechas, el acuse y el plazo (segunda revisión del PR #142) ──

    /// <summary>
    /// Escanear B, ↑ (al renglón de arriba, A) y + (una más de A). Con el teclado: B entra y queda elegido, ↑ elige A y + suma a A. Por
    /// el puerto con la entrega lenta, ↑ no esperaba: movía la elección antes de que llegara B; B llegaba y quedaba elegido, y + (que sí
    /// espera) sumaba a B.
    /// </summary>
    [Fact]
    public async Task Las_flechas_esperan_lo_que_el_puerto_ya_leyo()
    {
        var paginaLibre = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (v, lector, puertos, entrega) = await VentaConLectorAsync((venta, codigo) => venta.Venta.Escaneado(codigo), "captura", paginaLibre.Task);
        await using (v)
        await using (lector)
        await using (entrega)
        {
            var lista = v.DeLaCopia();
            var (x, a, b) = (lista[0], lista[1], lista[2]);
            await v.EscanearAsync(x.Codigo!);
            await v.EscanearAsync(a.Codigo!);

            puertos.Abierto!.Mandar(b.Codigo + "\r");
            await VentaConCopia.EsperarAsync(() => lector.Codigos == 1, "que el puerto lea B");
            var arriba = v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("ArrowUp", "", HoraDeLaPagina()));
            await Task.Delay(150);
            Assert.False(arriba.IsCompleted, "↑ no esperó lo que el puerto ya leyó.");
            paginaLibre.SetResult();
            await arriba.WaitAsync(TimeSpan.FromSeconds(20));
            await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("+", "", HoraDeLaPagina())).WaitAsync(TimeSpan.FromSeconds(20));
            await VentaConCopia.EsperarAsync(() => !lector.HayEnCamino, "que la venta reciba B");
            await v.Pantalla.Dispatcher.InvokeAsync(() => { });

            var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
            Assert.True(renglones.Count == 3 && renglones[1].StartsWith($"2 {a.Descripcion}", StringComparison.Ordinal)
                        && renglones[2].StartsWith($"1 {b.Descripcion}", StringComparison.Ordinal),
                $"Escanear B, ↑ y +: la de más era para A. Quedó: [{string.Join(" | ", renglones)}]");
            Assert.Empty(v.Pantalla.Errores);
        }
    }

    /// <summary>
    /// Con el mostrador abierto (o el abono, con el cursor en su monto; o con caja.js creyendo que «Varios» sigue abierto), caja.js da el
    /// código a la venta y enseguida dice que lo recibió. La venta quitaba el panel (llamadas a la página, que en la ventana de Windows van
    /// y vienen) ANTES de formar el código en la cola: el acuse ya soltaba a quien esperaba y el código todavía no estaba ahí. F6 en ese
    /// momento ponía en espera el ticket sin B, y B caía en el ticket nuevo. Ahora el código toma su lugar en la cola antes, como en
    /// «Varios» (revisión del PR #133). Aquí la página tarda 100 ms por llamada para hacerlo visible (en la caja son unos milisegundos).
    /// </summary>
    [Theory]
    [InlineData("mostrador")]
    [InlineData("abono")]
    [InlineData("varios")]
    public async Task El_acuse_no_suelta_a_F6_antes_de_que_el_codigo_este_en_la_cola(string donde)
    {
        var pagina = new PaginaQueContesta();
        Func<VentaConCopia, string, Task> comoCajaJs = donde switch
        {
            "mostrador" => (venta, codigo) => venta.Venta.EnMostrador("Enter", codigo, null, rafaga: true),
            "abono" => (venta, codigo) => venta.Venta.EscaneoEnPago(codigo),
            _ => (venta, codigo) => venta.Venta.EnVarios(codigo, null, rafaga: true),
        };
        var (v, lector, puertos, entrega) = await VentaConLectorAsync(comoCajaJs, donde == "abono" ? "cobro" : donde, pagina: pagina);
        await using (v)
        await using (lector)
        await using (entrega)
        {
            var (a, b) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);
            if (donde == "mostrador")
            {
                await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("Ctrl+M", ""));
                Assert.Contains("Toca lo que el cliente señala", await v.Pantalla.TextoAsync());
            }
            else if (donde == "abono")
            {
                await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("Ctrl+B", ""));
                Assert.Contains("Abono a cuenta", await v.Pantalla.TextoAsync());
            }

            pagina.Demora = TimeSpan.FromMilliseconds(100);
            puertos.Abierto!.Mandar(b.Codigo + "\r");
            // caja.js ya dijo que lo recibió.
            await VentaConCopia.EsperarAsync(() => lector.Codigos == 1 && !lector.HayEnCamino, "el acuse de B");
            var enCola = (await VentaConCopia.RenglonesAsync(v.Pantalla)).Count;
            await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F6", null, HoraDeLaPagina())).WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(600);
            pagina.Demora = TimeSpan.Zero;
            await v.Pantalla.Dispatcher.InvokeAsync(() => { });

            var nuevo = await VentaConCopia.RenglonesAsync(v.Pantalla);
            Assert.True(nuevo.Count == 0,
                $"Con el acuse de B ya dado (renglones en ese momento: {enCola}), F6 puso en espera el ticket sin B y B cayó en el ticket nuevo: "
                + $"[{string.Join(" | ", nuevo)}]");
            await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F5", null));
            var retomado = await VentaConCopia.RenglonesAsync(v.Pantalla);
            Assert.Contains(retomado, r => r.StartsWith($"1 {b.Descripcion}", StringComparison.Ordinal));
            Assert.Empty(v.Pantalla.Errores);
        }
    }

    /// <summary>
    /// Si lo que leyó el puerto no llega a la venta en su plazo (la página se volvió a cargar a media entrega), la tecla sigue sin ello,
    /// se anota, y arriba se dice un momento «El lector tardó: revisa que esté todo en el ticket», sin preguntar nada (F12 ya confirma;
    /// segunda revisión del PR #142).
    /// </summary>
    [Fact]
    public async Task Si_lo_que_leyo_el_puerto_no_llega_a_tiempo_la_tecla_sigue_y_arriba_dice_que_el_lector_tardo()
    {
        var pagina = new PaginaQueContesta();
        var nunca = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (v, lector, puertos, entrega) = await VentaConLectorAsync((venta, codigo) => venta.Venta.Escaneado(codigo), "captura", nunca.Task, pagina);
        await using (v)
        await using (lector)
        await using (entrega)
        {
            var (a, b) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);
            puertos.Abierto!.Mandar(b.Codigo + "\r");
            await VentaConCopia.EsperarAsync(() => lector.Codigos == 1, "que el puerto lea B");

            var reloj = Stopwatch.StartNew();
            await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("F6", null, HoraDeLaPagina())).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(reloj.Elapsed >= PantallaVenta.EsperaDelLector - TimeSpan.FromMilliseconds(100), $"F6 no esperó su plazo: {reloj.Elapsed}");
            Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
            Assert.Contains(pagina.Llamadas, l => l.Funcion == "avisarDelLector" && Equals(l.Argumentos?[0], PantallaVenta.ElLectorTardo));
            Assert.Equal("El lector tardó: revisa que esté todo en el ticket.", PantallaVenta.ElLectorTardo);
            nunca.SetResult();
            Assert.Empty(v.Pantalla.Errores);
        }
    }
}
