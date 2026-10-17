using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Las fotos de los productos en esta caja (entrega 13, parte 2; docs/propuesta-interfaz.md, «Lo que se señala», punto 7):
/// salen en Ctrl+M, en la lista de la búsqueda y en el verificador, siempre de lo que ya tiene la caja, sin la red. Si un
/// producto no tiene foto (o todavía no baja), sus iniciales, como antes.
/// <para>
/// La copia solo dice de qué producto es cada foto y su huella (<see cref="PaqueteContingenciaDto.Fotos"/>): cada vez que llega
/// una copia o lo que cambió, la caja baja de fondo las fotos que no tiene (<c>api/fotos/{variante}</c>, una a una), las guarda
/// en su base (caja-local.db) por el escritor de siempre, en lotes chicos, y quita las que ya no van. Al abrir, las lee de su
/// base una vez y las tiene en memoria (unos 15 KB cada una: con 300 fotos, unos 5 MB). La copia completa no crece con ellas.
/// </para>
/// <para>
/// La pantalla las pide por su archivo (<see cref="FotoProducto.Archivo"/>, el <c>src</c> de su imagen): en la ventana de
/// Windows se lo da esta caja a su página (<see cref="Archivo"/>), sin la red. En la caja del navegador sin base local (en la
/// misma PC de la caja principal), las da el servicio: aquí solo se pregunta cuáles hay.
/// </para>
/// </summary>
public sealed class FotosDeLaCaja
{
    /// <summary>Lo más que pesa una foto que baja: las del servicio pesan unos 15 KB (a lo más, unos 40).</summary>
    public const int BytesMaximos = 256 * 1024;

    /// <summary>
    /// Cuánto se escribe junto en la base de la caja, como los pedazos de la copia: lo que escribe la venta mientras tanto espera
    /// a lo más un lote.
    /// </summary>
    public const int TamanoDelLote = 256 * 1024;

    /// <summary>Lo más que se espera cada foto.</summary>
    private static readonly TimeSpan EsperaFoto = TimeSpan.FromSeconds(10);

    /// <summary>Sin copia (el navegador): cada cuánto se vuelve a preguntar cuáles hay, mientras se enseñan.</summary>
    private static readonly TimeSpan VigenciaSinCopia = TimeSpan.FromSeconds(15);

    /// <summary>Una foto que ya tiene esta caja: su huella y la foto.</summary>
    private sealed record Guardada(string Huella, byte[] Datos);

    private readonly AlmacenLocal? _almacen;
    private readonly Transporte? _http;
    private readonly Func<Task<FotosDto?>>? _pedir;
    private readonly TimeProvider _reloj;
    private readonly Lock _candado = new();

    /// <summary>Las que ya bajó esta caja, por producto.</summary>
    private volatile ImmutableDictionary<Guid, Guardada> _guardadas = ImmutableDictionary<Guid, Guardada>.Empty;

    /// <summary>Las que dice la copia (de qué producto y su huella); vacío mientras no hay copia.</summary>
    private volatile FrozenDictionary<Guid, string>? _referencias;

    /// <summary>La lista de la copia de donde salieron (si la siguiente copia trae la misma, no hay nada que hacer).</summary>
    private IReadOnlyList<FotoDeProductoDto>? _lista;

    /// <summary>
    /// Si lo que dice la copia viene de la red (y no de la copia guardada al abrir, que puede ser de antes): solo entonces se
    /// quitan de la base las que ya no van. Si la caja abre sin conexión con una copia vieja, no borra lo que bajó después.
    /// </summary>
    private volatile bool _deLaRed;

    /// <summary>Leer las guardadas al abrir, fuera del hilo de la pantalla.</summary>
    private readonly Task _carga;

    /// <summary>Una vuelta de bajar a la vez; lo que llegue mientras, se atiende al terminar (<see cref="_otraVez"/>).</summary>
    private readonly SemaphoreSlim _bajando = new(1, 1);

    private volatile bool _otraVez;
    private readonly CancellationTokenSource _alto = new();

    /// <summary>
    /// Las que la caja principal ya no tenía cuando se pidieron (se quitó o cambió la foto mientras): con esa huella no se vuelven
    /// a pedir; la copia dirá lo que hay.
    /// </summary>
    private readonly ConcurrentDictionary<(Guid VarianteId, string Huella), bool> _noEstan = new();

