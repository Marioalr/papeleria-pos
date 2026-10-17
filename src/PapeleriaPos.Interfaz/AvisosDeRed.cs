using System.Net;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>Qué clase de puerto tiene una impresora de Windows.</summary>
public enum TipoDePuerto
{
    /// <summary>Puerto TCP/IP estándar: con la IP (o el nombre) de la impresora.</summary>
    TcpIp,

    /// <summary>WSD: Windows la busca en la red cada vez (con el router trabado, no la encuentra).</summary>
    Wsd,

    /// <summary>Una impresora compartida desde otra PC (\\PC\cola).</summary>
    Compartida,

    /// <summary>USB, paralelo, serie o a un archivo: no pasa por la red.</summary>
    Local,

    Otro,
}

/// <summary>El puerto de una impresora de Windows: su nombre, de qué clase es y, si es TCP/IP, a qué IP o nombre va.</summary>
public sealed record PuertoDeImpresora(string Nombre, TipoDePuerto Tipo, string? Host = null);

/// <summary>
/// Cómo está configurada una impresora de Windows (la caja de Windows lo lee del registro: src/PapeleriaPos.Caja). Las pruebas,
/// uno a mano. Sin esto (Linux, el navegador), no se sabe y no se avisa.
/// </summary>
public interface IPuertosDeImpresoras
{
    /// <summary>El puerto de esa impresora de Windows; vacío si no existe o no se puede leer.</summary>
    PuertoDeImpresora? DeLaImpresora(string nombre);
}

/// <summary>
/// Lo que la red de la tienda le pide al POS que diga en pantalla (docs/arquitectura.md, «La red de la matriz»; la propuesta de
/// interfaz, «Caja principal y secundarias»): la franja que distingue «sin internet» de «sin red local», y los avisos de lo que
/// está configurado por nombre o con WSD (con el router trabado, no se encuentra). Todo aquí es de solo leer: sale de lo que ya
/// se sabe (el latido, la configuración), no pregunta nada a la red.
/// </summary>
public static class AvisosDeRed
{
    /// <summary>Sin internet, con la red local bien: la venta no lo necesita y desconectar algo no ayuda.</summary>
    public const string SinInternet = "Sin internet: la venta sigue. No desconectes nada.";

    /// <summary>Esta caja no llega a la caja principal.</summary>
    public const string SinRedLocal = "Sin red local: esta caja trabaja con su copia";

    /// <summary>La caja principal: ni internet ni su router (lo que pasa cuando se traba el router).</summary>
    public const string SinInternetNiRedLocal = "Sin internet ni red local: esta caja sigue vendiendo.";

    /// <summary>La caja principal: su propio servicio no contesta (se está actualizando o se detuvo).</summary>
    public const string SinServicio = "El servicio de esta PC no contesta: se vende con la copia de esta caja.";

    public const string Lenta = "La red con la caja principal está lenta: se vende con la copia de esta caja hasta que mejore.";

    /// <summary>Qué dice la franja de la red (o nada).</summary>
    public enum Franja
    {
        Nada,
        SinRedLocal,
        SinServicio,
        Lenta,
        SinInternet,
        SinInternetNiRedLocal,
    }

    /// <summary>
    /// La franja de la red, sin modal y sin estorbar al cobro (docs/arquitectura.md, «En el POS»). Junta lo que esta caja sabe de
    /// la caja principal (su latido) con lo que la caja principal dice del internet y de su router (<see cref="RedDeLaPrincipalDto"/>):
    /// <list type="bullet">
    /// <item>no llega a la caja principal: «Sin red local: esta caja trabaja con su copia» (la caja principal, si es su propio
    /// servicio el que no contesta, lo dice así);</item>
    /// <item>llega, pero lenta: lo de siempre (la red lenta);</item>
    /// <item>llega y la caja principal dice que no hay internet: «Sin internet: la venta sigue. No desconectes nada.» En la caja
    /// principal, si tampoco contesta su router, la red local tampoco está (el router trabado): entonces no se dice «no desconectes
    /// nada», porque la guía de la tienda pide juntar los datos y desconectar el cable del módem;</item>
    /// <item>todo bien, o no se sabe (un servicio de antes): nada.</item>
    /// </list>
    /// </summary>
    /// <param name="servicioEnEstaPc">La caja principal (su servicio en esta PC).</param>
    /// <param name="simulando">La prueba simula que no hay conexión: se dice como en la caja 2.</param>
    public static Franja QueDice(bool servicioEnEstaPc, bool simulando, EstadoRed estado, RedDeLaPrincipalDto? red) =>
        (estado, servicioEnEstaPc && !simulando) switch
        {
            (EstadoRed.Caida, false) => Franja.SinRedLocal,
            (EstadoRed.Caida, true) => Franja.SinServicio,
            (EstadoRed.Lenta, false) => Franja.Lenta,
            _ when red?.Internet != false => Franja.Nada,
            (_, true) when red?.Router == false => Franja.SinInternetNiRedLocal,
            _ => Franja.SinInternet,
        };

