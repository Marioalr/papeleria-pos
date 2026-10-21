using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Herramientas.Simulador;

/// <summary>Lo que se midió y se revisó en el día simulado: de aquí sale el informe.</summary>
public sealed class ResultadoSimulacion
{
    public required OpcionesSimulador Opciones { get; init; }
    public required Uri Servicio { get; init; }
    public required Mediciones Mediciones { get; init; }
    public bool ServicioPropio { get; init; }

    /// <summary>De dónde salió la base del servicio, para el informe.</summary>
    public string? Base { get; init; }

    public DateTime Inicio { get; } = DateTime.Now;
    public string? VersionDelServicio { get; set; }
    public TimeSpan? DuracionDelDia { get; set; }
    public TimeSpan Total { get; set; }
    public List<CajaSimulada> Cajas { get; } = [];
    public GuionDelDia? Guion { get; set; }
    public IReadOnlyList<(TimeSpan Cuando, string Que)> Sucesos { get; set; } = [];

    /// <summary>La memoria del servicio (lo que ocupa en la RAM), en bytes: antes del día, la más alta durante él y al terminar.</summary>
    public long? MemoriaAntes { get; set; }

    public long? MemoriaPico { get; set; }
    public long? MemoriaDespues { get; set; }

    /// <summary>El procesador que usó el servicio durante el día (de todos sus hilos).</summary>
    public TimeSpan? ProcesadorDelServicio { get; set; }

    /// <summary>El que usó el simulador (en la misma PC, se lo quita al servicio).</summary>
    public TimeSpan? ProcesadorDelSimulador { get; set; }

    public int? ProductosEnLaCopia { get; set; }
    public int? CodigosEnLaCopia { get; set; }
    public Dictionary<int, long> BaseLocal { get; } = [];
    public List<Revision> Cuadre { get; set; } = [];

    /// <summary>Lo que no dejó terminar la simulación o no salió como debía (además de lo de cada caja).</summary>
    public List<string> Errores { get; } = [];

    public List<string> Notas { get; } = [];

    public IEnumerable<string> TodosLosErrores => Errores.Concat(Cajas.SelectMany(c => c.Errores));

    public bool Cuadra => Cuadre.Count > 0 && Cuadre.All(r => r.Bien);

    /// <summary>Cuadra y no hubo errores: la salida del comando es 0.</summary>
    public bool Paso => Cuadra && !TodosLosErrores.Any();
}

/// <summary>
/// El simulador de cajas (docs/prueba-sucursales-linux.md, «Simulador de cajas»): da de alta N cajas con el PIN del dueño (o
/// usa las que ya había), abre su turno y vende un día pico comprimido como la caja de verdad, con su contingencia; a media
/// jornada el dueño cambia un precio, la caja 1 da de alta un producto y, si se pide, la red de una caja se cae. Mide cuánto
/// tarda cada cosa, hace el corte de cada turno y al final cuadra todo contra la caja principal.
/// </summary>
/// <param name="red">
/// La red hasta el servicio: con <c>true</c>, la de lo de fondo. En la tienda, las de la caja (<see cref="RedCaja.Manejador"/>);
/// en la prueba automática, la del servicio en el mismo proceso.
/// </param>
public sealed class SimuladorCajas(OpcionesSimulador opciones, Uri servicio, Func<bool, HttpMessageHandler> red)
{
    /// <summary>A dónde se dice cómo va.</summary>
    public TextWriter Salida { get; init; } = TextWriter.Null;

    /// <summary>
    /// La base del servicio, para revisarla completa al final (cada existencia contra su kardex, folios repetidos); vacía si el
    /// servicio no es de esta PC: entonces se revisa solo por la API.
    /// </summary>
    public Func<PosDbContext>? Base { get; init; }

    /// <summary>El proceso del servicio, para su memoria y su procesador; vacío si no se sabe.</summary>
    public Process? Proceso { get; init; }

    /// <summary>El servicio lo arrancó el simulador con una copia de la base de prueba: no hace falta que diga que es de prueba.</summary>
    public bool ServicioPropio { get; init; }

