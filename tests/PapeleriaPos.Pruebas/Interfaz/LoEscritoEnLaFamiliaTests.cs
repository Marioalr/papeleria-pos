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
}
