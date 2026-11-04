using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Lo que la auditoría antes de cerrar el piloto encontró en la pantalla de venta (2026-11-04): lo que hacía que un artículo se fuera
/// sin cobrar, el descuento que se quedaba con Esc, Supr y − sin su permiso, el porqué del cobro en lo apagado y el ticket en curso.
/// </summary>
public sealed class AuditoriaEnLaVentaTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-auditoria-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static Task Tecla(VentaConCopia v, string tecla, string? texto = null) => v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Tecla(tecla, texto));

    private static async Task<string> Texto(VentaConCopia v) => PantallaSinNavegador.Limpio(await v.Pantalla.TextoAsync());

    /// <summary>El dueño le pone «No» al cajero en una acción, antes de abrir su venta.</summary>
    private static async Task NoPuedeAsync(ServicioDePrueba servicio, AccionControlada accion)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var dueno = await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>();
        var cajero = await (await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>();
        var respuesta = await http.PutAsJsonAsync($"api/cajas/{caja.Id}/usuarios-y-permisos/{cajero!.UsuarioId}",
            new SolicitudPermisoUsuario(dueno!.UsuarioId, accion, NivelPermiso.NoPermitido));
        respuesta.EnsureSuccessStatusCode();
    }

    // ── Lo que no entró no se olvida ────────────────────────────────────────

    [Fact]
    public async Task Un_codigo_que_no_existe_se_recuerda_despues_del_siguiente_escaneo_bajo_el_cuadro_y_en_el_cobro()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = v.DosDeLaCopia();

        await v.EscanearAsync("9911223344556");
        await v.Pantalla.EsperarAsync(t => t.Contains("No existe"), "el aviso");
        await v.EscanearAsync("9911223344557");
        await v.Pantalla.EsperarAsync(t => t.Contains("No existe «9911223344557»"), "el segundo aviso");
        await v.EscanearAsync(a.Codigo!);

        // Los dos: ninguno se pierde con el que sigue.
        var aviso = PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("no-se-agrego")).Texto);
        Assert.Contains("9911223344556", aviso);
        Assert.Contains("9911223344557", aviso);
        var cobro = PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("cobro-no-se-agrego")).Texto);
        Assert.Contains("9911223344556", cobro);
        Assert.Contains("9911223344557", cobro);

        // No impide cobrar el ticket: solo se recuerda.
        Assert.False((await v.Pantalla.PorIdAsync("cobrar-nuevo")).Desactivado);

        // Volver a escanear lo que no entró (ya sin la cantidad) lo quita de la lista.
        var nuevo = await v.DarDeAltaEnOtraCajaAsync("9911223344556", "Producto de la auditoría");
        await v.EscanearAsync("9911223344556");
        await v.Pantalla.EsperarAsync(t => t.Contains(nuevo.Descripcion), "el renglón nuevo");
        var despues = PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("no-se-agrego")).Texto);
        Assert.DoesNotContain("9911223344556", despues);
        Assert.Contains("9911223344557", despues);

        // Esc lo quita, y el ticket siguiente tampoco lo trae.
        await Tecla(v, "Escape", "");
        Assert.DoesNotContain("no se agregó", await Texto(v));
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task La_caja_principal_que_no_contesta_tambien_se_recuerda()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, plazo: TimeSpan.FromMilliseconds(50));
        var (a, _) = v.DosDeLaCopia();
        v.Red.Detenida = new TaskCompletionSource();

        await v.EscanearAsync("9911223344558");
        await v.Pantalla.EsperarAsync(t => t.Contains("no contestó"), "el aviso");
        await v.EscanearAsync(a.Codigo!);

        Assert.Contains("9911223344558", (await v.Pantalla.PorIdAsync("no-se-agrego")).Texto);
        Assert.Contains("9911223344558", (await v.Pantalla.PorIdAsync("cobro-no-se-agrego")).Texto);
    }

    [Fact]
    public async Task Cinco_asterisco_despues_de_un_codigo_que_no_existe_no_cambia_el_renglon_de_antes()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync("9911223344559");
        await v.Pantalla.EsperarAsync(t => t.Contains("No existe"), "el aviso");

        await v.EscanearAsync("5*");

        Assert.StartsWith("1 ", (await VentaConCopia.RenglonesAsync(v.Pantalla))[0]);
        Assert.Contains("9911223344559", await Texto(v));

        // Con lo siguiente que sí entró, «N*» vuelve a ser para ese renglón.
        await v.EscanearAsync(b.Codigo!);
        await v.EscanearAsync("5*");
        Assert.StartsWith("5 ", (await VentaConCopia.RenglonesAsync(v.Pantalla))[1]);
    }

    [Fact]
    public async Task Doce_asterisco_de_algo_que_no_existe_conserva_la_cantidad_al_darlo_de_alta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);

        await v.EscanearAsync("12*9911223344560");
        await v.Pantalla.EsperarAsync(t => t.Contains("Darlo de alta aquí"), "el aviso");
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Darlo de alta aquí"));
        await v.Pantalla.EsperarElementoAsync("alta-nombre");
        await v.Pantalla.EscribirAsync("alta-nombre", "Libreta de la auditoría");
        await v.Pantalla.EsperarAsync(t => t.Contains("Cuadernos"), "los departamentos");
        var precio = await v.Pantalla.ElementoAsync(e => e.Etiqueta == "input" && e.Atributos.GetValueOrDefault("inputmode")?.ToString() == "decimal", "el precio");
        await v.Pantalla.DispararAsync(precio, "oninput", new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "12.50" });
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Dar de alta y agregar"));
        await v.Pantalla.EsperarAsync(t => t.Contains("quedó dado de alta"), "el alta");

        Assert.StartsWith("12 ", (await VentaConCopia.RenglonesAsync(v.Pantalla))[0]);
        // Y ya no se recuerda como lo que no entró.
        Assert.DoesNotContain("no se agregó", await Texto(v));
    }

    // ── La línea se ve, y se dice qué entró ─────────────────────────────────

    /// <summary>Como la página: cuando le piden bajar hasta una línea, mira si esa línea ya está dibujada.</summary>
    private sealed class PaginaQueMiraLaFila(PaginaQueContesta interna) : IJSRuntime, IJSObjectReference
    {
        public PantallaSinNavegador? Pantalla { get; set; }

        public List<(string Selector, bool Dibujada, bool Ilumina)> Vistas { get; } = [];

        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
                return ValueTask.FromResult((T)(object)this);
            if (identifier == "mostrar" && Pantalla is { } pantalla)
            {
                var id = ((string)args![0]!).TrimStart('#');
                var dibujada = pantalla.LeerAsync().Result.Elementos.Any(e => e.Id == id);
                lock (Vistas)
                    Vistas.Add((id, dibujada, args.Length > 1 && args[1] is true));
            }
            return interna.InvokeAsync<T>(identifier, cancellationToken, args);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Al_escanear_la_lista_baja_cuando_la_linea_ya_esta_dibujada_y_dice_que_entro()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var pagina = new PaginaQueMiraLaFila(new PaginaQueContesta());
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina);
        pagina.Pantalla = v.Pantalla;
        var (a, b) = v.DosDeLaCopia();

        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync("2*" + b.Codigo);

        Assert.Equal([("linea-0", true, true), ("linea-1", true, true)], pagina.Vistas);
        var entro = PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("entro")).Texto);
        Assert.Contains(b.Descripcion, entro);
        Assert.Contains("2 ×", entro);

        // Y lo que se quita, también se dice.
        await Tecla(v, "Delete", "");
        Assert.Contains($"Se quitó «{b.Descripcion}»", await Texto(v));
    }

    [Fact]
    public async Task Un_clic_en_la_fila_regresa_el_cursor_al_cuadro()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var pagina = new PaginaQueContesta();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        var antes = pagina.Llamadas.Count;

        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("linea-0"));

        Assert.Contains(pagina.Llamadas.Skip(antes), l => l.Funcion == "enfocar" && (string?)l.Argumentos?[0] == "#codigo");
    }

    // ── F12 con algo escrito ────────────────────────────────────────────────

    [Fact]
    public async Task F12_con_un_codigo_escrito_lo_agrega_antes_de_cobrar()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);

        await Tecla(v, "F12-pago", b.Codigo);

        Assert.Equal(2, (await VentaConCopia.RenglonesAsync(v.Pantalla)).Count);
        await Tecla(v, "Enter-cobro");
        Assert.Contains("cobrada", await Texto(v));
        var venta = Assert.Single(await v.Cliente.VentasDelDiaAsync(v.Caja.Id));
        Assert.Equal(a.Precios[0].PrecioCentavos + b.Precios[0].PrecioCentavos, venta.TotalCentavos);
    }

    [Fact]
    public async Task F12_con_un_nombre_a_medias_no_lo_adivina_lo_recuerda_y_deja_cobrar()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);

        await Tecla(v, "F12-pago", "cuad");

        Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains("«cuad»", (await v.Pantalla.PorIdAsync("cobro-no-se-agrego")).Texto);
        await Tecla(v, "Enter-cobro");
        Assert.Contains("cobrada", await Texto(v));
    }

    // ── Un escaneo con un panel abierto ─────────────────────────────────────

    [Fact]
    public async Task Un_escaneo_con_un_panel_que_se_deja_lo_cierra_y_entra_al_ticket()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "F9", "");
        await v.Pantalla.EsperarAsync(t => t.Contains("Verificador"), "el verificador");

        await v.EscanearAsync(b.Codigo!);

        Assert.Equal(2, (await VentaConCopia.RenglonesAsync(v.Pantalla)).Count);
        Assert.DoesNotContain("Verificador", await Texto(v));
    }

    [Fact]
    public async Task Un_escaneo_con_F2_abierto_entra_al_ticket_sin_cerrarlo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await Tecla(v, "F2", "");
        await v.Pantalla.EsperarAsync(t => t.Contains("Impresiones"), "F2");

        await v.EscanearAsync(a.Codigo!);

        Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains((await v.Pantalla.LeerAsync()).Elementos, e => e.Clase?.Contains("vn-panel") == true);
    }

    [Fact]
    public async Task Un_escaneo_con_un_panel_que_espera_su_enter_no_entra_y_se_recuerda()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "Ctrl+D", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");

        await v.EscanearAsync(b.Codigo!);

        Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains($"Se leyó «{b.Codigo}»", await Texto(v));
        await Tecla(v, "Escape");
        Assert.Contains(b.Codigo!, (await v.Pantalla.PorIdAsync("cobro-no-se-agrego")).Texto);
    }

    [Theory]
    [InlineData("7501234567890*")]
    [InlineData("99999999999999999*")]
    public async Task Un_codigo_nunca_es_una_cantidad(string escrito)
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);

        await v.EscanearAsync(escrito);
        Assert.StartsWith("1 ", (await VentaConCopia.RenglonesAsync(v.Pantalla))[0]);
        Assert.Contains("Cantidad inválida", await Texto(v));

        await Tecla(v, "F11", escrito.TrimEnd('*'));
        Assert.StartsWith("1 ", (await VentaConCopia.RenglonesAsync(v.Pantalla))[0]);

        await v.EscanearAsync(escrito + b.Codigo);
        Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Empty(v.Pantalla.Errores);
    }

    // ── La lista y Enter ────────────────────────────────────────────────────

    [Fact]
    public async Task Una_palabra_con_numero_y_Enter_agrega_lo_que_la_lista_marca()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("cr2032"));
        await v.Pantalla.EsperarAsync(t => t.Contains("CR2032"), "la lista");
        await v.EscanearAsync("cr2032");

        Assert.Contains("CR2032", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.DoesNotContain("No existe", await Texto(v));
    }

    [Fact]
    public async Task Un_codigo_exacto_con_la_lista_abierta_sigue_siendo_ese_codigo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        // «ZZ01» es el código de uno y sale en el nombre de otro, que la lista marcaría primero.
        await v.DarDeAltaEnOtraCajaAsync("ZZ01", "Lápiz de la auditoría");
        await v.DarDeAltaEnOtraCajaAsync("ZZ02", "Bolsa con ZZ01 impreso");
        await v.Contingencia.ActualizarPaqueteAsync();

        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("ZZ01"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Bolsa"), "la lista");
        await v.EscanearAsync("ZZ01");

        Assert.Contains("Lápiz de la auditoría", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
    }
}
