using System.Collections.Concurrent;
using System.Text;
using PapeleriaPos.Impresion;

namespace PapeleriaPos.Pruebas.Impresion;

/// <summary>
/// La impresora de tickets de prueba: anota lo que le llega y falla como se le pida en cada intento, o se detiene hasta
/// soltarla (como la de la tienda mientras imprime lo de la otra caja).
/// </summary>
internal sealed class TicketeraDePrueba : IImpresora
{
    private int _intentos;

    public bool MuestraVistaPrevia => false;

    /// <summary>Lo que salió, en orden (el texto de cada papel).</summary>
    public ConcurrentQueue<string> Salieron { get; } = new();

    /// <summary>Lo que se le mandó en cada intento, haya salido o no.</summary>
    public ConcurrentQueue<byte[]> Mandados { get; } = new();

    /// <summary>Con qué falla cada intento (el primero es el 1); null: sale.</summary>
    public Func<int, Exception?>? Falla { get; set; }

    /// <summary>Mientras está puesta, lo que llega espera a que se suelte.</summary>
    public TaskCompletionSource? Detenida { get; set; }

    /// <summary>Lo que espera cada intento antes de salir o fallar (el primero es el 1).</summary>
    public Func<int, Task>? AntesDeCadaIntento { get; set; }

    public int Intentos => Volatile.Read(ref _intentos);

    public async Task EnviarAsync(byte[] escPos, CancellationToken ct = default)
    {
        var intento = Interlocked.Increment(ref _intentos);
        Mandados.Enqueue(escPos);
        if (Detenida is { } detenida)
            await detenida.Task.WaitAsync(ct);
        if (AntesDeCadaIntento?.Invoke(intento) is { } antes)
            await antes.WaitAsync(ct);
        if (Falla?.Invoke(intento) is { } error)
            throw error;
        Salieron.Enqueue(Encoding.UTF8.GetString(escPos));
    }

