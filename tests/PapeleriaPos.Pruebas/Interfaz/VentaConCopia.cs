using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La red entre la caja y el servicio de prueba, como se le pida: lenta (cada petición, o las de una ruta, tarda), caída (no
/// se puede conectar), con pérdidas, con la búsqueda de un producto por código detenida hasta soltarla, con la respuesta del
/// cobro retenida (el servicio ya cobró, pero la respuesta no llega) o perdida en las rutas que se digan (el servicio ya lo
/// guardó). Anota cada petición al salir y cada respuesta al llegar.
/// </summary>
internal sealed class RedDePrueba(HttpMessageHandler interno) : DelegatingHandler(interno)
{
    private readonly ConcurrentQueue<string> _pedidas = new();
    private readonly ConcurrentQueue<string> _respondidas = new();
    private readonly ConcurrentQueue<(string Ruta, bool ConPlazos)> _plazos = new();

    public TimeSpan Retraso { get; set; }

    /// <summary>Lo que tardan las de cada ruta (además de <see cref="Retraso"/>): la ruta con su consulta, como sale.</summary>
    public Func<string, TimeSpan>? RetrasoDe { get; set; }

    /// <summary>La caja principal está apagada: no se puede conectar (nada llega a ella).</summary>
    public bool Caida { get; set; }

    /// <summary>Las de estas rutas llegan y el servicio las guarda, pero la respuesta se pierde (se corta antes de llegar).</summary>
    public Func<string, bool>? PierdeRespuesta { get; set; }

    /// <summary>De cada cuántas peticiones se pierde una (0 a 1), con su propia semilla: así la prueba no cambia de una corrida a otra.</summary>
    public double Perdidas { get; set; }

    private readonly Random _azar = new(2026);

    /// <summary>Mientras está puesta, la búsqueda de un producto por código espera a que se suelte (o a que la caja se canse).</summary>
    public TaskCompletionSource? Detenida { get; set; }

    /// <summary>Mientras está puesta, todo espera a que se suelte: la red tan lenta que nada contesta.</summary>
    public TaskCompletionSource? TodoDetenido { get; set; }

    /// <summary>
    /// Mientras está puesta, el cobro (POST api/ventas) llega al servicio y se guarda, pero su respuesta espera a que se
    /// suelte (o a que la caja deje de esperarla): como el Wi-Fi que se cae justo después de cobrar.
    /// </summary>
    public TaskCompletionSource? CobroRetenido { get; set; }

    public int Cuantas(string ruta) => _pedidas.Count(p => p.StartsWith(ruta, StringComparison.Ordinal));

    /// <summary>Todas las que han salido, en orden.</summary>
    public int Todas => _pedidas.Count;

    public IReadOnlyList<string> Pedidas => [.. _pedidas];

    public bool Respondio(string contiene) => _respondidas.Any(r => r.Contains(contiene, StringComparison.Ordinal));

    /// <summary>Si las que salieron a esa ruta llevaban los plazos de la venta (<see cref="Transporte.PlazosDeLaVenta"/>), en orden.</summary>
    public List<bool> ConPlazos(string contiene) => _plazos.Where(p => p.Ruta.Contains(contiene, StringComparison.Ordinal)).Select(p => p.ConPlazos).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var ruta = request.RequestUri!.PathAndQuery;
        _pedidas.Enqueue(ruta);
        _plazos.Enqueue((ruta, Transporte.PlazosDeLaVenta is not null));
        if (Caida)
            throw new HttpRequestException(HttpRequestError.ConnectionError, "La caja principal no contesta.");
        bool perdida;
        lock (_azar)
            perdida = Perdidas > 0 && _azar.NextDouble() < Perdidas;
        if (perdida)
            throw new HttpRequestException("Se perdió en el Wi-Fi.");
        if (Retraso > TimeSpan.Zero)
            await Task.Delay(Retraso, cancellationToken);
        if (RetrasoDe?.Invoke(ruta) is { } suyo && suyo > TimeSpan.Zero)
            await Task.Delay(suyo, cancellationToken);
        if (Detenida is { } detenida && ruta.StartsWith("/api/productos/codigo/", StringComparison.Ordinal))
            await detenida.Task.WaitAsync(cancellationToken);
        if (TodoDetenido is { } todo)
            await todo.Task.WaitAsync(cancellationToken);
        var respuesta = await base.SendAsync(request, cancellationToken);
        if (CobroRetenido is { } retenido && request.Method == HttpMethod.Post && ruta == "/api/ventas")
        {
            try
            {
                await retenido.Task.WaitAsync(cancellationToken);
            }
            catch
            {
                respuesta.Dispose();
                throw;
            }
        }
        if (PierdeRespuesta?.Invoke(ruta) == true)
        {
            respuesta.Dispose();
            throw new HttpRequestException("Se cortó antes de la respuesta.");
        }
        _respondidas.Enqueue(ruta);
        return respuesta;
    }
}

