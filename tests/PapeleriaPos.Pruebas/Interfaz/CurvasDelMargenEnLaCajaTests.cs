using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Las curvas del margen en Configuración › Impresoras e impresiones (2026-11-08), sin navegador y contra el servicio de verdad: se ven
/// (con su gráfica y el probador) antes de elegirlas, cada punto se escribe con sus cantidades exactas y se guarda con Enter como los demás
/// renglones, se agregan y se quitan puntos, y al elegir «Por curvas» se cobra con ellas.
/// </summary>
public sealed class CurvasDelMargenEnLaCajaTests
{
    private sealed class NavegacionDePrueba : NavigationManager
    {
        public NavegacionDePrueba() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }

    private static ServiceProvider Servicios(ConexionCaja conexion, HttpClient http, PaginaQueContesta? pagina = null) =>
        new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(http, conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(pagina ?? new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .AddSingleton(new RendimientoCaja())
            .AddSingleton(new InstalacionDeLaCaja(null))
            .AddSingleton<NavigationManager>(new NavegacionDePrueba())
            .BuildServiceProvider();

    private static async Task<T> HastaQue<T>(Func<Task<T>> leer, Func<T, bool> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        T valor;
        while (!condicion(valor = await leer()) && DateTime.UtcNow < limite)
            await Task.Delay(50);
        return valor;
    }

    [Fact]
    public async Task Las_curvas_se_escriben_punto_por_punto_se_prueban_y_al_elegirlas_se_cobra_con_ellas()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var caja = (await propio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = Servicios(conexion, propio.Fabrica.CreateClient());
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var dueno = await cliente.EntrarAsync("dueno", "3333");
        async Task<CostosImpresionDto> Costos() => (await cliente.CostosImpresionAsync(caja.SucursalId, dueno.UsuarioId))!.Costos;
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;
        await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = dueno });

        // Antes de elegirlas ya se ven, con las sugeridas, su gráfica y el probador, y dicen que todavía no se cobra con ellas.
        await p.EsperarElementoAsync("curva-tinta-x-0");
        await p.EsperarAsync(t => t.Contains("Todavía no se cobra con ellas") && t.Contains("Margen de cada cara"), "las curvas y el probador");
        Assert.Equal("5", (await p.PorIdAsync("curva-tinta-x-0")).Valor);
        Assert.Contains((await p.LeerAsync()).Elementos, e => e.Etiqueta == "polyline");

        // Un punto con sus cantidades exactas, con Enter.
        await p.DispararAsync(await p.PorIdAsync("curva-tinta-y-0"), "onfocus", new FocusEventArgs { Type = "focus" });
        await p.EscribirAsync("curva-tinta-y-0", "300");
        await p.TeclaAsync("curva-tinta-y-0", "Enter");
        var costos = await HastaQue(Costos, c => c.Curvas!.PorTinta[0].Y == 300);
        Assert.Equal(new PuntoCurvaDto(5, 300), costos.Curvas!.PorTinta[0]);
        Assert.Equal(MargenDeImpresiones.PorCostoDelTrabajo, costos.Margen);

        // Agregar un punto (al final, al doble) y quitar el primero de la curva por caras.
        var puntos = costos.Curvas.PorCaras.Count;
        await p.OprimirAsync(await p.PorIdAsync("curva-caras-agregar"));
        costos = await HastaQue(Costos, c => c.Curvas!.PorCaras.Count == puntos + 1);
        Assert.Equal(costos.Curvas!.PorCaras[^2].X * 2, costos.Curvas.PorCaras[^1].X);
        await p.OprimirAsync(await p.PorIdAsync("curva-caras-quitar-0"));
        costos = await HastaQue(Costos, c => c.Curvas!.PorCaras.Count == puntos);
        Assert.NotEqual(1, costos.Curvas!.PorCaras[0].X);

        // Una curva con un punto que no sirve se queda escrita, con su porqué.
        await p.DispararAsync(await p.PorIdAsync("curva-tinta-x-1"), "onfocus", new FocusEventArgs { Type = "focus" });
        await p.EscribirAsync("curva-tinta-x-1", "500");
        await p.TeclaAsync("curva-tinta-x-1", "Enter");
        await p.EsperarAsync(t => t.Contains("La tinta de un punto de la curva por tinta va de más de 0 a 400 %"), "el porqué del punto");
        await p.EscribirAsync("curva-tinta-x-1", "25");
        await p.TeclaAsync("curva-tinta-x-1", "Enter");
        await HastaQue(Costos, c => c.Curvas!.PorTinta.Any(x => x.X == 25));

        // Elegir «Por curvas».
        await p.OprimirAsync(await p.PorIdAsync($"costo-margen-{(int)MargenDeImpresiones.PorCurvas}"));
        costos = await HastaQue(Costos, c => c.Margen == MargenDeImpresiones.PorCurvas);
        Assert.Equal(MargenDeImpresiones.PorCurvas, costos.Margen);
        await p.EsperarAsync(t => t.Contains("Así se cobra: cada cara lleva el margen de su tinta"), "que ya se cobra con ellas");
        Assert.Empty(p.Errores);
    }
    /// <summary>
    /// Cada papel con su margen y su producto (el dueño, 2026-11-16): el margen se escribe como «Cobra por hoja» y se guarda como %; con
    /// un departamento de papel, solo se eligen sus productos.
    /// </summary>
    [Fact]
    public async Task Cada_papel_lleva_su_margen_por_lo_que_se_cobra_por_hoja_y_su_producto_de_papel()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var caja = (await propio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = Servicios(conexion, propio.Fabrica.CreateClient());
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var dueno = await cliente.EntrarAsync("dueno", "3333");
        // Un departamento de papel con un producto, y otro producto fuera de él.
        var departamentos = await cliente.DarDeAltaDepartamentoAsync(new SolicitudDepartamento(dueno.UsuarioId, caja.SucursalId, "Papel y cartulinas"));
        var dePapel = departamentos.Departamentos.Single(d => d.Nombre == "Papel y cartulinas");
        var hoja = await cliente.GuardarProductoAsync(new SolicitudGuardarProducto(dueno.UsuarioId, caja.SucursalId,
            new FichaProductoDto(null, "Hoja bond carta", dePapel.Id, null, PapeleriaPos.Nucleo.Catalogo.TipoProducto.Mercancia, PapeleriaPos.Nucleo.Catalogo.Impuesto.Iva16, null, true, false,
                [new VarianteFichaDto(null, "", PapeleriaPos.Nucleo.Catalogo.UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Hoja", 1000, true, [])], [new PrecioFichaDto(null, 1000, 100)])])));
        async Task<CostosImpresionDto> Costos() => (await cliente.CostosImpresionAsync(caja.SucursalId, dueno.UsuarioId))!.Costos;
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;
        await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = dueno });

        var carta = (await Costos()).Papeles.ToList().FindIndex(x => x.Tamano == "Carta" && x.Grupo == "Normal");
        await p.EscribirAsync($"costo-papel-{carta}", "100");
        await p.EscribirAsync($"costo-hojas-{carta}", "500");
        await p.TeclaAsync($"costo-hojas-{carta}", "Enter");
        await HastaQue(Costos, c => c.Papeles[carta].PrecioPaquete == 100);
        // $0.20 la hoja; se cobra $0.30: 50 %.
        await p.EscribirAsync($"costo-papel-objetivo-{carta}", "0.30");
        await p.TeclaAsync($"costo-papel-objetivo-{carta}", "Enter");
        var costos = await HastaQue(Costos, c => c.Papeles[carta].Margen is not null);
        Assert.Equal(50m, costos.Papeles[carta].Margen);

        // Con el departamento de papel, la lista solo trae sus productos.
        var lista = await p.EsperarElementoAsync($"costo-papel-producto-{carta}");
        var opciones = (await p.LeerAsync()).Elementos.Where(e => e.Etiqueta == "option").Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        Assert.Contains("Hoja bond carta", opciones);
        await p.DispararAsync(lista, "onchange", new ChangeEventArgs { Value = hoja.Variantes.Single().Id.ToString() });
        costos = await HastaQue(Costos, c => c.Papeles[carta].VarianteId is not null);
        Assert.Equal((hoja.Variantes.Single().Id, "Hoja bond carta"), (costos.Papeles[carta].VarianteId, costos.Papeles[carta].Producto));
        // Ya tenía su margen escrito: se queda, y se dice a cuánto se vende el producto (2026-11-22).
        Assert.Equal(50m, costos.Papeles[carta].Margen);
        await p.EsperarAsync(t => t.Contains("Se quedó el margen del papel Carta normal (50 %, cobra $0.30 por hoja); «Hoja bond carta» se vende a $1.00"),
            "que se quedó el margen escrito");

        // Sin margen propio, al elegir el producto se toma su precio como lo que se cobra por hoja: $1.00 contra $0.20, 400 %.
        await p.EscribirAsync($"costo-papel-objetivo-{carta}", "");
        await p.TeclaAsync($"costo-papel-objetivo-{carta}", "Enter");
        await HastaQue(Costos, c => c.Papeles[carta].Margen is null);
        await p.DispararAsync(await p.PorIdAsync($"costo-papel-producto-{carta}"), "onchange", new ChangeEventArgs { Value = "" });
        await HastaQue(Costos, c => c.Papeles[carta].VarianteId is null);
        await p.DispararAsync(await p.PorIdAsync($"costo-papel-producto-{carta}"), "onchange", new ChangeEventArgs { Value = hoja.Variantes.Single().Id.ToString() });
        costos = await HastaQue(Costos, c => c.Papeles[carta].Margen is not null);
        Assert.Equal(400m, costos.Papeles[carta].Margen);
        await p.EsperarAsync(t => t.Contains("Se tomó el precio de «Hoja bond carta» para el papel Carta normal: cobra $1.00 por hoja"), "que se tomó su precio");
        Assert.Equal("1.00", (await p.PorIdAsync($"costo-papel-objetivo-{carta}")).Valor);
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// El papel que se elige en el probador trae su margen de la tabla de papeles, como % y como lo que se cobra por hoja (el dueño,
    /// 2026-11-22); se puede cambiar para probar sin guardarlo, y si se escribió algo, al elegir otro papel se queda.
    /// </summary>
    [Fact]
    public async Task El_papel_del_probador_trae_su_margen_de_la_tabla_y_lo_escrito_se_queda_al_cambiar_de_papel()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var caja = (await propio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = Servicios(conexion, propio.Fabrica.CreateClient());
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var dueno = await cliente.EntrarAsync("dueno", "3333");
        async Task<CostosImpresionDto> Costos() => (await cliente.CostosImpresionAsync(caja.SucursalId, dueno.UsuarioId))!.Costos;
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;
        await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = dueno });

        // Carta normal: $100 el paquete de 500 ($0.20 la hoja) y se cobra $0.30 por hoja (50 %).
        var carta = (await Costos()).Papeles.ToList().FindIndex(x => x.Tamano == "Carta" && x.Grupo == "Normal");
        await p.EscribirAsync($"costo-papel-{carta}", "100");
        await p.EscribirAsync($"costo-hojas-{carta}", "500");
        await p.TeclaAsync($"costo-hojas-{carta}", "Enter");
        await HastaQue(Costos, c => c.Papeles[carta].PrecioPaquete == 100);
        await p.EscribirAsync($"costo-papel-objetivo-{carta}", "0.30");
        await p.TeclaAsync($"costo-papel-objetivo-{carta}", "Enter");
        await HastaQue(Costos, c => c.Papeles[carta].Margen == 50);

        // El probador empieza en Carta y papel normal: su margen, precargado.
        async Task<string?> Valor(string id) => (await p.PorIdAsync(id)).Valor;
        var margen = await HastaQue(() => Valor("probador-papel-margen"), v => v == "50");
        Assert.Equal("50", margen);
        Assert.Equal("0.30", await Valor("probador-papel-cobra"));
        await p.EsperarAsync(t => t.Contains("Carta normal: su margen en la tabla de papeles es 50 %"), "de dónde sale el margen del papel");

        // Otro papel (sin precio ni margen propio): se precarga el suyo; al volver a papel normal, otra vez el 50.
        const string otro = "ThickPaper";
        Assert.Contains((await p.LeerAsync()).Elementos, e => e.Etiqueta == "option" && e.Valor == otro);
        await p.DispararAsync(await p.PorIdAsync("probador-papel"), "onchange", new ChangeEventArgs { Value = otro });
        await HastaQue(() => Valor("probador-papel-margen"), v => v == "");
        Assert.Equal("", await Valor("probador-papel-margen"));
        await p.DispararAsync(await p.PorIdAsync("probador-papel"), "onchange", new ChangeEventArgs { Value = "Plain" });
        Assert.Equal("50", await HastaQue(() => Valor("probador-papel-margen"), v => v == "50"));

        // Lo que se cobra por hoja se escribe y de ahí sale el %; el precio de la prueba cambia, sin guardarlo.
        await p.EscribirAsync("probador-papel-cobra", "0.40");
        Assert.Equal("100", await HastaQue(() => Valor("probador-papel-margen"), v => v == "100"));
        Assert.Equal(50m, (await Costos()).Papeles[carta].Margen);

        // Lo escrito se queda al elegir otro papel, y se dice.
        await p.DispararAsync(await p.PorIdAsync("probador-papel"), "onchange", new ChangeEventArgs { Value = otro });
        await p.EsperarAsync(t => t.Contains("Se quedó el margen que escribiste (100 %)"), "que se quedó lo escrito");
        Assert.Equal("100", await Valor("probador-papel-margen"));
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Un punto nuevo de una curva (el dueño, 2026-11-22): con el teclado, «Agregar un punto» lleva a su x; al escribirla y pasar a su y
    /// (Tab), el renglón se acomoda por su x y el cursor lo sigue a su y; con Enter, se guarda y el cursor pasa a la y del punto ya en su
    /// lugar. Una x repetida se dice en su renglón y no se mueve; una que no sirve, tampoco.
    /// </summary>
    [Fact]
    public async Task Un_punto_nuevo_se_acomoda_por_su_x_al_pasar_a_su_y_y_el_cursor_lo_sigue()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var caja = (await propio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var pagina = new PaginaQueContesta();
        await using var servicios = Servicios(conexion, propio.Fabrica.CreateClient(), pagina);
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var dueno = await cliente.EntrarAsync("dueno", "3333");
        async Task<CostosImpresionDto> Costos() => (await cliente.CostosImpresionAsync(caja.SucursalId, dueno.UsuarioId))!.Costos;
        var p = new PantallaSinNavegador(servicios);
        await using var _ = p;
        await p.AbrirAsync<ConfiguracionImpresora>(new() { [nameof(ConfiguracionImpresora.Caja)] = caja, [nameof(ConfiguracionImpresora.Sesion)] = dueno });
        await p.EsperarElementoAsync("curva-tinta-x-0");
        string? Ultima(string funcion) => pagina.Llamadas.LastOrDefault(l => l.Funcion == funcion).Argumentos?[0] as string;
        async Task<string?> Valor(string id) => (await p.PorIdAsync(id)).Valor;
        async Task Entrar(string id) => await p.DispararAsync(await p.PorIdAsync(id), "onfocus", new FocusEventArgs { Type = "focus" });
        async Task Dejar(string id) => await p.DispararAsync(await p.PorIdAsync(id), "onblur", new FocusEventArgs { Type = "blur" });

        // Las sugeridas: 5, 25, 60, 120 y 250 %. «Agregar un punto» lo pone al final (400 %) y el cursor va a su x.
        Assert.Equal([5m, 25m, 60m, 120m, 250m], (await Costos()).Curvas!.PorTinta.Select(x => x.X));
        await p.OprimirAsync(await p.PorIdAsync("curva-tinta-agregar"));
        await HastaQue(Costos, c => c.Curvas!.PorTinta.Count == 6);
        await HastaQue(() => Task.FromResult(Ultima("enfocar")), s => s == "#curva-tinta-x-5");
        Assert.Equal("#curva-tinta-x-5", Ultima("enfocar"));
        Assert.Equal("400", await Valor("curva-tinta-x-5"));

        // Su x, 10 %, y Tab a su y: el renglón pasa al segundo lugar (sin guardarse todavía) y el cursor lo sigue a su y.
        await Entrar("curva-tinta-x-5");
        await p.EscribirAsync("curva-tinta-x-5", "10");
        await Dejar("curva-tinta-x-5");
        await Entrar("curva-tinta-y-5");
        Assert.Equal("10", await Valor("curva-tinta-x-1"));
        Assert.Equal("250", await Valor("curva-tinta-x-5"));
        Assert.Equal("#curva-tinta-y-1", Ultima("seguirA"));
        Assert.Equal(400m, (await Costos()).Curvas!.PorTinta[^1].X);
        // Su margen, y al salir del renglón se guarda junto, ya en orden.
        await p.EscribirAsync("curva-tinta-y-1", "150");
        await Dejar("curva-tinta-y-1");
        var costos = await HastaQue(Costos, c => c.Curvas!.PorTinta.Any(x => x.X == 10));
        Assert.Equal([5m, 10m, 25m, 60m, 120m, 250m], costos.Curvas!.PorTinta.Select(x => x.X));
        Assert.Equal(150m, costos.Curvas.PorTinta[1].Y);

        // Una x repetida: se dice en su renglón y no se mueve ni se juntan.
        await Entrar("curva-tinta-x-0");
        await p.EscribirAsync("curva-tinta-x-0", "60");
        await Dejar("curva-tinta-x-0");
        await p.EsperarAsync(t => t.Contains("Ya hay un punto con 60 % de tinta: cambia uno de los dos (no se juntan)."), "el aviso del repetido");
        Assert.Equal("60", await Valor("curva-tinta-x-0"));
        Assert.Equal("true", (await p.PorIdAsync("curva-tinta-x-0")).Atributos.GetValueOrDefault("aria-invalid")?.ToString());
        costos = await HastaQue(Costos, c => c.Curvas!.PorTinta.Count == 6);
        Assert.Equal([5m, 10m, 25m, 60m, 120m, 250m], costos.Curvas!.PorTinta.Select(x => x.X));
        // Una que no sirve tampoco se mueve; corregida a 3 %, se queda primera y se guarda.
        await Entrar("curva-tinta-x-0");
        await p.EscribirAsync("curva-tinta-x-0", "abc");
        await Dejar("curva-tinta-x-0");
        Assert.Equal("abc", await Valor("curva-tinta-x-0"));
        await Entrar("curva-tinta-x-0");
        await p.EscribirAsync("curva-tinta-x-0", "3");
        await Dejar("curva-tinta-x-0");
        costos = await HastaQue(Costos, c => c.Curvas!.PorTinta[0].X == 3);
        Assert.Equal(3m, costos.Curvas!.PorTinta[0].X);
        await p.EsperarAsync(t => !t.Contains("Ya hay un punto con"), "que se quitó el aviso");

        // Con Enter, en la curva por caras: 2,500 → 50 caras se guarda y el cursor pasa a su y, ya en el segundo lugar.
        await Entrar("curva-caras-x-4");
        await p.EscribirAsync("curva-caras-x-4", "50");
        await p.TeclaAsync("curva-caras-x-4", "Enter");
        costos = await HastaQue(Costos, c => c.Curvas!.PorCaras.Any(x => x.X == 50));
        Assert.Equal([1m, 50m, 100m, 250m, 1000m], costos.Curvas!.PorCaras.Select(x => x.X));
        await HastaQue(() => Task.FromResult(Ultima("enfocar")), s => s == "#curva-caras-y-1");
        Assert.Equal("#curva-caras-y-1", Ultima("enfocar"));
        Assert.Equal("50", await Valor("curva-caras-x-1"));
        Assert.Empty(p.Errores);
    }
}