    public string? DescripcionDeLaBase { get; init; }

    /// <summary>Dónde van las bases locales de las cajas; vacío: una carpeta temporal que se borra al terminar.</summary>
    public string? Carpeta { get; init; }

    public async Task<ResultadoSimulacion> CorrerAsync(CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var mediciones = new Mediciones();
        var resultado = new ResultadoSimulacion
        {
            Opciones = opciones, Servicio = servicio, Mediciones = mediciones, ServicioPropio = ServicioPropio, Base = DescripcionDeLaBase,
        };
        var dia = new DiaSimulado(opciones) { Salida = Salida };
        var carpeta = Carpeta ?? Path.Combine(Path.GetTempPath(), $"simulador-cajas-{Guid.NewGuid():N}");
        Directory.CreateDirectory(carpeta);
        var cerradas = new HashSet<CajaSimulada>();
        using var plano = new HttpClient(red(false)) { BaseAddress = servicio, Timeout = TimeSpan.FromMinutes(2) };
        await using var memoria = Proceso is null ? null : new MuestreoDeMemoria(Proceso);
        try
        {
            await SimularAsync(resultado, dia, carpeta, plano, memoria, cerradas, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ErrorCaja or HttpRequestException or IOException or TimeoutException
                                       or TaskCanceledException or Microsoft.Data.Sqlite.SqliteException)
        {
            resultado.Errores.Add($"La simulación no terminó: {ex.Message}");
        }
        finally
        {
            foreach (var caja in resultado.Cajas.Where(c => !cerradas.Contains(c)))
                caja.Cerrar();
            if (Carpeta is null)
                Borrar(carpeta);
            resultado.Sucesos = dia.Sucesos;
            resultado.Total = total.Elapsed;
        }
        return resultado;
    }

    private async Task SimularAsync(ResultadoSimulacion resultado, DiaSimulado dia, string carpeta, HttpClient plano, MuestreoDeMemoria? memoria,
        HashSet<CajaSimulada> cerradas, CancellationToken ct)
    {
        var mediciones = resultado.Mediciones;
        var estado = await plano.GetFromJsonAsync<EstadoServicioDto>("api/estado", ct)
                     ?? throw new InvalidOperationException($"{servicio} no contestó como el servicio del POS.");
        // Nunca contra eleventa ni contra los datos de la tienda.
        if (estado.PilotoEleventa)
            throw new InvalidOperationException("Ese servicio está en el piloto con eleventa (manda sus ventas a eleventa): el simulador no corre ahí.");
        if (!ServicioPropio && !estado.Prueba)
            throw new InvalidOperationException("Ese servicio no es el de la prueba (su api/estado no dice que es de prueba) y el simulador vende de verdad "
                + "en su base. Usa --iniciar-servicio, que arranca uno propio con una copia de la base de prueba, o apunta a un servicio de prueba.");
        resultado.VersionDelServicio = estado.Version;

        // ── Dar de alta las cajas, con el PIN del dueño (o usar las que ya había) ──
        dia.Avisar($"Dando de alta {opciones.Cajas} {(opciones.Cajas == 1 ? "caja" : "cajas")} en {servicio}…");
        var alta = new ClienteCaja(plano, new ConexionCaja { Servicio = servicio });
        var sucursal = (await alta.OpcionesAltaAsync(servicio, new SolicitudCredenciales(opciones.Dueno.Usuario, opciones.Dueno.Pin)))[0];
        for (var n = 1; n <= opciones.Cajas; n++)
        {
            var nombre = $"Caja simulada {n}";
            var serie = $"S{(char)('A' + n - 1)}";
            var existente = sucursal.Cajas.FirstOrDefault(c => c.Nombre == nombre);
            if (existente is null && sucursal.Cajas.FirstOrDefault(c => c.Serie == serie) is { } otra)
                throw new InvalidOperationException($"La serie {serie} ya es de «{otra.Nombre}»: el simulador no la toma.");
            var inicio = Stopwatch.GetTimestamp();
            var dada = await alta.AltaAsync(servicio, new SolicitudAltaCaja(opciones.Dueno.Usuario, opciones.Dueno.Pin, sucursal.Id, existente?.Id,
                existente is null ? nombre : null, existente is null ? serie : null, $"{Environment.MachineName} (simulador, caja {n})"));
            mediciones.Anotar(Operacion.AltaDeLaCaja, inicio);
            resultado.Cajas.Add(new CajaSimulada(n, dada, servicio, red, carpeta, mediciones, dia));
        }
        var cajas = resultado.Cajas;

        // ── Abrir las cajas a la vez, como al abrir la tienda ──
        dia.Avisar("Abriendo las cajas: cada una baja su copia completa, entra quien atiende y abre su turno…");
        await Task.WhenAll(cajas.Select(c => c.AbrirAsync(ct)));
        resultado.Notas.AddRange(cajas.Select(c => c.TurnoDeOtraCorrida).OfType<string>());

        // El dueño, en la caja principal: cambia el precio a media jornada y al final ve el corte del día.
        using var deLaOficina = new HttpClient(red(false)) { Timeout = TimeSpan.FromMinutes(2) };
        var oficina = new ClienteCaja(deLaOficina, new ConexionCaja { Servicio = servicio, CajaId = cajas[0].Caja.Id, Llave = cajas[0].Conexion.Llave });
        var dueno = await oficina.EntrarAsync(opciones.Dueno.Usuario, opciones.Dueno.Pin);

        // ── El guion del día, con la copia recién bajada ──
        var copia = cajas[0].Contingencia.Paquete ?? throw new InvalidOperationException("La caja 1 no tiene su copia del catálogo.");
        var guion = GuionDelDia.Armar(copia, await cajas[0].Cliente.RapidosAsync(copia.Caja.SucursalId), opciones);
        resultado.Guion = guion;
        (resultado.ProductosEnLaCopia, resultado.CodigosEnLaCopia) = (copia.Productos.Count, copia.Codigos.Count);
        var existenciasAntes = copia.Productos.Where(p => p.LlevaInventario).ToDictionary(p => p.VarianteId, p => p.Existencia ?? 0);
        var nombres = copia.Productos.ToDictionary(p => p.VarianteId, p => p.Descripcion);

        var vigilancias = new List<Task>();
        dia.CambiarPrecioAsync = async () =>
        {
            if (guion.PrecioQueCambia is not { } producto)
                return;
            var inicio = Stopwatch.GetTimestamp();
            await oficina.CambiarMenudeoAsync(new SolicitudPrecioMenudeo(dueno.UsuarioId, sucursal.Id, producto.VarianteId, guion.PrecioNuevoCentavos));
            mediciones.Anotar(Operacion.CambioDePrecio, inicio);
            var cambio = Stopwatch.GetTimestamp();
            dia.Anotar($"El dueño cambió el precio de «{producto.Descripcion}»: de {Formato.Dinero(producto.Precios[0].PrecioCentavos)} a "
                + $"{Formato.Dinero(guion.PrecioNuevoCentavos)}.");
            lock (vigilancias)
                vigilancias.AddRange(cajas.Select(c => VigilarElPrecioAsync(c, producto, guion.PrecioNuevoCentavos, cambio, dia, resultado)));
        };

        // ── El día ──
        resultado.MemoriaAntes = memoria?.Ahora();
        var procesadorAntes = memoria?.Procesador();
        var simuladorAntes = Process.GetCurrentProcess().TotalProcessorTime;
        memoria?.EmpezarPico();
        dia.Avisar($"Empieza el día: {cajas.Count} × {opciones.Tickets} tickets"
            + (opciones.Minutos > 0 ? $" en {opciones.Minutos:0.#} min" : ", sin esperas")
            + (opciones.Caida is { } caida
                ? $"; la red de la {cajas[opciones.CajaDeLaCaida - 1].Nombre} se cae {caida.Cuanto} al {caida.Inicio:P0} del día."
                : "."));
        dia.Empezar();
        await Task.WhenAll(cajas.Select(async c =>
        {
            await c.VenderElDiaAsync(guion.PorCaja[c.Numero - 1], ct);
            await c.CerrarElTurnoAsync(ct);
        }));
        resultado.DuracionDelDia = dia.Transcurrido;
        Task[] pendientes;
        lock (vigilancias)
            pendientes = [.. vigilancias];
        await Task.WhenAll(pendientes);
        foreach (var caja in cajas)
        {
            if (!await caja.EsperarBandejaVaciaAsync(opciones.Espera))
                resultado.Errores.Add($"La {caja.Nombre} no subió su bandeja en {opciones.Espera.TotalSeconds:0} s: le quedan {caja.Contingencia.Pendientes}.");
        }
        mediciones.Midiendo = false;
        resultado.MemoriaPico = memoria?.Pico;
        resultado.MemoriaDespues = memoria?.Ahora();
        if (procesadorAntes is { } antes && memoria?.Procesador() is { } despues)
            resultado.ProcesadorDelServicio = despues - antes;
        resultado.ProcesadorDelSimulador = Process.GetCurrentProcess().TotalProcessorTime - simuladorAntes;
        dia.Avisar("Terminó el día. Cuadrando contra la caja principal…");

        // ── El cuadre: lo que dice la caja principal, lo que quedó en cada caja ──
        if (dia.ProductoNuevo.Task is { IsCompletedSuccessfully: true, Result: { } nuevo })
            nombres[nuevo.VarianteId] = nuevo.Descripcion;
        var delServicio = await ConsultarAsync(cajas, plano, oficina, dueno, guion, ct);
        var libros = cajas.Select(LibroDeCaja.De).ToList();
        var bandejas = new Dictionary<int, IReadOnlyList<ElementoDeLaBandeja>>();
        foreach (var caja in cajas)
        {
            caja.Cerrar();
            cerradas.Add(caja);
            bandejas[caja.Numero] = caja.LeerBandeja();
            resultado.BaseLocal[caja.Numero] = new FileInfo(caja.RutaLocal).Length;
        }
        delServicio = delServicio with { Bandejas = bandejas };
        if (Base is not null)
            delServicio = await RevisarLaBaseAsync(delServicio, nombres, ct);
        resultado.Cuadre = Cuadre.Revisar(libros, existenciasAntes, nombres, delServicio, guion.PrecioQueCambia is null ? null : guion.PrecioNuevoCentavos);
    }

    /// <summary>De que el dueño cambia el precio a que llega a la copia de la caja (por el aviso y lo que cambió).</summary>
    private async Task VigilarElPrecioAsync(CajaSimulada caja, ProductoVentaDto producto, long nuevo, long desde, DiaSimulado dia, ResultadoSimulacion resultado)
    {
        while (Stopwatch.GetElapsedTime(desde) < opciones.Espera)
        {
            if (caja.Contingencia.FotoDeLaCopia?.PorVariante(producto.VarianteId)?.Precios.Any(p => p.PresentacionId is null && p.PrecioCentavos == nuevo) == true)
            {
                resultado.Mediciones.Anotar(Operacion.PrecioEnLaCopia, Stopwatch.GetElapsedTime(desde));
                dia.PrecioEnLaCopia(caja.Numero).TrySetResult();
                return;
            }
            await Task.Delay(5);
        }
        lock (resultado.Errores)
            resultado.Errores.Add($"El precio que cambió el dueño no llegó a la copia de la {caja.Nombre} en {opciones.Espera.TotalSeconds:0} s.");
    }

    /// <summary>
    /// Lo que dice la caja principal, por la API como lo pregunta cada caja (con su llave): sus ventas del día, la cuenta de su
    /// turno, si sigue abierto, el corte del día (el dueño), y de cada producto que se movió su existencia y su kardex. Antes, la
    /// copia de cada caja se pone al día (con el aviso o el latido) para comparar sus existencias.
    /// </summary>
    private async Task<DelServicio> ConsultarAsync(
        IReadOnlyList<CajaSimulada> cajas, HttpClient plano, ClienteCaja oficina, SesionDto dueno, GuionDelDia guion, CancellationToken ct)
    {
        var ventas = new Dictionary<int, IReadOnlyList<VentaDelDiaDto>>();
        var resumenes = new Dictionary<int, CorteDto?>();
        var abiertos = new Dictionary<int, bool>();
        foreach (var caja in cajas)
        {
            ventas[caja.Numero] = await LeerAsync<List<VentaDelDiaDto>>(plano, caja, $"api/cajas/{caja.Caja.Id}/ventas-del-dia", ct) ?? [];
            resumenes[caja.Numero] = await LeerAsync<CorteDto>(plano, caja, $"api/turnos/{caja.Turno.Id}/resumen", ct);
            abiertos[caja.Numero] = await LeerAsync<TurnoDto>(plano, caja, $"api/cajas/{caja.Caja.Id}/turno", ct) is not null;
        }
        CorteDelDiaDto? corteDelDia = null;
        try
        {
            corteDelDia = await oficina.CorteDelDiaAsync(cajas[0].Caja.Id, dueno.UsuarioId);
        }
        catch (ErrorCaja)
        {
            // El cuadre lo dice.
        }

        var movidos = cajas.SelectMany(c => c.Ventas).SelectMany(v => v.Renglones).Where(r => r is { LlevaInventario: true, VarianteId: not null })
            .Select(r => r.VarianteId!.Value)
            .Concat(cajas.SelectMany(c => c.Devoluciones).SelectMany(d => d.Renglones).Select(r => r.VarianteId))
            .ToHashSet();
        var sucursal = cajas[0].Caja.SucursalId;
        var existencias = new Dictionary<Guid, decimal>();
        var kardex = new Dictionary<Guid, decimal>();
        foreach (var varianteId in movidos)
        {
            if (await LeerAsync<ProductoVentaDto>(plano, cajas[0], $"api/productos/{varianteId}?sucursalId={sucursal}", ct) is { } producto)
                existencias[varianteId] = producto.Existencia ?? 0;
            var movimientos = await LeerAsync<List<MovimientoKardexDto>>(plano, cajas[0], $"api/catalogo/variantes/{varianteId}/kardex?sucursalId={sucursal}", ct);
            // El primero es el último movimiento, con la suma de todos (aunque la lista traiga solo los últimos).
            kardex[varianteId] = movimientos?.FirstOrDefault()?.Existencia ?? 0;
        }

        // La copia de cada caja, ya al día con las versiones de la caja principal.
        var estado = await plano.GetFromJsonAsync<EstadoServicioDto>("api/estado", ct);
        var copias = new Dictionary<int, IReadOnlyDictionary<Guid, decimal>>();
        var precios = new Dictionary<int, long?>();
        foreach (var caja in cajas)
        {
            var inicio = Stopwatch.GetTimestamp();
            while (estado is not null && (caja.Contingencia.Paquete is not { } p || p.Version < estado.VersionDatos
                                          || (p.VersionExistencias ?? 0) < (estado.VersionExistencias ?? 0)))
            {
                if (Stopwatch.GetElapsedTime(inicio) > opciones.Espera)
                    break;
                await Task.Delay(50, ct);
            }
            var paquete = caja.Contingencia.Paquete!;
            copias[caja.Numero] = paquete.Productos.Where(p => movidos.Contains(p.VarianteId)).ToDictionary(p => p.VarianteId, p => p.Existencia ?? 0);
            precios[caja.Numero] = guion.PrecioQueCambia is { } cambio
                ? paquete.Productos.FirstOrDefault(p => p.VarianteId == cambio.VarianteId)?.Precios.FirstOrDefault(x => x.PresentacionId is null)?.PrecioCentavos
                : null;
        }
        return new DelServicio(ventas, resumenes, abiertos, corteDelDia, existencias, kardex, copias, precios,
            new Dictionary<int, IReadOnlyList<ElementoDeLaBandeja>>());
    }

    /// <summary>Lee de la caja principal con la llave de esa caja; vacío si no hay (204 o 404).</summary>
    private static async Task<T?> LeerAsync<T>(HttpClient http, CajaSimulada caja, string ruta, CancellationToken ct)
    {
        using var solicitud = new HttpRequestMessage(HttpMethod.Get, ruta);
        solicitud.Headers.Add(ClienteCaja.EncabezadoLlave, caja.Conexion.Llave);
        using var respuesta = await http.SendAsync(solicitud, ct);
        if (respuesta.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
            return default;
        respuesta.EnsureSuccessStatusCode();
        return await respuesta.Content.ReadFromJsonAsync<T>(ct);
    }

    /// <summary>Toda la base, cuando es de esta PC: cada existencia contra la suma de su kardex, y ningún folio dos veces.</summary>
    private async Task<DelServicio> RevisarLaBaseAsync(DelServicio delServicio, IReadOnlyDictionary<Guid, string> nombres, CancellationToken ct)
    {
        await using var db = Base!();
        var distintas = (await new RegistroInventario(db).VerificarExistenciasAsync(ct))
            .Select(d => $"«{nombres.GetValueOrDefault(d.VarianteId, d.VarianteId.ToString())}»: "
                         + $"existencia {d.CantidadGuardada} ({Formato.Dinero(d.CostoGuardado)}), "
                         + $"suma del kardex {d.CantidadKardex} ({Formato.Dinero(d.CostoKardex)}).")
            .ToList();
        var repetidos = (await db.Ventas.AsNoTracking().Where(v => v.Folio != null)
                .GroupBy(v => new { v.SucursalId, v.Serie, v.Folio })
                .Where(g => g.Count() > 1)
                .Select(g => new { g.Key.Serie, g.Key.Folio, Veces = g.Count() })
                .ToListAsync(ct))
            .Select(x => $"El folio {x.Serie}-{x.Folio:000000} está {x.Veces} veces.")
            .ToList();
        return delServicio with { ExistenciasDistintasEnLaBase = distintas, FoliosRepetidosEnLaBase = repetidos };
    }

    private static void Borrar(string carpeta)
    {
        for (var intento = 1; intento <= 20; intento++)
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(carpeta))
                    Directory.Delete(carpeta, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>
/// Lo que ocupa el servicio en la memoria (su conjunto de trabajo: lo que tiene en la RAM) y el procesador que lleva usado,
/// leídos del sistema cada medio segundo, con el más alto desde <see cref="EmpezarPico"/>.
/// </summary>
public sealed class MuestreoDeMemoria : IAsyncDisposable
{
    private readonly Process _proceso;
    private readonly Lock _candado = new();
    private readonly CancellationTokenSource _alto = new();
    private readonly Task _muestreo;
    private long _pico;

    public MuestreoDeMemoria(Process proceso)
    {
        _proceso = proceso;
        _muestreo = Task.Run(MuestrearAsync);
    }

    public long Pico => Interlocked.Read(ref _pico);

    public void EmpezarPico() => Interlocked.Exchange(ref _pico, Ahora() ?? 0);

    /// <summary>Lo que ocupa ahora; vacío si no se puede leer (el proceso ya terminó, o el sistema no deja).</summary>
    public long? Ahora()
    {
        lock (_candado)
        {
            try
            {
                _proceso.Refresh();
                return _proceso.HasExited ? null : _proceso.WorkingSet64;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return null;
            }
        }
    }

    public TimeSpan? Procesador()
    {
        lock (_candado)
        {
            try
            {
                _proceso.Refresh();
                return _proceso.HasExited ? null : _proceso.TotalProcessorTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return null;
            }
        }
    }

    private async Task MuestrearAsync()
    {
        while (!_alto.IsCancellationRequested)
        {
            if (Ahora() is { } ahora)
            {
                long pico;
                do
                    pico = Interlocked.Read(ref _pico);
                while (ahora > pico && Interlocked.CompareExchange(ref _pico, ahora, pico) != pico);
            }
            try
            {
                await Task.Delay(500, _alto.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _alto.CancelAsync();
        await _muestreo.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _alto.Dispose();
    }
}