/// <summary>
/// La venta de verdad de una caja con su copia (entrega 5): la pantalla, el cliente y la contingencia con su base local,
/// contra el servicio de prueba, con la red como se le pida (<see cref="RedDePrueba"/>).
/// </summary>
/// <param name="Cajero">Quien entró a la venta: el cajero, salvo que se pida otro.</param>
internal sealed record VentaConCopia(
    PantallaSinNavegador Pantalla, PantallaVenta Venta, ClienteCaja Cliente, Contingencia Contingencia, RedDePrueba Red, CajaDto Caja,
    SesionDto Cajero, ServiceProvider Servicios, RedDePrueba Fondo) : IAsyncDisposable
{
    /// <summary>La caja con su copia recién bajada, la venta abierta por el cajero y todo lo que pide al entrar ya contestado.</summary>
    /// <param name="plazo">Lo que espera a la caja principal por un código que no está en la copia; sin él, un minuto.</param>
    /// <param name="pagina">La página (caja.js); sin ella, una que contesta todo sin hacer nada.</param>
    /// <param name="fondoAparte">
    /// Lo de fondo (el latido, el aviso, la copia, la bandeja) por su propia red (<see cref="Fondo"/>), como la caja de verdad
    /// (entrega 5, parte 2); sin esto, todo por <see cref="Red"/>.
    /// </param>
    /// <param name="quien">Quién entra a la venta, con su PIN; sin él, el cajero. El turno es del cajero.</param>
    /// <param name="impresora">La impresora de tickets; sin ella, ninguna (el ticket se ve en pantalla).</param>
    /// <param name="registro">El registro de la caja (registros\caja-….log); sin él, no se anota nada.</param>
    /// <param name="reloj">El de la caja (con él decide el día de las promociones); sin él, el de la PC.</param>
    /// <param name="lector">El lector por puerto COM de la ventana de Windows (piloto alfa); sin él, ninguno, como en el navegador.</param>
    public static async Task<VentaConCopia> AbrirAsync(ServicioDePrueba servicio, string baseLocal, TimeSpan? plazo = null, IJSRuntime? pagina = null,
        bool fondoAparte = false, (string Usuario, string Pin)? quien = null, IImpresora? impresora = null, RegistroDiario? registro = null,
        TimeProvider? reloj = null, LectorPorPuerto? lector = null)
    {
        var red = new RedDePrueba(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(red);
        var fondo = fondoAparte ? new RedDePrueba(servicio.Fabrica.Server.CreateHandler()) : red;
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(baseLocal), fondoAparte ? new HttpClient(fondo) : http, conexion, reloj);
        var cliente = new ClienteCaja(http, conexion, contingencia) { PlazoDeLaPrincipal = plazo ?? TimeSpan.FromMinutes(1) };
        await contingencia.LatidoAsync();
        Assert.NotNull(contingencia.Foto);
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        var sesion = quien is var (usuario, pin) ? await cliente.EntrarAsync(usuario, pin) : cajero;
        var coleccion = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton(contingencia)
            .AddSingleton(new RendimientoCaja())
            .AddSingleton(impresora ?? new SinImpresora())
            .AddSingleton(pagina ?? new PaginaQueContesta())
            .AddScoped<ModuloCaja>();
        if (registro is not null)
            coleccion.AddSingleton(registro);
        if (lector is not null)
            coleccion.AddSingleton(lector);
        var servicios = coleccion.BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        var venta = await pantalla.AbrirAsync<PantallaVenta>(new()
        {
            [nameof(PantallaVenta.Caja)] = caja,
            [nameof(PantallaVenta.Sesion)] = sesion,
            [nameof(PantallaVenta.Turno)] = turno,
        });
        // Lo último que pide la venta al entrar es el conteo del día.
        await EsperarAsync(() => red.Respondio("/conteo-del-dia"), "lo que pide la venta al entrar");
        await pantalla.Dispatcher.InvokeAsync(() => { });
        return new VentaConCopia(pantalla, venta, cliente, contingencia, red, caja, sesion, servicios, fondo);
    }

    public Task EscanearAsync(string codigo) => Pantalla.Dispatcher.InvokeAsync(() => Venta.Escaneado(codigo));

    public static async Task EsperarAsync(Func<bool> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!condicion())
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No llegó {que}.");
            await Task.Delay(20);
        }
    }

    /// <summary>Productos de la copia que se venden por pieza, con el código de su pieza, en un orden fijo.</summary>
    public List<ProductoVentaDto> DeLaCopia()
    {
        var paquete = Contingencia.Paquete!;
        return paquete.Codigos
            .Select(c => (Codigo: c, Producto: paquete.Productos.FirstOrDefault(p => p.VarianteId == c.VarianteId)))
            .Where(x => x.Producto is { Unidad: UnidadBase.Pieza } p && p.Presentacion.Id == x.Codigo.PresentacionId
                        && p.Precios.Any(precio => precio.PresentacionId is null) && p.Codigo == x.Codigo.Codigo)
            .Select(x => x.Producto!)
            .DistinctBy(p => p.VarianteId)
            .OrderBy(p => p.VarianteId)
            .ToList();
    }

    public (ProductoVentaDto A, ProductoVentaDto B) DosDeLaCopia() => DeLaCopia() is var lista ? (lista[0], lista[1]) : default;

    /// <summary>Un producto dado de alta en otra caja después de que esta bajó su copia.</summary>
    public async Task<ProductoVentaDto> DarDeAltaEnOtraCajaAsync(string codigo, string nombre)
    {
        var departamento = (await Cliente.OpcionesCatalogoAsync()).Departamentos[0].Id;
        var nuevo = await Cliente.AltaRapidaAsync(new SolicitudAltaRapida(Cajero.UsuarioId, Caja.SucursalId, codigo, nombre, 4550, departamento));
        Assert.Null(Contingencia.Foto!.PorCodigo(codigo));
        return nuevo;
    }

    /// <summary>
    /// Los renglones del ticket en orden: las líneas y los «buscando…», cada uno con su texto, que empieza con la cantidad
    /// (sin los botones − y + de la línea).
    /// </summary>
    public static async Task<List<string>> RenglonesAsync(PantallaSinNavegador p) =>
        (await p.LeerAsync()).Elementos
            .Where(e => e.Etiqueta == "tr" && (e.Id?.StartsWith("linea-", StringComparison.Ordinal) == true || e.Clase == "vn-buscando"))
            .Select(e => Regex.Replace(PantallaSinNavegador.Limpio(e.Texto), @"^− (\S+) \+ ", "$1 "))
            .ToList();

    public async ValueTask DisposeAsync()
    {
        Red.Detenida?.TrySetResult();
        Red.CobroRetenido?.TrySetResult();
        Contingencia.Detener();
        await ((IAsyncDisposable)Pantalla).DisposeAsync();
        // Como al cerrar la caja: lo que se escribía en su base local termina y la suelta, antes de borrarla.
        Contingencia.Cerrar(TimeSpan.FromSeconds(20));
        await Servicios.DisposeAsync();
    }
}
