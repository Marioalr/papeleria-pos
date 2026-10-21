using System.Net;
using System.Net.Sockets;
using System.Text;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Encontrar la caja principal en la red sin exponer nada fuera de la tienda: lo que contesta el servicio por UDP (solo a la red
/// local, solo su puerto) y la prueba de que es la caja principal de una caja (con la huella de su llave, un reto y su dirección).
/// </summary>
public sealed class DescubrimientoTests
{
    private static readonly IReadOnlyList<(IPAddress, int)> Tienda = [(IPAddress.Parse("192.168.0.10"), 24)];

    [Theory]
    [InlineData("192.168.0.57", true)]
    [InlineData("192.168.0.255", true)]
    [InlineData("192.168.1.57", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("100.64.3.2", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:192.168.0.57", true)]
    [InlineData("::ffff:192.168.7.57", false)]
    [InlineData("fe80::1c2d:3e4f:5a6b:7c8d", true)]
    [InlineData("2806:2f0:9000::1", false)]
    public void Solo_la_red_de_la_tienda_es_red_local(string ip, bool local) =>
        Assert.Equal(local, RedLocal.EsDeLaRedLocal(IPAddress.Parse(ip), Tienda));

    /// <summary>Sin dirección es una llamada dentro del mismo proceso (las pruebas del servicio).</summary>
    [Fact]
    public void Sin_direccion_es_la_misma_pc() => Assert.True(RedLocal.EsDeLaRedLocal(null, Tienda));

    [Fact]
    public void La_pregunta_va_a_la_difusion_de_cada_red_de_esta_pc() =>
        Assert.Equal(["192.168.0.255", "10.20.255.255"],
            RedLocal.Difusiones([(IPAddress.Parse("192.168.0.10"), 24), (IPAddress.Parse("10.20.1.2"), 16), (IPAddress.Parse("192.168.0.11"), 24),
                (IPAddress.Parse("127.0.0.1"), 8), (IPAddress.Parse("172.16.0.9"), 32)]).Select(ip => ip.ToString()));

    [Theory]
    [InlineData("PAPELERIAPOS 5080", "http://192.168.0.57:5080/")]
    [InlineData("PAPELERIAPOS 5180", "http://192.168.0.57:5180/")]
    [InlineData("PAPELERIAPOS", null)]
    [InlineData("PAPELERIAPOS 99999", null)]
    [InlineData("OTRO 5080", null)]
    [InlineData("PAPELERIAPOS 5080 http://otro", null)]
    public void De_la_respuesta_solo_se_toma_el_puerto_y_la_ip_es_la_de_quien_contesto(string respuesta, string? servicio) =>
        Assert.Equal(servicio, Descubrimiento.LeerRespuesta(Encoding.UTF8.GetBytes(respuesta), IPAddress.Parse("192.168.0.57"))?.ToString());

    /// <summary>
    /// La prueba cambia con todo lo que la hace (la llave de la caja, la caja, el reto y la dirección): una de otra caja, de un reto
    /// viejo o hecha por la caja principal para su propia dirección (cuando otro equipo le pasa la pregunta) no sirve.
    /// </summary>
    [Fact]
    public void La_prueba_es_de_esa_llave_esa_caja_ese_reto_y_esa_direccion()
    {
        var huella = LlaveCaja.Huella(LlaveCaja.Nueva());
        var caja = Guid.NewGuid();
        var reto = Descubrimiento.NuevoReto();
        var prueba = Descubrimiento.Prueba(huella, caja, reto, "192.168.0.10:5080");

        Assert.Equal(prueba, Descubrimiento.Prueba(huella, caja, reto, "192.168.0.10:5080"));
        Assert.True(Descubrimiento.EsLaPrueba(prueba.ToLowerInvariant(), prueba));
        string[] otras =
        [
            Descubrimiento.Prueba(LlaveCaja.Huella(LlaveCaja.Nueva()), caja, reto, "192.168.0.10:5080"),
            Descubrimiento.Prueba(huella, Guid.NewGuid(), reto, "192.168.0.10:5080"),
            Descubrimiento.Prueba(huella, caja, Descubrimiento.NuevoReto(), "192.168.0.10:5080"),
            Descubrimiento.Prueba(huella, caja, reto, "192.168.0.66:5080"),
            Descubrimiento.Prueba(huella, caja, reto, "192.168.0.10:5180"),
        ];
        Assert.All(otras, otra => Assert.False(Descubrimiento.EsLaPrueba(otra, prueba)));
        Assert.False(Descubrimiento.EsLaPrueba(null, prueba));
        Assert.False(Descubrimiento.EsLaPrueba("", prueba));
    }

    [Fact]
    public void Cada_reto_es_nuevo_y_se_puede_mandar_en_la_direccion()
    {
        var retos = Enumerable.Range(0, 50).Select(_ => Descubrimiento.NuevoReto()).ToList();

        Assert.Equal(50, retos.Distinct().Count());
        Assert.All(retos, r => Assert.True(Descubrimiento.EsRetoValido(r)));
        Assert.All(retos, r => Assert.Equal(r, Uri.EscapeDataString(r)));
        Assert.False(Descubrimiento.EsRetoValido("corto"));
        Assert.False(Descubrimiento.EsRetoValido(new string('a', 129)));
        Assert.False(Descubrimiento.EsRetoValido("con espacios y cosas raras!"));
        Assert.False(Descubrimiento.EsRetoValido(null));
    }

    [Theory]
    [InlineData("192.168.0.10", 5080, "192.168.0.10:5080")]
    [InlineData("::ffff:192.168.0.10", 5080, "192.168.0.10:5080")]
    [InlineData("fe80::1", 5080, "[fe80::1]:5080")]
    public void La_direccion_con_que_se_firma_es_la_misma_de_los_dos_lados(string ip, int puerto, string direccion)
    {
        Assert.Equal(direccion, Descubrimiento.Direccion(IPAddress.Parse(ip), puerto));
        if (!ip.StartsWith("::ffff:", StringComparison.Ordinal))
            Assert.Equal(direccion, Descubrimiento.Direccion(new UriBuilder("http", ip, puerto).Uri));
    }

    [Fact]
    public void Una_direccion_por_nombre_no_se_firma() => Assert.Null(Descubrimiento.Direccion(new Uri("http://caja1:5080/")));

    /// <summary>
    /// En Windows, un «puerto inalcanzable» (ICMP) que llega a media búsqueda (de un equipo al que le llegó la pregunta, o uno falso)
    /// hace tronar la recepción con «conexión reiniciada»: se sigue escuchando y la respuesta de la caja principal se toma igual.
    /// </summary>
    [Fact]
    public async Task Una_conexion_reiniciada_no_corta_la_escucha()
    {
        var principal = new IPEndPoint(IPAddress.Parse("192.168.0.10"), Descubrimiento.Puerto);
        var llegan = new Queue<Func<UdpReceiveResult>>(
        [
            () => throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset),
            () => throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset),
            () => new UdpReceiveResult(Descubrimiento.ArmarRespuesta(5080), principal),
        ]);

        // Después de lo que llega, nada más: se espera hasta que se acaba el rato de escuchar.
        var encontrados = await Descubrimiento.EscucharAsync(async ct =>
        {
            if (llegan.TryDequeue(out var siguiente))
                return siguiente();
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal([new Uri("http://192.168.0.10:5080/")], encontrados);
    }

    /// <summary>
    /// El servicio contesta a lo más 5 preguntas por segundo a cada equipo (200 entre todos): quien pregunta sin parar no deja sin
    /// respuesta a una caja de verdad, que pregunta una vez.
    /// </summary>
    [Fact]
    public void Quien_pregunta_sin_parar_no_deja_sin_respuesta_a_las_cajas()
    {
        var limite = new PapeleriaPos.Servicio.LimiteDeRespuestas();
        var otro = IPAddress.Parse("192.168.0.66");
        var caja2 = IPAddress.Parse("192.168.0.11");

        var alOtro = Enumerable.Range(0, 50).Count(_ => limite.Contestar(otro, 100));
        var aLaCaja = limite.Contestar(caja2, 100);
        var alSiguienteSegundo = limite.Contestar(otro, 101);

        Assert.Equal(PapeleriaPos.Servicio.LimiteDeRespuestas.PorEquipo, alOtro);
        Assert.True(aLaCaja);
        Assert.True(alSiguienteSegundo);
        var muchos = Enumerable.Range(1, 250).Count(i => limite.Contestar(IPAddress.Parse($"10.0.{i / 200}.{i % 200}"), 101));
        Assert.Equal(PapeleriaPos.Servicio.LimiteDeRespuestas.EnTotal - 1, muchos);
    }

    /// <summary>Los clientes de la caja no usan proxy: la llave de la caja nunca pasa por un proxy de la red (WPAD).</summary>
    [Fact]
    public void Los_clientes_de_la_caja_no_usan_proxy()
    {
        using var manejador = RedCaja.Manejador(TimeSpan.FromSeconds(2));
        using var deLaPrueba = RedDeLaCaja.ManejadorDeLaPrueba();

        Assert.False(manejador.UseProxy);
        Assert.False(deLaPrueba.UseProxy);
        Assert.False(deLaPrueba.AllowAutoRedirect);
    }

    [Fact]
    public void La_pregunta_de_dns_es_una_pregunta_tipo_a_por_ese_nombre()
    {
        var pregunta = PruebasDeRed.PreguntaDns(0x1234, "tendawifi.com");

        Assert.Equal(
        [
            0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0,
            9, (byte)'t', (byte)'e', (byte)'n', (byte)'d', (byte)'a', (byte)'w', (byte)'i', (byte)'f', (byte)'i',
            3, (byte)'c', (byte)'o', (byte)'m', 0,
            0, 1, 0, 1,
        ], pregunta);
    }
}