    /// <summary>Sin copia: cuándo se preguntó la última vez cuáles hay, y la pregunta en curso.</summary>
    private long? _preguntadasEn;

    private Task? _preguntando;

    /// <summary>Llegó una foto, se quitó o cambió cuáles hay: lo que las enseña se vuelve a pintar.</summary>
    public event Action? Cambio;

    /// <summary>La de una caja con su base (la ventana de Windows, o el navegador con base local): la crea la contingencia.</summary>
    internal FotosDeLaCaja(AlmacenLocal almacen, Transporte http, TimeProvider? reloj = null)
    {
        (_almacen, _http, _reloj) = (almacen, http, reloj ?? TimeProvider.System);
        _carga = Task.Run(Cargar);
    }

    /// <summary>
    /// La de la caja del navegador sin base local, en la misma PC de la caja principal: <paramref name="pedir"/> dice cuáles hay
    /// (<c>api/fotos</c>), y las enseña el servicio.
    /// </summary>
    public FotosDeLaCaja(Func<Task<FotosDto?>> pedir, TimeProvider? reloj = null)
    {
        (_pedir, _reloj) = (pedir, reloj ?? TimeProvider.System);
        _carga = Task.CompletedTask;
    }

    /// <summary>Cuántas fotos tiene esta caja (las que ya bajó).</summary>
    public int Guardadas => _guardadas.Count;

    /// <summary>Cuántas dice la copia que hay y esta caja todavía no tiene (o tiene de antes).</summary>
    public int Faltan => _referencias is { } referencias ? referencias.Count(Falta) : 0;

    /// <summary>Termina cuando ya se leyeron de la base de la caja las que había al abrir.</summary>
    public Task Leidas => _carga;

    /// <summary>
    /// Dónde está la foto de ese producto para la pantalla (el <c>src</c> de su imagen), de lo que ya tiene esta caja; vacío si
    /// no tiene foto o todavía no baja: entonces van sus iniciales. Mientras baja la nueva, la de antes.
    /// </summary>
    public string? Direccion(Guid varianteId)
    {
        if (_pedir is not null)
        {
            PreguntarSiHaceFalta();
            return _referencias is { } todas && todas.TryGetValue(varianteId, out var huella) ? FotoProducto.Archivo(varianteId, huella) : null;
        }
        if (_referencias is not { } referencias || !referencias.ContainsKey(varianteId))
            return null;
        return _guardadas.TryGetValue(varianteId, out var guardada) ? FotoProducto.Archivo(varianteId, guardada.Huella) : null;
    }

    /// <summary>
    /// La foto de un archivo de la pantalla (<see cref="Direccion"/>), de lo que tiene esta caja: la ventana de Windows se la da
    /// a su página sin la red. Vacía si no es de una foto o esta caja no la tiene. Si ya bajó otra de ese producto (la cambiaron),
    /// la nueva: la pantalla se pinta otra vez con su dirección.
    /// </summary>
    public byte[]? Archivo(string ruta) =>
        FotoProducto.LeerArchivo(ruta, out var varianteId, out _) && _guardadas.TryGetValue(varianteId, out var guardada) ? guardada.Datos : null;

    /// <summary>La foto que tiene esta caja de ese producto, tal cual (para enseñarla aparte); vacía si no la tiene.</summary>
    public byte[]? Datos(Guid varianteId) => _guardadas.TryGetValue(varianteId, out var guardada) ? guardada.Datos : null;

    private bool Falta(KeyValuePair<Guid, string> referencia) =>
        !(_guardadas.TryGetValue(referencia.Key, out var guardada) && guardada.Huella == referencia.Value) && !_noEstan.ContainsKey((referencia.Key, referencia.Value));

    // ── La caja con su base: lo que dice la copia y lo que baja ─────────────