    public static async Task EsperarAsync(Func<bool> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!condicion())
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No llegó {que}.");
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// La cola de la impresora de tickets (entrega 5, parte 3, «Imprimir aparte»): lo que se imprime entra y la pantalla sigue;
/// sale en orden; si la impresora no contesta al conectarse se vuelve a intentar sola; si no sale, la cola se detiene hasta
/// «Reintentar» y nada se pierde. Las esperas se ponen cortas y se espera a cada cosa con un límite: no depende de la PC.
/// </summary>
public sealed class ColaDeImpresionTests
{
    private static readonly TimeSpan[] Cortas = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)];

    private static byte[] Papel(string texto) => Encoding.UTF8.GetBytes(texto);

    [Fact]
    public async Task Lo_que_entra_no_espera_a_la_impresora_y_sale_en_orden()
    {
        var impresora = new TicketeraDePrueba { Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cola = new ColaDeImpresion(impresora, Cortas);

        // La impresora está ocupada con lo primero: lo demás entra igual, al momento.
        var a = cola.Encolar("Ticket A-1", Papel("A"));
        var b = cola.Encolar("Ticket A-2", Papel("B"));
        var c = cola.Encolar("Devolución D-1", Papel("C"));
        await TicketeraDePrueba.EsperarAsync(() => a.Estado == EstadoImpresion.Imprimiendo, "lo primero a la impresora");

        Assert.Equal([EstadoImpresion.Imprimiendo, EstadoImpresion.EnCola, EstadoImpresion.EnCola], new[] { a, b, c }.Select(t => t.Estado));
        Assert.Equal(3, cola.Pendientes.Count);
        Assert.Null(cola.Problema);

        impresora.Detenida.SetResult();
        Assert.True(await c.Resultado.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(["A", "B", "C"], impresora.Salieron);
        Assert.All(new[] { a, b, c }, t => Assert.Equal(EstadoImpresion.Impreso, t.Estado));
        Assert.Empty(cola.Pendientes);
    }

    /// <summary>
    /// La impresora de tickets la comparten las dos cajas: si no contesta al conectarse (imprime lo de la otra), no se le mandó
    /// nada y se vuelve a intentar sola, sin que salga dos veces.
    /// </summary>
    [Fact]
    public async Task Si_no_contesta_al_conectarse_se_vuelve_a_intentar_sola()
    {
        var impresora = new TicketeraDePrueba { Falla = n => n <= 2 ? new ErrorAlConectar("No responde la impresora de tickets en 10.0.0.9:9100.") : null };
        var cola = new ColaDeImpresion(impresora, Cortas);
        // Lo que dice la cola cada vez que avisa (avisa en su hilo, justo después de cambiar).
        var vistos = new ConcurrentQueue<EstadoImpresion>();
        cola.Cambio += () =>
        {
            foreach (var t in cola.Pendientes)
                vistos.Enqueue(t.Estado);
        };
        var ticket = cola.Encolar("Ticket A-1", Papel("A"));

        Assert.True(await ticket.Resultado.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(3, impresora.Intentos);
        Assert.Equal(["A"], impresora.Salieron);
        Assert.Contains(EstadoImpresion.Ocupada, vistos);
        Assert.Null(cola.Problema);
    }

    /// <summary>
    /// Si no sale, la cola se detiene: lo que sigue espera en su orden (no se salta) y nada se pierde. «Reintentar» lo manda
    /// otra vez, y después lo demás.
    /// </summary>
    [Fact]
    public async Task Si_no_sale_la_cola_espera_a_reintentar_y_sigue_en_orden()
    {
        var apagada = true;
        var impresora = new TicketeraDePrueba { Falla = _ => apagada ? new ErrorAlConectar("No responde la impresora de tickets en 10.0.0.9:9100.") : null };
        var cola = new ColaDeImpresion(impresora, Cortas);

        var a = cola.Encolar("Ticket A-1", Papel("A"));
        Assert.False(await a.Resultado.WaitAsync(TimeSpan.FromSeconds(20)));
        var b = cola.Encolar("Ticket A-2", Papel("B"));

        // Se intentó una vez y otra con cada espera; después, no salió, y lo de atrás no se intentó.
        Assert.Equal(Cortas.Length + 1, impresora.Intentos);
        Assert.Equal(EstadoImpresion.NoSalio, a.Estado);
        Assert.Contains("Se intentó 4 veces", a.Motivo);
        Assert.Equal(EstadoImpresion.EnCola, b.Estado);
        Assert.Same(a, cola.Problema);
        Assert.True(cola.Detenida);
        Assert.Equal([a, b], cola.Pendientes);
        Assert.Empty(impresora.Salieron);

        apagada = false;
        cola.Reintentar();
        Assert.True(await b.Resultado.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(await a.Resultado.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(["A", "B"], impresora.Salieron);
        Assert.False(cola.Detenida);
        Assert.Empty(cola.Pendientes);
    }

    /// <summary>
    /// Lo que falla después de mandarle algo (sin papel, no confirmó) pudo salir a medias: no se vuelve a mandar solo, para que
    /// no salga dos veces. Espera a «Reintentar».
    /// </summary>
    [Fact]
    public async Task Lo_que_falla_despues_de_mandarlo_no_se_manda_otra_vez_solo()
    {
        var sinPapel = true;
        var impresora = new TicketeraDePrueba { Falla = _ => sinPapel ? new IOException("Se acabó el papel de la impresora de tickets.") : null };
        var cola = new ColaDeImpresion(impresora, Cortas);

        var corte = cola.Encolar("Corte del turno de Ana", Papel("Z"));

        Assert.False(await corte.Resultado.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, impresora.Intentos);
        Assert.Equal((EstadoImpresion.NoSalio, "Se acabó el papel de la impresora de tickets."), (corte.Estado, corte.Motivo));

        sinPapel = false;
        cola.Reintentar();
        Assert.True(await corte.Resultado.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(["Z"], impresora.Salieron);
    }

    /// <summary>
    /// El cajón es para dar el cambio en ese momento: se abre con su ticket si sale a tiempo (también si antes la impresora no
    /// contestó al conectarse: no se le había mandado nada), pero no otra vez con «Reintentar» después de mandárselo.
    /// </summary>
    [Fact]
    public async Task El_cajon_se_abre_con_su_ticket_al_momento_y_no_otra_vez_al_reintentar()
    {
        IReadOnlyList<Elemento> ticket = [new Renglon("Venta A-1")];
        var conCajon = TicketEscPos.Generar(ticket, abrirCajon: true);
        var sinCajon = TicketEscPos.Generar(ticket, abrirCajon: false);
        var impresora = new TicketeraDePrueba
        {
            Falla = n => n switch
            {
                1 => new ErrorAlConectar("No responde la impresora de tickets en 10.0.0.9:9100."),
                2 => new IOException("Se acabó el papel de la impresora de tickets."),
                _ => null,
            },
        };
        var cola = new ColaDeImpresion(impresora, Cortas, cajonAlMomento: TimeSpan.FromHours(1));

        var venta = cola.Encolar("Ticket A-1", ticket, abrirCajon: true);
        Assert.False(await venta.Resultado.WaitAsync(TimeSpan.FromSeconds(20)));
        cola.Reintentar();
        Assert.True(await venta.Resultado.WaitAsync(TimeSpan.FromSeconds(20)));

        // Los dos primeros intentos, con el cajón (el primero no llegó; el segundo sí, y falló después); el de «Reintentar», sin.
        Assert.Equal([conCajon, conCajon, sinCajon], impresora.Mandados);
    }

    /// <summary>
    /// Lo que esperó más de la cuenta (detrás de otro, o a la impresora) sale sin abrir el cajón; y si solo era abrirlo (una
    /// entrada de efectivo), ya no se manda nada.
    /// </summary>
    [Fact]
    public async Task Lo_que_espero_de_mas_sale_sin_abrir_el_cajon()
    {
        IReadOnlyList<Elemento> ticket = [new Renglon("Venta A-2")];
        var impresora = new TicketeraDePrueba { Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cola = new ColaDeImpresion(impresora, Cortas, cajonAlMomento: TimeSpan.Zero);

        var antes = cola.Encolar("Corte del turno de Ana", Papel("Z"));
        var venta = cola.Encolar("Ticket A-2", ticket, abrirCajon: true);
        var cajon = cola.AbrirCajon("El cajón (entrada E-1)");
        await TicketeraDePrueba.EsperarAsync(() => antes.Estado == EstadoImpresion.Imprimiendo, "el corte a la impresora");
        impresora.Detenida.SetResult();

        Assert.True(await cajon.Resultado.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(await venta.Resultado.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal([Papel("Z"), TicketEscPos.Generar(ticket, abrirCajon: false)], impresora.Mandados);
        Assert.Equal(EstadoImpresion.Impreso, cajon.Estado);
        Assert.Empty(cola.Pendientes);
    }

    [Fact]
    public void Hay_una_sola_cola_por_impresora()
    {
        var una = new TicketeraDePrueba();
        var otra = new TicketeraDePrueba();

        Assert.Same(ColaDeImpresion.De(una), ColaDeImpresion.De(una));
        Assert.NotSame(ColaDeImpresion.De(una), ColaDeImpresion.De(otra));
        Assert.True(ColaDeImpresion.De(new SinImpresora()).MuestraVistaPrevia);
    }
}
