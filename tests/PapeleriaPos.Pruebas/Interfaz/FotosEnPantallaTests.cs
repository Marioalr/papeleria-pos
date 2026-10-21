using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Impresion;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Las fotos en la venta (entrega 13, parte 2): la pieza de Ctrl+M con foto la enseña en lugar de sus iniciales, la lista de la
/// búsqueda la trae chica a la izquierda y el verificador (F9) grande. Siempre de la base de la caja, sin pedirle nada a la red
/// mientras se enseñan; el producto sin foto, sus iniciales como antes. La caja de verdad con su copia contra el servicio de
/// prueba; lo que se revisa no depende de lo que tarda la PC.
/// </summary>
public sealed class FotosEnPantallaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-fotos-pantalla-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    /// <summary>«Zanfona» con foto y «Ocarina» sin ella, en la repisa de arriba del mostrador, en ese orden.</summary>
    private sealed record Preparado(ProductoVentaDto ConFoto, ProductoVentaDto SinFoto, FotoGuardadaDto Foto);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Preparado> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var copia = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));
        var productos = copia.Codigos
            .Select(c => (Codigo: c, Producto: copia.Productos.FirstOrDefault(p => p.VarianteId == c.VarianteId)))
            .Where(x => x.Producto is { Unidad: UnidadBase.Pieza } p && p.Presentacion.Id == x.Codigo.PresentacionId && p.Codigo == x.Codigo.Codigo
                        && p.Precios.Any(precio => precio.PresentacionId is null))
            .Select(x => x.Producto!).DistinctBy(p => p.VarianteId).OrderBy(p => p.VarianteId).Skip(30).Take(2).ToList();
        // Con un nombre que ningún otro tiene: en la búsqueda sale solo, en su propio renglón.
        await using (var db = servicio.Base())
        {
            (await db.Variantes.SingleAsync(v => v.Id == productos[0].VarianteId)).Descripcion = "Zanfona";
            (await db.Variantes.SingleAsync(v => v.Id == productos[1].VarianteId)).Descripcion = "Ocarina";
            await db.SaveChangesAsync();
        }
        // La de «Ocarina», sin foto (otra prueba de esta base se la pone).
        Assert.True((await http.PostAsJsonAsync($"api/fotos/{productos[1].VarianteId}/quitar", new SolicitudQuitarFoto(encargado.UsuarioId, caja.SucursalId)))
            .IsSuccessStatusCode);
        var foto = await Leer<FotoGuardadaDto>(await http.PutAsJsonAsync($"api/fotos/{productos[0].VarianteId}",
            new SolicitudFoto(encargado.UsuarioId, caja.SucursalId, Convert.ToBase64String(ImagenesDePrueba.Foto(1600, 1200)))));
        var repisa = (await Leer<MostradorDto>(await http.GetAsync("api/mostrador"))).Exhibidores.Single(e => e.Nombre == "Mostrador").Lugares[0];
        await Leer<MostradorDto>(await http.PutAsJsonAsync("api/mostrador/acomodo", new SolicitudAcomodo(encargado.UsuarioId, caja.SucursalId,
            [new LugarConProductosDto(repisa.Id, [productos[0].VarianteId, productos[1].VarianteId])])));
        await servicio.CatalogoAlDiaAsync();
        var deAhora = (await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"))).Productos;
        return new Preparado(deAhora.Single(p => p.VarianteId == productos[0].VarianteId), deAhora.Single(p => p.VarianteId == productos[1].VarianteId), foto);
    }

    /// <summary>La venta con su copia y sus fotos ya bajadas (todas: las de las demás pruebas de esta base también).</summary>
    private async Task<VentaConCopia> AbrirAsync(Preparado p)
    {
        var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, fondoAparte: true);
        await VentaConCopia.EsperarAsync(() => v.Contingencia.Fotos.Direccion(p.ConFoto.VarianteId) is not null && v.Contingencia.Fotos.Faltan == 0,
            "las fotos en la caja");
        return v;
    }

    private static Task TeclaAsync(VentaConCopia v, string tecla) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, ""));

    /// <summary>Cuántas veces se le han pedido fotos a la red (la de la venta y la de fondo).</summary>
    private static int FotosPedidas(VentaConCopia v) => v.Red.Cuantas("/api/fotos") + v.Fondo.Cuantas("/api/fotos");

    private static string Archivo(Preparado p) => FotoProducto.Archivo(p.ConFoto.VarianteId, p.Foto.Huella);

    [Fact]
    public async Task En_Ctrl_M_la_pieza_con_foto_la_enseña_y_la_que_no_sus_iniciales()
    {
        var p = await PrepararAsync();
        await using var v = await AbrirAsync(p);
        var pedidas = FotosPedidas(v);

        await TeclaAsync(v, "Ctrl+M");
        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(
            e => e.Atributos.GetValueOrDefault("role") as string == "tab" && PantallaSinNavegador.Limpio(e.Texto) == "Mostrador", "el mostrador"));

        var elementos = (await v.Pantalla.LeerAsync()).Elementos;
        var (conFoto, sinFoto) = (elementos.FindIndex(e => e.Id == "pieza-0"), elementos.FindIndex(e => e.Id == "pieza-1"));
        var foto = elementos.FindIndex(e => e.Etiqueta == "img" && e.Clase == "vn-foto");
        // La foto va en la primera pieza (antes de la segunda), con la dirección de la que tiene la caja.
        Assert.InRange(foto, conFoto + 1, sinFoto - 1);
        Assert.Equal(Archivo(p), elementos[foto].Atributos["src"]);
        Assert.Contains("Zanfona", elementos[conFoto].Texto);
        // La otra, sus iniciales, como antes.
        Assert.Contains("Ocarina", elementos[sinFoto].Texto);
        Assert.Contains(elementos.Skip(sinFoto).Take(4),
            e => e.Etiqueta == "span" && e.Clase == "vn-foto" && PantallaSinNavegador.Limpio(e.Texto) == MostradorEnPantalla.Iniciales(p.SinFoto.Descripcion));
        Assert.Single(elementos, e => e.Etiqueta == "img" && e.Clase == "vn-foto");
        // Sin pedirle nada a la red.
        Assert.Equal(pedidas, FotosPedidas(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task En_la_lista_de_la_busqueda_va_chica_a_la_izquierda()
    {
        var p = await PrepararAsync();
        await using var v = await AbrirAsync(p);
        var pedidas = FotosPedidas(v);

        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("zanfona"));
        var elementos = (await v.Pantalla.LeerAsync()).Elementos;
        var renglon = Assert.Single(elementos, e => e.Id?.StartsWith("res-", StringComparison.Ordinal) == true);
        Assert.Contains("con-foto", renglon.Clase);
        Assert.Contains(elementos, e => e.Etiqueta == "img" && e.Clase == "vn-res-foto" && (string?)e.Atributos["src"] == Archivo(p));

        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("ocarina"));
        elementos = (await v.Pantalla.LeerAsync()).Elementos;
        renglon = Assert.Single(elementos, e => e.Id?.StartsWith("res-", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("con-foto", renglon.Clase);
        Assert.DoesNotContain(elementos, e => e.Clase == "vn-res-foto");
        Assert.Equal(pedidas, FotosPedidas(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task El_verificador_la_enseña_grande_y_sin_foto_las_iniciales()
    {
        var p = await PrepararAsync();
        await using var v = await AbrirAsync(p);
        var pedidas = FotosPedidas(v);

        await TeclaAsync(v, "F9");
        await v.Pantalla.EscribirAsync("verificador", p.ConFoto.Codigo!);
        await v.Pantalla.TeclaAsync("verificador", "Enter");
        await v.Pantalla.EsperarAsync(t => t.Contains("Zanfona", StringComparison.Ordinal), "el producto en el verificador");
        var foto = await v.Pantalla.ElementoAsync(e => e.Etiqueta == "img" && e.Clase == "vn-foto", "la foto en el verificador");
        Assert.Equal(Archivo(p), foto.Atributos["src"]);

        await v.Pantalla.EscribirAsync("verificador", p.SinFoto.Codigo!);
        await v.Pantalla.TeclaAsync("verificador", "Enter");
        await v.Pantalla.EsperarAsync(t => t.Contains("Ocarina", StringComparison.Ordinal), "el otro en el verificador");
        var elementos = (await v.Pantalla.LeerAsync()).Elementos;
        Assert.DoesNotContain(elementos, e => e.Etiqueta == "img" && e.Clase == "vn-foto");
        Assert.Contains(elementos, e => e.Etiqueta == "span" && e.Clase == "vn-foto"
            && PantallaSinNavegador.Limpio(e.Texto) == MostradorEnPantalla.Iniciales(p.SinFoto.Descripcion));
        Assert.Equal(pedidas, FotosPedidas(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Con el mostrador abierto, la foto que llega (la tomaron con el celular) se ve sin cerrarlo.</summary>
    [Fact]
    public async Task La_foto_que_llega_con_el_mostrador_abierto_se_ve_al_llegar()
    {
        var p = await PrepararAsync();
        await using var v = await AbrirAsync(p);
        await TeclaAsync(v, "Ctrl+M");
        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(
            e => e.Atributos.GetValueOrDefault("role") as string == "tab" && PantallaSinNavegador.Limpio(e.Texto) == "Mostrador", "el mostrador"));
        Assert.Single((await v.Pantalla.LeerAsync()).Elementos, e => e.Etiqueta == "img" && e.Clase == "vn-foto");

        var http = servicio.Fabrica.CreateClient();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var nueva = await Leer<FotoGuardadaDto>(await http.PutAsJsonAsync($"api/fotos/{p.SinFoto.VarianteId}",
            new SolicitudFoto(encargado.UsuarioId, v.Caja.SucursalId, Convert.ToBase64String(ImagenesDePrueba.Mitades(800, 800)))));
        await servicio.CatalogoAlDiaAsync();
        await v.Contingencia.LatidoAsync();

        var src = FotoProducto.Archivo(p.SinFoto.VarianteId, nueva.Huella);
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!(await v.Pantalla.LeerAsync()).Elementos.Any(e => e.Etiqueta == "img" && (string?)e.Atributos.GetValueOrDefault("src") == src))
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException("No llegó la foto nueva a la pieza.");
            await Task.Delay(20);
        }
        Assert.Equal(2, (await v.Pantalla.LeerAsync()).Elementos.Count(e => e.Etiqueta == "img" && e.Clase == "vn-foto"));
        Assert.Empty(v.Pantalla.Errores);
    }
}
