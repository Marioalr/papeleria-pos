using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El cobro con la versión de la copia, en la caja (entrega 5): si un precio cambió en la caja principal después de la copia,
/// la venta no se cobra con un precio que el cajero no vio; el ticket se pone al día, el panel lo dice y el cobro no se
/// repite solo. Sin conexión se cobra como siempre, con la copia.
/// </summary>
public sealed class CobroConLaVersionEnLaCajaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-version-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    /// <summary>Lo de la copia que se vende por pieza a un solo precio: una pieza cuesta lo que dice su precio.</summary>
    private static List<ProductoVentaDto> SinMayoreo(VentaConCopia v) => v.DeLaCopia().Where(p => p.Precios.Count == 1).ToList();

    /// <summary>El dueño cambia el precio en la caja principal (como desde otra caja), después de que esta bajó su copia.</summary>
    private async Task CambiarPrecioAsync(VentaConCopia v, ProductoVentaDto producto, long centavos)
    {
        var http = servicio.Fabrica.CreateClient();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var respuesta = await http.PostAsJsonAsync("api/catalogo/precio-menudeo", new SolicitudPrecioMenudeo(dueno.UsuarioId, v.Caja.SucursalId, producto.VarianteId, centavos));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
    }

    private async Task<VentaDelDiaDto> UltimaVentaAsync(VentaConCopia v) =>
        (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<VentaDelDiaDto>>($"api/cajas/{v.Caja.Id}/ventas-del-dia"))!
        .OrderByDescending(x => x.Folio, StringComparer.Ordinal).First();

    [Fact]
    public async Task Si_un_precio_cambio_despues_de_la_copia_no_cobra_pone_el_ticket_al_dia_y_lo_dice()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var producto = SinMayoreo(v)[0];
        var antes = producto.Precios[0].PrecioCentavos;
        var ahora = antes + 250;
        await v.EscanearAsync($"2*{producto.Codigo}");
        await CambiarPrecioAsync(v, producto, ahora);

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        var texto = await v.Pantalla.TextoAsync();
        Assert.Contains($"Cambió el precio de «{producto.Descripcion}»: revisa el total ({Formato.Dinero(2 * ahora)}) y cobra otra vez.", texto);
        Assert.DoesNotContain("Cobrado", texto);
        var linea = Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.StartsWith($"2 {producto.Descripcion}", linea);
        Assert.Contains(Formato.Dinero(2 * ahora), linea);
        // La foto se quedó con el precio de ahora: el siguiente escaneo ya lo trae, sin preguntar a la red.
        Assert.Equal(ahora, v.Contingencia.Foto!.PorCodigo(producto.Codigo!)!.Precios.Single().PrecioCentavos);

        // Enter otra vez: se cobra con el precio que ya vio el cajero.
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro");
        Assert.Equal(2 * ahora, (await UltimaVentaAsync(v)).TotalCentavos);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Un producto de la copia con código en su pieza y en su paquete (otra presentación), con precios de la pieza.</summary>
    private static (ProductoVentaDto Producto, string Pieza, string Paquete) ConPiezaYPaquete(VentaConCopia v)
    {
        var copia = v.Contingencia.Paquete!;
        return v.DeLaCopia()
            .Where(p => p.Precios.All(precio => precio.PresentacionId is null) && p.Precios.Count(precio => precio.CantidadMinimaMilesimas == 1000) == 1)
            .Select(p => (Producto: p, Paquete: copia.Codigos.FirstOrDefault(c => c.VarianteId == p.VarianteId && c.PresentacionId != p.Presentacion.Id)))
            .Where(x => x.Paquete is not null)
            .Select(x => (x.Producto, x.Producto.Codigo!, x.Paquete!.Codigo))
            .First();
    }

    /// <summary>
    /// La pieza de un producto se escaneó antes de que cambiara su precio, y su paquete después, con la copia nueva: cada
    /// línea lleva la versión de su precio. La pieza no se cobra al precio de ahora sin que el cajero lo vea: el ticket se
    /// pone al día y el panel lo dice.
    /// </summary>
    [Fact]
    public async Task Dos_presentaciones_del_mismo_producto_llevan_cada_una_la_version_de_su_precio()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (producto, pieza, paquete) = ConPiezaYPaquete(v);
        var antes = producto.Precios.Single(p => p.CantidadMinimaMilesimas == 1000).PrecioCentavos;
        var ahora = antes + 300;
        await v.EscanearAsync(pieza);
        var copia = v.Contingencia.Foto!.Version;
        await CambiarPrecioAsync(v, producto, ahora);
        await v.Contingencia.LatidoAsync();
        Assert.True(v.Contingencia.Foto!.Version > copia);
        await v.EscanearAsync(paquete);
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.Contains(Formato.Dinero(antes), renglones[0]);

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        var texto = await v.Pantalla.TextoAsync();
        Assert.Contains($"Cambió el precio de «{producto.Descripcion}»: revisa el total", texto);
        Assert.DoesNotContain("Cobrado", texto);
        Assert.Contains(Formato.Dinero(ahora), (await VentaConCopia.RenglonesAsync(v.Pantalla))[0]);

        // Enter otra vez: se cobra lo que ya se ve.
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro");
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Sin conexión, el cobro sigue exactamente como antes: con la copia de la caja y su propio folio, aunque en la caja
    /// principal el precio ya sea otro; al volver, sube con el precio que se cobró.
    /// </summary>
    [Fact]
    public async Task Sin_conexion_se_cobra_como_siempre_con_la_copia()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var producto = SinMayoreo(v)[1];
        var antes = producto.Precios[0].PrecioCentavos;
        await CambiarPrecioAsync(v, producto, antes + 400);
        v.Red.Caida = true;
        await v.Contingencia.LatidoAsync();
        Assert.False(v.Contingencia.EnLinea);

        await v.EscanearAsync(producto.Codigo!);
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro sin conexión");

        v.Red.Caida = false;
        await v.Contingencia.LatidoAsync();
        Assert.Equal(0, v.Contingencia.Pendientes);
        Assert.Equal(antes, (await UltimaVentaAsync(v)).TotalCentavos);
        Assert.Empty(v.Pantalla.Errores);
    }
}
