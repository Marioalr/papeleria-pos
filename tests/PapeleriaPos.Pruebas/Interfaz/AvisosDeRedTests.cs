using System.Net;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Lo que la caja dice de la red de la tienda (docs/arquitectura.md, «En el POS»): la franja que distingue «sin internet» de «sin
/// red local», y el aviso de la impresora de tickets (o de la caja principal) configurada por nombre, compartida o con WSD.
/// </summary>
public sealed class AvisosDeRedTests
{
    /// <summary>
    /// Cada combinación: si esta caja llega a la caja principal (buena, lenta o caída), lo que la caja principal dice del internet y
    /// de su router, si es la caja principal (su servicio en esta PC) y si la prueba simula que no hay conexión.
    /// </summary>
    [Theory]
    // Todo bien, o no se sabe (un servicio de antes, o todavía no revisa): nada.
    [InlineData(false, false, EstadoRed.Buena, true, true, null)]
    [InlineData(false, false, EstadoRed.Buena, null, null, null)]
    [InlineData(true, false, EstadoRed.Buena, true, true, null)]
    [InlineData(true, false, EstadoRed.Buena, null, null, null)]
    // Sin internet, con la red local bien: «no desconectes nada», en las dos cajas.
    [InlineData(false, false, EstadoRed.Buena, false, true, AvisosDeRed.SinInternet)]
    [InlineData(true, false, EstadoRed.Buena, false, true, AvisosDeRed.SinInternet)]
    // La caja principal sin saber de su router (no tiene, o todavía no lo revisa): no se dice «no desconectes nada».
    [InlineData(true, false, EstadoRed.Buena, false, null, AvisosDeRed.SinInternetNiRedLocal)]
    // La caja 2 llega a la caja principal (las dos en el switch) aunque el router de la principal no conteste: la red local sirve.
    [InlineData(false, false, EstadoRed.Buena, false, false, AvisosDeRed.SinInternet)]
    // La caja principal sin internet ni router (el router trabado): no se dice «no desconectes nada».
    [InlineData(true, false, EstadoRed.Buena, false, false, AvisosDeRed.SinInternetNiRedLocal)]
    // Esta caja no llega a la caja principal: «Sin red local», diga lo que haya dicho antes del internet.
    [InlineData(false, false, EstadoRed.Caida, null, null, AvisosDeRed.SinRedLocal)]
    [InlineData(false, false, EstadoRed.Caida, false, false, AvisosDeRed.SinRedLocal)]
    [InlineData(false, false, EstadoRed.Caida, true, true, AvisosDeRed.SinRedLocal)]
    // La prueba que simula que no hay conexión, en la misma PC: como la caja 2.
    [InlineData(true, true, EstadoRed.Caida, true, true, AvisosDeRed.SinRedLocal)]
    // La caja principal con su servicio sin contestar (se actualiza): no es la red.
    [InlineData(true, false, EstadoRed.Caida, false, false, AvisosDeRed.SinServicio)]
    // Lenta: lo de siempre; la de internet no se dice (la red local tampoco está bien).
    [InlineData(false, false, EstadoRed.Lenta, false, true, AvisosDeRed.Lenta)]
    [InlineData(false, false, EstadoRed.Lenta, true, true, AvisosDeRed.Lenta)]
    public void La_franja_dice_sin_internet_o_sin_red_local_segun_cada_caso(
        bool servicioEnEstaPc, bool simulando, EstadoRed estado, bool? internet, bool? router, string? dice)
    {
        var red = internet is null && router is null ? null : new RedDeLaPrincipalDto(internet, router);

        Assert.Equal(dice, AvisosDeRed.Texto(AvisosDeRed.QueDice(servicioEnEstaPc, simulando, estado, red)));
    }

    [Fact]
    public void Los_textos_son_los_que_se_pidieron()
    {
        Assert.Equal("Sin internet: la venta sigue. No desconectes nada.", AvisosDeRed.SinInternet);
        Assert.Equal("Sin red local: esta caja trabaja con su copia", AvisosDeRed.SinRedLocal);
    }

    /// <summary>Un puerto de Windows como se le pida, para la impresora que se llame así.</summary>
    private sealed class PuertosFalsos(params (string Impresora, PuertoDeImpresora? Puerto)[] puertos) : IPuertosDeImpresoras
    {
        public PuertoDeImpresora? DeLaImpresora(string nombre) =>
            puertos.FirstOrDefault(p => p.Impresora == nombre) is { Impresora: not null } p ? p.Puerto : throw new InvalidOperationException("No existe.");
    }

