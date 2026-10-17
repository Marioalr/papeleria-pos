using System.Net;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La bitácora de red (la red de la tienda en el POS): el vigía anota solo lo que cambia (se perdió y volvió el internet, la caja
/// principal, el router, un equipo; la IP, el Wi-Fi, la red buena, lenta o caída) y un resumen cada 15 minutos, sin llenarse aunque
/// la falla dure horas; y de ahí sale el resumen de Configuración › Acerca de. Sin red de verdad y con el reloj a mano.
/// </summary>
public sealed class BitacoraDeRedTests : IDisposable
{
    private readonly RelojDeLaRed _reloj = RelojDeLaRed.Nuevo();
    private readonly CarpetaDePrueba _carpeta = new();
    private readonly EquipoFalso _equipo = new();
    private readonly PruebasFalsas _pruebas = new();

    public void Dispose() => _carpeta.Dispose();

    private VigiaDeLaRed Vigia(OpcionesDelVigia? opciones = null, string nombre = "red-caja")
    {
        var vigia = new VigiaDeLaRed(new BitacoraDeRed(_carpeta.Ruta, nombre, _reloj), _equipo, _pruebas,
            opciones ?? new OpcionesDelVigia { Quien = "esta caja", Encendida = () => TimeSpan.FromMinutes(5) }, _reloj);
        vigia.Empezar();
        return vigia;
    }

    /// <summary>Una vuelta del vigía cada 30 s (como en la caja de verdad), cuantas se pidan.</summary>
    private async Task VueltasAsync(VigiaDeLaRed vigia, int cuantas)
    {
        for (var i = 0; i < cuantas; i++)
        {
            await vigia.RevisarAsync();
            _reloj.Avanzar(TimeSpan.FromSeconds(30));
        }
    }

    private static OpcionesDelVigia DeLaPrincipal => new()
    {
        Quien = "la caja principal",
        Internet = OpcionesDelVigia.InternetPorOmision,
        Encendida = () => TimeSpan.FromMinutes(5),
    };

    [Fact]
    public async Task Empieza_con_la_hora_en_que_arranco_la_pc_y_como_esta_conectada()
    {
        var vigia = Vigia();

        await vigia.RevisarAsync();

        Assert.Equal(
        [
            "Empieza la bitácora de red de esta caja (la PC arrancó a las 13:55:00 del 17/10).",
            "Esta PC: Wi-Fi «Tienda» (señal 80 %), IP 192.168.0.11/24 por DHCP, router 192.168.0.1 (MAC 50:2b:73:aa:bb:cc).",
        ], _carpeta.Lineas());
    }

    /// <summary>
    /// Doce horas sin internet (como las caídas de Megacable), revisando cada 30 s: una línea cuando se pierde y otra cuando vuelve,
    /// con cuánto duró; no una por revisión. Y un resumen cada 15 minutos.
    /// </summary>
    [Fact]
    public async Task Doce_horas_sin_internet_son_dos_lineas_y_los_resumenes()
    {
        var vigia = Vigia(DeLaPrincipal, "red-servicio");
        await VueltasAsync(vigia, 2);
        _pruebas.SinInternet();

        await VueltasAsync(vigia, 12 * 120);
        _pruebas.Conectar = (_, _) => TimeSpan.FromMilliseconds(18);
        await VueltasAsync(vigia, 1);

        var lineas = _carpeta.Lineas("red-servicio");
        Assert.Single(lineas, l => l.StartsWith("Se perdió «internet»: 1.1.1.1:443 y 8.8.8.8:443 no contestan en 2 s; el router sí contesta.", StringComparison.Ordinal));
        // Se da por perdido en la segunda revisión sin respuesta (14:01:30) y vuelve en la primera que contesta.
        Assert.Single(lineas, l => l.StartsWith("Volvió «internet» tras 11 h 59 min (se perdió a las 14:01:30):", StringComparison.Ordinal));
        Assert.Equal(48, lineas.Count(l => l.StartsWith("Resumen de 15 min:", StringComparison.Ordinal)));
        Assert.Equal(2 + 2 + 48, lineas.Count);
        Assert.True(vigia.Internet);
    }

    /// <summary>
    /// La caja principal arranca con el router trabado (después de un apagón, sin no-break): el router no le dio dirección y tiene
    /// una 169.254, sin puerta de enlace. El router cuenta como perdido de una vez (aunque nunca se le vio), así que la franja no
    /// dice «no desconectes nada» con la red local caída; vuelve cuando el router le da dirección y contesta.
    /// </summary>
    [Fact]
    public async Task La_caja_principal_que_arranca_sin_router_no_dice_no_desconectes_nada()
    {
        _equipo.Foto = EquipoFalso.SinDireccionDelRouter;
        _pruebas.SinRed();
        var vigia = Vigia(DeLaPrincipal, "red-servicio");

        await VueltasAsync(vigia, 3);

        Assert.Equal(new RedDeLaPrincipalDto(false, false), vigia.Estado with { DesdeUtc = null });
        Assert.Equal(AvisosDeRed.Franja.SinInternetNiRedLocal, AvisosDeRed.QueDice(servicioEnEstaPc: true, simulando: false, EstadoRed.Buena, vigia.Estado));
        Assert.Contains("Se perdió «el router»: esta PC no tiene router: el router no le dio dirección (169.254).", _carpeta.Lineas("red-servicio"));

        _equipo.Foto = new FotoDeLaRed("Ethernet", TipoDeConexion.Cable, IPAddress.Parse("192.168.0.10"), 24, EquipoFalso.RouterDeLaTienda, true);
        _pruebas.Ping = _ => TimeSpan.FromMilliseconds(2);
        await VueltasAsync(vigia, 1);

        Assert.True(vigia.Router);
        Assert.Contains(_carpeta.Lineas("red-servicio"), l => l.StartsWith("Volvió «el router» tras 1 min 30 s", StringComparison.Ordinal));
    }

    /// <summary>Un ping perdido no es una caída: el router se da por perdido con dos revisiones seguidas sin respuesta, y vuelve con la primera.</summary>
    [Fact]
    public async Task El_router_se_pierde_con_dos_revisiones_seguidas_y_vuelve_con_una()
    {
        var vigia = Vigia();
        await VueltasAsync(vigia, 1);

        _pruebas.Ping = _ => null;
        _pruebas.Conectar = (_, _) => null;
        await VueltasAsync(vigia, 1);
        Assert.True(vigia.Router);
        _pruebas.Ping = _ => TimeSpan.FromMilliseconds(2);
        await VueltasAsync(vigia, 1);
        _pruebas.Ping = _ => null;
        await VueltasAsync(vigia, 2);
        Assert.False(vigia.Router);
        await VueltasAsync(vigia, 10);
        _pruebas.Ping = _ => TimeSpan.FromMilliseconds(2);
        await VueltasAsync(vigia, 1);

        var lineas = _carpeta.Lineas();
        Assert.Equal(["Se perdió «el router»: 192.168.0.1 no contesta el ping ni su página (puerto 80) en 1 s."],
            lineas.Where(l => l.StartsWith("Se perdió", StringComparison.Ordinal)));
        Assert.Equal(["Volvió «el router» tras 5 min 30 s (se perdió a las 14:02:00): 192.168.0.1 contesta en 2 ms."],
            lineas.Where(l => l.StartsWith("Volvió", StringComparison.Ordinal)));
        Assert.True(vigia.Router);
    }

    /// <summary>Si el router no contesta el ping (algunos lo apagan) pero sí su página, contesta: no es una caída.</summary>
    [Fact]
    public async Task Un_router_sin_ping_pero_con_su_pagina_contesta()
    {
        var vigia = Vigia();
        _pruebas.Ping = _ => null;

        await VueltasAsync(vigia, 5);

        Assert.True(vigia.Router);
        Assert.DoesNotContain(_carpeta.Lineas(), l => l.StartsWith("Se perdió", StringComparison.Ordinal));
    }

    /// <summary>El DNS del router trabado se anota (lo que docs/notas.md pide ver: si no contesta ni su propio nombre, está trabado).</summary>
    [Fact]
    public async Task El_dns_del_router_trabado_se_anota_una_vez()
    {
        var vigia = Vigia();
        await VueltasAsync(vigia, 1);
        _pruebas.Dns = (_, _) => null;

        await VueltasAsync(vigia, 5);
        _pruebas.Dns = (_, _) => TimeSpan.FromMilliseconds(3);
        await VueltasAsync(vigia, 5);

        var lineas = _carpeta.Lineas();
        Assert.Single(lineas, l => l == "El DNS del router no contesta («tendawifi.com» en 1 s): está trabado o se reinicia.");
        Assert.Single(lineas, l => l == "El DNS del router volvió a contestar.");
    }

    /// <summary>
    /// La caja: la red con la caja principal pasa de buena a lenta, se cae y vuelve; cada cambio una vez, con los últimos latidos, y
    /// los latidos de en medio solo en el resumen.
    /// </summary>
    [Fact]
    public void Los_latidos_anotan_cuando_la_red_cambia_de_buena_a_lenta_o_caida()
    {
        var vigia = Vigia();
        var principal = new Uri("http://192.168.0.10:5080/");
        void Latidos(int cuantos, TimeSpan? tarda, EstadoRed estado)
        {
            for (var i = 0; i < cuantos; i++)
            {
                vigia.Latido(tarda, estado, principal);
                _reloj.Avanzar(TimeSpan.FromSeconds(5));
            }
        }

        Latidos(10, TimeSpan.FromMilliseconds(9), EstadoRed.Buena);
        Latidos(1, TimeSpan.FromMilliseconds(340), EstadoRed.Buena);
        Latidos(5, TimeSpan.FromMilliseconds(410), EstadoRed.Lenta);
        Latidos(24, null, EstadoRed.Caida);
        Latidos(1, TimeSpan.FromMilliseconds(12), EstadoRed.Buena);
        Latidos(20, TimeSpan.FromMilliseconds(8), EstadoRed.Buena);

        Assert.Equal(
        [
            "Red con la caja principal: buena → lenta (últimos latidos: 9 ms, 340 ms y 410 ms).",
            "Se perdió «la caja principal»: 192.168.0.10:5080 no contesta; antes, la red lenta (últimos latidos: 410 ms, 410 ms y 410 ms).",
            "Volvió «la caja principal» tras 2 min (se perdió a las 14:01:20): 192.168.0.10:5080 contesta en 12 ms; la red, buena.",
        ], _carpeta.Lineas().Skip(1));
    }

    /// <summary>
    /// Lo que la caja principal dice del internet se anota en la bitácora de la caja cuando cambia (solo cuando la caja principal
    /// contesta: sin ella, no se sabe).
    /// </summary>
    [Fact]
    public void Lo_que_dice_la_principal_del_internet_se_anota_cuando_cambia()
    {
        var vigia = Vigia();

        vigia.InternetDeLaPrincipal(true);
        vigia.InternetDeLaPrincipal(null);
        vigia.InternetDeLaPrincipal(false);
        _reloj.Avanzar(TimeSpan.FromMinutes(3));
        vigia.InternetDeLaPrincipal(false);
        vigia.InternetDeLaPrincipal(null);
        vigia.InternetDeLaPrincipal(true);

        Assert.Equal(
        [
            "Se perdió «internet»: lo dice la caja principal.",
            "Volvió «internet» tras 3 min (se perdió a las 14:00:00): lo dice la caja principal.",
        ], _carpeta.Lineas().Skip(1));
    }

    /// <summary>Lo que docs/notas.md pide de la tarjeta: si cambia la IP (o se queda con una 169.254), el router, su MAC o el Wi-Fi.</summary>
    [Fact]
    public async Task Anota_cuando_cambia_la_ip_el_router_su_mac_o_el_wifi()
    {
        var vigia = Vigia();
        await VueltasAsync(vigia, 1);
        var antes = _equipo.Foto;

        _equipo.Foto = antes with { Senal = 70 };
        await VueltasAsync(vigia, 1);
        _equipo.Foto = antes with { Senal = 40 };
        await VueltasAsync(vigia, 1);
        _equipo.Foto = antes with { Senal = 35 };
        await VueltasAsync(vigia, 1);
        _equipo.Foto = antes with { Ip = IPAddress.Parse("169.254.10.20"), Prefijo = 16, Senal = 35 };
        await VueltasAsync(vigia, 1);
        _equipo.Foto = antes with { Ip = IPAddress.Parse("192.168.0.57"), Senal = 35 };
        await VueltasAsync(vigia, 1);
        _equipo.Foto = antes with { Ip = IPAddress.Parse("192.168.0.57"), Senal = 35, MacDelRouter = "c8:3a:35:00:11:22" };
        await VueltasAsync(vigia, 1);
        _equipo.Foto = new FotoDeLaRed("Ethernet", TipoDeConexion.Cable, IPAddress.Parse("192.168.0.11"), 24, EquipoFalso.RouterDeLaTienda, false);
        await VueltasAsync(vigia, 1);

        Assert.Equal(
        [
            "La señal del Wi-Fi bajó a 40 % (regular).",
            "Esta PC cambió de IP: IP 192.168.0.11/24 por DHCP → IP 169.254.10.20/16 (puesta por Windows: nadie le dio dirección).",
            "Esta PC no tiene una IP del router (169.254.x.x): el router no le dio dirección.",
            "Esta PC cambió de IP: IP 169.254.10.20/16 (puesta por Windows: nadie le dio dirección) → IP 192.168.0.57/24 por DHCP.",
            "El router 192.168.0.1 cambió de MAC: 50:2b:73:aa:bb:cc → c8:3a:35:00:11:22 (otro aparato contesta con su IP).",
            "Esta PC cambió de conexión: Wi-Fi «Tienda» (señal 35 %) → cable (Ethernet).",
            "Esta PC cambió de IP: IP 192.168.0.57/24 por DHCP → IP 192.168.0.11/24 fija.",
        ], _carpeta.Lineas().Skip(2));
    }

    /// <summary>A la impresora de tickets solo se le hace ping (una conexión al 9100 le estorbaría a eleventa), y se anota si deja de contestar.</summary>
    [Fact]
    public async Task A_la_impresora_de_tickets_solo_se_le_hace_ping()
    {
        var impresora = IPAddress.Parse("192.168.0.20");
        var vigia = Vigia(new OpcionesDelVigia { Equipos = [("la impresora de tickets", impresora)] });
        await VueltasAsync(vigia, 1);

        _pruebas.Ping = ip => ip.Equals(impresora) ? null : TimeSpan.FromMilliseconds(2);
        await VueltasAsync(vigia, 4);
        _pruebas.Ping = _ => TimeSpan.FromMilliseconds(2);
        await VueltasAsync(vigia, 1);

        Assert.DoesNotContain(_pruebas.Hechas, h => h.Contains("192.168.0.20:", StringComparison.Ordinal));
        var lineas = _carpeta.Lineas();
        Assert.Contains("Se perdió «la impresora de tickets»: 192.168.0.20 no contesta el ping en 1 s.", lineas);
        Assert.Contains(lineas, l => l.StartsWith("Volvió «la impresora de tickets» tras 1 min 30 s", StringComparison.Ordinal));
    }

    /// <summary>
    /// Un equipo que no ha contestado el ping desde que empezó la bitácora (el firewall de Windows no lo deja si no se le permite)
    /// no cuenta como caído: se dice una vez. Si un día contesta, desde ahí sí cuenta.
    /// </summary>
    [Fact]
    public async Task Un_equipo_que_nunca_contesto_el_ping_no_cuenta_como_caido()
    {
        var otraCaja = IPAddress.Parse("192.168.0.10");
        var vigia = Vigia(new OpcionesDelVigia { Equipos = [("la PC de eleventa", otraCaja)] });
        _pruebas.Ping = ip => ip.Equals(otraCaja) ? null : TimeSpan.FromMilliseconds(2);

        await VueltasAsync(vigia, 20);
        _pruebas.Ping = _ => TimeSpan.FromMilliseconds(2);
        await VueltasAsync(vigia, 1);
        _pruebas.Ping = ip => ip.Equals(otraCaja) ? null : TimeSpan.FromMilliseconds(2);
        await VueltasAsync(vigia, 2);

        Assert.Equal(
        [
            "«la PC de eleventa» (192.168.0.10) no contesta el ping desde que empezó la bitácora (su firewall puede no dejarlo): no se cuenta como caída.",
            "«la PC de eleventa» (192.168.0.10) ya contesta el ping (2 ms).",
            "Se perdió «la PC de eleventa»: 192.168.0.10 no contesta el ping en 1 s.",
        ], _carpeta.Lineas().Skip(2));
    }

    /// <summary>Lo configurado por nombre o con WSD queda en la bitácora al empezar, para quien la revise después de una caída.</summary>
    [Fact]
    public void Lo_configurado_por_nombre_se_anota_al_empezar()
    {
        var vigia = new VigiaDeLaRed(new BitacoraDeRed(_carpeta.Ruta, "red-caja", _reloj), _equipo, _pruebas,
            new OpcionesDelVigia { Quien = "esta caja", AlEmpezar = () => ["La impresora de tickets usa un puerto WSD."] }, _reloj);

        vigia.Empezar();

        Assert.Equal("La impresora de tickets usa un puerto WSD.", _carpeta.Lineas()[1]);
    }

    /// <summary>Una tormenta de tráfico (mucha difusión de golpe) se anota al empezar y al calmarse, no en cada revisión.</summary>
    [Fact]
    public async Task Una_tormenta_de_difusion_se_anota_al_empezar_y_al_calmarse()
    {
        var vigia = Vigia();
        await VueltasAsync(vigia, 1);

        for (var i = 1; i <= 4; i++)
        {
            _equipo.Foto = _equipo.Foto with { Difusion = _equipo.Foto.Difusion + 30 * 5000 };
            await VueltasAsync(vigia, 1);
        }
        for (var i = 1; i <= 4; i++)
        {
            _equipo.Foto = _equipo.Foto with { Difusion = _equipo.Foto.Difusion + 30 * 10 };
            await VueltasAsync(vigia, 1);
        }

        var lineas = _carpeta.Lineas();
        Assert.Single(lineas, l => l == "Llega mucha difusión a esta PC: 5,000 paquetes por segundo (puede ser una tormenta de tráfico).");
        Assert.Single(lineas, l => l == "La difusión volvió a lo normal: 10 paquetes por segundo.");
    }

    [Fact]
    public async Task El_resumen_de_cada_15_minutos_dice_lo_que_tardaron_los_latidos_y_el_router()
    {
        var vigia = Vigia();
        await vigia.RevisarAsync();
        for (var i = 0; i < 30; i++)
        {
            vigia.Latido(TimeSpan.FromMilliseconds(i < 29 ? 6 : 40), EstadoRed.Buena, new Uri("http://192.168.0.10:5080/"));
            _reloj.Avanzar(TimeSpan.FromSeconds(30));
            _equipo.Foto = _equipo.Foto with { Difusion = _equipo.Foto.Difusion + 300, Descartados = _equipo.Foto.Descartados + 1 };
            await vigia.RevisarAsync();
        }

        Assert.Equal("Resumen de 15 min: la caja principal en 6 ms a 40 ms (la mitad, 6 ms) (30 latidos, ninguno sin respuesta); "
                     + "el router en 3 ms (31 de 31 contestaron); señal del Wi-Fi 80 %; difusión 10 por segundo, 30 descartados.",
            _carpeta.Lineas().Single(l => l.StartsWith("Resumen", StringComparison.Ordinal)));
    }

    /// <summary>
    /// El resumen de Configuración › Acerca de sale de los archivos, también de los días anteriores: cada caída con desde y hasta. Lo
    /// que seguía caído cuando se cerró la caja se da por terminado en su última línea (no se vio volver).
    /// </summary>
    [Fact]
    public void Las_caidas_se_leen_de_los_archivos_al_abrir_otra_vez()
    {
        File.WriteAllLines(Path.Combine(_carpeta.Ruta, "red-caja-2026-10-16.log"),
        [
            "2026-10-16 09:00:00  Empieza la bitácora de red de esta caja.",
            "2026-10-16 12:00:00  Se perdió «internet»: lo dice la caja principal.",
            "2026-10-16 12:00:05  Se perdió «la caja principal»: 192.168.0.10:5080 no contesta.",
            "2026-10-16 12:30:05  Volvió «la caja principal» tras 30 min (se perdió a las 12:00:05): contesta en 9 ms.",
            "2026-10-16 21:00:00  Resumen de 15 min: sin internet (según la caja principal).",
        ]);
        File.WriteAllLines(Path.Combine(_carpeta.Ruta, "red-caja-2026-10-17.log"),
        [
            "2026-10-17 08:00:00  Empieza la bitácora de red de esta caja.",
            "2026-10-17 08:10:00  Se perdió «el router»: 192.168.0.1 no contesta.",
            "2026-10-17 08:12:00  Volvió «el router» tras 2 min (se perdió a las 08:10:00): contesta en 2 ms.",
            "2026-10-17 08:13:00  Se perdió «la impresora de tickets»: 192.168.0.20 no contesta el ping en 1 s.",
        ]);
        var bitacora = new BitacoraDeRed(_carpeta.Ruta, "red-caja", _reloj);

        bitacora.Empezar("esta caja");

        Assert.Equal(
        [
            new CaidaDeRed("la impresora de tickets", new DateTime(2026, 10, 17, 8, 13, 0), new DateTime(2026, 10, 17, 8, 13, 0), FinAproximado: true),
            new CaidaDeRed("el router", new DateTime(2026, 10, 17, 8, 10, 0), new DateTime(2026, 10, 17, 8, 12, 0)),
            new CaidaDeRed("la caja principal", new DateTime(2026, 10, 16, 12, 0, 5), new DateTime(2026, 10, 16, 12, 30, 5)),
            new CaidaDeRed("internet", new DateTime(2026, 10, 16, 12, 0, 0), new DateTime(2026, 10, 16, 21, 0, 0), FinAproximado: true),
        ], bitacora.Caidas());
        Assert.False(bitacora.Perdido("la impresora de tickets"));
    }

    /// <summary>Se guardan 10 días: al abrir, se quitan los archivos de antes (y no se tocan los demás archivos de la carpeta).</summary>
    [Fact]
    public void Se_guardan_diez_dias()
    {
        foreach (var dia in new[] { "2026-10-01", "2026-10-07", "2026-10-08", "2026-10-16" })
            File.WriteAllText(Path.Combine(_carpeta.Ruta, $"red-caja-{dia}.log"), "");
        File.WriteAllText(Path.Combine(_carpeta.Ruta, "caja-2026-10-01.log"), "");

        new BitacoraDeRed(_carpeta.Ruta, "red-caja", _reloj).Empezar("esta caja");

        Assert.Equal(["caja-2026-10-01.log", "red-caja-2026-10-08.log", "red-caja-2026-10-16.log", "red-caja-2026-10-17.log"],
            Directory.EnumerateFiles(_carpeta.Ruta).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>Lo que se pierde y vuelve en vivo también entra al resumen, con lo que sigue caído al final.</summary>
    [Fact]
    public void El_resumen_tiene_lo_de_ahora()
    {
        var bitacora = new BitacoraDeRed(_carpeta.Ruta, "red-servicio", _reloj);
        bitacora.Empezar("la caja principal");

        Assert.True(bitacora.SePerdio("internet", "no contesta."));
        Assert.False(bitacora.SePerdio("internet", "no contesta."));
        _reloj.Avanzar(TimeSpan.FromMinutes(7));
        Assert.True(bitacora.Volvio("internet", "contesta."));
        Assert.False(bitacora.Volvio("internet", "contesta."));
        bitacora.SePerdio("el router", "no contesta.");

        Assert.Equal(
        [
            new CaidaDeRed("el router", new DateTime(2026, 10, 17, 14, 7, 0), null),
            new CaidaDeRed("internet", new DateTime(2026, 10, 17, 14, 0, 0), new DateTime(2026, 10, 17, 14, 7, 0)),
        ], bitacora.Caidas());
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(60, "1 min")]
    [InlineData(1048, "17 min 28 s")]
    [InlineData(7500, "2 h 5 min")]
    [InlineData(43200, "12 h")]
    [InlineData(273600, "3 d 4 h")]
    public void Las_duraciones_se_dicen_redondeadas(int segundos, string texto) =>
        Assert.Equal(texto, Textos.Duracion(TimeSpan.FromSeconds(segundos)));
}