    /// <summary>
    /// Lo que dice la copia que se acaba de poner (la guardada al abrir, la completa o con lo que cambió): de qué productos hay
    /// foto y cuál. Las que falten se bajan de fondo; si no cambió nada, no hace nada.
    /// </summary>
    /// <param name="deLaRed">Si la copia viene de la caja principal (y no de la base, al abrir).</param>
    internal void Referencias(IReadOnlyList<FotoDeProductoDto>? fotos, bool deLaRed)
    {
        bool cambiaron, bajar;
        lock (_candado)
        {
            // La primera de la red confirma lo que dice: entonces sí se quitan las que ya no van, aunque sea la misma lista.
            var primeraDeLaRed = deLaRed && !_deLaRed;
            _deLaRed |= deLaRed;
            if (ReferenceEquals(fotos, _lista))
                (cambiaron, bajar) = (false, primeraDeLaRed && fotos is not null);
            else
            {
                var antes = _referencias;
                var nuevas = fotos?.DistinctBy(f => f.VarianteId).ToFrozenDictionary(f => f.VarianteId, f => f.Huella);
                (_lista, _referencias) = (fotos, nuevas);
                (cambiaron, bajar) = (!Mismas(antes, nuevas), fotos is not null);
                if (cambiaron)
                    _noEstan.Clear();
            }
        }
        if (cambiaron)
            Avisar();
        if (bajar)
            _ = PonerAlDiaAsync();
    }

    /// <summary>Si falta alguna (no se pudo bajar: no había conexión), otra vuelta. Lo pide el latido, con la caja principal contestando.</summary>
    internal void SiFaltan()
    {
        if (_almacen is not null && !_alto.IsCancellationRequested && _referencias is { } referencias && referencias.Any(Falta))
            _ = PonerAlDiaAsync();
    }

    /// <summary>
    /// Baja de fondo las fotos que faltan y quita las que ya no van; una vuelta a la vez (lo que llegue mientras, se atiende al
    /// terminar). Nunca detiene a la pantalla: lo nuevo se enseña de un jalón al guardar cada lote.
    /// </summary>
    public Task PonerAlDiaAsync()
    {
        if (_almacen is null)
            return Task.CompletedTask;
        _otraVez = true;
        return Task.Run(async () =>
        {
            while (_otraVez && !_alto.IsCancellationRequested && await _bajando.WaitAsync(0))
            {
                try
                {
                    while (_otraVez && !_alto.IsCancellationRequested)
                    {
                        _otraVez = false;
                        try
                        {
                            await PonerAlDiaUnaVezAsync();
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            // Las fotos nunca tumban la caja (la base ocupada, algo que no se esperaba): lo que falte se intenta
                            // otra vez con la siguiente copia o el siguiente latido.
                        }
                    }
                }
                finally
                {
                    _bajando.Release();
                }
            }
        });
    }

