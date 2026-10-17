using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La red con la caja principal en tres estados, con histéresis (entrega 5, parte 2): buena, lenta o caída según lo que tarda
/// el latido, sin alternar a cada latido. Las latencias se le dan a mano: nada depende de lo que tarde la PC.
/// </summary>
public sealed class CalidadDeLaRedTests
{
    private static readonly TimeSpan Rapido = TimeSpan.FromMilliseconds(9);
    /// <summary>Más que lo que se pide para volver de lenta (200 ms) y menos que lo lento (300 ms).</summary>
    private static readonly TimeSpan Medio = TimeSpan.FromMilliseconds(250);

    /// <summary>Un Wi-Fi regular: no es la red de milisegundos, pero tampoco la que hace esperar.</summary>
    private static readonly TimeSpan Regular = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Lento = TimeSpan.FromMilliseconds(650);

    private readonly RelojFijo _reloj = new(new DateTimeOffset(2026, 10, 17, 16, 0, 0, TimeSpan.Zero));

    private CalidadDeLaRed Nueva() => new(ReglasDeLaRed.DeLaTienda, _reloj);

    [Fact]
    public void Un_latido_lento_suelto_no_la_cambia_y_dos_seguidos_la_ponen_lenta()
    {
        var red = Nueva();

        red.Contesto(Lento);
        Assert.Equal(EstadoRed.Buena, red.Estado);
        red.Contesto(Rapido);
        red.Contesto(Lento);
        Assert.Equal(EstadoRed.Buena, red.Estado);
        Assert.True(red.Contesto(Lento));

        Assert.Equal(EstadoRed.Lenta, red.Estado);
        Assert.Equal(Lento, red.Latencia);
    }

    [Fact]
    public void De_lenta_vuelve_con_tres_buenos_seguidos_y_uno_medio_o_lento_vuelve_a_contar()
    {
        var red = Nueva();
        red.Contesto(Lento);
        red.Contesto(Lento);

        red.Contesto(Rapido);
        red.Contesto(Rapido);
        red.Contesto(Medio);
        Assert.Equal(EstadoRed.Lenta, red.Estado);
        red.Contesto(Rapido);
        red.Contesto(Rapido);
        Assert.Equal(EstadoRed.Lenta, red.Estado);
        Assert.True(red.Contesto(Rapido));

        Assert.Equal(EstadoRed.Buena, red.Estado);
    }

    /// <summary>
    /// Con un Wi-Fi regular (150 ms) un tropiezo la pone lenta (algo no llegó a tiempo), pero no se queda así: vuelve con tres
    /// latidos seguidos de menos de 200 ms, y no vuelve a bajar mientras siga igual.
    /// </summary>
    [Fact]
    public void Con_un_wifi_regular_vuelve_de_lenta_y_no_alterna()
    {
        var red = Nueva();
        red.PlazoVencido();

        red.Contesto(Regular);
        red.Contesto(Regular);
        Assert.Equal(EstadoRed.Lenta, red.Estado);
        Assert.True(red.Contesto(Regular));
        Assert.Equal(EstadoRed.Buena, red.Estado);
        for (var i = 0; i < 10; i++)
            red.Contesto(Regular);

        Assert.Equal(EstadoRed.Buena, red.Estado);
    }

    [Fact]
    public void Entre_los_dos_umbrales_no_baja()
    {
        var red = Nueva();

        for (var i = 0; i < 10; i++)
            red.Contesto(Medio);

        Assert.Equal(EstadoRed.Buena, red.Estado);
    }

    [Fact]
    public void Si_no_contesta_se_cae_y_al_contestar_bien_vuelve_de_una_vez()
    {
        var red = Nueva();

        Assert.True(red.Fallo());
        Assert.Equal(EstadoRed.Caida, red.Estado);
        Assert.Null(red.Latencia);
        Assert.True(red.Contesto(Rapido));

        Assert.Equal(EstadoRed.Buena, red.Estado);
    }

    [Fact]
    public void Si_vuelve_contestando_lento_queda_lenta()
    {
        var red = Nueva();
        red.Fallo();

        red.Contesto(Lento);

        Assert.Equal(EstadoRed.Lenta, red.Estado);
    }

    /// <summary>Si se cae otra vez a menos de dos minutos de la anterior, va y viene: al volver pasa por lenta (tres buenos).</summary>
    [Fact]
    public void Si_va_y_viene_al_volver_pasa_por_lenta()
    {
        var red = Nueva();
        red.Fallo();
        red.Contesto(Rapido);
        _reloj.Ahora += TimeSpan.FromSeconds(40);

        red.Fallo();
        red.Contesto(Rapido);
        Assert.Equal(EstadoRed.Lenta, red.Estado);
        red.Contesto(Rapido);
        red.Contesto(Rapido);
        Assert.Equal(EstadoRed.Buena, red.Estado);

        // Una caída mucho después ya no es «va y viene».
        _reloj.Ahora += TimeSpan.FromMinutes(10);
        red.Fallo();
        red.Contesto(Rapido);
        Assert.Equal(EstadoRed.Buena, red.Estado);
    }

    /// <summary>Algo que el cajero esperaba no llegó a tiempo: lenta de una vez, para no hacerlo esperar otra vez.</summary>
    [Fact]
    public void Un_plazo_vencido_la_pone_lenta_de_una_vez()
    {
        var red = Nueva();

        Assert.True(red.PlazoVencido());
        Assert.Equal(EstadoRed.Lenta, red.Estado);
        Assert.False(red.PlazoVencido());
        red.Fallo();
        Assert.False(red.PlazoVencido());
        Assert.Equal(EstadoRed.Caida, red.Estado);
    }

