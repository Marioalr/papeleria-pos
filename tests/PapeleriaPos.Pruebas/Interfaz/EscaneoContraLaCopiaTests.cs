using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El escaneo contra la copia local de la caja, siempre (entrega 5, docs/propuesta-interfaz.md, «Rendimiento»): la venta de
/// verdad (la pantalla, el cliente, la contingencia con su base local) contra el servicio de verdad, con la red como se le
/// pida. Lo que se revisa no depende de lo que tarda la PC: que no haya peticiones a la red, el orden de las líneas y lo que
/// dice la pantalla, esperando a cada cosa con un límite.
/// </summary>
public sealed class EscaneoContraLaCopiaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-escaneo-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    private Task<VentaConCopia> AbrirAsync(TimeSpan? plazo = null) => VentaConCopia.AbrirAsync(servicio, _baseLocal, plazo);

    private static (ProductoVentaDto A, ProductoVentaDto B) DosDeLaCopia(VentaConCopia v) => v.DosDeLaCopia();

    private static Task<ProductoVentaDto> DarDeAltaEnOtraCajaAsync(VentaConCopia v, string codigo, string nombre) => v.DarDeAltaEnOtraCajaAsync(codigo, nombre);

    private static Task<List<string>> RenglonesAsync(PantallaSinNavegador p) => VentaConCopia.RenglonesAsync(p);

    [Fact]
    public async Task La_foto_resuelve_cada_codigo_como_la_copia_de_antes_y_como_el_servicio()
    {
        await using var v = await AbrirAsync();
        var paquete = v.Contingencia.Paquete!;
        var foto = v.Contingencia.Foto!;

        Assert.Equal(paquete.Version, foto.Version);
        Assert.Equal(paquete.Productos.Count, foto.Productos);
        // Cada código de la copia (los de las presentaciones y los de más de un producto), igual que la contingencia de antes.
        Assert.All(paquete.Codigos, c =>
        {
            var antes = FotoCatalogoTests.ComoAntes(paquete, c.Codigo);
            Assert.Equal(FotoCatalogoTests.Clave(antes), FotoCatalogoTests.Clave(foto.PorCodigo(c.Codigo.ToLowerInvariant())));
            Assert.Equal(antes?.Precios, foto.PorCodigo(c.Codigo)?.Precios);
        });
        // Y como el servicio: los de las presentaciones que no son la base, y otros más.
        var muestra = paquete.Codigos.Where(c => paquete.Productos.Any(p => p.VarianteId == c.VarianteId && p.Presentacion.Id != c.PresentacionId))
            .Take(15).Concat(paquete.Codigos.Take(15)).ToList();
        Assert.NotEmpty(muestra);
        foreach (var c in muestra)
        {
            var delServicio = await v.Cliente.PorCodigoAsync(c.Codigo, v.Caja.SucursalId);
            Assert.Equal(FotoCatalogoTests.Clave(delServicio), FotoCatalogoTests.Clave(foto.PorCodigo(c.Codigo)));
            Assert.Equal(delServicio!.Precios.Select(p => (p.PresentacionId, p.CantidadMinimaMilesimas, p.PrecioCentavos)).Order(),
                foto.PorCodigo(c.Codigo)!.Precios.Select(p => (p.PresentacionId, p.CantidadMinimaMilesimas, p.PrecioCentavos)).Order());
        }
    }

    /// <summary>
    /// Con la red lenta (cada petición tarda 3 s) o caída, lo de la copia entra al momento, en orden y sin ninguna petición a
    /// la red: antes se esperaba al servicio en cada escaneo (hasta 15 s con la conexión medio caída). Que no haya peticiones
    /// no depende de lo que tarde la PC; el reloj solo confirma que no se esperó a la red.
    /// </summary>
    [Fact]
    public async Task Con_la_red_lenta_o_caida_lo_de_la_copia_entra_al_momento_sin_preguntar_a_la_red()
    {
        await using var v = await AbrirAsync();
        var (a, b) = DosDeLaCopia(v);
        v.Red.Retraso = TimeSpan.FromSeconds(3);
        var pedidas = v.Red.Cuantas("/api/productos/");

        var reloj = Stopwatch.StartNew();
        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync($"3*{b.Codigo}");
        reloj.Stop();
        var renglones = await RenglonesAsync(v.Pantalla);

        Assert.Equal(pedidas, v.Red.Cuantas("/api/productos/"));
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(2.5), $"Los escaneos esperaron a la red: {reloj.Elapsed}.");
        Assert.Equal(2, renglones.Count);
        Assert.StartsWith($"1 {a.Descripcion}", renglones[0]);
        Assert.StartsWith($"3 {b.Descripcion}", renglones[1]);

        // Caída: igual, de la copia.
        (v.Red.Retraso, v.Red.Caida) = (TimeSpan.Zero, true);
        await v.EscanearAsync(a.Codigo!);
        renglones = await RenglonesAsync(v.Pantalla);
        Assert.Equal(pedidas, v.Red.Cuantas("/api/productos/"));
        Assert.StartsWith($"2 {a.Descripcion}", renglones[0]);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Un producto recién dado de alta en otra caja no está en la copia: entra como «buscando…» en su lugar, los escaneos que
    /// siguen no lo esperan, y cuando la caja principal contesta toma su lugar y queda en la foto (el siguiente ya no pregunta).
    /// </summary>
    [Fact]
    public async Task Lo_que_no_esta_en_la_copia_entra_buscando_en_su_lugar_y_llega_de_la_principal()
    {
        await using var v = await AbrirAsync();
        var (a, b) = DosDeLaCopia(v);
        var nuevo = await DarDeAltaEnOtraCajaAsync(v, "9900000000105", "Libreta recién dada de alta");
        v.Red.Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync(nuevo.Codigo!);
        await v.EscanearAsync(b.Codigo!);
        var mientras = await RenglonesAsync(v.Pantalla);

        Assert.Equal(3, mientras.Count);
        Assert.StartsWith($"1 {a.Descripcion}", mientras[0]);
        Assert.StartsWith("1 Buscando… 9900000000105", mientras[1]);
        Assert.StartsWith($"1 {b.Descripcion}", mientras[2]);
        Assert.Equal(1, v.Red.Cuantas("/api/productos/codigo/"));

        v.Red.Detenida.SetResult();
        await v.Pantalla.EsperarAsync(t => t.Contains("Libreta recién dada de alta") && !t.Contains("Buscando…"), "la línea del producto nuevo");
        var despues = await RenglonesAsync(v.Pantalla);
        Assert.Equal(3, despues.Count);
        Assert.StartsWith($"1 {a.Descripcion}", despues[0]);
        Assert.StartsWith("1 Libreta recién dada de alta", despues[1]);
        Assert.Contains("$45.50", despues[1]);
        Assert.StartsWith($"1 {b.Descripcion}", despues[2]);

        // Quedó en la foto: el siguiente escaneo ya no pregunta.
        Assert.NotNull(v.Contingencia.Foto!.PorCodigo("9900000000105"));
        await v.EscanearAsync(nuevo.Codigo!);
        Assert.Equal(1, v.Red.Cuantas("/api/productos/codigo/"));
        Assert.StartsWith("2 Libreta recién dada de alta", (await RenglonesAsync(v.Pantalla))[1]);
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Si_la_principal_dice_que_no_existe_ofrece_darlo_de_alta_o_el_articulo_comun()
    {
        await using var v = await AbrirAsync();

        await v.EscanearAsync("9900000000204");
        await v.Pantalla.EsperarAsync(t => t.Contains("No existe «9900000000204». Revisa el código o elige:"), "el aviso de que no existe");

        Assert.Empty(await RenglonesAsync(v.Pantalla));
        Assert.NotNull(await v.Pantalla.BotonAsync("Darlo de alta aquí"));
        Assert.NotNull(await v.Pantalla.BotonAsync("Venderlo como artículo común"));
        Assert.Null(v.Contingencia.Foto!.PorCodigo("9900000000204"));
    }

    [Fact]
    public async Task Si_la_principal_no_contesta_a_tiempo_el_renglon_sale_y_lo_dice()
    {
        await using var v = await AbrirAsync(plazo: TimeSpan.FromMilliseconds(200));
        var (a, _) = DosDeLaCopia(v);
        var nuevo = await DarDeAltaEnOtraCajaAsync(v, "9900000000303", "Lápiz recién dado de alta");
        v.Red.Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync(nuevo.Codigo!);
        await v.Pantalla.EsperarAsync(t => t.Contains("«9900000000303» no está en la copia de esta caja y la caja principal no contestó: escanéalo otra vez en un momento."),
            "el aviso de que no contestó");

        var renglones = await RenglonesAsync(v.Pantalla);
        Assert.Single(renglones);
        Assert.StartsWith($"1 {a.Descripcion}", renglones[0]);
        // No contestar a tiempo no es estar sin conexión: eso lo decide el latido.
        Assert.True(v.Contingencia.ServidorResponde);
    }

    /// <summary>
    /// El cobro espera lo que se le está preguntando a la caja principal y nunca cobra un «buscando…»: si llegó, el cajero ve
    /// el total nuevo antes de cobrar; si no llegó, se le dice. Con Enter otra vez, se cobra lo que hay.
    /// </summary>
    [Fact]
    public async Task El_cobro_espera_lo_que_se_esta_buscando_y_nunca_lo_cobra()
    {
        await using var v = await AbrirAsync();
        var (a, _) = DosDeLaCopia(v);
        var nuevo = await DarDeAltaEnOtraCajaAsync(v, "9900000000402", "Goma recién dada de alta");
        await v.EscanearAsync(a.Codigo!);
        v.Red.Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await v.EscanearAsync(nuevo.Codigo!);

        var cobro = v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        v.Red.Detenida.SetResult();
        await cobro;

        var texto = await v.Pantalla.TextoAsync();
        Assert.Contains("Llegó lo último que escaneaste: el total ahora es", texto);
        Assert.DoesNotContain("Cobrado", texto);
        var renglones = await RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.StartsWith("1 Goma recién dada de alta", renglones[1]);

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro");
        Assert.Empty(await RenglonesAsync(v.Pantalla));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Si_lo_que_se_busca_no_llega_el_cobro_lo_dice_y_no_cobra()
    {
        await using var v = await AbrirAsync(plazo: TimeSpan.FromMilliseconds(200));
        var (a, _) = DosDeLaCopia(v);
        var nuevo = await DarDeAltaEnOtraCajaAsync(v, "9900000000501", "Regla recién dada de alta");
        await v.EscanearAsync(a.Codigo!);
        v.Red.Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await v.EscanearAsync(nuevo.Codigo!);

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        var texto = await v.Pantalla.TextoAsync();
        Assert.Contains("Lo último que escaneaste no entró al ticket (el aviso de la izquierda dice por qué): cobra cuando esté completo.", texto);
        Assert.Contains("«9900000000501» no está en la copia de esta caja y la caja principal no contestó", texto);
        Assert.DoesNotContain("Cobrado", texto);
        Assert.Single(await RenglonesAsync(v.Pantalla));
    }

    /// <summary>Al bajar una copia nueva, la foto cambia de un jalón: quien tenía la de antes no ve un cambio a medias.</summary>
    [Fact]
    public async Task Una_copia_nueva_cambia_la_foto_de_un_jalon()
    {
        await using var v = await AbrirAsync();
        var antes = v.Contingencia.Foto!;
        var nuevo = await DarDeAltaEnOtraCajaAsync(v, "9900000000600", "Compás recién dado de alta");

        await v.Contingencia.LatidoAsync();

        var despues = v.Contingencia.Foto!;
        Assert.NotSame(antes, despues);
        Assert.True(despues.Version > antes.Version);
        Assert.Null(antes.PorCodigo(nuevo.Codigo!));
        Assert.Equal(nuevo.VarianteId, despues.PorCodigo(nuevo.Codigo!)!.VarianteId);
        Assert.Equal(despues.Version, despues.PorCodigo(nuevo.Codigo!)!.Version);
    }
}