    private async Task PonerAlDiaUnaVezAsync()
    {
        await _carga.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_referencias is not { } referencias)
            return;
        // Las que ya no van (se les quitó la foto en la caja principal): con lo que dice la red, no con la copia de la base.
        if (_deLaRed && _guardadas.Keys.Where(id => !referencias.ContainsKey(id)).ToList() is { Count: > 0 } sobran)
        {
            if (_alto.IsCancellationRequested)
                return;
            await _almacen!.EscribirAsync(l => l.QuitarFotos(sobran), "las fotos que se quitaron", laLeen: false);
            _guardadas = _guardadas.RemoveRange(sobran);
            Avisar();
        }
        var faltan = referencias.Where(Falta).Select(r => r.Key).Order().ToList();
        var lote = new List<(Guid VarianteId, string Huella, byte[] Datos)>();
        var bytes = 0;
        foreach (var varianteId in faltan)
        {
            if (_alto.IsCancellationRequested)
                return;
            byte[]? datos;
            try
            {
                datos = await _http!.GetBytesAsync($"api/fotos/{varianteId}", EsperaFoto, BytesMaximos, _alto.Token);
            }
            catch (ErrorCaja)
            {
                // Sin conexión (o un error del servicio): lo que falta se pide en otra vuelta (la siguiente copia o el latido).
                break;
            }
            if (datos is null)
            {
                // Se quitó (o cambió) mientras: lo dirá la copia.
                _noEstan[(varianteId, referencias[varianteId])] = true;
                continue;
            }
            var huella = FotoProducto.HuellaDe(datos);
            // Cambió entre la copia y ahora: se guarda la de ahora, y la de la copia ya no se pide (la copia dirá la nueva).
            if (huella != referencias[varianteId])
                _noEstan[(varianteId, referencias[varianteId])] = true;
            lote.Add((varianteId, huella, datos));
            bytes += datos.Length;
            if (bytes >= TamanoDelLote)
            {
                await GuardarAsync(lote);
                (lote, bytes) = ([], 0);
            }
        }
        await GuardarAsync(lote);
    }

    /// <summary>Un lote de fotos que bajaron: en la base de la caja (por el escritor de siempre) y, ya guardadas, en la pantalla.</summary>
    private async Task GuardarAsync(List<(Guid VarianteId, string Huella, byte[] Datos)> lote)
    {
        if (lote.Count == 0 || _alto.IsCancellationRequested)
            return;
        await _almacen!.EscribirAsync(l =>
        {
            foreach (var (varianteId, huella, datos) in lote)
                l.GuardarFoto(varianteId, huella, datos);
        }, "las fotos de los productos", laLeen: false);
        _guardadas = _guardadas.SetItems(lote.Select(f => KeyValuePair.Create(f.VarianteId, new Guardada(f.Huella, f.Datos))));
        Avisar();
    }

    /// <summary>Las que había en la base de la caja al abrir; si no se pueden leer, como si no hubiera (se vuelven a bajar).</summary>
    private void Cargar()
    {
        try
        {
            _guardadas = _almacen!.LeerFotos().DistinctBy(f => f.VarianteId)
                .ToImmutableDictionary(f => f.VarianteId, f => new Guardada(f.Huella, f.Datos));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException)
        {
            return;
        }
        if (!_guardadas.IsEmpty)
            Avisar();
    }

    /// <summary>
    /// Se cierra la caja: ya no se baja nada (lo que iba bajando se suelta) y se espera, a lo más ese rato, a que termine lo que
    /// escribía en la base de la caja, para que después se pueda cerrar.
    /// </summary>
    internal void Detener(TimeSpan espera)
    {
        var limite = DateTime.UtcNow + espera;
        _alto.Cancel();
        ((IAsyncResult)_carga).AsyncWaitHandle.WaitOne(Restante(limite));
        if (_bajando.Wait(Restante(limite)))
            _bajando.Release();
    }

    private static TimeSpan Restante(DateTime limite) => limite - DateTime.UtcNow is var falta && falta > TimeSpan.Zero ? falta : TimeSpan.Zero;

    // ── La caja sin base (el navegador): cuáles hay, de la caja principal ───

    /// <summary>Sin copia: se pregunta cuáles hay la primera vez que se enseña una, y de nuevo si ya pasó un rato.</summary>
    private void PreguntarSiHaceFalta()
    {
        lock (_candado)
        {
            if (_preguntando is { IsCompleted: false } || _preguntadasEn is { } en && _reloj.GetElapsedTime(en) < VigenciaSinCopia)
                return;
            _preguntadasEn = _reloj.GetTimestamp();
            _preguntando = Task.Run(PreguntarAsync);
        }
    }

    private async Task PreguntarAsync()
    {
        try
        {
            if (await _pedir!() is not { } fotos)
                return;
            var nuevas = fotos.Fotos.DistinctBy(f => f.VarianteId).ToFrozenDictionary(f => f.VarianteId, f => f.Huella);
            var antes = _referencias;
            _referencias = nuevas;
            if (!Mismas(antes, nuevas))
                Avisar();
        }
        catch (Exception ex) when (ex is ErrorCaja or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Sin respuesta: siguen las de antes (o las iniciales) y se pregunta otra vez en un rato.
        }
    }

    /// <summary>Si dicen lo mismo (sin copia y sin ninguna foto, también: en la pantalla no cambia nada).</summary>
    private static bool Mismas(FrozenDictionary<Guid, string>? a, FrozenDictionary<Guid, string>? b) =>
        ReferenceEquals(a, b) || (a?.Count ?? 0) == 0 && (b?.Count ?? 0) == 0
        || a is not null && b is not null && a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var huella) && huella == x.Value);

    private void Avisar()
    {
        try
        {
            Cambio?.Invoke();
        }
        catch (Exception)
        {
            // Lo que pinta las fotos no tumba lo que las baja (corre en su propio hilo).
        }
    }
}
