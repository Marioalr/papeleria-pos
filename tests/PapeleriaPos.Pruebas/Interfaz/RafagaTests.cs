using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La prueba de la ráfaga (docs/propuesta-interfaz.md, «Cómo se mide»; entrega 5, parte 2): 20 códigos como los teclea el
/// lector (2 ms por carácter, 30 ms entre códigos) con la red lenta y con pérdidas, mientras sube la versión del catálogo y la
/// otra caja cobra (la caja se pone al día por su red de fondo, a la vez). Tienen que quedar las 20 líneas en orden, sin
/// códigos cortados, y sin ninguna petición a la red de la venta durante la ráfaga: eso se cuenta en la red de prueba, no se
/// mide con el reloj, así que no falla al azar.
/// </summary>
public sealed class RafagaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-rafaga-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Veinte_codigos_con_la_red_lenta_y_con_perdidas_dejan_las_20_lineas_en_orden_sin_ir_a_la_red()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, fondoAparte: true);
        v.Contingencia.IniciarAvisos();
        await VentaConCopia.EsperarAsync(() => v.Fondo.Cuantas($"/api/cajas/{v.Caja.Id}/avisos") > 0, "la conexión del aviso");
        // 20 productos distintos de la copia, por pieza, con el código de su pieza; otros dos para lo que pasa en la otra caja.
        var deLaCopia = v.DeLaCopia().DistinctBy(p => p.Descripcion).ToList();
        var rafaga = deLaCopia.Take(20).ToList();
        var (conPrecioNuevo, vendidoEnLaOtra) = (deLaCopia[20], deLaCopia.Skip(21).First(p => p.LlevaInventario));
        var directo = servicio.Fabrica.CreateClient();
        var dueno = await Leer<SesionDto>(await directo.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;

        // La red, lenta y con pérdidas: la de la venta y la de fondo.
        foreach (var red in new[] { v.Red, v.Fondo })
            (red.Retraso, red.Perdidas) = (TimeSpan.FromMilliseconds(300), 0.3);
        var antes = v.Red.Pedidas.Count;

        // Mientras pasa la ráfaga, en la caja principal: un precio nuevo (sube la versión del catálogo) y la otra caja cobra
        // (sube la de las existencias). La caja se entera por el aviso y se pone al día de fondo, a la vez.
        var enLaOtraCaja = Task.Run(async () =>
        {
            await Task.Delay(150);
            Assert.True((await directo.PostAsJsonAsync("api/catalogo/precio-menudeo",
                new SolicitudPrecioMenudeo(dueno.UsuarioId, v.Caja.SucursalId, conPrecioNuevo.VarianteId, conPrecioNuevo.Precios[0].PrecioCentavos + 500)))
                .IsSuccessStatusCode);
            await Task.Delay(150);
            await Leer<TicketDto>(await directo.PostAsJsonAsync("api/ventas", new SolicitudCobro(v.Caja.Id, turno.Id, v.Cajero.UsuarioId,
                [new LineaSolicitada(vendidoEnLaOtra.VarianteId, vendidoEnLaOtra.Presentacion.Id, 2000)], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)])));
        });

        // El lector: cada código, 2 ms por carácter y su Enter; 30 ms entre uno y otro. No espera a la pantalla.
        var escaneos = new List<Task>();
        var hora = 0.0;
        foreach (var producto in rafaga)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(2 * producto.Codigo!.Length));
            hora += 2 * producto.Codigo.Length;
            var (codigo, suHora) = (producto.Codigo, hora);
            escaneos.Add(v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escaneado(codigo, suHora)));
            await Task.Delay(30);
            hora += 30;
        }
        await Task.WhenAll(escaneos).WaitAsync(TimeSpan.FromSeconds(30));
        var durante = v.Red.Pedidas.Skip(antes).ToList();
        await enLaOtraCaja;

        // Las 20 líneas, en el orden del lector, cada una con su producto completo (un código cortado sería «No existe»).
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(20, renglones.Count);
        for (var i = 0; i < 20; i++)
            Assert.StartsWith($"1 {rafaga[i].Descripcion}", renglones[i]);
        var texto = await v.Pantalla.TextoAsync();
        Assert.DoesNotContain("No existe", texto);
        Assert.DoesNotContain("Buscando", texto);
        // Ninguna petición a la red de la venta durante la ráfaga. (La única que la pantalla hace sola es contar las impresiones
        // por cobrar del chip, cada 45 s: no tiene que ver con escanear y no cuenta.)
        Assert.DoesNotContain(durante, r => !r.Contains("/impresiones/por-cobrar", StringComparison.Ordinal));
        Assert.Empty(v.Pantalla.Errores);

        // Con la red bien otra vez, la copia queda al día con lo que pasó en la caja principal (el latido de respaldo).
        foreach (var red in new[] { v.Red, v.Fondo })
            (red.Retraso, red.Perdidas) = (TimeSpan.Zero, 0);
        // (Lo que el aviso ya estaba poniendo al día con la red lenta puede seguir en camino: se espera, con límite.)
        await v.Contingencia.LatidoAsync();
        await VentaConCopia.EsperarAsync(() => v.Contingencia.Foto!.PorCodigo(conPrecioNuevo.Codigo!)!.Precios.Single().PrecioCentavos
                                               == conPrecioNuevo.Precios[0].PrecioCentavos + 500
                                               && v.Contingencia.Foto.PorCodigo(vendidoEnLaOtra.Codigo!)!.Existencia == vendidoEnLaOtra.Existencia - 2,
            "la copia al día con el precio nuevo y la venta de la otra caja");
        // Y la ráfaga sigue intacta.
        Assert.Equal(20, (await VentaConCopia.RenglonesAsync(v.Pantalla)).Count);
    }
}
