using System.Globalization;
using System.Text.Json;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Interfaz;

/// <summary>Busca en la red los servicios del POS que contestan (<see cref="Descubrimiento.BuscarAsync"/>); las pruebas, uno a mano.</summary>
public interface IBuscadorDeLaPrincipal
{
    Task<IReadOnlyList<Uri>> BuscarAsync(TimeSpan espera, CancellationToken ct = default);
}

/// <summary>El de verdad: una pregunta por UDP a toda la red (y a la difusión de cada red de esta PC).</summary>
public sealed class BuscadorPorDifusion : IBuscadorDeLaPrincipal
{
    public async Task<IReadOnlyList<Uri>> BuscarAsync(TimeSpan espera, CancellationToken ct = default) => await Descubrimiento.BuscarAsync(espera, ct);
}

/// <summary>
/// La caja vuelve a encontrar sola a la caja principal si le cambió la IP (la red de la tienda en el POS,
/// docs/arquitectura.md, «La red de la matriz»): si la dirección guardada no contesta un rato (<see cref="CaidaParaBuscar"/>),
/// pregunta en la red por UDP (un paquete por red, sin recorrerla equipo por equipo) y a cada servicio que contesta le pide que
/// pruebe que es su caja principal (<see cref="Descubrimiento.Prueba"/>): un reto nuevo, firmado con la huella de la llave de
/// esta caja y con la dirección en que lo recibió. Solo se cambia a uno que lo prueba; entonces guarda la dirección nueva
/// (<see cref="ConexionCaja.AlGuardar"/>) y lo anota en la bitácora de red. Lo que contesta sin probarlo no se usa: se anota.
/// <para>
/// Mientras la dirección guardada contesta, no hace nada. Sin llave (la caja del navegador, la de la prueba) o con la caja
/// principal en esta misma PC, tampoco: no hay a quién más buscar. Si no la encuentra, vuelve a buscar cada vez más espaciado
/// (<see cref="Esperas"/>): durante una caída de la red no llena la red ni la bitácora. «Volver a buscarla», en la franja,
/// busca al momento (<see cref="BuscarAhoraAsync"/>).
/// </para>
/// </summary>
public sealed class ReencuentroDeLaPrincipal(
    ConexionCaja conexion, IBuscadorDeLaPrincipal buscador, HttpClient http, TimeProvider? reloj = null, BitacoraDeRed? bitacora = null)
{
    /// <summary>Lo que la dirección guardada tiene que llevar sin contestar antes de buscar en la red.</summary>
    public static readonly TimeSpan CaidaParaBuscar = TimeSpan.FromSeconds(30);

    /// <summary>Lo que se espera entre una búsqueda y la siguiente mientras no la encuentra: cada vez más, hasta 5 minutos.</summary>
    public static readonly IReadOnlyList<TimeSpan> Esperas = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>Lo que se esperan las respuestas a la pregunta por UDP.</summary>
    public static readonly TimeSpan EsperaRespuestas = TimeSpan.FromMilliseconds(1500);

    /// <summary>Lo que se espera a que un servicio encontrado pruebe quién es.</summary>
    public static readonly TimeSpan EsperaPrueba = TimeSpan.FromSeconds(2);

    /// <summary>Cuántos de los que contestan se revisan en cada búsqueda (en la tienda hay uno).</summary>
    public const int CuantosSeRevisan = 5;

    /// <summary>
    /// Lo más que se lee de lo que contesta un servicio encontrado (la prueba es un renglón): uno que contesta de más no llena la
    /// memoria de la caja.
    /// </summary>
    public const int TopeDeLaRespuesta = 4096;

    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;
    private readonly Lock _candado = new();
    private DateTimeOffset? _caidaDesde;
    private DateTimeOffset? _siguiente;
    private int _busquedas;
    private Task<bool> _busqueda = Task.FromResult(false);

    /// <summary>Lo que ya se anotó en esta caída: no se repite en cada búsqueda.</summary>
    private readonly HashSet<string> _anotado = new(StringComparer.Ordinal);

    /// <summary>
    /// Si esta caja puede buscar a su caja principal: con llave, con la caja principal en otra PC y sin simular que no hay
    /// conexión (la prueba con una persona real).
    /// </summary>
    public bool Aplica => !conexion.Servicio.IsLoopback && conexion.CajaId is not null && conexion.Llave is not null && !conexion.SimularSinConexion;

    /// <summary>Está buscando ahora (la franja dice «Buscando…»).</summary>
    public bool Buscando
    {
        get
        {
            lock (_candado)
                return !_busqueda.IsCompleted;
        }
    }

    /// <summary>Lo que dijo la última búsqueda, para la franja; vacío si no ha buscado en esta caída.</summary>
    public string? Resultado { get; private set; }

    /// <summary>Empezó o terminó una búsqueda: la franja se vuelve a pintar.</summary>
    public event Action? Cambio;

    /// <summary>Se cambió a la dirección nueva (ya guardada): el latido la prueba al momento.</summary>
    public event Action<Uri>? SeCambio;

    /// <summary>La última búsqueda (para las pruebas: esperar a que termine).</summary>
    internal Task<bool> UltimaBusqueda
    {
        get
        {
            lock (_candado)
                return _busqueda;
        }
    }

    /// <summary>
    /// Lo que dijo el latido: si la dirección guardada contestó. Si no contesta desde hace <see cref="CaidaParaBuscar"/> y ya
    /// toca, busca, sin esperarla (corre aparte del latido).
    /// </summary>
    public void Latido(bool contesto)
    {
        var ahora = _reloj.GetUtcNow();
        lock (_candado)
        {
            if (contesto)
            {
                if (_busquedas > 0 && _busqueda.IsCompleted)
                    bitacora?.Anotar($"La caja principal contestó otra vez: se deja de buscarla (se buscó {Veces(_busquedas)}).");
                (_caidaDesde, _siguiente, _busquedas, Resultado) = (null, null, 0, null);
                _anotado.Clear();
                return;
            }
            _caidaDesde ??= ahora;
            if (!Aplica || !_busqueda.IsCompleted || ahora - _caidaDesde < CaidaParaBuscar || _siguiente is { } siguiente && ahora < siguiente)
                return;
            _busqueda = Task.Run(() => BuscarAsync(automatica: true, CancellationToken.None));
        }
        Cambio?.Invoke();
    }

    /// <summary>
    /// «Volver a buscarla» (la franja): busca al momento, sin esperar su turno. Solo se cambia a una que pruebe que es la caja
    /// principal de esta caja. Dice si se cambió.
    /// </summary>
    public Task<bool> BuscarAhoraAsync(CancellationToken ct = default)
    {
        Task<bool> busqueda;
        lock (_candado)
        {
            if (!_busqueda.IsCompleted)
                return _busqueda;
            if (!Aplica)
                return Task.FromResult(false);
            busqueda = _busqueda = Task.Run(() => BuscarAsync(automatica: false, ct), ct);
        }
        Cambio?.Invoke();
        return busqueda;
    }

    private async Task<bool> BuscarAsync(bool automatica, CancellationToken ct)
    {
        var cambio = false;
        try
        {
            int busqueda;
            TimeSpan? sinContestar;
            lock (_candado)
            {
                busqueda = ++_busquedas;
                sinContestar = _caidaDesde is { } desde ? _reloj.GetUtcNow() - desde : null;
            }
            var guardada = conexion.Servicio;
            if (busqueda == 1)
                bitacora?.Anotar($"Se busca la caja principal en la red{(automatica ? "" : " («Volver a buscarla»)")}: {guardada.Authority} "
                                 + $"{(sinContestar is { } t ? $"no contesta desde hace {Textos.Duracion(t)}" : "no contesta")}.");
            IReadOnlyList<Uri> encontrados;
            try
            {
                encontrados = await buscador.BuscarAsync(EsperaRespuestas, ct);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
            {
                encontrados = [];
            }
            var noProbaron = new List<string>();
            var laMisma = false;
            foreach (var encontrado in encontrados.Take(CuantosSeRevisan))
            {
                if (MismaDireccion(encontrado, guardada))
                {
                    laMisma = true;
                    continue;
                }
                if (await EsLaPrincipalAsync(encontrado, ct))
                {
                    cambio = await CambiarAAsync(guardada, encontrado);
                    return cambio;
                }
                noProbaron.Add(encontrado.Authority);
            }
            bool anotar;
            lock (_candado)
            {
                // La caja principal contestó otra vez mientras se buscaba: ya no hay nada que decir.
                if (automatica && _caidaDesde is null)
                    return false;
                Resultado = laMisma
                    ? $"La caja principal contesta en la red en {guardada.Authority}, pero no su servicio: la caja sigue intentando."
                    : encontrados.Count == 0
                        ? $"No contestó ninguna caja principal en la red ({Hora()})."
                        : $"Contestó {Textos.Lista(noProbaron)}, pero no probó ser la caja principal de esta caja: no se usa.";
                // Cada cosa distinta una vez por caída: buscar cada pocos minutos durante 12 horas no llena la bitácora.
                anotar = _anotado.Add(laMisma ? "misma" : encontrados.Count == 0 ? "ninguna" : string.Join(",", noProbaron.Order(StringComparer.Ordinal)));
            }
            if (anotar)
                bitacora?.Anotar(laMisma
                    ? $"Contesta en la red en {guardada.Authority} (la misma dirección), pero su servicio no contesta."
                    : encontrados.Count == 0
                        ? "No contestó ninguna caja principal en la red; se vuelve a buscar cada vez más espaciado."
                        : $"Contestó {Textos.Lista(noProbaron)}, pero no probó con la llave de esta caja que es su caja principal: no se usa.");
            return false;
        }
        finally
        {
            lock (_candado)
            {
                // Si mientras tanto contestó (el latido ya lo dejó en cero), no hay siguiente: empieza de nuevo en la próxima caída.
                if (automatica && !cambio && _caidaDesde is not null)
                    _siguiente = _reloj.GetUtcNow() + Esperas[Math.Clamp(_busquedas, 1, Esperas.Count) - 1];
            }
            Cambio?.Invoke();
        }
    }

    /// <summary>
    /// Le pide al servicio encontrado que pruebe que es la caja principal de esta caja: un reto nuevo, sin mandarle la llave (a
    /// alguien que todavía no prueba quién es no se le da). Sí, solo si su prueba es la que se espera para esa dirección.
    /// </summary>
    private async Task<bool> EsLaPrincipalAsync(Uri encontrado, CancellationToken ct)
    {
        if (Descubrimiento.Direccion(encontrado) is not { } direccion || conexion.CajaId is not { } caja || conexion.Llave is not { } llave)
            return false;
        var reto = Descubrimiento.NuevoReto();
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(EsperaPrueba);
        try
        {
            using var pregunta = new HttpRequestMessage(HttpMethod.Get,
                new Uri(encontrado, $"{Descubrimiento.RutaIdentidad}?caja={caja:D}&reto={Uri.EscapeDataString(reto)}"));
            using var respuesta = await http.SendAsync(pregunta, HttpCompletionOption.ResponseHeadersRead, limite.Token);
            if (!respuesta.IsSuccessStatusCode || respuesta.Content.Headers.ContentLength is > TopeDeLaRespuesta)
                return false;
            // Solo lo que cabe en el tope: lo demás no es una prueba.
            await using var flujo = await respuesta.Content.ReadAsStreamAsync(limite.Token);
            var leido = new byte[TopeDeLaRespuesta + 1];
            var largo = 0;
            int parte;
            while (largo < leido.Length && (parte = await flujo.ReadAsync(leido.AsMemory(largo), limite.Token)) > 0)
                largo += parte;
            if (largo > TopeDeLaRespuesta)
                return false;
            var identidad = JsonSerializer.Deserialize<IdentidadServicioDto>(leido.AsSpan(0, largo), JsonSerializerOptions.Web);
            return Descubrimiento.EsLaPrueba(identidad?.Prueba, Descubrimiento.Prueba(LlaveCaja.Huella(llave), caja, reto, direccion));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException
                                       or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Se cambia a la dirección que probó ser la caja principal y la guarda (caja.json), aunque no se pueda guardar.</summary>
    private async Task<bool> CambiarAAsync(Uri antes, Uri nueva)
    {
        conexion.Servicio = nueva;
        Resultado = $"Se encontró la caja principal en {nueva.Authority}.";
        bitacora?.Anotar($"La caja principal cambió de dirección: {antes.Authority} → {nueva.Authority}. Contestó en la red y probó con la llave de "
                         + "esta caja que es su caja principal: se guarda la dirección nueva.");
        try
        {
            if (conexion.AlGuardar is { } guardar)
                await guardar(conexion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Security.Cryptography.CryptographicException)
        {
            // Se usa igual mientras la caja siga abierta; al abrirla otra vez, la vuelve a buscar.
            bitacora?.Anotar($"No se pudo guardar la dirección nueva ({ex.Message}): al abrir la caja otra vez, se vuelve a buscar.");
        }
        SeCambio?.Invoke(nueva);
        return true;
    }

    private static bool MismaDireccion(Uri a, Uri b) =>
        string.Equals(a.Host.Trim('[', ']'), b.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private string Hora() => _reloj.GetLocalNow().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Veces(int veces) => veces == 1 ? "una vez" : $"{veces} veces";
}
