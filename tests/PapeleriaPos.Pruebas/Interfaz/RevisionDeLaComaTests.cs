using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La página con el cuadro de captura de verdad, como lo maneja caja.js: «escribirCaptura» lo reemplaza (el cursor al final),
/// «limpiarCaptura» lo vacía y «regresarACaptura» escribe solo si está vacío (y lo deja seleccionado). Lo demás contesta sin
/// hacer nada, como <see cref="PaginaQueContesta"/>, y todo se anota.
/// </summary>
public sealed class CuadroDeCapturaDePrueba : IJSRuntime, IJSObjectReference
{
    private readonly object _candado = new();
    private readonly List<(string Funcion, object?[]? Argumentos)> _llamadas = [];
    private string _valor = "";

    /// <summary>Lo que hay en el cuadro de captura de la página (lo que caja.js vacía con su Enter, la prueba lo vacía aquí).</summary>
    public string Valor
    {
        get { lock (_candado) return _valor; }
        set { lock (_candado) _valor = value; }
    }

    /// <summary>Lo que se le pidió a la página, en orden.</summary>
    public IReadOnlyList<(string Funcion, object?[]? Argumentos)> Llamadas
    {
        get { lock (_candado) return [.. _llamadas]; }
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        object? resultado = null;
        lock (_candado)
        {
            _llamadas.Add((identifier, args));
            switch (identifier)
            {
                case "import":
                    resultado = this;
                    break;
                case "escribirCaptura":
                    _valor = (string)args![0]!;
                    break;
                case "limpiarCaptura":
                    _valor = "";
                    break;
                case "regresarACaptura":
                    var vacio = _valor.Length == 0;
                    if (vacio)
                        _valor = (string)args![0]!;
                    resultado = vacio;
                    break;
            }
        }
        return ValueTask.FromResult(resultado is TValue valor ? valor : default!);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// La revisión de 1592ce8 y 7ebf74d (la coma solo separa los miles; PR #138), con las pruebas del revisor adaptadas: Ctrl+D en
/// porcentaje ya no toma «$5» como 5 % (ALTA); lo rechazado regresa al cuadro solo si está vacío, no se cobra mientras siga ahí,
/// y en «Varios» se queda en su panel con el código esperando (MEDIA); los enteros no toman un punto (MEDIA); «1,000.» es dinero
/// (BAJA); tres decimales en el dinero no se adivinan (BAJA); el aviso de la coma no borra otro error (BAJA).
/// </summary>
public sealed class RevisionDeLaComaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private const string DosYMedio = "Los decimales van con punto: 2.5 (la coma solo separa los miles: 1,000).";

    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-revcoma-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static Task TeclaAsync(VentaConCopia v, string tecla, string? texto = "") => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, texto));

    /// <summary>Lo que dice la venta bajo el cuadro de captura.</summary>
    private static async Task<string> AvisoAsync(VentaConCopia v) =>
        PantallaSinNavegador.Limpio((await v.Pantalla.ElementoAsync(e => e.Clase?.StartsWith("vn-aviso", StringComparison.Ordinal) == true,
            "el aviso bajo el cuadro")).Texto);

    private static int Cobros(VentaConCopia v) => v.Red.Pedidas.Count(r => r == "/api/ventas");

    /// <summary>F12 desde el cuadro de captura (caja.js manda lo que tiene) y Enter en «Pagó con»: exacto.</summary>
    private static async Task CobrarExactoAsync(VentaConCopia v, CuadroDeCapturaDePrueba cuadro)
    {
        await TeclaAsync(v, "F12", cuadro.Valor);
        await v.Pantalla.EsperarElementoAsync("pago-nuevo");
        await v.Pantalla.TeclaAsync("pago-nuevo", "Enter");
    }

    // ── ALTA: Ctrl+D en porcentaje (lo que abre por omisión) tomaba «$5» como 5 % ──────────────────────────────

    /// <summary>
    /// Ctrl+D abre en «Porcentaje (%)»: «$5» ahí son pesos, no 5 % (con un ticket de $1,000 eran $50 de descuento, no $5). No se
    /// aplica nada y el panel dice que en porcentaje va sin «$» y que los pesos van en «Monto ($)». «5» sí es 5 %.
    /// </summary>
    [Fact]
    public async Task Descuento_en_porcentaje_no_toma_pesos_como_porcentaje()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync($"10*{a.Codigo}");
        await TeclaAsync(v, "Ctrl+D", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");
        Assert.Contains("Cuánto (%)", await v.Pantalla.TextoAsync());

        await v.Pantalla.EscribirAsync("descuento-valor", "$5");
        var motivo = await v.Pantalla.ElementoAsync(e => e.Etiqueta == "input"
            && e.Atributos.GetValueOrDefault("placeholder")?.ToString()?.StartsWith("Cliente frecuente", StringComparison.Ordinal) == true, "el motivo");
        await v.Pantalla.DispararAsync(motivo, "oninput", new ChangeEventArgs { Value = "Pesos en porcentaje" });
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Aplicar"));

        var texto = PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync());
        Assert.Contains("Escribe el porcentaje sin «$»: «$5» son pesos. Para descontar pesos, elige «Monto ($)».", texto);
        Assert.DoesNotContain(await VentaConCopia.RenglonesAsync(v.Pantalla), r => r.Contains("Pesos en porcentaje", StringComparison.Ordinal));

        await v.Pantalla.EscribirAsync("descuento-valor", "5");
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Aplicar"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Descuento −$"), "el descuento de 5 %");
        Assert.Contains(await VentaConCopia.RenglonesAsync(v.Pantalla), r => r.Contains("Pesos en porcentaje", StringComparison.Ordinal));
        Assert.Empty(v.Pantalla.Errores);
    }

    // ── MEDIA: los enteros tomaban «1.000» como 1 ─────────────────────────────────────────────────────────────

    /// <summary>Un entero no lleva punto: «1.000» podía ser uno o mil (antes era 1), «12.000» era 12 y «3.0», 3.</summary>
    [Theory]
    [InlineData("1.000")]
    [InlineData("12.000")]
    [InlineData("3.0")]
    public void Un_entero_no_toma_un_punto(string escrito)
    {
        Assert.Null(Formato.LeerEntero(escrito));
        Assert.NotNull(Formato.ProblemaDeEntero(escrito));
    }

    /// <summary>
    /// Recibir mercancía (ElegirProducto con AlContar, como en «Recibir» de una compra): «1.000*código» ya no cuenta una pieza; dice
    /// cómo escribir mil. «1,000*código» sí son mil.
    /// </summary>
    [Fact]
    public async Task Recibir_mercancia_1_punto_000_por_codigo_no_cuenta_una_pieza()
    {
        var c = await CajaAsync();
        var codigo = $"RC{Guid.NewGuid():N}"[..14];
        await ProductoAsync(c, "Clip revisión coma", codigo, TipoProducto.Mercancia);
        long? contadas = null;
        await using var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<ElegirProducto>(new()
        {
            [nameof(ElegirProducto.SucursalId)] = c.Datos.SucursalId,
            [nameof(ElegirProducto.Accion)] = "Contar",
            [nameof(ElegirProducto.AlContar)] = EventCallback.Factory.Create<(ProductoVentaDto, PresentacionDto, long)>(this, x => contadas = x.Item3),
        });
        await p.EscribirAsync("codigo-compra", $"1.000*{codigo}");
        await p.TeclaAsync("codigo-compra", "Enter");
        Assert.Null(contadas);
        Assert.Contains("Va entero, sin punto: si son 1,000, escríbelo 1000 o 1,000.", PantallaSinNavegador.Limpio(await p.TextoAsync()));

        await p.EscribirAsync("codigo-compra", $"1,000*{codigo}");
        await p.TeclaAsync("codigo-compra", "Enter");
        await HastaQueAsync(() => contadas is not null, "lo contado");
        Assert.Equal(1000L, contadas);
        Assert.Empty(p.Errores);
    }

    // ── MEDIA: lo rechazado regresaba al cuadro sin ver qué tenía ──────────────────────────────────────────────

    /// <summary>
    /// «2,5*código» se rechaza dentro de la cola de los escaneos: con la cola ocupada (una palabra que se le pregunta a la caja
    /// principal), el rechazo llega cuando el lector ya teclea el siguiente en el cuadro. Antes lo pisaba; ahora lo que el lector
    /// teclea se queda, y lo que no se agregó se sigue viendo bajo el cuadro aunque después entre otro.
    /// </summary>
    [Fact]
    public async Task Lo_rechazado_no_pisa_lo_que_se_esta_escaneando()
    {
        var cuadro = new CuadroDeCapturaDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: cuadro);
        var (a, b) = v.DosDeLaCopia();

        // La cola, ocupada: una palabra sin números se le pregunta primero a la caja principal.
        v.Red.Detenida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lento = v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escaneado("engrapadora"));
        // Detrás: «2,5*A» (caja.js ya vació el cuadro con su Enter) y luego B, escaneado y con su Enter.
        var rechazado = v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escaneado($"2,5*{a.Codigo}"));
        var siguiente = v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escaneado(b.Codigo!));
        // El lector ya va tecleando el código del que sigue en el cuadro vacío.
        cuadro.Valor = "75099";

        v.Red.Detenida.SetResult();
        await lento;
        await rechazado;
        await siguiente;

        Assert.Contains(await VentaConCopia.RenglonesAsync(v.Pantalla), r => r.Contains(b.Descripcion, StringComparison.Ordinal));
        Assert.DoesNotContain(await VentaConCopia.RenglonesAsync(v.Pantalla), r => r.Contains(a.Descripcion, StringComparison.Ordinal));
        Assert.Equal("75099", cuadro.Valor);
        Assert.DoesNotContain(cuadro.Llamadas, l => l.Funcion == "escribirCaptura");
        // B entró después: lo que no se agregó sigue a la vista.
        Assert.Equal($"«2,5*{a.Codigo}» no se agregó: corrígelo y escríbelo otra vez.",
            PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("no-se-agrego")).Texto));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Con el cuadro vacío, lo rechazado regresa a él seleccionado (un escaneo lo reemplaza, no se le pega). Si un escaneo lo
    /// reemplaza, lo que no se agregó se sigue viendo bajo el cuadro y en el cobro, sin impedir cobrar; al cobrar, se va.
    /// </summary>
    [Fact]
    public async Task Lo_rechazado_regresa_al_cuadro_vacio_y_un_escaneo_lo_reemplaza_sin_perderlo_de_vista()
    {
        var cuadro = new CuadroDeCapturaDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: cuadro);
        var lista = v.DeLaCopia();
        var (a, b, c) = (lista[0], lista[1], lista[2]);
        await v.EscanearAsync(b.Codigo!);

        await v.EscanearAsync($"2,5*{a.Codigo}");
        Assert.Equal($"2,5*{a.Codigo}", cuadro.Valor);
        Assert.Contains(cuadro.Llamadas, l => l.Funcion == "regresarACaptura");
        Assert.Contains(DosYMedio, await AvisoAsync(v));
        Assert.Contains($"Corrige o borra lo del cuadro antes de cobrar: «2,5*{a.Codigo}» no se agregó",
            PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("cobro-rechazado")).Texto));

        // El lector escanea C sobre lo seleccionado: lo reemplaza, y su Enter lo manda (caja.js vacía el cuadro).
        cuadro.Valor = "";
        await v.EscanearAsync(c.Codigo!);
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.Contains(renglones, r => r.Contains(c.Descripcion, StringComparison.Ordinal));
        Assert.Contains($"«2,5*{a.Codigo}» no se agregó: corrígelo y escríbelo otra vez.", await AvisoAsync(v));
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "cobro-rechazado");
        Assert.Equal($"«2,5*{a.Codigo}» no se agregó a este ticket", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("cobro-no-se-agrego")).Texto));

        // Ya no está en el cuadro: se cobra (lo dicho, sin un segundo Enter), y con el ticket siguiente se va.
        var antes = Cobros(v);
        await CobrarExactoAsync(v, cuadro);
        await v.Pantalla.EsperarAsync(t => Regex.IsMatch(t, @"Venta \S+ cobrada"), "el cobro");
        Assert.Equal(antes + 1, Cobros(v));
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id is "no-se-agrego" or "cobro-no-se-agrego");
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Con «2,5*A» rechazado en el cuadro, F12 (también con el cursor ya en «Pagó con») y Enter no cobran el ticket sin A: el cursor
    /// regresa al cuadro y lo dice ahí y en el cobro. Borrarlo deja cobrar sin él; corregirlo («4*A») lo agrega y entonces cobra.
    /// </summary>
    [Fact]
    public async Task Con_un_escaneo_rechazado_en_el_cuadro_F12_no_cobra_sin_el()
    {
        var cuadro = new CuadroDeCapturaDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: cuadro);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(b.Codigo!);
        await v.EscanearAsync($"2,5*{a.Codigo}");
        Assert.Equal($"2,5*{a.Codigo}", cuadro.Valor);
        var antes = Cobros(v);
        var corrige = $"Corrige o borra lo del cuadro antes de cobrar: «2,5*{a.Codigo}» no se agregó.";

        // F12 desde el cuadro y Enter en «Pagó con»: no cobran, y el cursor regresa al cuadro.
        var enfocados = cuadro.Llamadas.Count(l => l.Funcion == "enfocar" && Equals(l.Argumentos?[0], "#codigo"));
        await CobrarExactoAsync(v, cuadro);
        Assert.Equal(antes, Cobros(v));
        Assert.Contains(corrige, await AvisoAsync(v));
        Assert.True(cuadro.Llamadas.Count(l => l.Funcion == "enfocar" && Equals(l.Argumentos?[0], "#codigo")) > enfocados);
        Assert.True((await v.Pantalla.PorIdAsync("cobrar-nuevo")).Desactivado);
        // F12 con el cursor ya en «Pagó con» (caja.js lo movió): tampoco.
        await TeclaAsync(v, "F12-pago", cuadro.Valor);
        Assert.Equal(antes, Cobros(v));
        Assert.DoesNotMatch(@"Venta \S+ cobrada", await v.Pantalla.TextoAsync());

        // Borrarlo a mano deja cobrar sin él, y ya no se recuerda.
        cuadro.Valor = "";
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo(""));
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id is "cobro-rechazado" or "cobro-no-se-agrego" or "no-se-agrego");
        await CobrarExactoAsync(v, cuadro);
        await v.Pantalla.EsperarAsync(t => Regex.IsMatch(t, @"Venta \S+ cobrada"), "el cobro sin A");
        Assert.Equal(antes + 1, Cobros(v));

        // Otra vez, y borrado con F12 enseguida (caja.js no alcanzó a decir que se borró): F12 trae el cuadro vacío y cobra. En el
        // ticket siguiente, lo que se escriba en el cuadro no se toma por lo rechazado de antes.
        await v.EscanearAsync(b.Codigo!);
        await v.EscanearAsync($"2,5*{a.Codigo}");
        cuadro.Valor = "";
        await CobrarExactoAsync(v, cuadro);
        await HastaQueAsync(() => Cobros(v) == antes + 2, "el cobro con el cuadro vacío");
        await v.EscanearAsync(b.Codigo!);
        cuadro.Valor = "75";
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("75"));
        await CobrarExactoAsync(v, cuadro);
        await HastaQueAsync(() => Cobros(v) == antes + 3, "el cobro sin lo de antes");
        cuadro.Valor = "";

        // Rechazado, y corregido en el cuadro («4*A» y Enter): entra, y entonces cobra los dos.
        await v.EscanearAsync(b.Codigo!);
        await v.EscanearAsync($"2,5*{a.Codigo}");
        await TeclaAsync(v, "F12", cuadro.Valor);
        Assert.Equal(antes + 3, Cobros(v));
        cuadro.Valor = "";
        await v.EscanearAsync($"4*{a.Codigo}");
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Contains(renglones, r => r.StartsWith($"4 {a.Descripcion}", StringComparison.Ordinal));
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id is "cobro-rechazado" or "cobro-no-se-agrego" or "no-se-agrego");
        await CobrarExactoAsync(v, cuadro);
        await HastaQueAsync(() => Cobros(v) == antes + 4, "el cobro con A");
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// «Varios» con el código antes de la cantidad (PR #133): «2,5» se rechaza y se queda en su panel, en el paso de la cantidad;
    /// nada regresa al cuadro, así que el siguiente escaneo no se le pega. Ese escaneo no mete al que espera con una pieza
    /// adivinada ni entra: lo dice. Con la cantidad buena, el que esperaba entra con ella, y el otro se escanea después.
    /// </summary>
    [Fact]
    public async Task En_varios_lo_rechazado_no_se_pega_al_siguiente_escaneo()
    {
        var cuadro = new CuadroDeCapturaDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, fondoAparte: true, pagina: cuadro);
        var (a, b) = v.DosDeLaCopia();
        Task EnVariosAsync(string texto, bool rafaga = false) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.EnVarios(texto, 1234.5, rafaga));
        async Task<string> PanelAsync() =>
            PantallaSinNavegador.Limpio((await v.Pantalla.ElementoAsync(e => e.Clase == "vn-varios", "el panel de «Varios»")).Texto);

        await TeclaAsync(v, "Insert", "");
        await EnVariosAsync(a.Codigo!, rafaga: true);
        await EnVariosAsync("2,5");
        Assert.Equal("", cuadro.Valor);
        Assert.Contains("«2,5»: corrígela y Enter", await PanelAsync());
        Assert.Contains(DosYMedio, await PanelAsync());

        // El lector escribe en el cuadro (vacío) y su Enter llega en ráfaga (caja.js enterDeVarios): solo su código.
        cuadro.Valor += b.Codigo;
        var escaneado = cuadro.Valor;
        cuadro.Valor = "";
        Assert.Equal(b.Codigo, escaneado);
        await EnVariosAsync(escaneado, rafaga: true);
        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
        var panel = await PanelAsync();
        Assert.Contains($"«{b.Codigo}» no se agregó: primero corrige la cantidad de «{a.Descripcion}» («2,5»)", panel);
        Assert.Contains(a.Descripcion, panel);
        // Enter sin nada no es «una»: la cantidad se escribe.
        await EnVariosAsync("");
        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains($"Escribe la cantidad de «{a.Descripcion}»: «2,5» no se tomó.", await PanelAsync());

        await EnVariosAsync("4");
        await v.EscanearAsync(b.Codigo!);
        await VentaConCopia.EsperarAsync(() => v.Cliente.Foto is not null, "la copia");
        var renglones = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.Equal(2, renglones.Count);
        Assert.StartsWith($"4 {a.Descripcion}", renglones[0]);
        Assert.StartsWith($"1 {b.Descripcion}", renglones[1]);
        Assert.DoesNotContain(cuadro.Llamadas, l => l.Funcion is "regresarACaptura" or "escribirCaptura");
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// En «Varios», la fracción de algo por pieza también es una cantidad rechazada: el código espera la buena y otro escaneo no lo
    /// mete con una pieza (antes sí: entraba uno de lo que se quiso en fracción).
    /// </summary>
    [Fact]
    public async Task En_varios_la_fraccion_de_algo_por_pieza_tampoco_se_adivina()
    {
        var cuadro = new CuadroDeCapturaDePrueba();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, fondoAparte: true, pagina: cuadro);
        var (a, b) = v.DosDeLaCopia();
        Task EnVariosAsync(string texto, bool rafaga = false) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.EnVarios(texto, 1234.5, rafaga));

        await TeclaAsync(v, "Insert", "");
        await EnVariosAsync("1.5");
        await EnVariosAsync(a.Codigo!, rafaga: true);
        await EnVariosAsync(b.Codigo!, rafaga: true);
        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains($"«{b.Codigo}» no se agregó: primero corrige la cantidad de «{a.Descripcion}» («1.5»)",
            PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync()));

        await EnVariosAsync("2");
        Assert.StartsWith($"2 {a.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Empty(v.Pantalla.Errores);
    }

    // ── BAJA: «1,000.» se rechazaba con el aviso de los centavos («1000.» sí pasaba) ──────────────────────────

    [Fact]
    public void Mil_con_coma_y_punto_al_final_es_dinero()
    {
        Assert.Equal(100000L, Formato.LeerPesos("1000."));
        Assert.Equal(100000L, Formato.LeerPesos("1,000."));
        Assert.Null(Formato.ProblemaDePesos("1,000."));
        Assert.Null(Formato.LeerPesos("12,50."));
    }

    // ── BAJA: tres decimales en el dinero («1.500» era $1.50) ─────────────────────────────────────────────────

    /// <summary>Decidido y por revisar: «1.500» no se adivina en ningún campo de dinero; dice cómo escribir mil quinientos.</summary>
    [Fact]
    public void Tres_decimales_en_el_dinero_no_se_adivinan()
    {
        Assert.Null(Formato.LeerPesos("1.500"));
        Assert.Equal("Si son 1,500 pesos, escríbelo 1500 o 1,500; los centavos van con dos decimales.", Formato.ProblemaDePesos("1.500"));
        Assert.Equal(150000L, Formato.LeerPesos("1500"));
        Assert.Equal(150000L, Formato.LeerPesos("1,500"));
        Assert.Equal(150L, Formato.LeerPesos("1.50"));
        Assert.Null(AgregarInventarioEnPantalla.Costo("1.500", out var problema));
        Assert.Equal("Si son 1,500 pesos, escríbelo 1500 o 1,500; los centavos van con dos decimales.", problema);
    }

    // ── BAJA: en Etiquetas, el aviso de la coma borraba otro error ────────────────────────────────────────────

    /// <summary>
    /// Etiquetas: con un error que no es de cómo se escribe (el del servicio al preparar la hoja: uno de la lista ya no existe),
    /// un número bien escrito en otro campo ya no lo borra; el aviso de la coma sí se va al corregirla. Las etiquetas de un
    /// renglón y «Empezar en» no toman un punto.
    /// </summary>
    [Fact]
    public async Task En_etiquetas_el_aviso_de_la_coma_no_borra_otro_error()
    {
        var c = await CajaAsync();
        var lista = new EtiquetasPorImprimir();
        lista.Agregar(Guid.NewGuid(), "Lo que ya no existe", $"RX{Guid.NewGuid():N}"[..12], 2);
        await using var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Etiquetas>(new()
        {
            [nameof(Etiquetas.Caja)] = c.Datos,
            [nameof(Etiquetas.Sesion)] = c.Encargado,
            [nameof(Etiquetas.Lista)] = lista,
        });
        await p.EsperarAsync(t => t.Contains("Vista previa"), "la página");
        await p.OprimirAsync(await p.BotonAsync("Vista previa"));
        await p.EsperarAsync(t => t.Contains("ya no existe."), "el error del servicio");
        var delServicio = PantallaSinNavegador.Limpio((await p.ElementoAsync(e => e.Etiqueta == "p" && e.Clase == "error", "el error")).Texto);

        // «Mover a la derecha (mm)»: bien escrito, el otro error se queda (antes se borraba).
        var derecha = await p.ElementoAsync(e => e.Etiqueta == "input" && Equals(e.Atributos.GetValueOrDefault("inputmode"), "decimal"), "«Mover a la derecha»");
        await p.DispararAsync(derecha, "onchange", new ChangeEventArgs { Value = "2" });
        Assert.Contains(delServicio, PantallaSinNavegador.Limpio(await p.TextoAsync()));
        // Con la coma, su aviso; corregida, se va el suyo.
        await p.DispararAsync(derecha, "onchange", new ChangeEventArgs { Value = "2,5" });
        Assert.Contains(DosYMedio, PantallaSinNavegador.Limpio(await p.TextoAsync()));
        await p.DispararAsync(derecha, "onchange", new ChangeEventArgs { Value = "2.5" });
        Assert.DoesNotContain("Los decimales van con punto", await p.TextoAsync());

        // Las etiquetas del renglón y «Empezar en la etiqueta»: enteras, sin punto; la cantidad no cambia.
        var cuantas = await p.ElementoAsync(e => e.Etiqueta == "input" && Equals(e.Atributos.GetValueOrDefault("min"), "0"), "las etiquetas del renglón");
        await p.DispararAsync(cuantas, "onchange", new ChangeEventArgs { Value = "1.000" });
        Assert.Contains("Va entero, sin punto: si son 1,000, escríbelo 1000 o 1,000.", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        Assert.Equal(2, lista.Renglones.Single().Cantidad);
        var empezar = await p.ElementoAsync(e => e.Etiqueta == "input" && Equals(e.Atributos.GetValueOrDefault("min"), "1"), "«Empezar en la etiqueta»");
        await p.DispararAsync(empezar, "onchange", new ChangeEventArgs { Value = "3.0" });
        Assert.Contains("«3.0» va entero, sin punto ni decimales.", PantallaSinNavegador.Limpio(await p.TextoAsync()));
        Assert.Empty(p.Errores);
    }

    // ── Lo que está bien: la regla no depende de la cultura de la PC ──────────────────────────────────────────

    [Theory]
    [InlineData("es-ES")]
    [InlineData("es-MX")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    public void La_regla_no_depende_de_la_cultura_de_la_PC(string cultura)
    {
        var (antes, antesUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            Assert.Equal(125050L, Formato.LeerPesos("1,250.50"));
            Assert.Equal(125000L, Formato.LeerPesos(" $1,250 "));
            Assert.Equal(1250L, Formato.LeerPesos("12.50"));
            Assert.Equal(10000L, Formato.LeerPesos("100."));
            Assert.Equal(100000L, Formato.LeerPesos("1,000."));
            Assert.Equal(50L, Formato.LeerPesos(".5"));
            Assert.Null(Formato.LeerPesos("12,50"));
            Assert.Null(Formato.LeerPesos("1.500"));
            Assert.Null(Formato.LeerPesos("1.234,50"));
            Assert.Null(Formato.LeerPesos("-5"));
            Assert.Null(Formato.LeerPesos("1" + new string('0', 40)));
            Assert.Equal("Los centavos van con punto: 12.50 (la coma solo separa los miles: 1,250.00).", Formato.ProblemaDePesos("12,50"));
            Assert.Equal("Si son 1,500 pesos, escríbelo 1500 o 1,500; los centavos van con dos decimales.", Formato.ProblemaDePesos("1.500"));
            Assert.Equal(2.5m, Formato.LeerCantidad("2.5"));
            Assert.Equal(1000m, Formato.LeerCantidad("1,000"));
            Assert.Null(Formato.LeerCantidad("2,5"));
            Assert.Null(Formato.LeerCantidad("$2"));
            Assert.Equal(1000L, Formato.LeerEntero("1,000"));
            Assert.Null(Formato.LeerEntero("1.000"));
            Assert.Null(Formato.LeerEntero("99999999999999999999999"));
            Assert.Equal(2.5m, MostradorEnPantalla.Cantidad("2.5"));
            Assert.Equal(1000m, MostradorEnPantalla.Cantidad("1,000"));
            Assert.Null(MostradorEnPantalla.Cantidad("2,5"));
            Assert.Null(MostradorEnPantalla.Cantidad("7501234567890"));
            Assert.True(TemporadaEnPantalla.LeerCantidad("1,000", out var mil) && mil == 1_000_000);
            Assert.Equal("$1,250.50", Formato.Dinero(125050));
            Assert.Equal("Va entero, sin punto: si son 1,000, escríbelo 1000 o 1,000.", Formato.ProblemaDeEntero("1.000"));
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (antes, antesUi);
        }
    }

    /// <summary>«Pagó con» con la PC en es-ES (la coma es su decimal): «12,50» no cobra y «1,000» cobra mil pesos.</summary>
    [Theory]
    [InlineData("es-ES")]
    [InlineData("es-MX")]
    public async Task Pago_con_no_depende_de_la_cultura(string cultura)
    {
        var antes = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultura);
        try
        {
            await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
            Assert.Equal(cultura, await v.Pantalla.Dispatcher.InvokeAsync(() => CultureInfo.CurrentCulture.Name));
            var (a, _) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);
            await v.Pantalla.EsperarElementoAsync("pago-nuevo");
            var previos = Cobros(v);
            await v.Pantalla.EscribirAsync("pago-nuevo", "12,50");
            await v.Pantalla.EsperarElementoAsync("pago-problema");
            await v.Pantalla.TeclaAsync("pago-nuevo", "Enter");
            Assert.Equal(previos, Cobros(v));
            await v.Pantalla.EscribirAsync("pago-nuevo", "1,000");
            await v.Pantalla.TeclaAsync("pago-nuevo", "Enter");
            await v.Pantalla.EsperarAsync(t => Regex.IsMatch(t, @"Venta \S+ cobrada"), "el cobro");
            var ticket = (await v.Cliente.UltimoTicketAsync(v.Caja.Id))!;
            Assert.Equal((FormaPago.Efectivo, 100_000L), (ticket.Cobros.Single().Forma, ticket.Cobros.Single().RecibidoCentavos));
        }
        finally
        {
            CultureInfo.CurrentCulture = antes;
        }
    }

    // ── Lo de las pruebas ──────────────────────────────────────────────────────────────────────────────────

    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Encargado);

    private async Task<Caja> CajaAsync()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("encargado", "2222"));
    }

    /// <summary>Un producto con su pieza y ese código, de $20.</summary>
    private async Task ProductoAsync(Caja c, string nombre, string codigo, TipoProducto tipo)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, tipo, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                    [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])], [new PrecioFichaDto(null, 1000, 2000)]),
            ])));
        await servicio.CatalogoAlDiaAsync();
    }

    private static async Task HastaQueAsync(Func<bool> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!condicion())
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No pasó: {que}.");
            await Task.Delay(20);
        }
    }
}
