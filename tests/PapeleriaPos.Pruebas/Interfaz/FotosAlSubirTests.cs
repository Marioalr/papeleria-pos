using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Impresion;
using PapeleriaPos.Pruebas.Servicio;
using PapeleriaPos.Servicio;
using PapeleriaPos.Servicio.Componentes;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Subir las fotos (entrega 13, parte 2), sin navegador, contra el servicio de prueba: desde el celular, en la página del conteo
/// («Fotos»: se escanea el producto, se toma la foto, se ve y se guarda), y desde la PC, en la ficha del producto (F3: se elige o
/// se pega, se ve, se guarda o se quita). La cámara y el selector de archivos son del navegador: aquí la foto llega como la deja
/// la página (ya achicada).
/// </summary>
public sealed class FotosAlSubirTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Abierta(PantallaSinNavegador Pantalla, CajaDto Caja, SesionDto Sesion, PaginaQueContesta Pagina);

    private async Task<(CajaDto Caja, SesionDto Sesion)> EntrarAsync(string usuario, string pin)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var sesion = (await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin))).Content.ReadFromJsonAsync<SesionDto>())!;
        return (caja, sesion);
    }

    /// <summary>Como en el servicio: la página del celular habla con la API de su misma PC, sin base local.</summary>
    private ServiceProvider Servicios(CajaDto caja, PaginaQueContesta pagina)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        return new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(pagina)
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .AddSingleton(new SesionesCelular(TimeProvider.System))
            .BuildServiceProvider();
    }

    private async Task<(Abierta Abierta, T Componente)> AbrirAsync<T>(string usuario, string pin, Func<CajaDto, SesionDto, Dictionary<string, object?>> parametros)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        var (caja, sesion) = await EntrarAsync(usuario, pin);
        var pagina = new PaginaQueContesta();
        var pantalla = new PantallaSinNavegador(Servicios(caja, pagina));
        var componente = await pantalla.AbrirAsync<T>(parametros(caja, sesion));
        return (new Abierta(pantalla, caja, sesion, pagina), componente);
    }

    /// <summary>Un producto que se vende por pieza, con el código de su pieza, distinto en cada prueba.</summary>
    private async Task<ProductoVentaDto> ProductoAsync(int cual)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var copia = (await http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete"))!;
        return copia.Codigos
            .Select(c => (Codigo: c, Producto: copia.Productos.FirstOrDefault(p => p.VarianteId == c.VarianteId)))
            .Where(x => x.Producto is { } p && p.Presentacion.Id == x.Codigo.PresentacionId && p.Codigo == x.Codigo.Codigo)
            .Select(x => x.Producto!).DistinctBy(p => p.VarianteId).OrderBy(p => p.VarianteId).ElementAt(cual);
    }

    private async Task<PapeleriaPos.Nucleo.Catalogo.FotoProducto?> EnLaBaseAsync(Guid varianteId)
    {
        await using var db = servicio.Base();
        return await db.FotosProducto.AsNoTracking().SingleOrDefaultAsync(f => f.VarianteId == varianteId);
    }

    [Fact]
    public async Task Desde_el_celular_se_escanea_se_toma_la_foto_se_ve_y_se_guarda()
    {
        var producto = await ProductoAsync(50);
        var (a, celular) = await AbrirAsync<FotosCelular>("encargado", "2222", (caja, sesion) => new()
        {
            [nameof(FotosCelular.Caja)] = caja,
            [nameof(FotosCelular.Sesion)] = sesion,
        });
        await using var pantalla = a.Pantalla;

        // El código, como lo deja el lector (o escrito) y Enter: su tarjeta, sin foto todavía.
        await pantalla.EscribirAsync("buscar-foto", producto.Codigo!);
        await pantalla.TeclaAsync("buscar-foto", "Enter");
        await pantalla.EsperarAsync(t => t.Contains("Sin foto todavía", StringComparison.Ordinal), "la tarjeta del producto");
        Assert.Contains(producto.Descripcion, await pantalla.TextoAsync());
        Assert.Contains((await pantalla.LeerAsync()).Elementos, e => e.Etiqueta == "label" && e.Texto.Contains("Tomar la foto"));

        // La de la cámara, como la deja la página (ya achicada): se ve antes de guardarla.
        var tomada = ImagenesDePrueba.Foto(960, 720);
        await pantalla.Dispatcher.InvokeAsync(() => celular.Tomada(tomada));
        Assert.Contains("Así se va a ver", await pantalla.TextoAsync());
        Assert.Contains((await pantalla.LeerAsync()).Elementos, e => e.Etiqueta == "img" && ((string?)e.Atributos["src"])!.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal));
        Assert.Null(await EnLaBaseAsync(producto.VarianteId));

        await pantalla.OprimirAsync(await pantalla.BotonAsync("Guardar"));

        await pantalla.EsperarAsync(t => t.Contains("Guardada: ya llega a las cajas.", StringComparison.Ordinal), "la foto guardada");
        var guardada = await EnLaBaseAsync(producto.VarianteId);
        Assert.NotNull(guardada);
        Assert.Equal((240, 180), (guardada.Ancho, guardada.Alto));
        await using (var db = servicio.Base())
            Assert.True(await db.Bitacora.AnyAsync(b => b.Detalle.EndsWith("(desde el celular)") && b.UsuarioId == a.Sesion.UsuarioId));
        // Se ve como quedó (la guardada), y se puede quitar.
        var vista = (await pantalla.LeerAsync()).Elementos.Single(e => e.Etiqueta == "img");
        Assert.Equal($"data:image/jpeg;base64,{Convert.ToBase64String(guardada.Datos)}", vista.Atributos["src"]);
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Quitar la foto"));
        await pantalla.EsperarAsync(t => t.Contains("Quitada", StringComparison.Ordinal), "la foto quitada");
        Assert.Null(await EnLaBaseAsync(producto.VarianteId));
        Assert.Empty(pantalla.Errores);
    }

    [Fact]
    public async Task Desde_el_celular_el_nombre_da_la_lista_y_dice_cual_ya_tiene_foto()
    {
        var producto = await ProductoAsync(55);
        var (caja, encargado) = await EntrarAsync("encargado", "2222");
        var http = servicio.Fabrica.CreateClient();
        Assert.True((await http.PutAsJsonAsync($"api/fotos/{producto.VarianteId}",
            new SolicitudFoto(encargado.UsuarioId, caja.SucursalId, Convert.ToBase64String(ImagenesDePrueba.Foto(500, 500))))).IsSuccessStatusCode);
        var (a, _) = await AbrirAsync<FotosCelular>("encargado", "2222", (c, s) => new() { [nameof(FotosCelular.Caja)] = c, [nameof(FotosCelular.Sesion)] = s });
        await using var pantalla = a.Pantalla;

        await pantalla.EscribirAsync("buscar-foto", producto.Descripcion);
        await pantalla.EsperarAsync(t => t.Contains("con foto", StringComparison.Ordinal), "la lista con la que ya tiene foto");
        await pantalla.OprimirAsync(await pantalla.ElementoAsync(e => e.Etiqueta == "button" && e.Texto.Contains(producto.Codigo!), "el producto en la lista"));

        await pantalla.EsperarAsync(t => t.Contains("Tomar otra foto", StringComparison.Ordinal), "la foto que ya tiene");
        Assert.Contains((await pantalla.LeerAsync()).Elementos, e => e.Etiqueta == "img");
        Assert.Empty(pantalla.Errores);
    }

    [Fact]
    public async Task El_cajero_no_guarda_fotos_desde_el_celular()
    {
        var producto = await ProductoAsync(57);
        var (a, celular) = await AbrirAsync<FotosCelular>("cajero", "1111", (c, s) => new() { [nameof(FotosCelular.Caja)] = c, [nameof(FotosCelular.Sesion)] = s });
        await using var pantalla = a.Pantalla;
        await pantalla.EscribirAsync("buscar-foto", producto.Codigo!);
        await pantalla.TeclaAsync("buscar-foto", "Enter");
        await pantalla.EsperarAsync(t => t.Contains(producto.Descripcion, StringComparison.Ordinal), "la tarjeta del producto");

        await pantalla.Dispatcher.InvokeAsync(() => celular.Tomada(ImagenesDePrueba.Foto(400, 300)));
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Guardar"));

        await pantalla.EsperarAsync(t => t.Contains("«Productos, precios y etiquetas»", StringComparison.Ordinal), "por qué no");
        Assert.Null(await EnLaBaseAsync(producto.VarianteId));
    }

    /// <summary>
    /// La página del conteo: quien tiene «Productos, precios y etiquetas» ve «Fotos» junto a «Contar» y el celular recuerda dónde
    /// estaba; el cajero solo cuenta.
    /// </summary>
    [Theory]
    [InlineData("encargado", "2222", true)]
    [InlineData("cajero", "1111", false)]
    public async Task En_la_pagina_del_conteo_Fotos_sale_a_quien_puede(string usuario, string pin, bool puede)
    {
        var (caja, _) = await EntrarAsync(usuario, pin);
        var pagina = new PaginaQueContesta();
        await using var pantalla = new PantallaSinNavegador(Servicios(caja, pagina));
        await pantalla.AbrirAsync<ConteoCelular>(new());
        await pantalla.EsperarElementoAsync("entrada-usuario");
        await pantalla.EscribirAsync("entrada-usuario", usuario);
        await pantalla.EscribirAsync("entrada-pin", pin);
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Entrar"));
        await pantalla.EsperarAsync(t => t.Contains("Salir", StringComparison.Ordinal), "la página después de entrar");

        var pestanas = (await pantalla.LeerAsync()).Elementos.Where(e => e.Atributos.GetValueOrDefault("role") as string == "tab")
            .Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        if (!puede)
        {
            Assert.Empty(pestanas);
            Assert.DoesNotContain((await pantalla.LeerAsync()).Elementos, e => e.Id == "buscar-foto");
            return;
        }
        Assert.Equal(["Contar", "Fotos"], pestanas);
        await pantalla.OprimirAsync(await pantalla.ElementoAsync(e => e.Atributos.GetValueOrDefault("role") as string == "tab"
            && PantallaSinNavegador.Limpio(e.Texto) == "Fotos", "«Fotos»"));
        await pantalla.EsperarElementoAsync("buscar-foto");
        Assert.Equal("fotos", await pagina.InvokeAsync<string?>("leer", ["papeleria-pos.conteo.pestana"]));
        Assert.StartsWith("Fotos ·", PantallaSinNavegador.Limpio((await pantalla.LeerAsync()).Elementos.First(e => e.Clase == "barra-celular").Texto));
    }

    [Fact]
    public async Task En_la_ficha_se_elige_o_se_pega_se_ve_se_guarda_y_se_quita()
    {
        var producto = await ProductoAsync(60);
        var (a, ficha) = await AbrirAsync<FotoDeLaVariante>("encargado", "2222", (caja, sesion) => new()
        {
            [nameof(FotoDeLaVariante.Caja)] = caja,
            [nameof(FotoDeLaVariante.Sesion)] = sesion,
            [nameof(FotoDeLaVariante.VarianteId)] = producto.VarianteId,
            [nameof(FotoDeLaVariante.Nombre)] = producto.Descripcion,
            [nameof(FotoDeLaVariante.PuedeEditar)] = true,
        });
        await using var pantalla = a.Pantalla;
        Assert.Contains("Sin foto: en las cajas salen sus iniciales.", await pantalla.TextoAsync());
        Assert.Contains((await pantalla.LeerAsync()).Elementos, e => e.Etiqueta == "label" && e.Texto.Contains("Elegir una foto…"));
        // El cuadro de la foto recibe Ctrl+V.
        Assert.Equal("0", (await pantalla.PorIdAsync($"foto-{producto.VarianteId:N}")).Atributos["tabindex"]);
        // «Con el celular…» enseña dónde abrir la página para tomarla (su código QR y su dirección, si esta PC tiene red).
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Con el celular…"));
        await pantalla.EsperarAsync(t => t.Contains("toca «Fotos»", StringComparison.Ordinal) || t.Contains("No se encontró la red", StringComparison.Ordinal),
            "dónde abrir la página en el celular");
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Ocultar el código"));

        // Lo que no se pudo pegar lo dice; lo que se pegó (ya achicado por la página) se ve antes de guardarlo.
        await pantalla.Dispatcher.InvokeAsync(() => ficha.ImagenPegada(null));
        Assert.Contains("No se pudo pegar esa imagen", await pantalla.TextoAsync());
        await pantalla.Dispatcher.InvokeAsync(() => ficha.ImagenPegada(ImagenesDePrueba.Foto(960, 640)));
        Assert.Contains("Así se va a ver", await pantalla.TextoAsync());
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Cancelar"));
        Assert.Contains("Sin foto", await pantalla.TextoAsync());
        Assert.Null(await EnLaBaseAsync(producto.VarianteId));

        await pantalla.Dispatcher.InvokeAsync(() => ficha.Elegida(ImagenesDePrueba.Foto(960, 640)));
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Guardar la foto"));

        await pantalla.EsperarAsync(t => t.Contains("Guardada: así llega a las cajas (240×160", StringComparison.Ordinal), "la foto guardada");
        var guardada = await EnLaBaseAsync(producto.VarianteId);
        Assert.NotNull(guardada);
        Assert.Contains((await pantalla.LeerAsync()).Elementos, e => e.Etiqueta == "img"
            && (string?)e.Atributos["src"] == $"data:image/jpeg;base64,{Convert.ToBase64String(guardada.Datos)}");
        await using (var db = servicio.Base())
            Assert.True(await db.Bitacora.AnyAsync(b => b.Detalle.EndsWith("(desde la PC)") && b.UsuarioId == a.Sesion.UsuarioId));

        await pantalla.OprimirAsync(await pantalla.BotonAsync("Quitar la foto"));
        await pantalla.EsperarAsync(t => t.Contains("Quitada: en las cajas vuelven a salir sus iniciales.", StringComparison.Ordinal), "la foto quitada");
        Assert.Null(await EnLaBaseAsync(producto.VarianteId));
        Assert.DoesNotContain((await pantalla.LeerAsync()).Elementos, e => e.Etiqueta == "img");
        Assert.Empty(pantalla.Errores);
    }

    [Fact]
    public async Task En_la_ficha_quien_no_edita_el_catalogo_solo_la_ve()
    {
        var producto = await ProductoAsync(62);
        var (a, _) = await AbrirAsync<FotoDeLaVariante>("cajero", "1111", (caja, sesion) => new()
        {
            [nameof(FotoDeLaVariante.Caja)] = caja,
            [nameof(FotoDeLaVariante.Sesion)] = sesion,
            [nameof(FotoDeLaVariante.VarianteId)] = producto.VarianteId,
            [nameof(FotoDeLaVariante.Nombre)] = producto.Descripcion,
            [nameof(FotoDeLaVariante.PuedeEditar)] = false,
        });
        await using var pantalla = a.Pantalla;

        var elementos = (await pantalla.LeerAsync()).Elementos;
        Assert.DoesNotContain(elementos, e => e.Etiqueta is "label" or "button");
        Assert.False((await pantalla.PorIdAsync($"foto-{producto.VarianteId:N}")).Atributos.ContainsKey("tabindex"));
    }
}
