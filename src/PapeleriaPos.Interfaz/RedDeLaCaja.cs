using System.Net;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que la ventana de Windows pone para la red de la caja (la red de la tienda en el POS): dónde va la bitácora de red, cómo
/// está configurada la impresora de tickets y lo que solo sabe Windows. Sin esto (el navegador), la caja trabaja como antes.
/// </summary>
public sealed class OpcionesRedDeLaCaja
{
    /// <summary>La carpeta del registro de la caja: ahí va red-caja-AAAA-MM-DD.log. Vacía: sin bitácora de red.</summary>
    public string? CarpetaBitacora { get; init; }

    /// <summary>La impresora de tickets como está en caja.json: para avisar si está por nombre o con WSD, y para hacerle ping.</summary>
    public string? Impresora { get; init; }

    /// <summary>El puerto de cada impresora de Windows (lo lee la caja de Windows).</summary>
    public IPuertosDeImpresoras? Puertos { get; init; }

    /// <summary>El Wi-Fi y la MAC del router (los lee la caja de Windows).</summary>
    public IDetallesDeRed? Detalles { get; init; }

    /// <summary>Las pruebas de la red (ping); las pruebas del POS ponen unas a mano.</summary>
    public IPruebasDeRed? Pruebas { get; init; }

    /// <summary>La tarjeta de red de esta PC; las pruebas del POS ponen una a mano.</summary>
    public IRedDelEquipo? Equipo { get; init; }

    /// <summary>Cómo se busca a la caja principal en la red; las pruebas del POS ponen uno a mano.</summary>
    public IBuscadorDeLaPrincipal? Buscador { get; init; }

    /// <summary>Con qué se le pide a un servicio encontrado que pruebe quién es; las pruebas del POS ponen el suyo.</summary>
    public HttpClient? Identidad { get; init; }

    public TimeProvider? Reloj { get; init; }
}

/// <summary>
/// La red de la caja (la red de la tienda en el POS, docs/arquitectura.md, «La red de la matriz»): su bitácora de red y su vigía,
/// volver a encontrar a la caja principal si le cambió la IP, y los avisos de lo configurado por nombre o con WSD. La
/// contingencia le pasa cada latido (<see cref="Latido"/>) y la deja vigilando mientras la caja esté abierta.
/// </summary>
public sealed class RedDeLaCaja
{
    public RedDeLaCaja(ConexionCaja conexion, BitacoraDeRed? bitacora = null, VigiaDeLaRed? vigia = null, ReencuentroDeLaPrincipal? reencuentro = null,
        string? avisoImpresora = null)
    {
        Conexion = conexion;
        Bitacora = bitacora;
        Vigia = vigia;
        Reencuentro = reencuentro;
        AvisoImpresora = avisoImpresora;
        if (reencuentro is not null)
            reencuentro.Cambio += () => Cambio?.Invoke();
    }

