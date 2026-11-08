using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// En el renglón de una familia, Enter agrega lo que se escribió, no el más vendido (el dueño, al probar la caja 2 con la base
/// real, 2026-11-07: «el renglón junta varios y Enter agrega el más vendido, no el que escribí»). Con los listones de eleventa,
/// que se venden por metro: «liston 1» también encuentra el 1.5 y el 12.0, y el 1.5 es el que más se vende. La base de prueba
/// trae además sus listones de mentiras («Listón 1 cm Rojo»…): con «colores» solo salen estos, y su renglón dice «3 tamaños».
/// </summary>
public sealed class LoEscritoEnLaFamiliaTests : IAsyncLifetime
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-familia-{Guid.NewGuid():N}.db");
    private ServicioDePrueba _servicio = null!;

    /// <summary>Los tres listones, como se llaman en eleventa, con un código cada uno.</summary>
    private static readonly (string Nombre, string Codigo)[] Listones =
    [
        ("Liston 1.0 V Colores X Metro", "9900000011001"), ("Liston 1.5 V/Colores X Metro", "9900000011002"),
        ("Liston 12.0 V Colores X Metro", "9900000011003"),
    ];

    private const string ElDe1 = "Liston 1.0 V Colores X Metro";
    private const string ElMasVendido = "Liston 1.5 V/Colores X Metro";

    /// <summary>Uno propio: los listones y su venta no se cruzan con las demás pruebas.</summary>
    public async Task InitializeAsync()
    {
        _servicio = await ServicioDePrueba.CrearAsync();
        var http = _servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var opciones = (await http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!;
        var variantes = new Dictionary<string, VarianteFichaDto>();
        foreach (var (nombre, codigo) in Listones)
        {
            var ficha = await Leer<FichaProductoDto>(await http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(encargado.UsuarioId,
                caja.SucursalId, new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
                [
                    new VarianteFichaDto(null, "", UnidadBase.Metro, true, [new PresentacionFichaDto(null, "Metro", 1000, true, [new CodigoFichaDto(codigo)])],
                        [new PrecioFichaDto(null, 1000, 800)]),
                ]))));
            variantes[nombre] = ficha.Variantes[0];
        }
        // El 1.5 es el más vendido: 2 metros.
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        var masVendido = variantes[ElMasVendido];
        await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(masVendido.Id!.Value, masVendido.Presentaciones[0].Id!.Value, 2000)], [new CobroSolicitado(FormaPago.Efectivo, 2000)])));
        // Lo vendido se cuenta una vez al día; aquí, como si fuera otro día.
        _servicio.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.CatalogoEnMemoria>().Invalidar();
        await _servicio.CatalogoAlDiaAsync();
    }

    public async Task DisposeAsync()
    {
        await _servicio.DisposeAsync();
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Lo que dice abajo el renglón de estos listones (los de mentiras salen en otros).</summary>
    private static async Task<string> SubDelRenglonAsync(VentaConCopia v) =>
        PantallaSinNavegador.Limpio((await v.Pantalla.ElementoAsync(e => e.Clase == "vn-res-sub" && e.Texto.Contains("3 tamaños"), "el renglón de los listones")).Texto);

    [Fact]
    public async Task Enter_en_el_renglon_de_la_familia_agrega_lo_escrito_con_sus_metros()
    {
        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);
        // Sin decir la medida: el más vendido, como antes.
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("liston colores"));
        Assert.Equal("3 tamaños · Enter agrega 1.5, el más vendido", await SubDelRenglonAsync(v));
        // Con la medida, esa; la cantidad en metros va antes del asterisco, como «0.8*cop» con un código.
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("0.8*liston 1 colores"));
        Assert.Equal("3 tamaños · Enter agrega 1.0, lo que escribiste", await SubDelRenglonAsync(v));
        await v.EscanearAsync("0.8*liston 1 colores");

        Assert.StartsWith($"0.8 {ElDe1}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Enter_sin_abrir_la_lista_y_lo_que_abre_la_flecha_usan_la_misma_regla()
    {
        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);
        // Enter antes de que salga la lista (como el lector de la PC lenta): el nombre se busca ahí mismo, con la misma regla.
        await v.EscanearAsync("1.5*liston 1 colores");
        Assert.StartsWith($"1.5 {ElDe1}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));

        // → despliega la familia con el cursor en lo que agrega Enter; la etiqueta «más vendido» se queda en el 1.5.
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("liston 1 colores"));
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla("ArrowRight", "liston 1 colores"));
        var (_, elementos) = await v.Pantalla.LeerAsync();
        var activo = elementos.Single(e => e.Clase?.Split(' ') is { } c && c.Contains("vn-res-valor") && c.Contains("activa"));
        Assert.Contains("1.0", PantallaSinNavegador.Limpio(activo.Texto));
        var conEtiqueta = elementos.Single(e => e.Clase?.Split(' ').Contains("vn-res-valor") == true && e.Texto.Contains("más vendido"));
        Assert.Contains("1.5", PantallaSinNavegador.Limpio(conEtiqueta.Texto));
        Assert.Empty(v.Pantalla.Errores);
    }

    // ── Buscar dentro de la familia desplegada y elegir varios (pedidos del dueño, 2026-11-07) ──

    private const string ElDe12 = "Liston 12.0 V Colores X Metro";

    /// <summary>Lo que dice la línea de arriba de los valores al buscar dentro de la familia.</summary>
    private static async Task<string?> FiltroAsync(VentaConCopia v) =>
        (await v.Pantalla.LeerAsync()).Elementos.FirstOrDefault(e => e.Clase == "vn-res-filtro") is { } f ? PantallaSinNavegador.Limpio(f.Texto) : null;

    private static async Task<int> ValoresALaVistaAsync(VentaConCopia v) =>
        (await v.Pantalla.LeerAsync()).Elementos.Count(e => e.Clase?.Split(' ').Contains("vn-res-valor") == true);

    private static Task DesplegarAsync(VentaConCopia v, string texto) => v.Pantalla.Dispatcher.InvokeAsync(async () =>
    {
        await v.Venta.Escribiendo(texto);
        await v.Venta.Tecla("ArrowRight", texto);
    });

    private static Task EscribirAsync(VentaConCopia v, string texto) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo(texto));

    private static Task TeclaAsync(VentaConCopia v, string tecla, string texto) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, texto));

    [Fact]
    public async Task Con_la_familia_desplegada_lo_que_se_escribe_busca_dentro_de_ella()
    {
        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);
        await DesplegarAsync(v, "liston colores");
        Assert.Equal(3, await ValoresALaVistaAsync(v));
        Assert.StartsWith("Escribe para buscar entre estos tamaños", await FiltroAsync(v));

        // «12» deja solo el 12.0 (no el 1.0 ni el 1.5): cada palabra escrita empieza una del valor.
        await EscribirAsync(v, "12");
        Assert.Equal(1, await ValoresALaVistaAsync(v));
        Assert.StartsWith("«12»: 1 de 3", await FiltroAsync(v));
        // Lo que no está en la familia lo dice, y la lista no se busca otra vez.
        await EscribirAsync(v, "azul");
        Assert.Equal(0, await ValoresALaVistaAsync(v));
        Assert.StartsWith("Ninguno dice «azul»", await FiltroAsync(v));

        // Esc (o ←) cierra la familia: lo buscado regresa y la lista sigue como estaba.
        await TeclaAsync(v, "Escape-familia", "azul");
        Assert.Null(await FiltroAsync(v));
        Assert.Equal(0, await ValoresALaVistaAsync(v));
        Assert.Contains("3 tamaños", await SubDelRenglonAsync(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Enter_dentro_de_la_familia_agrega_el_que_quedo_con_la_cantidad_de_antes()
    {
        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);
        // La cantidad se queda en el cuadro al desplegar: «2*» y luego «12».
        await DesplegarAsync(v, "2*liston colores");
        await EscribirAsync(v, "2*12");
        await v.EscanearAsync("2*12");
        Assert.StartsWith($"2 {ElDe12}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));

        // Sin escribir nada después de desplegar, Enter (el cuadro vacío) agrega donde está el cursor: lo que agrega su renglón.
        await DesplegarAsync(v, "liston colores");
        await v.EscanearAsync("");
        Assert.Equal(2, (await VentaConCopia.RenglonesAsync(v.Pantalla)).Count);
        Assert.Contains(await VentaConCopia.RenglonesAsync(v.Pantalla), r => r.StartsWith($"1 {ElMasVendido}", StringComparison.Ordinal));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Lo_que_lee_el_lector_con_la_familia_desplegada_entra_por_su_codigo()
    {
        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);
        await DesplegarAsync(v, "liston colores");
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escaneado("9900000011003", delLector: true));
        Assert.StartsWith($"1 {ElDe12}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Ctrl_Enter_aparta_varios_con_su_globito_y_Enter_agrega_solo_los_apartados()
    {
        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);
        await DesplegarAsync(v, "liston colores");
        await EscribirAsync(v, "12");
        await TeclaAsync(v, "Ctrl+Enter", "12");
        // Otra vez con la cantidad: «3*1.0» aparta 3 del 1.0, y repetirlo le suma.
        await EscribirAsync(v, "3*1.0");
        await TeclaAsync(v, "Ctrl+Enter", "3*1.0");
        await TeclaAsync(v, "Ctrl+Enter", "3*1.0");
        var (_, elementos) = await v.Pantalla.LeerAsync();
        var tira = PantallaSinNavegador.Limpio(elementos.Single(e => e.Clase == "vn-apartados").Texto);
        Assert.Contains($"1{ElDe12}".Replace(" ", ""), tira.Replace(" ", ""), StringComparison.Ordinal);
        Assert.Contains($"26×{ElDe1}".Replace(" ", ""), tira.Replace(" ", ""), StringComparison.Ordinal);
        Assert.Contains(elementos, e => e.Clase == "vn-apartado" && PantallaSinNavegador.Limpio(e.Texto) == "2");
        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));

        // El cursor en el 1.5 sin apartarlo: Enter agrega solo los dos apartados, en orden.
        await EscribirAsync(v, "1.5");
        await v.EscanearAsync("1.5");
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.Contains(renglones, r => r.StartsWith($"1 {ElDe12}", StringComparison.Ordinal));
        Assert.Contains(renglones, r => r.StartsWith($"6 {ElDe1}", StringComparison.Ordinal));
        Assert.DoesNotContain(renglones, r => r.Contains(ElMasVendido, StringComparison.Ordinal));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Con_apartados_F12_no_cobra_sin_ellos()
    {
        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);
        await v.EscanearAsync("9900000011001");
        await DesplegarAsync(v, "liston colores");
        await EscribirAsync(v, "12");
        await TeclaAsync(v, "Ctrl+Enter", "12");
        await TeclaAsync(v, "F12", "12");

        Assert.Contains("Hay 1 producto apartado sin agregar", PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));
        Assert.Contains((await v.Pantalla.LeerAsync()).Elementos, e => e.Clase == "vn-apartados");
        // Lo escrito tampoco se agregó al ir al cobro: sigue solo el del principio.
        Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        await v.EscanearAsync("");
        Assert.Equal(2, (await VentaConCopia.RenglonesAsync(v.Pantalla)).Count);
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Supr_quita_el_ultimo_apartado_y_Esc_los_quita_todos()
    {
        await using var v = await VentaConCopia.AbrirAsync(_servicio, _baseLocal);
        await DesplegarAsync(v, "liston colores");
        await EscribirAsync(v, "12");
        await TeclaAsync(v, "Ctrl+Enter", "12");
        await EscribirAsync(v, "1.0");
        await TeclaAsync(v, "Ctrl+Enter", "1.0");
        await TeclaAsync(v, "Delete-apartado", "1.0");
        await v.EscanearAsync("1.0");
        Assert.StartsWith($"1 {ElDe12}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));

        await DesplegarAsync(v, "liston colores");
        await TeclaAsync(v, "Ctrl+Enter", "");
        await TeclaAsync(v, "Escape", "");
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Clase == "vn-apartados");
        await v.EscanearAsync("");
        Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Empty(v.Pantalla.Errores);
    }
}
