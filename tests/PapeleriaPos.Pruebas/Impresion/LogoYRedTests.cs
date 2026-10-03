using System.Net;
using System.Net.Sockets;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using SkiaSharp;

namespace PapeleriaPos.Pruebas.Impresion;

public class LogoYRedTests
{
    /// <summary>Una imagen de prueba: mitad izquierda negra, mitad derecha blanca, con transparencia abajo.</summary>
    private static byte[] Imagen(int ancho, int alto)
    {
        using var mapa = new SKBitmap(ancho, alto);
        for (var y = 0; y < alto; y++)
            for (var x = 0; x < ancho; x++)
                mapa.SetPixel(x, y, y >= alto * 3 / 4 ? SKColors.Transparent : x < ancho / 2 ? SKColors.Black : SKColors.White);
        using var imagen = SKImage.FromBitmap(mapa);
        return imagen.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static bool Negro(LogoTicketDto logo, int x, int y)
    {
        var bits = Convert.FromBase64String(logo.BitsBase64);
        return (bits[y * ((logo.Ancho + 7) / 8) + x / 8] & (0x80 >> (x % 8))) != 0;
    }

    [Fact]
    public void El_logo_se_reduce_al_ancho_del_papel_sin_deformarse()
    {
        var logo = LogoTicket.DesdeImagen(Imagen(1200, 300));

        Assert.Equal(LogoTicket.AnchoMaximo, logo.Ancho);
        Assert.Equal(96, logo.Alto);
        Assert.Equal((logo.Ancho + 7) / 8 * logo.Alto, Convert.FromBase64String(logo.BitsBase64).Length);
    }

    [Fact]
    public void Negro_queda_negro_y_blanco_o_transparente_queda_blanco()
    {
        var logo = LogoTicket.DesdeImagen(Imagen(200, 100));

        Assert.True(Negro(logo, 10, 10));
        Assert.False(Negro(logo, 190, 10));
        Assert.False(Negro(logo, 10, 95)); // transparente
    }

    [Fact]
    public void Un_logo_chico_no_se_agranda()
    {
        var logo = LogoTicket.DesdeImagen(Imagen(120, 40));

        Assert.Equal((120, 40), (logo.Ancho, logo.Alto));
    }

    [Fact]
    public void Un_archivo_que_no_es_imagen_se_rechaza()
    {
        Assert.Throws<ArgumentException>(() => LogoTicket.DesdeImagen("no soy una imagen"u8.ToArray()));
    }

    [Fact]
    public void La_vista_previa_es_un_png()
    {
        var png = LogoTicket.ComoPng(LogoTicket.DesdeImagen(Imagen(200, 100)));

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
    }

    [Fact]
    public void El_ticket_con_logo_lo_manda_como_imagen_raster()
    {
        var logo = LogoTicket.DesdeImagen(Imagen(200, 100));
        var bytes = TicketEscPos.Generar(Ticket(logo), abrirCajon: false);
        var bytesPorRenglon = (logo.Ancho + 7) / 8;

        byte[] encabezado = [0x1D, (byte)'v', (byte)'0', 0, (byte)bytesPorRenglon, 0, (byte)logo.Alto, 0];
        var inicio = IndiceDe(bytes, encabezado);
        Assert.True(inicio > 0);
        Assert.Equal(Convert.FromBase64String(logo.BitsBase64), bytes[(inicio + 8)..(inicio + 8 + bytesPorRenglon * logo.Alto)]);
        Assert.Contains("[logo 200×100]", TicketTexto.Generar(Ticket(logo)));
    }

    [Fact]
    public async Task La_impresora_de_red_recibe_los_comandos_por_tcp()
    {
        using var escucha = new TcpListener(IPAddress.Loopback, 0);
        escucha.Start();
        var puerto = ((IPEndPoint)escucha.LocalEndpoint).Port;
        var recibido = Task.Run(async () =>
        {
            using var cliente = await escucha.AcceptTcpClientAsync();
            using var memoria = new MemoryStream();
            await cliente.GetStream().CopyToAsync(memoria);
            return memoria.ToArray();
        });

        var impresora = (ImpresoraRed)Impresoras.Crear($"tcp://127.0.0.1:{puerto}");
        await impresora.ImprimirAsync(Ticket(null), abrirCajon: true);

        Assert.Equal(TicketEscPos.Generar(Ticket(null), abrirCajon: true), await recibido);
    }

    [Fact]
    public async Task Si_la_impresora_no_responde_se_avisa()
    {
        using var escucha = new TcpListener(IPAddress.Loopback, 0);
        escucha.Start();
        var puerto = ((IPEndPoint)escucha.LocalEndpoint).Port;
        escucha.Stop(); // nadie escucha en ese puerto

        var error = await Assert.ThrowsAsync<IOException>(() => new ImpresoraRed("127.0.0.1", puerto).ImprimirAsync(Ticket(null), false));
        Assert.Contains("No responde la impresora", error.Message);
    }

    [Fact]
    public void Sin_configuracion_no_hay_impresora_y_un_nombre_es_de_windows()
    {
        Assert.True(Impresoras.Crear("").MuestraVistaPrevia);
        Assert.Throws<ArgumentException>(() => Impresoras.Crear("Tickets"));
        Assert.IsType<SinImpresora>(Impresoras.Crear("Tickets", _ => new SinImpresora()));
    }

    [Fact]
    public void La_configuracion_limita_renglones_a_48_caracteres()
    {
        var c = new ConfiguracionTicket();

        c.CambiarTextos(["  Papelería  ", "", "Calle 1"], ["Gracias"]);
        Assert.Equal(["Papelería", "Calle 1"], c.RenglonesEncabezado);
        Assert.Throws<ArgumentException>(() => c.CambiarTextos([new string('x', 49)], []));
        Assert.Throws<ArgumentException>(() => c.CambiarLogo(10, 2, new byte[3]));
    }

    private static TicketDto Ticket(LogoTicketDto? logo) => new(
        "A-000001", new DateTime(2026, 10, 3, 18, 0, 0), "Caja 1", "Ana",
        [new LineaTicketDto("1", "Goma", 500, 500)], 1, 500, 69,
        [new CobroTicketDto(FormaPago.Efectivo, 500, 500, 0, null)], ["Papelería"], ["Gracias"], logo);

    private static int IndiceDe(byte[] datos, byte[] buscado)
    {
        for (var i = 0; i <= datos.Length - buscado.Length; i++)
        {
            if (datos.AsSpan(i, buscado.Length).SequenceEqual(buscado))
                return i;
        }
        return -1;
    }
}
