using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Web;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Ctrl+M en la venta de verdad (entrega 13, docs/propuesta-interfaz.md, «Lo que se señala: exhibidos y temporada»): la caja
/// con su copia contra el servicio de prueba. Primero la temporada que corre y luego el mostrador, cada pieza en su lugar; se
/// toca lo que el cliente señala (con un número antes, esa cantidad; también con las flechas y Enter) y queda en el ticket
/// sin preguntar a la red. En el verificador, tocar solo enseña el precio. El chip de la temporada abre Ctrl+M en ella. Lo
/// que se revisa no depende de lo que tarda la PC ni del día en que se corre: las temporadas de la prueba tienen fechas
/// alrededor de hoy, y las que deja la migración se quitan en esta base.
/// </summary>
public sealed class MostradorEnLaVentaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-mostrador-{Guid.NewGuid():N}.db");
    private readonly string _baseDelDueno = Path.Combine(Path.GetTempPath(), $"caja-mostrador-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
        ArchivosDePrueba.BorrarBase(_baseDelDueno);
    }

    /// <summary>
    /// Lo que se acomodó para las pruebas: productos de la copia que se venden por pieza, con su código. F no está acomodado:
    /// tiene además un código corto (<see cref="CodigoCorto"/>).
    /// </summary>
    private sealed record Acomodado(
        ProductoVentaDto A, ProductoVentaDto B, ProductoVentaDto C, ProductoVentaDto D, ProductoVentaDto E, ProductoVentaDto F, Guid Feria, Guid OtraFeria);

    /// <summary>Un código corto, como la etiqueta que se le pone a lo que no trae código de fábrica: también parece una cantidad.</summary>
    private const string CodigoCorto = "45";

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>
    /// Una sola vez por base (las pruebas de esta clase la comparten): sin las temporadas que deja la migración (corren según el
    /// día), dos que corren hoy («Feria de prueba» con A; «Otra feria» con E), una que no («Fuera de temporada», con B), el
    /// mostrador con B y C arriba y D abajo, una «Vitrina de prueba» para acomodar, y F con el código corto.
    /// </summary>
    private async Task<Acomodado> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var copia = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));
        var productos = copia.Codigos
            .Select(c => (Codigo: c, Producto: copia.Productos.FirstOrDefault(p => p.VarianteId == c.VarianteId)))
            .Where(x => x.Producto is { Unidad: UnidadBase.Pieza } p && p.Presentacion.Id == x.Codigo.PresentacionId && p.Codigo == x.Codigo.Codigo
                        && p.Precios.Any(precio => precio.PresentacionId is null))
            .Select(x => x.Producto!).DistinctBy(p => p.VarianteId).OrderBy(p => p.VarianteId).Skip(40).Take(6).ToList();
        var mostrador = await Leer<MostradorDto>(await http.GetAsync("api/mostrador"));
        Guid Id(MostradorDto m, string nombre) => m.Exhibidores.Single(e => e.Nombre == nombre).Id;
        if (mostrador.Exhibidores.All(e => e.Nombre != "Feria de prueba"))
        {
            foreach (var deLaMigracion in mostrador.Exhibidores.Where(e => e.Tipo == TipoExhibidor.Temporada))
                await Leer<MostradorDto>(await http.PostAsJsonAsync($"api/mostrador/{deLaMigracion.Id}/quitar", new SolicitudMostrador(dueno.UsuarioId, caja.SucursalId)));
            var hoy = Temporadas.Hoy(TimeProvider.System);
            async Task<Guid> TemporadaAsync(string nombre, DateOnly desde, DateOnly hasta, Guid producto)
            {
                var m = await Leer<MostradorDto>(await http.PostAsJsonAsync("api/mostrador",
                    new SolicitudNuevoExhibidor(dueno.UsuarioId, caja.SucursalId, nombre, TipoExhibidor.Temporada)));
                var id = Id(m, nombre);
                await Leer<MostradorDto>(await http.PutAsJsonAsync($"api/mostrador/{id}", new SolicitudCambiarExhibidor(dueno.UsuarioId, caja.SucursalId,
                    Fechas: new FechasTemporadaDto(DiaDelAnio.De(desde), DiaDelAnio.De(hasta)))));
                m = await Leer<MostradorDto>(await http.PostAsJsonAsync($"api/mostrador/{id}/lugares", new SolicitudLugar(dueno.UsuarioId, caja.SucursalId, "Mesa")));
                await Leer<MostradorDto>(await http.PutAsJsonAsync("api/mostrador/acomodo", new SolicitudAcomodo(dueno.UsuarioId, caja.SucursalId,
                    [new LugarConProductosDto(m.Exhibidores.Single(e => e.Id == id).Lugares[0].Id, [producto])])));
                return id;
            }
            // Alrededor de hoy (también si hoy es fin de año: cruzan el año), y una lejos de hoy.
            await TemporadaAsync("Feria de prueba", hoy.AddDays(-1), hoy.AddDays(1), productos[0].VarianteId);
            await TemporadaAsync("Otra feria", hoy.AddDays(-2), hoy.AddDays(3), productos[4].VarianteId);
            await TemporadaAsync("Fuera de temporada", hoy.AddDays(60), hoy.AddDays(70), productos[1].VarianteId);
            var repisas = mostrador.Exhibidores.Single(e => e.Nombre == "Mostrador").Lugares;
            await Leer<MostradorDto>(await http.PutAsJsonAsync("api/mostrador/acomodo", new SolicitudAcomodo(dueno.UsuarioId, caja.SucursalId,
                [new LugarConProductosDto(repisas[0].Id, [productos[1].VarianteId, productos[2].VarianteId]), new LugarConProductosDto(repisas[1].Id, [productos[3].VarianteId])])));
            var vitrina = await Leer<MostradorDto>(await http.PostAsJsonAsync("api/mostrador",
                new SolicitudNuevoExhibidor(dueno.UsuarioId, caja.SucursalId, "Vitrina de prueba", TipoExhibidor.Exhibidor)));
            var conLugar = await Leer<MostradorDto>(await http.PostAsJsonAsync($"api/mostrador/{Id(vitrina, "Vitrina de prueba")}/lugares",
                new SolicitudLugar(dueno.UsuarioId, caja.SucursalId, "Arriba")));
            await Leer<MostradorDto>(await http.PutAsJsonAsync("api/mostrador/acomodo", new SolicitudAcomodo(dueno.UsuarioId, caja.SucursalId,
                [new LugarConProductosDto(conLugar.Exhibidores.Single(e => e.Nombre == "Vitrina de prueba").Lugares[0].Id, [productos[1].VarianteId])])));
            mostrador = await Leer<MostradorDto>(await http.GetAsync("api/mostrador"));
            await using var db = servicio.Base();
            db.CodigosBarras.Add(new CodigoBarras { Codigo = CodigoCorto, PresentacionId = productos[5].Presentacion.Id, Origen = OrigenCodigo.Interno });
            await db.SaveChangesAsync();
        }
        await servicio.CatalogoAlDiaAsync();
        return new Acomodado(productos[0], productos[1], productos[2], productos[3], productos[4], productos[5], Id(mostrador, "Feria de prueba"),
            Id(mostrador, "Otra feria"));
    }

    private Task<VentaConCopia> AbrirAsync((string Usuario, string Pin)? quien = null) =>
        VentaConCopia.AbrirAsync(servicio, quien is null ? _baseLocal : _baseDelDueno, fondoAparte: true, quien: quien);

    private static Task TeclaAsync(VentaConCopia v, string tecla, string? texto = "") => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, texto));

    /// <summary>Lo que manda caja.js con el mostrador abierto y el cursor en el cuadro de captura.</summary>
    private static Task EnMostradorAsync(VentaConCopia v, string tecla, string texto = "", bool rafaga = false) =>
        v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.EnMostrador(tecla, texto, 1234.5, rafaga));

    private static async Task<string> LugarElegidoAsync(VentaConCopia v) =>
        PantallaSinNavegador.Limpio((await v.Pantalla.ElementoAsync(e => e.Atributos.GetValueOrDefault("role") as string == "tab" && e.Clase == "on",
            "el exhibidor o la temporada elegida")).Texto);

    private static async Task<bool> PanelAbiertoAsync(VentaConCopia v) =>
        (await v.Pantalla.LeerAsync()).Elementos.Any(e => e.Clase?.StartsWith("vn-mostrador-panel", StringComparison.Ordinal) == true);

    [Fact]
    public async Task Ctrl_M_abre_primero_la_temporada_que_corre_y_tocar_agrega_sin_preguntar_a_la_red()
    {
        var p = await PrepararAsync();
        await using var v = await AbrirAsync();

        await TeclaAsync(v, "Ctrl+M");
        var texto = await v.Pantalla.TextoAsync();
        var lugares = (await v.Pantalla.LeerAsync()).Elementos.Where(e => e.Atributos.GetValueOrDefault("role") as string == "tab")
            .Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        // Las temporadas que corren, luego los exhibidores; la que no corre no sale.
        Assert.Equal(["Feria de prueba", "Otra feria", "Mostrador", "Vitrina de prueba"], lugares);
        Assert.Contains("Toca lo que el cliente señala. Con un número en el cuadro, agrega esa cantidad.", texto);
        Assert.DoesNotContain("Fuera de temporada", texto);
        var pieza = await v.Pantalla.PorIdAsync("pieza-0");
        Assert.Contains(p.A.Descripcion, pieza.Texto);
        Assert.Contains(Formato.Dinero(PrecioDeMenudeo.De(p.A.Precios, v.Caja.SucursalId)), pieza.Texto);
        Assert.Contains(MostradorEnPantalla.Iniciales(p.A.Descripcion), pieza.Texto);

        await v.Pantalla.OprimirAsync(pieza);

        // Una pieza, el panel se cierra y queda el total, listo para F12; ni el mostrador ni el producto se le pidieron a la red.
        Assert.StartsWith($"1 {p.A.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.False(await PanelAbiertoAsync(v));
        Assert.Contains(Formato.Dinero(PrecioDeMenudeo.De(p.A.Precios, v.Caja.SucursalId)), await v.Pantalla.TextoAsync());
        Assert.Equal(0, v.Red.Cuantas("/api/mostrador"));
        Assert.Equal(0, v.Red.Cuantas("/api/productos"));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Un_numero_antes_agrega_esa_cantidad_tambien_con_las_flechas_y_Enter()
    {
        var p = await PrepararAsync();
        await using var v = await AbrirAsync();

        // «3» en el cuadro, Ctrl+M, Tab hasta el mostrador y tocar la segunda de arriba.
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("3"));
        await TeclaAsync(v, "Ctrl+M", "3");
        await EnMostradorAsync(v, "Tab", "3");
        await EnMostradorAsync(v, "Tab", "3");
        Assert.Equal("Mostrador", await LugarElegidoAsync(v));
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("pieza-1"));

        // Con el teclado: Ctrl+M, al mostrador, ↓ a la repisa de abajo, «2» y Enter.
        await TeclaAsync(v, "Ctrl+M");
        await EnMostradorAsync(v, "Shift+Tab");
        await EnMostradorAsync(v, "Shift+Tab");
        Assert.Equal("Mostrador", await LugarElegidoAsync(v));
        await EnMostradorAsync(v, "ArrowDown");
        Assert.Contains(p.D.Descripcion, (await v.Pantalla.ElementoAsync(e => e.Clase == "vn-pieza activa", "la pieza elegida")).Texto);
        await EnMostradorAsync(v, "Enter", "2");

        // Y sin número, una: Enter con la primera elegida.
        await TeclaAsync(v, "Ctrl+M");
        await EnMostradorAsync(v, "Enter");

        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(3, renglones.Count);
        Assert.StartsWith($"3 {p.C.Descripcion}", renglones[0]);
        Assert.StartsWith($"2 {p.D.Descripcion}", renglones[1]);
        Assert.StartsWith($"1 {p.A.Descripcion}", renglones[2]);
        Assert.False(await PanelAbiertoAsync(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Con el mostrador abierto el cursor sigue en el cuadro: lo que escanea el lector (una ráfaga) o un código tecleado entra
    /// al ticket como siempre, nunca como la cantidad de la pieza elegida; y lo que no es una cantidad no se toma como tal.
    /// </summary>
    [Fact]
    public async Task Un_escaneo_con_el_mostrador_abierto_entra_al_ticket_y_no_es_una_cantidad()
    {
        var p = await PrepararAsync();
        await using var v = await AbrirAsync();

        await TeclaAsync(v, "Ctrl+M");
        await EnMostradorAsync(v, "Enter", p.B.Codigo!, rafaga: true);
        Assert.False(await PanelAbiertoAsync(v));
        await TeclaAsync(v, "Ctrl+M");
        await EnMostradorAsync(v, "Enter", p.C.Codigo!);
        // Lo que se escribe que no es una cantidad no se agrega al tocar: lo dice el panel.
        await TeclaAsync(v, "Ctrl+M");
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("cuaderno"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("pieza-0"));
        Assert.Contains("Antes de tocar una pieza solo va la cantidad, de hasta cuatro cifras: «cuaderno» no lo es.", await v.Pantalla.TextoAsync());
        // Una fracción en algo que se vende por pieza tampoco: el panel se queda, para corregirla.
        await EnMostradorAsync(v, "Enter", "1.5");
        Assert.True(await PanelAbiertoAsync(v));
        Assert.Contains($"«{p.A.Descripcion}» se vende por pieza: escribe cuántas, sin fracciones.", await v.Pantalla.TextoAsync());

        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.StartsWith($"1 {p.B.Descripcion}", renglones[0]);
        Assert.StartsWith($"1 {p.C.Descripcion}", renglones[1]);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Un código corto que existe («45») con el mostrador abierto, tecleado o del lector sin reconocerse por su ritmo, entra al
    /// ticket como un escaneo y no como 45 de la pieza elegida; con «*», «45*», sí es la cantidad. Y tocar una pieza con un
    /// código largo en el cuadro no agrega billones: antes de tocar solo va una cantidad de hasta cuatro cifras.
    /// </summary>
    [Fact]
    public async Task Un_codigo_corto_con_el_mostrador_abierto_es_un_escaneo_y_uno_largo_no_es_la_cantidad()
    {
        var p = await PrepararAsync();
        await using var v = await AbrirAsync();

        await TeclaAsync(v, "Ctrl+M");
        await EnMostradorAsync(v, "Enter", CodigoCorto);
        Assert.False(await PanelAbiertoAsync(v));
        await TeclaAsync(v, "Ctrl+M");
        await EnMostradorAsync(v, "Enter", $"{CodigoCorto}*");
        Assert.False(await PanelAbiertoAsync(v));

        // Un número largo que se quedó en el cuadro (no es ningún código: su dígito verificador no cuadra) y un código.
        await TeclaAsync(v, "Ctrl+M");
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("7501234567890"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("pieza-0"));
        Assert.True(await PanelAbiertoAsync(v));
        Assert.Contains("Antes de tocar una pieza solo va la cantidad, de hasta cuatro cifras: «7501234567890» no lo es.", await v.Pantalla.TextoAsync());
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo(p.B.Codigo!));
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("pieza-0"));
        Assert.True(await PanelAbiertoAsync(v));
        Assert.Contains($"«{p.B.Codigo}» es el código de {p.B.Descripcion}: Enter lo agrega.", await v.Pantalla.TextoAsync());

        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.StartsWith($"1 {p.F.Descripcion}", renglones[0]);
        Assert.StartsWith($"45 {p.A.Descripcion}", renglones[1]);
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task En_el_verificador_tocar_solo_ensenia_el_precio_y_se_puede_agregar()
    {
        var p = await PrepararAsync();
        await using var v = await AbrirAsync();

        await TeclaAsync(v, "F9");
        await TeclaAsync(v, "Ctrl+M");
        Assert.Contains("Verificador: tocar uno solo muestra su precio.", await v.Pantalla.TextoAsync());
        // El cuadro, en ámbar: lo que se escanee ahí solo enseña el precio.
        Assert.Contains("verificar", (await v.Pantalla.ElementoAsync(e => e.Clase?.StartsWith("vn-captura ", StringComparison.Ordinal) == true, "el cuadro")).Clase);
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("pieza-0"));

        var verificado = await v.Pantalla.TextoAsync();
        Assert.Contains("Verificador de precios", verificado);
        Assert.Contains(p.A.Descripcion, verificado);
        Assert.Contains(Formato.Dinero(PrecioDeMenudeo.De(p.A.Precios, v.Caja.SucursalId)), verificado);
        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Agregarlo al ticket"));
        Assert.StartsWith($"1 {p.A.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.DoesNotContain("Verificador de precios", await v.Pantalla.TextoAsync());

        // Un escaneo con el mostrador en el verificador tampoco agrega: enseña su precio.
        await TeclaAsync(v, "F9");
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Mostrador y temporada"));
        await EnMostradorAsync(v, "Enter", p.C.Codigo!, rafaga: true);
        await v.Pantalla.EsperarAsync(t => t.Contains(p.C.Descripcion, StringComparison.Ordinal), "el precio de lo escaneado");
        Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Cada temporada que corre tiene su chip arriba; tocarlo abre Ctrl+M en ella. En las páginas de atrás no sale.</summary>
    [Fact]
    public async Task El_chip_de_cada_temporada_que_corre_abre_Ctrl_M_en_ella()
    {
        var p = await PrepararAsync();
        await using var v = await AbrirAsync();

        var chips = (await v.Pantalla.LeerAsync()).Elementos.Where(e => e.Clase?.Contains("vn-chip-temporada", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(["Feria de prueba Ctrl+M", "Otra feria Ctrl+M"], chips.Select(c => PantallaSinNavegador.Limpio(c.Texto)));
        await v.Pantalla.OprimirAsync(chips[1]);

        Assert.True(await PanelAbiertoAsync(v));
        Assert.Equal("Otra feria", await LugarElegidoAsync(v));
        Assert.Contains(p.E.Descripcion, (await v.Pantalla.PorIdAsync("pieza-0")).Texto);

        // En Clientes (una página), sin chip.
        await TeclaAsync(v, "Escape");
        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Clientes", "la pestaña Clientes"));
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Clase?.Contains("vn-chip-temporada", StringComparison.Ordinal) == true);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Sin la caja principal, Ctrl+M sale de la copia y lo que se toca se vende con ella.</summary>
    [Fact]
    public async Task Sin_la_red_el_mostrador_se_vende_con_la_copia()
    {
        var p = await PrepararAsync();
        await using var v = await AbrirAsync();
        (v.Red.Caida, v.Fondo.Caida) = (true, true);
        v.Contingencia.MarcarSinConexion();
        Assert.False(v.Contingencia.EnLinea);

        await TeclaAsync(v, "Ctrl+M");
        await EnMostradorAsync(v, "Tab");
        await EnMostradorAsync(v, "Tab");
        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("pieza-0"));

        Assert.StartsWith($"1 {p.B.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Equal(0, v.Red.Cuantas("/api/mostrador"));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// «Acomodar» solo con «Productos, precios y etiquetas» (el cajero no lo ve). El dueño agrega un producto buscándolo, lo
    /// mueve con Shift+→ y lo quita con Supr; cada cosa se guarda al momento en la caja principal.
    /// </summary>
    [Fact]
    public async Task Acomodar_es_de_quien_edita_el_catalogo_y_se_guarda_al_momento()
    {
        var p = await PrepararAsync();
        await using (var cajero = await AbrirAsync())
        {
            await TeclaAsync(cajero, "Ctrl+M");
            Assert.DoesNotContain("Acomodar", await cajero.Pantalla.TextoAsync());
        }
        await using var v = await AbrirAsync(("dueno", "3333"));
        var http = servicio.Fabrica.CreateClient();
        async Task<List<Guid>> VitrinaAsync() =>
            (await Leer<MostradorDto>(await http.GetAsync("api/mostrador"))).Exhibidores.Single(e => e.Nombre == "Vitrina de prueba").Lugares[0].Productos.ToList();

        await TeclaAsync(v, "Ctrl+M");
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Acomodar"));
        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Atributos.GetValueOrDefault("role") as string == "tab"
            && PantallaSinNavegador.Limpio(e.Texto) == "Vitrina de prueba", "la vitrina"));
        Assert.Contains("Acomodar: así queda en todas las cajas.", await v.Pantalla.TextoAsync());
        // Al acomodar también salen las temporadas que no corren (se preparan antes).
        Assert.Contains("Fuera de temporada", await v.Pantalla.TextoAsync());

        // Se busca por el nombre (sale en la lista) o se escanea el código y Enter: se agrega después de la elegida.
        await v.Pantalla.EscribirAsync("mostrador-agregar", p.D.Descripcion);
        Assert.Contains(p.D.Descripcion, (await v.Pantalla.ElementoAsync(e => e.Clase == "vn-acomodar-res", "los resultados")).Texto);
        await v.Pantalla.EscribirAsync("mostrador-agregar", p.D.Codigo!);
        await v.Pantalla.TeclaAsync("mostrador-agregar", "Enter");
        Assert.Equal([p.B.VarianteId, p.D.VarianteId], await VitrinaAsync());
        // La que llegó queda elegida: Shift+← la pone antes.
        await v.Pantalla.DispararAsync(await v.Pantalla.PorIdAsync("mostrador-agregar"), "onkeydown", new KeyboardEventArgs { Key = "ArrowLeft", ShiftKey = true });
        Assert.Equal([p.D.VarianteId, p.B.VarianteId], await VitrinaAsync());
        await v.Pantalla.TeclaAsync("mostrador-agregar", "Delete");
        Assert.Equal([p.B.VarianteId], await VitrinaAsync());

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Listo"));
        Assert.Contains("Toca lo que el cliente señala.", await v.Pantalla.TextoAsync());
        Assert.Empty(v.Pantalla.Errores);
    }
}