    /// <summary>
    /// La de la caja de verdad (la ventana de Windows): la bitácora en la carpeta del registro, el vigía (la tarjeta, el router y
    /// la impresora de tickets) y volver a encontrar a la caja principal por la red con la llave de esta caja.
    /// </summary>
    public static RedDeLaCaja Crear(ConexionCaja conexion, OpcionesRedDeLaCaja? opciones)
    {
        opciones ??= new OpcionesRedDeLaCaja();
        var avisoImpresora = AvisosDeRed.DeLaImpresora(opciones.Impresora, opciones.Puertos);
        if (opciones.CarpetaBitacora is not { Length: > 0 } carpeta)
            return new RedDeLaCaja(conexion, avisoImpresora: avisoImpresora);
        var reloj = opciones.Reloj ?? TimeProvider.System;
        var bitacora = new BitacoraDeRed(carpeta, "red-caja", reloj);
        var equipos = AvisosDeRed.IpDeLaImpresora(opciones.Impresora, opciones.Puertos) is { } impresora
            ? new List<(string, IPAddress)> { ("la impresora de tickets", impresora) }
            : [];
        var vigia = new VigiaDeLaRed(bitacora, opciones.Equipo ?? new RedDelEquipo(opciones.Detalles), opciones.Pruebas ?? new PruebasDeRed(),
            new OpcionesDelVigia
            {
                Quien = "esta caja",
                AlTerminar = "se cerró la caja",
                Equipos = equipos,
                // La caja principal con su propia caja: el router lo revisa su servicio, en la misma PC.
                RevisarRouter = !conexion.Servicio.IsLoopback,
                AlEmpezar = () => new[] { avisoImpresora, AvisosDeRed.DelServicio(conexion.Servicio) }.OfType<string>(),
                Hacia = () => IPAddress.TryParse(conexion.Servicio.Host.Trim('[', ']'), out var ip) && !IPAddress.IsLoopback(ip) ? ip : null,
            }, reloj);
        var reencuentro = new ReencuentroDeLaPrincipal(conexion, opciones.Buscador ?? new BuscadorPorDifusion(), opciones.Identidad ?? ClienteDeLaPrueba(),
            reloj, bitacora);
        return new RedDeLaCaja(conexion, bitacora, vigia, reencuentro, avisoImpresora);
    }

    /// <summary>
    /// El cliente con que se le pide a un servicio encontrado que pruebe quién es: aparte del de la venta, sin seguir
    /// redirecciones (lo que conteste ese mismo servicio es lo que cuenta) y sin guardar conexiones.
    /// </summary>
    private static HttpClient ClienteDeLaPrueba() => new(ManejadorDeLaPrueba()) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Sin proxy (nada de la red ve a quién se le pregunta), sin seguir redirecciones y sin guardar conexiones.</summary>
    internal static SocketsHttpHandler ManejadorDeLaPrueba() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = ReencuentroDeLaPrincipal.EsperaPrueba,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(1),
    };

    public ConexionCaja Conexion { get; }

    public BitacoraDeRed? Bitacora { get; }

    public VigiaDeLaRed? Vigia { get; }

    public ReencuentroDeLaPrincipal? Reencuentro { get; }

    /// <summary>La impresora de tickets está por nombre, compartida o con WSD: el chip de los tickets y la entrada lo dicen.</summary>
    public string? AvisoImpresora { get; }

    /// <summary>
    /// Esta caja busca a la caja principal por su nombre (lo dice Configuración › Acerca de). Se vuelve a ver cada vez: si la
    /// encuentra en la red, guarda su IP y deja de decirlo.
    /// </summary>
    public string? AvisoServicio => AvisosDeRed.DelServicio(Conexion.Servicio);

    /// <summary>Empezó o terminó una búsqueda de la caja principal: la franja se vuelve a pintar.</summary>
    public event Action? Cambio;

    /// <summary>Lo de la caja mientras esté abierta: el vigía (con la primera y la última línea de la bitácora).</summary>
    public Task VigilarAsync(CancellationToken ct) => Vigia?.VigilarAsync(ct) ?? Task.CompletedTask;

    /// <summary>
    /// Cada latido: lo que tardó (vacío si no contestó), cómo quedó la red y lo que la caja principal dice del internet. Lo anota el
    /// vigía y, si la caja principal no contesta un rato, se la busca en la red.
    /// </summary>
    public void Latido(TimeSpan? latencia, EstadoRed estado, RedDeLaPrincipalDto? red)
    {
        Vigia?.Latido(latencia, estado, Conexion.Servicio);
        // Lo que la caja principal dice del internet; en la caja principal lo anota su servicio, en la misma PC.
        if (latencia is not null && !Conexion.Servicio.IsLoopback)
            Vigia?.InternetDeLaPrincipal(red?.Internet);
        Reencuentro?.Latido(contesto: latencia is not null);
    }
}
