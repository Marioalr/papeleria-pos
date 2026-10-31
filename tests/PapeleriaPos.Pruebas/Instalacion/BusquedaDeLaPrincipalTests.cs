using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Interfaz;
using ServicioDePrueba = PapeleriaPos.Pruebas.Servicio.ServicioDePrueba;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// «Buscar en la red» del asistente: el mismo protocolo que la caja (Descubrimiento) y no uno nuevo, y la caja principal cuenta solo si su
/// api/estado dice que es Papelería POS.
/// </summary>
public sealed class BusquedaDeLaPrincipalTests
{
    [Fact]
    public void La_pregunta_la_respuesta_y_los_puertos_son_los_de_la_caja()
    {
        Assert.Equal(Descubrimiento.Puerto, BusquedaDeLaPrincipal.PuertoDeBusqueda);
        Assert.Equal(Descubrimiento.Pregunta, BusquedaDeLaPrincipal.Pregunta);
        Assert.Equal(Descubrimiento.Respuesta, BusquedaDeLaPrincipal.Respuesta);
        foreach (var respuesta in new[] { "PAPELERIAPOS 5080", "PAPELERIAPOS 5180", "PAPELERIAPOS", "PAPELERIAPOS 99999", "OTRO 5080" })
            Assert.Equal(Descubrimiento.LeerRespuesta(Encoding.UTF8.GetBytes(respuesta), IPAddress.Parse("192.168.0.57")),
                BusquedaDeLaPrincipal.LeerRespuesta(Encoding.UTF8.GetBytes(respuesta), IPAddress.Parse("192.168.0.57")));
    }

    [Fact]
    public void La_difusion_de_cada_red_es_la_misma_que_la_de_la_caja()
    {
        (IPAddress, int)[] redes = [(IPAddress.Parse("192.168.0.10"), 24), (IPAddress.Parse("10.20.1.2"), 16), (IPAddress.Parse("172.16.0.9"), 32),
            (IPAddress.Parse("127.0.0.1"), 8)];

        Assert.Equal(RedLocal.Difusiones(redes).Select(ip => ip.ToString()),
            redes.Select(r => BusquedaDeLaPrincipal.Difusion(r.Item1, r.Item2)).OfType<IPAddress>().Select(ip => ip.ToString()));
    }

    /// <summary>De punta a punta por UDP, con lo que contesta el servicio (Descubrimiento.EsPregunta y ArmarRespuesta), en un puerto de esta PC.</summary>
    [Fact]
    public async Task Encuentra_al_que_contesta_como_el_servicio()
    {
        using var servicio = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var puerto = ((IPEndPoint)servicio.Client.LocalEndPoint!).Port;
        // Con su límite: si la pregunta no llegara, la prueba falla en lugar de quedarse esperando.
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var contestando = Task.Run(async () =>
        {
            var pregunta = await servicio.ReceiveAsync(limite.Token);
            if (Descubrimiento.EsPregunta(pregunta.Buffer))
                await servicio.SendAsync(Descubrimiento.ArmarRespuesta(5080), pregunta.RemoteEndPoint, limite.Token);
        });

        var encontradas = await BusquedaDeLaPrincipal.BuscarAsync(TimeSpan.FromSeconds(3), [new IPEndPoint(IPAddress.Loopback, puerto)]);

        await contestando;
        Assert.Equal([new Uri("http://127.0.0.1:5080/")], encontradas);
    }

    [Theory]
    [InlineData("192.168.1.50", "http://192.168.1.50:5080/")]
    [InlineData(" 192.168.1.50 ", "http://192.168.1.50:5080/")]
    [InlineData("192.168.1.50:5090", "http://192.168.1.50:5090/")]
    [InlineData("http://192.168.1.50:5080/", "http://192.168.1.50:5080/")]
    [InlineData("http://192.168.1.50", "http://192.168.1.50:5080/")]
    [InlineData("http://192.168.1.50:80", "http://192.168.1.50/")]
    [InlineData("PRINCIPAL", "http://principal:5080/")]
    [InlineData("", null)]
    [InlineData("la de enfrente", null)]
    [InlineData("ftp://192.168.1.50", null)]
    public void La_direccion_se_entiende_como_en_la_pantalla_de_alta(string texto, string? queda) =>
        Assert.Equal(queda, BusquedaDeLaPrincipal.Direccion(texto)?.ToString());

    [Fact]
    public async Task El_servicio_de_verdad_contesta_su_estado()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();

        // Con tiempo de sobra: la primera petición a un servicio recién arrancado, con toda la suite corriendo a la par, pasa de los 4 s
        // que se esperan en la red de la tienda (aquí se revisa que se entienda lo que contesta, no lo que tarda).
        var estado = await BusquedaDeLaPrincipal.ProbarAsync(new Uri("http://localhost/"), servicio.Fabrica.Server.CreateHandler(),
            espera: TimeSpan.FromMinutes(2));

        Assert.True(estado.Contesta, estado.Error);
        Assert.NotEmpty(estado.Sucursales);
        Assert.False(string.IsNullOrEmpty(estado.Version));
        Assert.StartsWith("localhost · ", estado.Texto);
    }

    [Fact]
    public async Task Otro_programa_en_ese_puerto_no_cuenta()
    {
        var estado = await BusquedaDeLaPrincipal.ProbarAsync(new Uri("http://192.168.1.50:5080/"),
            new Contesta("""{ "servicio": "Otra cosa", "version": "9" }"""));

        Assert.False(estado.Contesta);
        Assert.Equal("192.168.1.50: contesta, pero no es Papelería POS", estado.Texto);
    }

    [Fact]
    public async Task Si_no_contesta_lo_dice_pronto()
    {
        using var cerrado = new TcpListener(IPAddress.Loopback, 0);
        cerrado.Start();
        var puerto = ((IPEndPoint)cerrado.LocalEndpoint).Port;
        cerrado.Stop();

        var estado = await BusquedaDeLaPrincipal.ProbarAsync(new Uri($"http://127.0.0.1:{puerto}/"), espera: TimeSpan.FromSeconds(3));

        Assert.False(estado.Contesta);
        Assert.Contains("no contestó", estado.Error);
    }

    /// <summary>Contesta siempre lo mismo, como lo contestaría otro programa en ese puerto.</summary>
    private sealed class Contesta(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
