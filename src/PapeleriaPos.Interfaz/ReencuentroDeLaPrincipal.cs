using System.Globalization;
using System.Net;
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

/// <summary>Cómo quedó la dirección guardada al pedirle que pruebe quién es (<see cref="ReencuentroDeLaPrincipal.ComprobarGuardadaAsync"/>).</summary>
public enum Comprobacion
{
    /// <summary>Es la caja principal de esta caja (y, si esta caja ya conoce su llave, la misma de siempre).</summary>
    Es,

    /// <summary>Un servicio de antes, sin la prueba: se sigue como antes (solo si esta caja todavía no conoce su llave).</summary>
    SinLaPrueba,

    /// <summary>El servicio dice que no conoce a esta caja: la quitaron o la dieron de alta en otra PC.</summary>
    NoLaConoce,

    /// <summary>Contesta, pero no prueba ser la caja principal de esta caja (o no la de siempre): no se le manda la llave.</summary>
    NoEs,

    /// <summary>No contesta.</summary>
    NoContesta,
}

/// <summary>
/// La caja vuelve a encontrar sola a la caja principal si le cambió la IP (la red de la tienda en el POS,
/// docs/arquitectura.md, «La red de la matriz»): si la dirección guardada no contesta un rato (<see cref="CaidaParaBuscar"/>),
/// pregunta en la red por UDP (un paquete por red, sin recorrerla equipo por equipo) y a cada servicio que contesta le pide que
/// pruebe que es su caja principal (<see cref="Descubrimiento.Prueba"/>): un reto nuevo, firmado con la huella de la llave de
/// esta caja y con la dirección en que lo recibió, y además con la llave propia del servicio. Solo se cambia si una sola caja
/// principal lo prueba; entonces guarda la dirección nueva (<see cref="ConexionCaja.AlGuardar"/>) y lo anota en la bitácora de red.
/// Lo que contesta sin probarlo no se usa: se anota.
/// <para>
/// La llave del servicio (<see cref="ConexionCaja.LlaveDelServicio"/>): la caja la recuerda al darse de alta o la primera vez que
/// su dirección guardada prueba quién es. Desde entonces, un servicio con una copia de su base (la PC de antes, un respaldo en
/// otra PC) sabe la huella de su llave, pero no firma como su caja principal: nunca gana. Si dos prueban ser cajas principales
/// distintas, tampoco se cambia sola: lo dice para que se avise al dueño.
/// </para>
/// <para>
/// Mientras la dirección guardada contesta, no hace nada. Sin llave (la caja del navegador, la de la prueba) o con la caja
/// principal en esta misma PC, tampoco: no hay a quién más buscar. Si no la encuentra, vuelve a buscar cada vez más espaciado
/// (<see cref="Esperas"/>): durante una caída de la red no llena la red ni la bitácora. «Volver a buscarla», en la franja,
/// busca al momento (<see cref="BuscarAhoraAsync"/>).
/// </para>
/// </summary>
/// <param name="redes">
/// Las redes de esta caja: solo se revisa lo que contesta desde ellas (lo de otra red no es de la tienda). Sin esto, las de sus
/// tarjetas de cable y Wi-Fi; las pruebas ponen las suyas.
/// </param>
public sealed class ReencuentroDeLaPrincipal(
    ConexionCaja conexion, IBuscadorDeLaPrincipal buscador, HttpClient http, TimeProvider? reloj = null, BitacoraDeRed? bitacora = null,
    Func<IReadOnlyList<(IPAddress Direccion, int Prefijo)>>? redes = null)
{
    /// <summary>Lo que la dirección guardada tiene que llevar sin contestar antes de buscar en la red.</summary>
    public static readonly TimeSpan CaidaParaBuscar = TimeSpan.FromSeconds(30);

    /// <summary>Lo que se espera entre una búsqueda y la siguiente mientras no la encuentra: cada vez más, hasta 5 minutos.</summary>
    public static readonly IReadOnlyList<TimeSpan> Esperas = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>Lo que se esperan las respuestas a la pregunta por UDP.</summary>
    public static readonly TimeSpan EsperaRespuestas = TimeSpan.FromMilliseconds(1500);

    /// <summary>Lo que se espera a que un servicio encontrado pruebe quién es.</summary>
    public static readonly TimeSpan EsperaPrueba = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Cuántas IP distintas de las que contestan se revisan en cada búsqueda (en la tienda contesta una): de una IP, una sola vez
    /// (con el puerto de la dirección guardada, si contestó con él), así que muchas respuestas falsas desde un mismo equipo no
    /// dejan fuera a la de verdad.
    /// </summary>
    public const int CuantasSeRevisan = 64;

    /// <summary>Cuántas se revisan a la vez: la búsqueda entera tarda unos segundos aunque contesten muchas.</summary>
    public const int ALaVez = 16;

    /// <summary>Lo que dice la franja si dos o más prueban ser cajas principales distintas: no se cambia sola a ninguna.</summary>
    public const string DosPrincipales = "Hay dos cajas principales en la red: avisa al dueño.";

    /// <summary>Lo que dice la franja si la dirección guardada sabe de esta caja pero no firma como su caja principal de siempre.</summary>
    public const string OtraCajaPrincipal =
        "La caja principal no probó ser la misma de siempre: si se cambió o se reinstaló su PC, el dueño da de alta esta caja otra vez.";

    /// <summary>
    /// Lo más que se lee de lo que contesta un servicio encontrado (la prueba es un renglón): uno que contesta de más no llena la
    /// memoria de la caja.
    /// </summary>
    public const int TopeDeLaRespuesta = 4096;

    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;
    private readonly Lock _candado = new();

    /// <summary>Sin <see cref="ConexionCaja.DireccionDeConfianza"/>, la de confianza es con la que abrió la caja (no una que encontró sola).</summary>
    private readonly Uri _conLaQueAbrio = conexion.Servicio;

    /// <summary>
    /// Desde cuándo no contesta y cuándo toca buscar otra vez, con la marca de tiempo de la PC (no la hora: si Windows corrige su
    /// reloj en medio de una caída, no se adelanta ni se atrasa la búsqueda).
    /// </summary>
    private long? _caidaDesde;

    private (long Desde, TimeSpan Espera)? _siguiente;
    private int _busquedas;
    private Task<bool> _busqueda = Task.FromResult(false);

    /// <summary>Lo que ya se anotó en esta caída: no se repite en cada búsqueda.</summary>
    private readonly HashSet<string> _anotado = new(StringComparer.Ordinal);

    /// <summary>Lo último que dijo la comprobación de la dirección guardada (se anota solo cuando cambia).</summary>
    private Comprobacion? _comprobada;

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

    /// <summary>Lo que dijo la última búsqueda (o la comprobación), para la franja; vacío si no ha buscado en esta caída.</summary>
    public string? Resultado { get; private set; }

    /// <summary>
    /// La dirección guardada sabe de esta caja, pero no firma con la llave de su caja principal de siempre (se reinstaló, o es una
    /// copia de su base): la franja ofrece darla de alta otra vez.
    /// </summary>
    public bool OtraIdentidad { get; private set; }

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
        var ahora = _reloj.GetTimestamp();
        lock (_candado)
        {
            if (contesto)
            {
                if (_busquedas > 0 && _busqueda.IsCompleted)
                    bitacora?.Anotar($"La caja principal contestó otra vez: se deja de buscarla (se buscó {Veces(_busquedas)}).");
                (_caidaDesde, _siguiente, _busquedas, Resultado, OtraIdentidad) = (null, null, 0, null, false);
                _anotado.Clear();
                return;
            }
            _caidaDesde ??= ahora;
            if (!Aplica || !_busqueda.IsCompleted || _reloj.GetElapsedTime(_caidaDesde.Value, ahora) < CaidaParaBuscar
                || _siguiente is { } siguiente && _reloj.GetElapsedTime(siguiente.Desde, ahora) < siguiente.Espera)
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

    /// <summary>
    /// Después de una caída (o al abrir la caja), antes de volver a mandarle la llave: que la dirección guardada pruebe que es la
    /// caja principal de esta caja (por nombre, cada IP de ese nombre). Si lo prueba y esta caja todavía no conocía la llave del
    /// servicio, la recuerda desde ahora y la guarda, si es la dirección que puso el dueño (<see cref="ConexionCaja.DireccionDeConfianza"/>).
    /// </summary>
    public async Task<Comprobacion> ComprobarGuardadaAsync(CancellationToken ct = default)
    {
        var guardada = conexion.Servicio;
        var revisiones = await Task.WhenAll((await DireccionesAsync(guardada, ct)).Select(d => RevisarAsync(d, ct)));
        Comprobacion resultado;
        if (revisiones.FirstOrDefault(r => r.Resultado == Prueba.Probo) is { } probo)
        {
            resultado = Comprobacion.Es;
            // La primera vez, solo en la dirección que puso el dueño: no en una que la caja encontró sola.
            if (conexion.LlaveDelServicio is null && probo.LlaveDelServicio is { } llave && MismaDireccion(guardada, conexion.DireccionDeConfianza ?? _conLaQueAbrio))
                await RecordarAsync(llave, guardada);
        }
        else if (revisiones.Any(r => r.Resultado == Prueba.OtraIdentidad))
            resultado = Comprobacion.NoEs;
        else if (revisiones.Any(r => r.Resultado == Prueba.NoLaConoce))
            resultado = Comprobacion.NoLaConoce;
        // Un servicio de antes (sin la prueba): como antes, salvo que esta caja ya conozca su llave (entonces sí la tiene).
        else if (revisiones.Any(r => r.Resultado == Prueba.SinLaPrueba))
            resultado = conexion.LlaveDelServicio is null ? Comprobacion.SinLaPrueba : Comprobacion.NoEs;
        else if (revisiones.Length == 0 || revisiones.All(r => r.Resultado == Prueba.NoContesto))
            resultado = Comprobacion.NoContesta;
        else
            resultado = Comprobacion.NoEs;

        var otra = revisiones.Any(r => r.Resultado == Prueba.OtraIdentidad);
        bool cambio;
        lock (_candado)
        {
            cambio = _comprobada != resultado;
            _comprobada = resultado;
            OtraIdentidad = otra && resultado == Comprobacion.NoEs;
            if (resultado == Comprobacion.NoEs)
                Resultado = otra ? OtraCajaPrincipal : $"{guardada.Authority} contesta, pero no probó ser la caja principal de esta caja: no se le manda la llave.";
        }
        if (cambio && resultado != Comprobacion.Es && resultado != Comprobacion.NoContesta)
            bitacora?.Anotar(resultado switch
            {
                Comprobacion.NoEs when otra => $"{guardada.Authority} sabe de esta caja, pero no firma con la llave de su caja principal de siempre "
                                               + "(se reinstaló, o es una copia de su base): no se le manda la llave de la caja.",
                Comprobacion.NoEs => $"{guardada.Authority} contesta, pero no probó ser la caja principal de esta caja: no se le manda la llave.",
                Comprobacion.NoLaConoce => $"{guardada.Authority} dice que no conoce a esta caja (la quitaron o la dieron de alta en otra PC).",
                _ => $"{guardada.Authority} es un servicio de antes (sin la prueba de quién es): se sigue como antes.",
            });
        if (cambio)
            Cambio?.Invoke();
        return resultado;
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
                sinContestar = _caidaDesde is { } desde ? _reloj.GetElapsedTime(desde) : null;
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
            // Cada IP una vez (de las redes de esta caja), y todas a la vez: ninguna respuesta, falsa o no, deja fuera a otra.
            var candidatos = Candidatos(encontrados, guardada, (redes ?? RedLocal.Subredes)());
            using var turnos = new SemaphoreSlim(ALaVez);
            var revisados = await Task.WhenAll(candidatos.Select(async c =>
            {
                await turnos.WaitAsync(ct);
                try
                {
                    return await RevisarAsync(c, ct);
                }
                finally
                {
                    turnos.Release();
                }
            }));
            var probaron = revisados.Where(r => r.Resultado == Prueba.Probo).ToList();
            var copias = revisados.Where(r => r.Resultado == Prueba.OtraIdentidad).Select(r => r.Direccion.Authority).ToList();
            var noProbaron = revisados.Where(r => r.Resultado is not (Prueba.Probo or Prueba.OtraIdentidad)).Select(r => r.Direccion.Authority).ToList();
            // Cada caja principal por su llave (una de antes, sin llave, por su dirección): la misma puede contestar por dos tarjetas.
            var principales = probaron.GroupBy(r => r.LlaveDelServicio ?? r.Direccion.Authority).ToList();
            // La dirección guardada cuenta como «la misma» solo si prueba quién es: una respuesta falsa desde su IP no detiene nada.
            var laMisma = probaron.Any(p => MismaDireccion(p.Direccion, guardada));
            if (!laMisma && principales.Count == 1)
            {
                if (copias.Count > 0 && bitacora is not null)
                    bitacora.Anotar($"También contestó {Textos.Lista(copias)}, que sabe de esta caja pero no firma con la llave de su caja principal "
                                    + "(una copia de su base): no se usa. Avisa al dueño.");
                cambio = await CambiarAAsync(guardada, principales[0].First().Direccion);
                return cambio;
            }
            string clave, linea;
            lock (_candado)
            {
                // La caja principal contestó otra vez mientras se buscaba: ya no hay nada que decir.
                if (automatica && _caidaDesde is null)
                    return false;
                if (laMisma)
                {
                    Resultado = $"La caja principal contesta en {guardada.Authority} y probó quién es: la caja sigue intentando.";
                    (clave, linea) = ("misma", $"Contesta en la red en {guardada.Authority} (la misma dirección) y probó quién es; el latido vuelve solo.");
                }
                else if (principales.Count > 1 || copias.Count > 0)
                {
                    // Una puede ser una copia de su base (la PC de antes, un respaldo en otra PC): no se cambia sola a ninguna.
                    Resultado = DosPrincipales;
                    var cuales = Textos.Lista([.. probaron.Select(p => p.Direccion.Authority), .. copias]);
                    (clave, linea) = ($"dos:{cuales}", principales.Count > 1
                        ? $"Contestaron {cuales}, y prueban ser cajas principales distintas de esta caja (una puede ser una copia de su base): no se "
                          + "cambia sola a ninguna. La caja sigue con su copia; avisa al dueño."
                        : $"Contestó {cuales}, que sabe de esta caja pero no firma con la llave de su caja principal (una copia de su base): no se usa. "
                          + "La caja sigue con su copia; avisa al dueño.");
                }
                else if (encontrados.Count == 0)
                {
                    Resultado = $"No contestó ninguna caja principal en la red ({Hora()}).";
                    (clave, linea) = ("ninguna", "No contestó ninguna caja principal en la red; se vuelve a buscar cada vez más espaciado.");
                }
                else
                {
                    var cuales = noProbaron.Count > 0 ? Textos.Lista(noProbaron) : $"{encontrados.Count} fuera de la red de esta caja";
                    Resultado = $"Contestó {cuales}, pero no probó ser la caja principal de esta caja: no se usa.";
                    (clave, linea) = (string.Join(",", noProbaron.Order(StringComparer.Ordinal)),
                        $"Contestó {cuales}, pero no probó con la llave de esta caja que es su caja principal: no se usa.");
                }
            }
            // Cada cosa distinta una vez por caída: buscar cada pocos minutos durante 12 horas no llena la bitácora.
            bool anotar;
            lock (_candado)
                anotar = _anotado.Add(clave);
            if (anotar)
                bitacora?.Anotar(linea);
            return false;
        }
        finally
        {
            lock (_candado)
            {
                // Si mientras tanto contestó (el latido ya lo dejó en cero), no hay siguiente: empieza de nuevo en la próxima caída.
                if (automatica && !cambio && _caidaDesde is not null)
                    _siguiente = (_reloj.GetTimestamp(), Esperas[Math.Clamp(_busquedas, 1, Esperas.Count) - 1]);
            }
            Cambio?.Invoke();
        }
    }

    /// <summary>Lo que se supo de un servicio al pedirle que pruebe quién es.</summary>
    internal enum Prueba
    {
        /// <summary>No contestó: no se pudo conectar, o no a tiempo.</summary>
        NoContesto,

        /// <summary>Contestó, pero no es la caja principal de esta caja (la prueba no cuadra, o contestó cualquier cosa).</summary>
        NoProbo,

        /// <summary>
        /// Sabe la huella de la llave de esta caja, pero no firma con la llave del servicio que esta caja conoce: otra caja principal
        /// (se reinstaló, o una copia de su base).
        /// </summary>
        OtraIdentidad,

        /// <summary>Probó que es la caja principal de esta caja (y, si esta caja ya conoce su llave, que es la misma).</summary>
        Probo,

        /// <summary>Un servicio de antes: no tiene la prueba.</summary>
        SinLaPrueba,

        /// <summary>El servicio dice que no conoce a esta caja.</summary>
        NoLaConoce,
    }

    /// <param name="LlaveDelServicio">La llave del servicio, si la trajo con una firma que cuadra.</param>
    internal sealed record Revision(Uri Direccion, Prueba Resultado, string? LlaveDelServicio = null);

    /// <summary>
    /// Le pide al servicio que pruebe que es la caja principal de esta caja: un reto nuevo, sin mandarle la llave (a alguien que
    /// todavía no prueba quién es no se le da). Probó si su prueba es la que se espera para esa dirección y, si esta caja ya conoce
    /// la llave del servicio, si firma con esa.
    /// </summary>
    private async Task<Revision> RevisarAsync(Uri servicio, CancellationToken ct)
    {
        if (Descubrimiento.Direccion(servicio) is not { } direccion || conexion.CajaId is not { } caja || conexion.Llave is not { } llave)
            return new Revision(servicio, Prueba.NoProbo);
        var reto = Descubrimiento.NuevoReto();
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(EsperaPrueba);
        try
        {
            using var pregunta = new HttpRequestMessage(HttpMethod.Get,
                new Uri(servicio, $"{Descubrimiento.RutaIdentidad}?caja={caja:D}&reto={Uri.EscapeDataString(reto)}"));
            using var respuesta = await http.SendAsync(pregunta, HttpCompletionOption.ResponseHeadersRead, limite.Token);
            if (respuesta.StatusCode == HttpStatusCode.NotFound)
            {
                // ¿Un servicio de antes (no tiene la ruta) o uno que no conoce a esta caja?
                var error = await LeerAsync<ErrorDto>(respuesta, limite.Token);
                return new Revision(servicio, error?.Codigo == "caja-sin-alta" ? Prueba.NoLaConoce : Prueba.SinLaPrueba);
            }
            if (!respuesta.IsSuccessStatusCode)
                return new Revision(servicio, Prueba.NoProbo);
            var identidad = await LeerAsync<IdentidadServicioDto>(respuesta, limite.Token);
            if (identidad is null || !Descubrimiento.EsLaPrueba(identidad.Prueba, Descubrimiento.Prueba(LlaveCaja.Huella(llave), caja, reto, direccion)))
                return new Revision(servicio, Prueba.NoProbo);
            // La llave del servicio cuenta solo con una firma que cuadra con este reto y esta dirección.
            var suya = identidad.LlaveDelServicio is { } publica
                       && Descubrimiento.FirmaValida(publica, Descubrimiento.MensajeFirmado(caja, reto, direccion, identidad.Prueba), identidad.Firma)
                ? publica
                : null;
            return conexion.LlaveDelServicio is { } conocida && suya != conocida
                ? new Revision(servicio, Prueba.OtraIdentidad, suya)
                : new Revision(servicio, Prueba.Probo, suya);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // No contestó, o se cortó a media respuesta: no detiene la búsqueda, se revisa el siguiente.
            return new Revision(servicio, Prueba.NoContesto);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return new Revision(servicio, Prueba.NoProbo);
        }
    }

    /// <summary>Lo que contestó, si cabe en el tope (un JSON chico); vacío si no es eso.</summary>
    private static async Task<T?> LeerAsync<T>(HttpResponseMessage respuesta, CancellationToken ct) where T : class
    {
        if (respuesta.Content.Headers.ContentLength is > TopeDeLaRespuesta)
            return null;
        await using var flujo = await respuesta.Content.ReadAsStreamAsync(ct);
        var leido = new byte[TopeDeLaRespuesta + 1];
        var largo = 0;
        int parte;
        while (largo < leido.Length && (parte = await flujo.ReadAsync(leido.AsMemory(largo), ct)) > 0)
            largo += parte;
        if (largo is 0 or > TopeDeLaRespuesta)
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(leido.AsSpan(0, largo), JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>La dirección guardada por IP, esa; por nombre, cada IP de ese nombre (si el DNS contesta a tiempo).</summary>
    private static async Task<IReadOnlyList<Uri>> DireccionesAsync(Uri guardada, CancellationToken ct)
    {
        if (RedLocal.EsIp(guardada.Host))
            return [guardada];
        try
        {
            var ips = await Dns.GetHostAddressesAsync(guardada.Host, ct).WaitAsync(EsperaPrueba, ct);
            return [.. ips.Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Distinct().Take(4)
                .Select(ip => new UriBuilder(guardada) { Host = ip.ToString(), Path = "/" }.Uri)];
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TimeoutException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>La primera vez que su caja principal prueba quién es (con su dirección de siempre), esta caja recuerda su llave.</summary>
    private async Task RecordarAsync(string llave, Uri guardada)
    {
        conexion.LlaveDelServicio = llave;
        bitacora?.Anotar($"Esta caja recuerda la llave de su caja principal ({Descubrimiento.HuellaDeLaLlave(llave)}, en {guardada.Authority}): desde "
                         + "ahora solo se cambia a una que firme con ella.");
        try
        {
            if (conexion.AlGuardar is { } guardar)
                await guardar(conexion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Security.Cryptography.CryptographicException)
        {
            // Se recuerda mientras la caja siga abierta; al abrirla otra vez, se vuelve a aprender igual.
            bitacora?.Anotar($"No se pudo guardar la llave de la caja principal ({ex.Message}): se vuelve a aprender al abrir la caja.");
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

    /// <summary>
    /// Lo que se revisa de lo que contestó: solo IP (no nombres), solo de las redes de esta caja (si se saben), cada IP una vez
    /// (con el puerto de la dirección guardada si contestó con él; si no, el primero), hasta <see cref="CuantasSeRevisan"/>.
    /// </summary>
    internal static List<Uri> Candidatos(IEnumerable<Uri> encontrados, Uri guardada, IReadOnlyList<(IPAddress Direccion, int Prefijo)> redes)
    {
        var candidatos = new List<Uri>();
        foreach (var deUnaIp in encontrados.GroupBy(u => u.Host.Trim('[', ']'), StringComparer.OrdinalIgnoreCase))
        {
            if (!IPAddress.TryParse(deUnaIp.Key, out var ip) || redes.Count > 0 && !RedLocal.EsDeLaRedLocal(ip, redes))
                continue;
            candidatos.Add(deUnaIp.FirstOrDefault(u => u.Port == guardada.Port) ?? deUnaIp.First());
            if (candidatos.Count == CuantasSeRevisan)
                break;
        }
        return candidatos;
    }

    private static bool MismaDireccion(Uri a, Uri b) =>
        string.Equals(a.Host.Trim('[', ']'), b.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private string Hora() => _reloj.GetLocalNow().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Veces(int veces) => veces == 1 ? "una vez" : $"{veces} veces";
}
