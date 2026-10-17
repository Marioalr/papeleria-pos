using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
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

    public void Dispose() => BasesDePrueba.Borrar(_baseLocal);

    /// <summary>Lo de la copia que se vende por pieza a un solo precio: una pieza cuesta lo que dice su precio.</summary>
    private static List<ProductoVentaDto> SinMayoreo(VentaConCopia v) => v.DeLaCopia().Where(p => p.Precios.Count == 1).ToList();

    /// <summary>El dueño cambia el precio en la caja principal (como desde otra caja), después de que esta bajó su copia.</summary>
    private Task CambiarPrecioAsync(VentaConCopia v, ProductoVentaDto producto, long centavos) => CambiarPrecioAsync(servicio, v, producto, centavos);

    private static async Task CambiarPrecioAsync(ServicioDePrueba servicio, VentaConCopia v, ProductoVentaDto producto, long centavos)
    {
        var http = servicio.Fabrica.CreateClient();
        var dueno = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var respuesta = await http.PostAsJsonAsync("api/catalogo/precio-menudeo", new SolicitudPrecioMenudeo(dueno.UsuarioId, v.Caja.SucursalId, producto.VarianteId, centavos));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        // El catálogo en memoria del servicio se recarga en segundo plano (entrega 5, parte 2).
        await servicio.CatalogoAlDiaAsync();
    }

    private Task<VentaDelDiaDto> UltimaVentaAsync(VentaConCopia v) => UltimaVentaAsync(servicio, v);

    private static async Task<VentaDelDiaDto> UltimaVentaAsync(ServicioDePrueba servicio, VentaConCopia v) =>
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

    /// <summary>
    /// Con la red lenta la caja cobra con su copia, que se sigue poniendo al día (entrega 5, parte 2, revisión): si mientras
    /// tanto llegó otro precio de algo del ticket, no se cobra con un precio que el cajero no vio. Como cuando lo dice la caja
    /// principal, el ticket se pone al día con el de la copia, el panel lo dice y el cobro no se repite solo; al cobrar otra
    /// vez, con el que ya vio.
    /// </summary>
    [Fact]
    public async Task Con_la_red_lenta_no_se_cobra_un_precio_que_llego_despues_de_lo_que_vio_el_cajero()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(propio, _baseLocal);
        var producto = SinMayoreo(v)[0];
        var ahora = producto.Precios[0].PrecioCentavos + 300;
        await v.EscanearAsync($"2*{producto.Codigo}");
        await VentaConCopia.EsperarAsync(() => VentaConCopia.RenglonesAsync(v.Pantalla).Result.Count == 1, "la línea");
        // Algo no llegó a tiempo y la red quedó lenta; el dueño cambia el precio y el cambio llega a la copia con el latido.
        v.Contingencia.MarcarLenta();
        await CambiarPrecioAsync(propio, v, producto, ahora);
        await v.Contingencia.LatidoAsync();
        Assert.Equal(EstadoRed.Lenta, v.Contingencia.EstadoDeLaRed);
        Assert.Equal(ahora, v.Contingencia.FotoDeLaCopia!.PorVariante(producto.VarianteId)!.Precios[0].PrecioCentavos);

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        var texto = await v.Pantalla.TextoAsync();
        Assert.Contains($"Cambió el precio de «{producto.Descripcion}»: revisa el total ({Formato.Dinero(2 * ahora)}) y cobra otra vez.", texto);
        Assert.DoesNotContain("Cobrado", texto);
        Assert.Equal(0, v.Contingencia.Pendientes);

        // Otra vez: con el precio que ya vio, con la copia; al volver la red, sube así.
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro con la copia");
        for (var i = 0; i < 5 && !v.Contingencia.EnLinea; i++)
            await v.Contingencia.LatidoAsync();
        Assert.Equal((0, 0), (v.Contingencia.Pendientes, v.Contingencia.ConError));
        Assert.Equal(2 * ahora, (await UltimaVentaAsync(propio, v)).TotalCentavos);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Lo que llega a la copia sin cambiar precios (otro nombre del producto) no detiene el cobro con la red lenta: llegó con
    /// otra versión, pero sus precios son los que vio el cajero.
    /// </summary>
    [Fact]
    public async Task Con_la_red_lenta_lo_que_llega_sin_cambiar_el_precio_no_detiene_el_cobro()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(propio, _baseLocal);
        var producto = SinMayoreo(v)[1];
        var version = v.Contingencia.FotoDeLaCopia!.PorVariante(producto.VarianteId)!.Version;
        await v.EscanearAsync(producto.Codigo!);
        await VentaConCopia.EsperarAsync(() => VentaConCopia.RenglonesAsync(v.Pantalla).Result.Count == 1, "la línea");
        v.Contingencia.MarcarLenta();
        await using (var db = propio.Base())
        {
            var variante = await db.Variantes.Include(x => x.Producto).SingleAsync(x => x.Id == producto.VarianteId);
            variante.Producto!.Nombre += " (nombre nuevo)";
            await db.SaveChangesAsync();
        }
        await propio.CatalogoAlDiaAsync();
        await v.Contingencia.LatidoAsync();
        Assert.True(v.Contingencia.FotoDeLaCopia!.PorVariante(producto.VarianteId)!.Version > version);

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro con la copia");
        Assert.Equal(1, v.Contingencia.Pendientes);
        Assert.Empty(v.Pantalla.Errores);
    }
}
