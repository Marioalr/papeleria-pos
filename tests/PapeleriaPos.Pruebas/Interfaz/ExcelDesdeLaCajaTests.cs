using System.Net.Http.Json;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Un Excel que tarda en armarse no deja la caja sin conexión (revisión del PR #134): los archivos van con su propio tope (en la
/// caja, <see cref="RedCaja.TopeDescargas"/>, dos minutos) y no con el de lo interactivo (15 s); si aun así se acaba, la caja
/// principal sigue armándolo, y eso es lo que dice la caja, sin pasar a sin conexión.
/// </summary>
public sealed class ExcelDesdeLaCajaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"excel-caja-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    /// <summary>La caja principal tarda en armar cada Excel (muchos días de tickets).</summary>
    private sealed class ExcelLento(HttpMessageHandler interno, TimeSpan espera) : DelegatingHandler(interno)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/excel", StringComparison.Ordinal))
                await Task.Delay(espera, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task Un_excel_que_tarda_no_deja_la_caja_sin_conexion()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = (await (await local.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion);
        await contingencia.LatidoAsync();
        Assert.True(contingencia.ServidorResponde);
        var lento = new ExcelLento(servicio.Fabrica.Server.CreateHandler(), TimeSpan.FromSeconds(1));
        // Lo interactivo con un tope que el Excel pasa (en la caja, 15 s); los archivos, con el suyo (en la caja, 2 min).
        HttpClient Interactivo() => new(lento, disposeHandler: false) { Timeout = TimeSpan.FromMilliseconds(300) };

        var cliente = new ClienteCaja(Interactivo(), conexion, contingencia, new HttpClient(lento, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) });
        var archivo = await cliente.ExcelVentasPorPeriodoAsync(caja.Id, dueno.UsuarioId, null, null, null);

        Assert.EndsWith(".xlsx", archivo.Nombre);
        Assert.NotEmpty(archivo.Datos);
        Assert.True(contingencia.ServidorResponde);

        // Si se le acaba su tope: la caja principal sigue armándolo, y la caja no pasa a sin conexión (antes, sí).
        var conTopeCorto = new ClienteCaja(Interactivo(), conexion, contingencia, new HttpClient(lento, disposeHandler: false) { Timeout = TimeSpan.FromMilliseconds(300) });
        var error = await Assert.ThrowsAsync<ErrorCaja>(() => conTopeCorto.ExcelVentasPorPeriodoAsync(caja.Id, dueno.UsuarioId, null, null, null));

        Assert.Equal(ClienteCaja.ExcelTarda, error.Message);
        Assert.Contains("La caja principal sigue armando el Excel", error.Message);
        Assert.True(contingencia.ServidorResponde);

        // Sin la caja principal (no contesta: no es que tarde), sí lo dice como sin conexión.
        var apagada = new ClienteCaja(Interactivo(), conexion, contingencia, new HttpClient(new SinRed()));
        var sinConexion = await Assert.ThrowsAsync<ErrorCaja>(() => apagada.ExcelVentasPorPeriodoAsync(caja.Id, dueno.UsuarioId, null, null, null));
        Assert.Equal(ClienteCaja.ExcelSinConexion, sinConexion.Message);
        Assert.False(contingencia.ServidorResponde);
    }

    /// <summary>La caja principal apagada: la conexión no se puede abrir.</summary>
    private sealed class SinRed : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "No se puede conectar.");
    }

    [Fact]
    public void La_red_de_la_caja_baja_los_archivos_con_su_propio_tope()
    {
        using var red = new RedCaja(new HttpClientHandler(), new HttpClientHandler());
        Assert.Equal(RedCaja.TopeInteractivo, red.Interactivo.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(2), red.Descargas.Timeout);
    }
}