    /// <summary>El texto de la franja; vacío si no dice nada.</summary>
    public static string? Texto(Franja franja) => franja switch
    {
        Franja.SinRedLocal => SinRedLocal,
        Franja.SinServicio => SinServicio,
        Franja.Lenta => Lenta,
        Franja.SinInternet => SinInternet,
        Franja.SinInternetNiRedLocal => SinInternetNiRedLocal,
        _ => null,
    };

    // ── Lo configurado por nombre o con WSD ─────────────────────────────────

    /// <summary>El chip de los tickets cuando la impresora está por nombre, compartida o con WSD.</summary>
    public const string ConfiguralaPorIp = "configúrala por IP";

    /// <summary>
    /// Si la impresora de tickets está configurada de una forma que depende del router (con el router trabado no se encuentra):
    /// por nombre (<c>tcp://tickets:9100</c>, o una de Windows con su puerto TCP/IP a un nombre), con un puerto WSD, o compartida
    /// desde otra PC. Lo que se recomienda: configurarla por IP, con el puerto TCP/IP estándar (docs/arquitectura.md). Vacío si
    /// está por IP, no pasa por la red (USB) o no se sabe.
    /// </summary>
    /// <param name="configuracion">Como está en caja.json: «tcp://192.168.0.20:9100», el nombre de una impresora de Windows o vacío.</param>
    /// <param name="puertos">Lo de Windows (el puerto de cada impresora); sin él, de una impresora de Windows no se sabe.</param>
    public static string? DeLaImpresora(string? configuracion, IPuertosDeImpresoras? puertos)
    {
        if (string.IsNullOrWhiteSpace(configuracion))
            return null;
        var texto = configuracion.Trim();
        if (texto.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(texto, UriKind.Absolute, out var uri) && !RedLocal.EsIp(uri.Host) ? PorNombre(uri.Host) : null;
        if (texto.StartsWith(@"\\", StringComparison.Ordinal))
            return Compartida(texto);
        PuertoDeImpresora? puerto;
        try
        {
            puerto = puertos?.DeLaImpresora(texto);
        }
        catch (Exception)
        {
            // Sin poder leerlo, no se avisa: el aviso es una ayuda, no algo que pueda tumbar la caja.
            return null;
        }
        return puerto switch
        {
            { Tipo: TipoDePuerto.Wsd } => $"La impresora de tickets «{texto}» usa un puerto WSD: si el router se traba, Windows no la encuentra. "
                                          + "Configúrala por IP, con el puerto TCP/IP estándar (sin cambiarle el nombre).",
            { Tipo: TipoDePuerto.TcpIp, Host: { } host } when !RedLocal.EsIp(host) => PorNombre(host),
            { Tipo: TipoDePuerto.Compartida, Nombre: var nombre } => Compartida(nombre),
            _ => null,
        };
    }

    /// <summary>La IP de la impresora de tickets, si está por IP: el vigía le hace ping (solo ping). Vacía si no se sabe.</summary>
    public static IPAddress? IpDeLaImpresora(string? configuracion, IPuertosDeImpresoras? puertos)
    {
        if (string.IsNullOrWhiteSpace(configuracion))
            return null;
        var texto = configuracion.Trim();
        string? host;
        if (texto.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            host = Uri.TryCreate(texto, UriKind.Absolute, out var uri) ? uri.Host : null;
        else
        {
            try
            {
                host = texto.StartsWith(@"\\", StringComparison.Ordinal) ? null : puertos?.DeLaImpresora(texto) is { Tipo: TipoDePuerto.TcpIp } puerto ? puerto.Host : null;
            }
            catch (Exception)
            {
                host = null;
            }
        }
        return host is not null && IPAddress.TryParse(host.Trim('[', ']'), out var ip) ? ip : null;
    }

    /// <summary>
    /// Si esta caja busca a la caja principal por su nombre (el instalador sugiere el de la PC): con el router trabado no la
    /// encuentra. Si deja de contestar, la caja la busca en la red y, si la encuentra, guarda su IP. Vacío si está por IP o es
    /// esta misma PC.
    /// </summary>
    public static string? DelServicio(Uri servicio) =>
        servicio.IsLoopback || RedLocal.EsIp(servicio.Host)
            ? null
            : $"Esta caja busca a la caja principal por su nombre («{servicio.Host}»): si el router se traba, no la encuentra. Mejor por su IP; "
              + "si deja de contestar, la caja la busca en la red y guarda su IP.";

    private static string PorNombre(string host) =>
        $"La impresora de tickets está configurada por nombre («{host}»): si el router se traba, no se encuentra. Configúrala por IP.";

    private static string Compartida(string ruta)
    {
        var pc = ruta.TrimStart('\\').Split('\\')[0];
        return $"La impresora de tickets se usa compartida desde otra PC («{pc}»): si esa PC o el router fallan, no imprime. "
               + "Configúrala por IP, con el puerto TCP/IP estándar.";
    }
}
