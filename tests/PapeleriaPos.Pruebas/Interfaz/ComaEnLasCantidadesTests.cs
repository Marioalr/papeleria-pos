using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Las cantidades y los porcentajes de la caja con la misma regla que el dinero (revisión del PR #138): el punto es el decimal y
/// la coma solo separa los miles, de tres en tres. Antes, según la pantalla, «2,5» eran 25 (la venta: «2,5*código» cobraba 25) o
/// 2.5 (el mostrador). Ahora «2,5» se pide con punto, ahí donde está el cajero, y «1,000» son mil en todas. En la venta, lo
/// rechazado no agrega nada y regresa al cuadro de captura con su código (no se pierde), sin preguntarle nada a la red.
/// </summary>
public sealed class ComaEnLasCantidadesTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private const string DosYMedio = "Los decimales van con punto: 2.5 (la coma solo separa los miles: 1,000).";

    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-cantidades-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private static Task TeclaAsync(VentaConCopia v, string tecla, string? texto = "") => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, texto));

    /// <summary>Lo que dice la venta bajo el cuadro de captura.</summary>
    private static async Task<string> AvisoAsync(VentaConCopia v) =>
        PantallaSinNavegador.Limpio((await v.Pantalla.ElementoAsync(e => e.Clase?.StartsWith("vn-aviso", StringComparison.Ordinal) == true,
            "el aviso bajo el cuadro")).Texto);

    /// <summary>Lo último que la venta regresó al cuadro de captura (caja.js ya lo había quitado con el Enter).</summary>
    private static string? EnElCuadro(PaginaQueContesta pagina) =>
        pagina.Llamadas.LastOrDefault(l => l.Funcion == "escribirCaptura").Argumentos?[0] as string;

    /// <summary>
    /// «2,5*código» no agrega nada (antes eran 25 piezas): el aviso bajo el cuadro, y lo escaneado regresa al cuadro para
    /// corregirlo. Un código que no está en la copia tampoco se busca (ni «buscando…» ni la red). «1,000*código» son mil, y «2,5*»
    /// sin código (la cantidad del renglón) tampoco pasa.
    /// </summary>
    [Fact]
    public async Task Dos_coma_cinco_por_codigo_no_agrega_ni_pierde_el_codigo_y_mil_por_codigo_son_mil()
    {
        var pagina = new PaginaQueContesta();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina);
        var (a, _) = v.DosDeLaCopia();

        await v.EscanearAsync($"2,5*{a.Codigo}");
        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains(DosYMedio, await AvisoAsync(v));
        Assert.Equal($"2,5*{a.Codigo}", EnElCuadro(pagina));

        var preguntas = v.Red.Cuantas("/api/productos/codigo/");
        await v.EscanearAsync("2,5*9900000002018");
        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Equal(preguntas, v.Red.Cuantas("/api/productos/codigo/"));
        Assert.Equal("2,5*9900000002018", EnElCuadro(pagina));

        await v.EscanearAsync($"1,000*{a.Codigo}");
        Assert.StartsWith($"1,000 {a.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        await v.EscanearAsync("2,5*");
        Assert.StartsWith($"1,000 {a.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Contains(DosYMedio, await AvisoAsync(v));
        Assert.Equal("2,5*", EnElCuadro(pagina));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// «Varios» (Ins): «2,5» como cantidad, sola o con el código («2,5*código»), se pide con punto en el panel y regresa al
    /// cuadro; nada se agrega. «1,000» son mil.
    /// </summary>
    [Fact]
    public async Task Varios_pide_los_decimales_con_punto_y_mil_son_mil()
    {
        var pagina = new PaginaQueContesta();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, fondoAparte: true, pagina: pagina);
        var (a, _) = v.DosDeLaCopia();
        Task EnVariosAsync(string texto, bool rafaga = false) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.EnVarios(texto, 1234.5, rafaga));
        async Task<string> PanelAsync() =>
            PantallaSinNavegador.Limpio((await v.Pantalla.ElementoAsync(e => e.Clase == "vn-varios", "el panel de «Varios»")).Texto);

        await TeclaAsync(v, "Insert");
        await EnVariosAsync("2,5");
        Assert.Contains(DosYMedio, await PanelAsync());
        Assert.Equal("2,5", EnElCuadro(pagina));
        await EnVariosAsync($"2,5*{a.Codigo}");
        Assert.Contains(DosYMedio, await PanelAsync());
        Assert.Equal($"2,5*{a.Codigo}", EnElCuadro(pagina));
        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));

        await EnVariosAsync("1,000");
        Assert.Contains("Cantidad 1,000", await PanelAsync());
        await EnVariosAsync(a.Codigo!, rafaga: true);
        Assert.StartsWith($"1,000 {a.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Empty(v.Pantalla.Errores);
    }

    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Encargado);

    private async Task<Caja> PrepararAsync()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var cliente = new ClienteCaja(servicio.Fabrica.CreateClient(), conexion);
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("encargado", "2222"));
    }

    /// <summary>
    /// El conteo del día: «2,5» se pide con punto (antes contaba 25) y no se cuenta nada; «1,000» cuenta mil.
    /// </summary>
    [Fact]
    public async Task El_conteo_del_dia_pide_los_decimales_con_punto_y_mil_son_mil()
    {
        var c = await PrepararAsync();
        var conteo = await c.Cliente.ConteoDelDiaAsync(c.Datos.SucursalId, c.Encargado.UsuarioId);
        var indice = conteo.Renglones.ToList().FindIndex(r => r.Contado is null);
        var renglon = conteo.Renglones[indice];
        await using var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<ConteoDelDia>(new() { [nameof(ConteoDelDia.Caja)] = c.Datos, [nameof(ConteoDelDia.Sesion)] = c.Encargado });
        var campo = $"contar-{indice}";
        await p.EsperarElementoAsync(campo);

        await p.EscribirAsync(campo, "2,5");
        await p.TeclaAsync(campo, "Enter");
        Assert.Contains($"«{renglon.Producto}»: {DosYMedio}", await p.TextoAsync());
        Assert.Equal(conteo.Contados, (await c.Cliente.ConteoDelDiaAsync(c.Datos.SucursalId, c.Encargado.UsuarioId)).Contados);

        await p.EscribirAsync(campo, "1,000");
        await p.TeclaAsync(campo, "Enter");
        var despues = await c.Cliente.ConteoDelDiaAsync(c.Datos.SucursalId, c.Encargado.UsuarioId);
        Assert.Equal(1000m, despues.Renglones.Single(r => r.VarianteId == renglon.VarianteId).Contado);
        Assert.Empty(p.Errores);
    }
}