    /// <summary>Mientras la propia caja baja su copia completa (llena la red), un latido lento no cuenta.</summary>
    [Fact]
    public void Mientras_baja_la_copia_un_latido_lento_no_cuenta()
    {
        var red = Nueva();

        red.Contesto(Lento, ocupada: true);
        red.Contesto(Lento, ocupada: true);
        red.Contesto(Lento, ocupada: true);

        Assert.Equal(EstadoRed.Buena, red.Estado);
    }

    /// <summary>Solo si contesta o no: lo que tarde no cuenta (lo que usan las pruebas de lo demás).</summary>
    [Fact]
    public void Con_solo_si_contesta_lo_que_tarda_no_cuenta()
    {
        var red = new CalidadDeLaRed(ReglasDeLaRed.SoloSiContesta, _reloj);

        red.Contesto(TimeSpan.FromSeconds(5));
        red.Contesto(TimeSpan.FromSeconds(5));
        Assert.Equal(EstadoRed.Buena, red.Estado);
        red.Fallo();
        red.Contesto(TimeSpan.FromSeconds(5));
        Assert.Equal(EstadoRed.Buena, red.Estado);
    }

    /// <summary>La red lenta se ve donde ya se ve la conexión: el chip de arriba y la franja.</summary>
    [Fact]
    public void La_red_lenta_se_ve_en_el_chip_y_en_la_franja()
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"caja-red-{Guid.NewGuid():N}.db");
        Contingencia? contingencia = null;
        try
        {
            var conexion = new ConexionCaja { Servicio = new Uri("http://192.0.2.10:5080/"), CajaId = Guid.NewGuid() };
            contingencia = new Contingencia(new AlmacenLocal(ruta), new HttpClient(), conexion, reglas: ReglasDeLaRed.DeLaTienda);

            Assert.Equal(EstadoCaja.ChipConexion.ConectadaALaPrincipal, EstadoCaja.QueDice(contingencia, conexion));
            contingencia.MarcarLenta();

            Assert.Equal(EstadoRed.Lenta, contingencia.EstadoDeLaRed);
            Assert.False(contingencia.EnLinea);
            Assert.True(contingencia.ServidorResponde);
            Assert.Equal(EstadoCaja.ChipConexion.RedLenta, EstadoCaja.QueDice(contingencia, conexion));
            // En la PC del servicio no hay red de por medio: «Servicio aquí», como siempre.
            var aqui = new ConexionCaja { Servicio = new Uri("http://localhost:5080/") };
            Assert.Equal(EstadoCaja.ChipConexion.ServicioAqui, EstadoCaja.QueDice(contingencia, aqui));
        }
        finally
        {
            contingencia?.Cerrar(TimeSpan.FromSeconds(10));
            BasesDePrueba.Borrar(ruta);
        }
    }

    /// <summary>
    /// La caja de verdad: dos clientes (lo interactivo con tope de 15 s y lo de fondo sin tope, con plazo por llamada), con
    /// keepalive de TCP y conexiones que se renuevan; la contingencia con las reglas de la tienda, y el cliente con los plazos de
    /// la venta (800 ms leer, 2.5 s cobrar).
    /// </summary>
    [Fact]
    public void La_caja_de_verdad_lleva_dos_clientes_con_keepalive_las_reglas_de_la_tienda_y_los_plazos()
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"caja-red-{Guid.NewGuid():N}.db");
        var conexion = new ConexionCaja { Servicio = new Uri("http://192.0.2.10:5080/"), CajaId = Guid.NewGuid() };
        var servicios = new ServiceCollection().AgregarInterfazCaja(conexion, new SinImpresora(), ruta).BuildServiceProvider();
        try
        {
            var red = servicios.GetRequiredService<RedCaja>();
            var contingencia = servicios.GetRequiredService<Contingencia>();
            contingencia.Detener();
            using var alcance = servicios.CreateScope();
            var cliente = alcance.ServiceProvider.GetRequiredService<ClienteCaja>();

            Assert.Equal(RedCaja.TopeInteractivo, red.Interactivo.Timeout);
            Assert.Equal(Timeout.InfiniteTimeSpan, red.Fondo.Timeout);
            Assert.Same(ReglasDeLaRed.DeLaTienda, contingencia.ReglasDeLaRed);
            Assert.Equal((Transporte.PlazoLectura, Transporte.PlazoEscritura), (cliente.PlazoLectura, cliente.PlazoEscritura));
            Assert.Equal((800, 2500), ((int)Transporte.PlazoLectura.TotalMilliseconds, (int)Transporte.PlazoEscritura.TotalMilliseconds));

            var manejador = RedCaja.Manejador(TimeSpan.FromSeconds(2));
            Assert.NotNull(manejador.ConnectCallback);
            Assert.Equal((RedCaja.VidaDeLaConexion, RedCaja.ConexionSinUsar, TimeSpan.FromSeconds(2)),
                (manejador.PooledConnectionLifetime, manejador.PooledConnectionIdleTimeout, manejador.ConnectTimeout));
        }
        finally
        {
            servicios.GetRequiredService<Contingencia>().Cerrar(TimeSpan.FromSeconds(10));
            servicios.Dispose();
            BasesDePrueba.Borrar(ruta);
        }
    }

    /// <summary>Cada conexión, con keepalive de TCP: tras 5 s sin nada pregunta cada 2 s, y a las 3 sin respuesta la da por muerta.</summary>
    [Fact]
    public void Cada_conexion_lleva_keepalive_de_TCP()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        RedCaja.ConKeepAlive(socket);

        Assert.NotEqual(0, (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(5, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!);
            Assert.Equal(2, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!);
            Assert.Equal(3, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!);
        }
    }
}