    private static readonly PuertosFalsos Puertos = new(
        ("POS-8360", new PuertoDeImpresora("WSD-4f1c9e2a", TipoDePuerto.Wsd)),
        ("Tickets", new PuertoDeImpresora("IP_192.168.0.20", TipoDePuerto.TcpIp, "192.168.0.20")),
        ("Tickets nombre", new PuertoDeImpresora("TICKETS", TipoDePuerto.TcpIp, "impresora-tickets")),
        ("Tickets USB", new PuertoDeImpresora("USB001", TipoDePuerto.Local)),
        ("Tickets en la otra", new PuertoDeImpresora(@"\\CAJA1\POS-8360", TipoDePuerto.Compartida)));

    [Theory]
    [InlineData("POS-8360", "La impresora de tickets «POS-8360» usa un puerto WSD: si el router se traba, Windows no la encuentra. Configúrala por IP, con el puerto TCP/IP estándar (sin cambiarle el nombre).")]
    [InlineData("Tickets nombre", "La impresora de tickets está configurada por nombre («impresora-tickets»): si el router se traba, no se encuentra. Configúrala por IP.")]
    [InlineData("tcp://tickets.local:9100", "La impresora de tickets está configurada por nombre («tickets.local»): si el router se traba, no se encuentra. Configúrala por IP.")]
    [InlineData(@"\\CAJA1\POS-8360", "La impresora de tickets se usa compartida desde otra PC («CAJA1»): si esa PC o el router fallan, no imprime. Configúrala por IP, con el puerto TCP/IP estándar.")]
    [InlineData("Tickets en la otra", "La impresora de tickets se usa compartida desde otra PC («CAJA1»): si esa PC o el router fallan, no imprime. Configúrala por IP, con el puerto TCP/IP estándar.")]
    public void La_impresora_de_tickets_por_nombre_compartida_o_con_wsd_avisa_que_se_configure_por_ip(string configuracion, string aviso) =>
        Assert.Equal(aviso, AvisosDeRed.DeLaImpresora(configuracion, Puertos));

    [Theory]
    [InlineData("tcp://192.168.0.20:9100")]
    [InlineData("Tickets")]
    [InlineData("Tickets USB")]
    [InlineData("")]
    [InlineData(null)]
    // Una que Windows no tiene (o no se puede leer): no se sabe, no se avisa (el aviso de «no salió» lo dirá si no imprime).
    [InlineData("No existe")]
    public void Por_ip_por_usb_o_sin_saber_no_avisa(string? configuracion) =>
        Assert.Null(AvisosDeRed.DeLaImpresora(configuracion, Puertos));

    /// <summary>Sin lo de Windows (el navegador, Linux) no se sabe cómo está una impresora de Windows: no avisa.</summary>
    [Fact]
    public void Sin_lo_de_windows_una_impresora_de_windows_no_avisa() =>
        Assert.Null(AvisosDeRed.DeLaImpresora("POS-8360", null));

    /// <summary>A la que está por IP el vigía le hace ping; a las demás no hay a qué.</summary>
    [Theory]
    [InlineData("tcp://192.168.0.20:9100", "192.168.0.20")]
    [InlineData("Tickets", "192.168.0.20")]
    [InlineData("Tickets nombre", null)]
    [InlineData("POS-8360", null)]
    [InlineData(@"\\CAJA1\POS-8360", null)]
    [InlineData(null, null)]
    public void La_ip_de_la_impresora_para_hacerle_ping(string? configuracion, string? ip) =>
        Assert.Equal(ip, AvisosDeRed.IpDeLaImpresora(configuracion, Puertos)?.ToString());

    [Theory]
    [InlineData("http://192.168.0.10:5080/", null)]
    [InlineData("http://localhost:5080/", null)]
    [InlineData("http://CAJA1:5080/", "Esta caja busca a la caja principal por su nombre («caja1»): si el router se traba, no la encuentra. Mejor por su IP; si deja de contestar, la caja la busca en la red y guarda su IP.")]
    public void La_caja_principal_por_nombre_tambien_avisa(string servicio, string? aviso) =>
        Assert.Equal(aviso, AvisosDeRed.DelServicio(new Uri(servicio)));
}
