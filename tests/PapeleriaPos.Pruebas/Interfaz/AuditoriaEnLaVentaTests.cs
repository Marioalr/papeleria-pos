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

    // ── «Quitar un producto» en No ──────────────────────────────────────────

    [Fact]
    public async Task Sin_permiso_de_quitar_ni_Supr_ni_la_tecla_menos_ni_bajar_la_cantidad_quitan_nada()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await NoPuedeAsync(servicio, AccionControlada.QuitarProducto);
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync("3*" + a.Codigo);
        var antes = await VentaConCopia.RenglonesAsync(v.Pantalla);

        await Tecla(v, "Delete", "");
        Assert.Equal(antes, await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains("Eso no está entre lo que puedes hacer", await Texto(v));

        await Tecla(v, "-", "");
        Assert.Equal(antes, await VentaConCopia.RenglonesAsync(v.Pantalla));

        await v.EscanearAsync("1*");
        Assert.Equal(antes, await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains("Eso no está entre lo que puedes hacer", await Texto(v));

        // Subir sí se puede.
        await Tecla(v, "+", "");
        Assert.StartsWith("4 ", (await VentaConCopia.RenglonesAsync(v.Pantalla))[0]);
        Assert.Empty(v.Pantalla.Errores);
    }

    [Fact]
    public async Task Eliminar_este_ticket_quita_todo_con_un_solo_renglon_en_la_bitacora()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync(b.Codigo!);

        await v.EscanearAsync("/eliminar");

        Assert.Empty(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Contains("Se eliminó el ticket", await Texto(v));
        using var db = servicio.Base();
        var renglon = await db.Bitacora.SingleAsync(x => x.Accion == AccionControlada.QuitarProducto);
        Assert.Contains(a.Descripcion, renglon.Detalle);
        Assert.Contains(b.Descripcion, renglon.Detalle);
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

    // ── El descuento que nadie autorizó ─────────────────────────────────────

    [Fact]
    public async Task Esc_en_la_autorizacion_del_descuento_lo_deshace()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync("10*" + a.Codigo);
        var antes = await VentaConCopia.RenglonesAsync(v.Pantalla);

        await Tecla(v, "Ctrl+D", "");
        await v.Pantalla.EscribirAsync("descuento-valor", "15");
        var motivo = await v.Pantalla.ElementoAsync(e => e.Etiqueta == "input" && e.Atributos.GetValueOrDefault("placeholder")?.ToString()?.StartsWith("Cliente frecuente") == true, "el motivo");
        await v.Pantalla.DispararAsync(motivo, "oninput", new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "prueba" });
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Aplicar (Enter)"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Lo autoriza"), "la autorización");

        await Tecla(v, "Escape");

        Assert.Equal(antes, await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.DoesNotContain("Descuentos", await Texto(v));
    }

    // ── Lo que impide cobrar, donde se está mirando ─────────────────────────

    [Fact]
    public async Task Si_el_cobro_no_pasa_el_porque_sale_en_el_panel_y_lo_escrito_queda_seleccionado()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var pagina = new PaginaQueContesta();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina, quien: ("dueno", "3333"));
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);

        // No alcanza: el panel ya lo dice, y lo escrito queda seleccionado, para que lo siguiente lo reemplace.
        await v.Pantalla.EscribirAsync("pago-nuevo", "1");
        var antes = pagina.Llamadas.Count;
        await Tecla(v, "Enter-cobro");
        Assert.Contains(pagina.Llamadas.Skip(antes), l => l.Funcion == "enfocar" && (string?)l.Argumentos?[0] == "#pago-nuevo");

        // El turno se cierra debajo de la venta (el corte de eleventa): el cobro falla y se dice en el panel.
        await v.Pantalla.EscribirAsync("pago-nuevo", "");
        using (var db = servicio.Base())
        {
            var turno = await db.Turnos.FirstAsync(t => t.CerradoUtc == null);
            turno.CerradoUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        await Tecla(v, "Enter-cobro");
        var alertas = (await v.Pantalla.LeerAsync()).Elementos.Where(e => e.Clase == "vn-alerta").Select(e => e.Texto);
        Assert.Contains(alertas, t => t.Contains("turno", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Mientras_se_cobra_el_boton_lo_dice_y_se_apaga()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        v.Red.CobroRetenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? cobro = null;
        await v.Pantalla.Dispatcher.InvokeAsync(() => { cobro = v.Venta.Tecla("Enter-cobro", null); });
        await VentaConCopia.EsperarAsync(() => v.Red.Cuantas("/api/ventas") >= 1, "el cobro en camino");

        var boton = await v.Pantalla.PorIdAsync("cobrar-nuevo");
        Assert.Contains("Cobrando", boton.Texto);
        Assert.True(boton.Desactivado);

        v.Red.CobroRetenido.SetResult();
        await cobro!.WaitAsync(TimeSpan.FromSeconds(20));
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
        Assert.Contains("no es una cantidad", await Texto(v));

        await Tecla(v, "F11", escrito.TrimEnd('*'));
        Assert.StartsWith("1 ", (await VentaConCopia.RenglonesAsync(v.Pantalla))[0]);

        await v.EscanearAsync(escrito + b.Codigo);
        Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Empty(v.Pantalla.Errores);
    }

    // ── Lo que truena en una tecla ──────────────────────────────────────────

    /// <summary>La página, que truena en lo que se le diga.</summary>
    private sealed class PaginaQueTruena(PaginaQueContesta interna) : IJSRuntime, IJSObjectReference
    {
        public string? Truena { get; set; }

        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
                return ValueTask.FromResult((T)(object)this);
            if (identifier == Truena)
                throw new InvalidCastException("Algo que nadie esperaba.");
            return interna.InvokeAsync<T>(identifier, cancellationToken, args);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Lo_que_truena_en_una_tecla_se_dice_y_el_ticket_se_queda()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var pagina = new PaginaQueTruena(new PaginaQueContesta());
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        pagina.Truena = "ventanaAbierta";

        await Tecla(v, "F9", "");

        Assert.Contains("Algo falló y no se hizo", await Texto(v));
        Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla));
        Assert.Empty(v.Pantalla.Errores);
        pagina.Truena = null;
    }

    // ── El ticket en curso ──────────────────────────────────────────────────

    [Fact]
    public async Task El_ticket_que_se_atiende_se_guarda_solo_y_vuelve_en_espera_sin_preguntar()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var (a, b) = (default(ProductoVentaDto)!, default(ProductoVentaDto)!);
        var ventaId = Guid.Empty;
        await using (var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal))
        {
            (a, b) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);
            await v.EscanearAsync(b.Codigo!);
            // Sin F6 ni nada: como si en este momento se fuera la luz.
            await VentaConCopia.EsperarAsync(() => v.Contingencia.TicketsEnEspera() is [{ Lineas.Count: 2 }], "el ticket guardado");
            ventaId = v.Contingencia.TicketsEnEspera()[0].VentaId;

            // Lo último, también.
            await v.EscanearAsync("3*" + a.Codigo);
            await VentaConCopia.EsperarAsync(() => v.Contingencia.TicketsEnEspera() is [{ Lineas: var l }] && l.Any(x => x.CantidadMilesimas == 4000), "la cantidad nueva");
        }
        await using (var otra = await VentaConCopia.AbrirAsync(servicio, _baseLocal))
        {
            Assert.Contains("Volvió el ticket", await Texto(otra));
            await Tecla(otra, "F5", "");
            Assert.Equal(2, (await VentaConCopia.RenglonesAsync(otra.Pantalla)).Count);
            // Se cobra como la misma venta, y al cobrarla ya no se guarda: el segundo que se esperaba no la trae de regreso.
            await Tecla(otra, "Enter-cobro");
            Assert.Contains("cobrada", await Texto(otra));
            await VentaConCopia.EsperarAsync(() => otra.Contingencia.TicketsEnEspera().Count == 0, "la espera vacía");
            await Task.Delay(1300);
            Assert.Empty(otra.Contingencia.TicketsEnEspera());
            Assert.Contains(await otra.Cliente.VentasDelDiaAsync(otra.Caja.Id), x => x.Id == ventaId);
        }
    }

    [Fact]
    public async Task Al_quitar_la_pantalla_el_ticket_queda_como_estaba_sin_esperar_el_segundo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await v.EscanearAsync(b.Codigo!);

        // Lo que hace «Volver a cargar» o «Algo falló»: la pantalla se quita con sus dos renglones.
        await v.Pantalla.Dispatcher.InvokeAsync(async () => await v.Venta.DisposeAsync());

        Assert.Equal(2, Assert.Single(v.Contingencia.TicketsEnEspera()).Lineas.Count);
    }

    [Theory]
    // Se entiende, pero le faltan sus listas: Rearmar truena.
    [InlineData("""[{"VentaId":"01a1a800-1c36-73d9-a5ee-0bf12c8970fa","CreadaUtc":"2026-11-04T00:00:00Z"}]""")]
    // De otra versión: no se entiende.
    [InlineData("""[{"VentaId":5,"01a1a800-1c36-73d9-a5ee-0bf12c8970fa":1}]""")]
    public async Task Lo_guardado_que_no_se_arma_no_deja_sin_entrar_a_la_venta_y_se_aparta(string guardado)
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using (var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal))
        {
        }
        using (var c = new SqliteConnection(ArchivosDePrueba.Cadena(_baseLocal)))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO Ajustes (Clave, Valor) VALUES ('espera', $v) ON CONFLICT (Clave) DO UPDATE SET Valor = $v";
            cmd.Parameters.AddWithValue("$v", guardado);
            cmd.ExecuteNonQuery();
        }

        await using var otra = await VentaConCopia.AbrirAsync(servicio, _baseLocal);

        Assert.Empty(otra.Pantalla.Errores);
        Assert.Equal("input", (await otra.Pantalla.PorIdAsync("codigo")).Etiqueta);
        Assert.Empty(otra.Contingencia.TicketsEnEspera());
        Assert.Equal(guardado, LeerAjuste(_baseLocal, "espera-sin-leer-%"));
    }

    private static string? LeerAjuste(string baseLocal, string patron)
    {
        using var c = new SqliteConnection(ArchivosDePrueba.Cadena(baseLocal));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Valor FROM Ajustes WHERE Clave LIKE $p";
        cmd.Parameters.AddWithValue("$p", patron);
        return cmd.ExecuteScalar() as string;
    }

    // ── El turno que cerró el corte de eleventa ─────────────────────────────

    private static async Task CerrarElTurnoAllaAsync(ServicioDePrueba servicio)
    {
        using var db = servicio.Base();
        var turno = await db.Turnos.FirstAsync(t => t.CerradoUtc == null);
        turno.CerradoUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task En_el_piloto_el_corte_de_eleventa_con_el_ticket_vacio_pasa_solo_al_siguiente_turno()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        await v.Contingencia.RecordarPilotoEleventaAsync(true);
        await VentaConCopia.EsperarAsync(() => v.Contingencia.PilotoEleventa, "el piloto");
        await v.Pantalla.Dispatcher.InvokeAsync(() => { });

        // Con el turno abierto, nada.
        await v.Pantalla.Dispatcher.InvokeAsync(v.Venta.RevisarElTurnoAsync);
        Assert.DoesNotContain("ya se cerró", await Texto(v));

        await CerrarElTurnoAllaAsync(servicio);
        var siguio = false;
        await v.Pantalla.Dispatcher.InvokeAsync(async () =>
        {
            await v.Venta.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PantallaVenta.AlCerrarTurno)] = EventCallback.Factory.Create(this, () => { siguio = true; }),
            }));
            await v.Venta.RevisarElTurnoAsync();
        });

        Assert.True(siguio);
    }

    [Fact]
    public async Task En_el_piloto_el_corte_de_eleventa_con_algo_en_el_ticket_lo_dice_y_lo_guarda_en_espera()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.Contingencia.RecordarPilotoEleventaAsync(true);
        await v.Pantalla.Dispatcher.InvokeAsync(() => { });
        await v.EscanearAsync(a.Codigo!);

        await CerrarElTurnoAllaAsync(servicio);
        var siguio = false;
        await v.Pantalla.Dispatcher.InvokeAsync(async () =>
        {
            await v.Venta.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PantallaVenta.AlCerrarTurno)] = EventCallback.Factory.Create(this, () => { siguio = true; }),
            }));
            await v.Venta.RevisarElTurnoAsync();
        });

        Assert.False(siguio);
        Assert.Contains("ya se cerró con el corte de eleventa", await Texto(v));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Abrir el siguiente turno"));
        Assert.True(siguio);
        Assert.Single(v.Contingencia.TicketsEnEspera());
    }

    [Fact]
    public async Task Turno_de_otro_en_el_piloto_pide_la_lectura_y_sigue_solo_al_cerrarse()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        var sesion = await v.Cliente.EntrarAsync("encargado", "2222");
        await using var otra = new PantallaSinNavegador(v.Servicios);
        var siguio = 0;
        var pantalla = await otra.AbrirAsync<TurnoDeOtro>(new()
        {
            [nameof(TurnoDeOtro.Caja)] = v.Caja,
            [nameof(TurnoDeOtro.Turno)] = turno,
            [nameof(TurnoDeOtro.Sesion)] = sesion,
            [nameof(TurnoDeOtro.Piloto)] = true,
            [nameof(TurnoDeOtro.AlTerminarCorte)] = EventCallback.Factory.Create(this, () => { siguio++; }),
        });
        await VentaConCopia.EsperarAsync(() => v.Red.Cuantas("/api/eleventa/leer") >= 1, "la lectura");
        Assert.Contains("aquí sigue solo", await otra.TextoAsync());

        await otra.Dispatcher.InvokeAsync(pantalla.RevisarElTurnoAsync);
        Assert.Equal(0, siguio);

        await CerrarElTurnoAllaAsync(servicio);
        await otra.Dispatcher.InvokeAsync(pantalla.RevisarElTurnoAsync);
        Assert.Equal(1, siguio);
    }

    // ── Rápidos que no llegaron ─────────────────────────────────────────────

    [Fact]
    public async Task Los_rapidos_que_no_llegaron_al_entrar_vuelven_con_la_conexion_y_Alt_lo_dice()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        Assert.Contains("Alt+1", await Texto(v));

        // Se vuelve a entrar sin conexión: otra pantalla, sobre la misma caja.
        v.Red.Caida = true;
        await v.Contingencia.LatidoAsync();
        await using var fuera = new PantallaSinNavegador(v.Servicios);
        var sinRed = await fuera.AbrirAsync<PantallaVenta>(new()
        {
            [nameof(PantallaVenta.Caja)] = v.Caja,
            [nameof(PantallaVenta.Sesion)] = v.Cajero,
            [nameof(PantallaVenta.Turno)] = await v.Cliente.TurnoAsync(v.Caja.Id),
        });
        await fuera.EsperarAsync(t => t.Contains("Poner en espera"), "la venta");
        await Task.Delay(300);
        Assert.DoesNotContain("Copia B/N", await fuera.TextoAsync());
        await fuera.Dispatcher.InvokeAsync(() => sinRed.Tecla("Alt+1", ""));
        Assert.Contains("Los rápidos no han llegado", await fuera.TextoAsync());

        v.Red.Caida = false;
        for (var i = 0; i < 4 && !v.Contingencia.EnLinea; i++)
            await v.Contingencia.LatidoAsync();
        Assert.True(v.Contingencia.EnLinea);
        await fuera.EsperarAsync(t => t.Contains("Copia B/N"), "los rápidos");
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

    // ── En el piloto, el folio ──────────────────────────────────────────────

    [Fact]
    public async Task En_el_piloto_un_numero_que_no_existe_ofrece_abrirlo_como_folio()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        await v.Contingencia.RecordarPilotoEleventaAsync(true);
        await v.Pantalla.Dispatcher.InvokeAsync(() => { });

        await v.EscanearAsync("4521");
        await Task.Delay(1000);
        Assert.True((await v.Pantalla.TextoAsync()).Contains("Es un folio"), await Texto(v));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Es un folio"));

        await v.Pantalla.EsperarAsync(t => t.Contains("Devoluciones y cancelaciones"), "la devolución");
        Assert.DoesNotContain("no se agregó", await Texto(v));
    }

    [Fact]
    public async Task Fuera_del_piloto_un_numero_que_no_existe_no_ofrece_folio()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        await v.EscanearAsync("4521");
        await v.Pantalla.EsperarAsync(t => t.Contains("No existe"), "el aviso");
        Assert.DoesNotContain("Es un folio", await Texto(v));
    }

    // ── El aviso del corte, en su lugar ─────────────────────────────────────

    [Fact]
    public async Task El_aviso_de_tickets_sin_cobrar_va_dentro_de_lo_que_le_da_sus_colores()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Corte"));

        await v.Pantalla.EsperarAsync(t => t.Contains("sin cobrar"), "el aviso");
        var (_, elementos) = await v.Pantalla.LeerAsync();
        Assert.Contains(elementos, e => e.Clase?.Split(' ').Contains("vn-corte") == true && e.Texto.Contains("sin cobrar"));
    }

    // ── Lo chico de la venta ────────────────────────────────────────────────

    [Fact]
    public async Task F5_recorre_todos_los_tickets_en_espera()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var lista = v.DeLaCopia();
        foreach (var producto in lista.Take(2))
        {
            await v.EscanearAsync(producto.Codigo!);
            await Tecla(v, "F6", "");
        }
        await v.EscanearAsync(lista[2].Codigo!);

        var vistos = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            await Tecla(v, "F5", "");
            vistos.Add((await VentaConCopia.RenglonesAsync(v.Pantalla))[0]);
        }

        Assert.Equal(3, vistos.Distinct().Count());
    }

    [Fact]
    public async Task Vaciar_el_ticket_no_pasa_el_pago_ni_la_nota_al_siguiente()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await v.Pantalla.EscribirAsync("pago-nuevo", "500");
        await Tecla(v, "Delete", "");

        await v.EscanearAsync(b.Codigo!);

        Assert.Equal("", (await v.Pantalla.PorIdAsync("pago-nuevo")).Valor ?? "");
        Assert.DoesNotContain("cambio", (await v.Pantalla.PorIdAsync("cobrar-nuevo")).Texto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void El_boton_de_un_rapido_redondea_como_el_renglon()
    {
        var producto = new ProductoVentaDto(Guid.NewGuid(), "x", "1", default, UnidadBase.Pieza, false, 0m, null!, [], [new PrecioDto(Guid.NewGuid(), null, null, 1000, 100)]);
        Assert.Equal(13, PantallaVenta.PrecioDelRapido(new Rapido("x", producto.VarianteId, 0.125m), producto, Guid.NewGuid()));
    }

    [Fact]
    public async Task En_el_abono_Enter_con_el_monto_vacio_no_liquida_y_Liquidar_si()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        var cliente = await v.Cliente.GuardarClienteAsync(new SolicitudGuardarCliente(v.Cajero.UsuarioId, v.Caja.SucursalId,
            new ClienteRegistradoDto(Guid.Empty, "Arq. de la auditoría", 0, null, true)));
        await v.Cliente.GuardarCondicionesAsync(cliente.Id, new SolicitudCreditoCliente(v.Cajero.UsuarioId, v.Caja.SucursalId, 100000, 30));
        await v.Cliente.CobrarAsync(new SolicitudCobro(v.Caja.Id, v.Venta.Turno.Id, v.Cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Mercancía", 5000)], [new CobroSolicitado(FormaPago.Credito, 5000)], Guid.NewGuid(), ClienteId: cliente.Id));

        await Tecla(v, "Ctrl+B", "");
        await v.Pantalla.EsperarElementoAsync("buscar-deudor");
        await v.Pantalla.EsperarAsync(t => t.Contains("Arq. de la auditoría"), "la lista");
        await v.Pantalla.TeclaAsync("buscar-deudor", "Enter");
        await v.Pantalla.EsperarElementoAsync("abono-monto");

        await v.Pantalla.TeclaAsync("abono-monto", "Enter");
        Assert.Contains("Escribe cuánto abona", await Texto(v));
        Assert.Equal(5000, (await v.Cliente.CreditoAsync(cliente.Id)).SaldoCentavos);

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Liquidar"));
        await v.Pantalla.TeclaAsync("abono-monto", "Enter");
        await VentaConCopia.EsperarAsync(() => v.Cliente.CreditoAsync(cliente.Id).Result.SaldoCentavos == 0, "el abono");
    }

    /// <summary>Dos que deben, y la pantalla del abono abierta en la lista.</summary>
    private async Task<(VentaConCopia V, PaginaQueContesta Pagina, ClienteRegistradoDto Ana, ClienteRegistradoDto Beto)> AbonoConDosDeudoresAsync(ServicioDePrueba servicio)
    {
        var pagina = new PaginaQueContesta();
        var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina, quien: ("dueno", "3333"));
        var (ana, beto) = (await FiadoAsync(v, "Ana del abono", 5000), await FiadoAsync(v, "Beto del abono", 7000));
        await Tecla(v, "Ctrl+B", "");
        await v.Pantalla.EsperarElementoAsync("buscar-deudor");
        await v.Pantalla.EsperarAsync(t => t.Contains("Beto del abono"), "la lista");
        return (v, pagina, ana, beto);
    }

    private static async Task<ClienteRegistradoDto> FiadoAsync(VentaConCopia v, string nombre, long centavos)
    {
        var cliente = await v.Cliente.GuardarClienteAsync(new SolicitudGuardarCliente(v.Cajero.UsuarioId, v.Caja.SucursalId,
            new ClienteRegistradoDto(Guid.Empty, nombre, 0, null, true)));
        await v.Cliente.GuardarCondicionesAsync(cliente.Id, new SolicitudCreditoCliente(v.Cajero.UsuarioId, v.Caja.SucursalId, 100000, 30));
        await MasFiadoAsync(v, cliente, centavos);
        return cliente;
    }

    private static Task MasFiadoAsync(VentaConCopia v, ClienteRegistradoDto cliente, long centavos) =>
        v.Cliente.CobrarAsync(new SolicitudCobro(v.Caja.Id, v.Venta.Turno.Id, v.Cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Mercancía", centavos)], [new CobroSolicitado(FormaPago.Credito, centavos)], Guid.NewGuid(), ClienteId: cliente.Id));

    private static async Task ElegirAlDeudorAsync(VentaConCopia v, string nombre)
    {
        await v.Pantalla.EscribirAsync("buscar-deudor", nombre);
        await v.Pantalla.TeclaAsync("buscar-deudor", "Enter");
        await v.Pantalla.EsperarElementoAsync("abono-monto");
    }

    /// <summary>«Abona» vacío no dice «liquida» ni deja oprimir «Abonar», y Enter pide el monto: nada registra un abono sin haber entrado dinero.</summary>
    private static async Task SinLiquidarAsync(VentaConCopia v, ClienteRegistradoDto cliente, long saldo)
    {
        Assert.DoesNotContain("(liquida)", await Texto(v));
        Assert.True((await v.Pantalla.BotonAsync("Abonar (Enter)")).Desactivado);
        await v.Pantalla.TeclaAsync("abono-monto", "Enter");
        Assert.Contains("Escribe cuánto abona", await Texto(v));
        Assert.Equal(saldo, (await v.Cliente.CreditoAsync(cliente.Id)).SaldoCentavos);
    }

    [Fact]
    public async Task En_el_abono_Liquidar_no_se_queda_puesto_para_otro_cliente_ni_al_escribir()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var (v, pagina, ana, beto) = await AbonoConDosDeudoresAsync(servicio);
        await using var _ = v;

        await ElegirAlDeudorAsync(v, "Ana");
        await SinLiquidarAsync(v, ana, 5000);

        // Se oprime «Liquidar» y no era ella: el siguiente cliente empieza sin eso.
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Liquidar"));
        Assert.Contains("(liquida)", await Texto(v));
        Assert.DoesNotContain("Escribe cuánto abona", await Texto(v));
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Otro cliente"));
        await ElegirAlDeudorAsync(v, "Beto");
        await SinLiquidarAsync(v, beto, 7000);

        // «Liquidar», se escribe algo y se borra: tampoco.
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Liquidar"));
        await v.Pantalla.EscribirAsync("abono-monto", "10");
        await v.Pantalla.EscribirAsync("abono-monto", "");
        await SinLiquidarAsync(v, beto, 7000);

        // Oprimirlo deja el cursor donde el siguiente Enter lo registra; el Enter que se quedó apretado no cuenta.
        var antes = pagina.Llamadas.Count;
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Liquidar"));
        await v.Pantalla.EsperarAsync(_ => pagina.Llamadas.Skip(antes).Any(l => l.Funcion == "enfocar" && (string?)l.Argumentos?[0] == "#abono-monto"), "el cursor en el monto");
        await v.Pantalla.DispararAsync(await v.Pantalla.PorIdAsync("abono-monto"), "onkeydown", new KeyboardEventArgs { Key = "Enter", Repeat = true });
        Assert.Equal(7000, (await v.Cliente.CreditoAsync(beto.Id)).SaldoCentavos);
        await v.Pantalla.TeclaAsync("abono-monto", "Enter");
        await VentaConCopia.EsperarAsync(() => v.Cliente.CreditoAsync(beto.Id).Result.SaldoCentavos == 0, "el abono");
    }

    [Fact]
    public async Task En_el_abono_si_el_saldo_cambio_Liquidar_se_vuelve_a_oprimir()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var (v, _, ana, _) = await AbonoConDosDeudoresAsync(servicio);
        await using var _ = v;

        await ElegirAlDeudorAsync(v, "Ana");
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Liquidar"));
        await MasFiadoAsync(v, ana, 1000);
        await v.Pantalla.TeclaAsync("abono-monto", "Enter");
        await v.Pantalla.EsperarAsync(t => t.Contains("$60.00"), "el saldo de ahora");

        // Se liquidaba lo que se vio: con el saldo nuevo, otro Enter no se lleva los $60.00.
        await SinLiquidarAsync(v, ana, 6000);
    }

    [Fact]
    public async Task Lo_que_dice_que_entro_sigue_al_renglon_si_luego_cambia_su_cantidad()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        async Task<string> Entro() => PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("entro")).Texto);

        await v.EscanearAsync(a.Codigo!);
        Assert.Contains("1 ×", await Entro());

        await v.EscanearAsync("5*");
        Assert.Contains("5 ×", await Entro());

        await Tecla(v, "+", "");
        Assert.Contains("6 ×", await Entro());
    }

    [Fact]
    public async Task Vigilar_el_turno_no_le_dice_nada_a_la_red_ni_toca_la_cuenta_del_turno()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        await v.Contingencia.RecordarPilotoEleventaAsync(true);
        await VentaConCopia.EsperarAsync(() => v.Contingencia.PilotoEleventa, "el piloto");
        await v.Pantalla.Dispatcher.InvokeAsync(() => { });
        var deLaCaja = $"/api/cajas/{v.Caja.Id}/turno";
        var resumenes = v.Red.Cuantas("/resumen");

        // Tarda más que el plazo de la venta (la lectura de eleventa apartó la base): nadie lo espera, y la caja sigue en línea.
        v.Cliente.PlazoLectura = TimeSpan.FromMilliseconds(50);
        v.Red.RetrasoDe = ruta => ruta.EndsWith(deLaCaja, StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero;
        await v.Pantalla.Dispatcher.InvokeAsync(v.Venta.RevisarElTurnoAsync);
        Assert.Equal(EstadoRed.Buena, v.Contingencia.EstadoDeLaRed);
        Assert.False(v.Red.ConPlazos(deLaCaja).Last());
        Assert.Equal(resumenes, v.Red.Cuantas("/resumen"));

        // Se pierde la respuesta: no se sabe, y quien dice si hay red es el latido.
        v.Red.RetrasoDe = _ => TimeSpan.Zero;
        v.Red.PierdeRespuesta = ruta => ruta.EndsWith(deLaCaja, StringComparison.Ordinal);
        await v.Pantalla.Dispatcher.InvokeAsync(v.Venta.RevisarElTurnoAsync);
        Assert.Equal(EstadoRed.Buena, v.Contingencia.EstadoDeLaRed);
        Assert.DoesNotContain("ya se cerró", await Texto(v));
    }

    [Fact]
    public async Task Vigilar_el_turno_en_la_pantalla_del_turno_de_otro_tampoco_marca_la_red()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        var sesion = await v.Cliente.EntrarAsync("encargado", "2222");
        await using var otra = new PantallaSinNavegador(v.Servicios);
        var siguio = 0;
        var pantalla = await otra.AbrirAsync<TurnoDeOtro>(new()
        {
            [nameof(TurnoDeOtro.Caja)] = v.Caja,
            [nameof(TurnoDeOtro.Turno)] = turno,
            [nameof(TurnoDeOtro.Sesion)] = sesion,
            [nameof(TurnoDeOtro.Piloto)] = true,
            [nameof(TurnoDeOtro.AlTerminarCorte)] = EventCallback.Factory.Create(this, () => { siguio++; }),
        });
        var deLaCaja = $"/api/cajas/{v.Caja.Id}/turno";
        v.Cliente.PlazoLectura = TimeSpan.FromMilliseconds(50);
        v.Red.RetrasoDe = ruta => ruta.EndsWith(deLaCaja, StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero;

        await otra.Dispatcher.InvokeAsync(pantalla.RevisarElTurnoAsync);

        Assert.Equal(EstadoRed.Buena, v.Contingencia.EstadoDeLaRed);
        Assert.Equal(0, siguio);
    }

    [Fact]
    public async Task Volver_a_escanear_no_reemplaza_el_descuento_a_mano_por_el_del_cliente()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("dueno", "3333"));
        await v.Cliente.GuardarClienteAsync(new SolicitudGuardarCliente(v.Cajero.UsuarioId, v.Caja.SucursalId,
            new ClienteRegistradoDto(Guid.Empty, "Escuela de la auditoría", 500, null, true)));
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "Ctrl+L", "");
        await v.Pantalla.EsperarAsync(t => t.Contains("Escuela de la auditoría"), "la lista de clientes");
        await v.Pantalla.OprimirAsync(await v.Pantalla.ElementoAsync(e => e.Etiqueta == "tr" && e.Texto.Contains("Escuela de la auditoría"), "el cliente"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cliente: Escuela de la auditoría"), "el cliente en el ticket");
        await Tecla(v, "Ctrl+D", "");
        await v.Pantalla.EsperarElementoAsync("descuento-valor");
        await v.Pantalla.EscribirAsync("descuento-valor", "20");
        var motivo = await v.Pantalla.ElementoAsync(e => e.Etiqueta == "input" && e.Atributos.GetValueOrDefault("placeholder")?.ToString()?.StartsWith("Cliente frecuente") == true, "el motivo");
        await v.Pantalla.DispararAsync(motivo, "oninput", new ChangeEventArgs { Value = "A mano" });
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Aplicar"));
        await v.Pantalla.EsperarAsync(t => t.Contains("A mano"), "el descuento a mano");

        await v.EscanearAsync(a.Codigo!);

        Assert.Contains("A mano", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
    }

    [Fact]
    public async Task La_ficha_en_espera_dice_cuando_empezo_este_cliente()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        var abierta = DateTime.UtcNow;
        await Task.Delay(50);
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "F6", "");
        Assert.True(Assert.Single(v.Contingencia.TicketsEnEspera()).CreadaUtc > abierta);
    }

    [Fact]
    public void La_hoja_de_codigos_con_uno_dice_uno()
    {
        Assert.Equal("1 producto en la hoja.", HojaCodigos.MensajeDeLaHoja(1, 0));
        Assert.Equal("1 producto en la hoja. A 1 se le dio código interno: ya se puede cobrar escaneándolo.", HojaCodigos.MensajeDeLaHoja(1, 1));
        Assert.Equal("3 productos en la hoja. A 2 se les dio código interno: ya se pueden cobrar escaneándolo.", HojaCodigos.MensajeDeLaHoja(3, 2));
    }

    [Fact]
    public async Task Articulo_comun_lleva_lo_que_se_buscaba_y_el_cuadro_queda_vacio()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var pagina = new PaginaQueContesta();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina);

        await Tecla(v, "Ctrl+P", "papel china morado");
        await v.Pantalla.EsperarElementoAsync("comun-descripcion");
        Assert.Equal("papel china morado", (await v.Pantalla.PorIdAsync("comun-descripcion")).Valor);
        await VentaConCopia.EsperarAsync(() => pagina.Llamadas.Any(l => l.Funcion == "enfocar" && (string?)l.Argumentos?[0] == "#comun-precio"), "el cursor en el precio");
        await v.Pantalla.EscribirAsync("comun-precio", "5");
        var antes = pagina.Llamadas.Count;
        await v.Pantalla.TeclaAsync("comun-precio", "Enter");

        Assert.Contains("papel china morado", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Contains(pagina.Llamadas.Skip(antes), l => l.Funcion == "limpiarCaptura");
    }

    // ── Lo que encontró la revisión de la rama junta (2026-11-06) ───────────

    /// <summary>El botón de la barra no pasa por caja.js: le pregunta a la página, y lo escrito entra como con la tecla.</summary>
    [Fact]
    public async Task El_F12_de_la_barra_con_algo_escrito_en_el_cuadro_lo_agrega_como_la_tecla()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var pagina = new PaginaQueContesta();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        pagina.EnLaCaptura = b.Codigo;

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("F12"));

        await VentaConCopia.EsperarAsync(() => VentaConCopia.RenglonesAsync(v.Pantalla).Result.Count == 2, "lo escrito en el ticket");
        Assert.Null(pagina.EnLaCaptura);
        Assert.Empty(await v.Cliente.VentasDelDiaAsync(v.Caja.Id));
    }

    /// <summary>Con un clic en «Pagó con» lo escrito se queda en el cuadro: Enter ahí no cobra sin ello, y el panel enseña el total nuevo.</summary>
    [Fact]
    public async Task Enter_en_el_cobro_con_algo_escrito_en_el_cuadro_lo_agrega_y_no_cobra_sin_ello()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);

        await Tecla(v, "Enter-cobro", b.Codigo);

        await VentaConCopia.EsperarAsync(() => VentaConCopia.RenglonesAsync(v.Pantalla).Result.Count == 2, "lo escrito en el ticket");
        Assert.Empty(await v.Cliente.VentasDelDiaAsync(v.Caja.Id));

        // Sin nada escrito, Enter cobra lo que se ve, sin más pasos.
        await Tecla(v, "Enter-cobro");
        var venta = Assert.Single(await v.Cliente.VentasDelDiaAsync(v.Caja.Id));
        Assert.Equal(a.Precios[0].PrecioCentavos + b.Precios[0].PrecioCentavos, venta.TotalCentavos);
    }

    [Fact]
    public async Task El_boton_de_cobrar_con_algo_escrito_en_el_cuadro_lo_agrega_y_no_cobra_sin_ello()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var pagina = new PaginaQueContesta();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina);
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        pagina.EnLaCaptura = b.Codigo;

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        await VentaConCopia.EsperarAsync(() => VentaConCopia.RenglonesAsync(v.Pantalla).Result.Count == 2, "lo escrito en el ticket");
        Assert.Empty(await v.Cliente.VentasDelDiaAsync(v.Caja.Id));

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        var venta = Assert.Single(await v.Cliente.VentasDelDiaAsync(v.Caja.Id));
        Assert.Equal(a.Precios[0].PrecioCentavos + b.Precios[0].PrecioCentavos, venta.TotalCentavos);
    }

    private async Task<Func<bool>> AlCerrarElTurnoAsync(VentaConCopia v)
    {
        var siguio = false;
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(PantallaVenta.AlCerrarTurno)] = EventCallback.Factory.Create(this, () => { siguio = true; }),
        })));
        return () => siguio;
    }

    /// <summary>
    /// El corte de eleventa cerró el turno justo al cobrar: el cambio no desaparece de la pantalla, pero tampoco la detiene para siempre, porque
    /// casi siempre lo último antes del corte es una venta. Un par de minutos después ya nadie lo está contando.
    /// </summary>
    [Fact]
    public async Task En_el_piloto_el_cambio_a_la_vista_detiene_un_rato_el_paso_al_siguiente_turno()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        await v.Contingencia.RecordarPilotoEleventaAsync(true);
        await VentaConCopia.EsperarAsync(() => v.Contingencia.PilotoEleventa, "el piloto");
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "Enter-cobro");
        Assert.Contains("Cobrado", await Texto(v));
        await CerrarElTurnoAllaAsync(servicio);
        var siguio = await AlCerrarElTurnoAsync(v);

        for (var i = 0; i < 3; i++)
        {
            await v.Pantalla.Dispatcher.InvokeAsync(v.Venta.RevisarElTurnoAsync);
            Assert.False(siguio());
            Assert.Contains("Cobrado", await Texto(v));
        }

        await v.Pantalla.Dispatcher.InvokeAsync(v.Venta.RevisarElTurnoAsync);
        Assert.True(siguio());
    }

    /// <summary>Ya se vio cerrado mientras el cambio seguía a la vista: si empieza otro cliente, se dice de una vez, y no hasta que F12 falle con él enfrente.</summary>
    [Fact]
    public async Task En_el_piloto_el_siguiente_cliente_ve_que_el_turno_ya_se_cerro_sin_esperar_la_vuelta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        await v.Contingencia.RecordarPilotoEleventaAsync(true);
        await VentaConCopia.EsperarAsync(() => v.Contingencia.PilotoEleventa, "el piloto");
        var (a, b) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await Tecla(v, "Enter-cobro");
        await CerrarElTurnoAllaAsync(servicio);
        await v.Pantalla.Dispatcher.InvokeAsync(v.Venta.RevisarElTurnoAsync);
        Assert.DoesNotContain("ya se cerró", await Texto(v));

        await v.EscanearAsync(b.Codigo!);

        Assert.Contains("ya se cerró", await Texto(v));
    }

    [Fact]
    public async Task El_boton_de_cobrar_con_un_nombre_a_medias_no_deja_la_lista_abierta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var pagina = new PaginaQueContesta();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, pagina: pagina);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("cuader"));
        await VentaConCopia.EsperarAsync(() => v.Pantalla.LeerAsync().Result.Elementos.Any(e => e.Atributos.GetValueOrDefault("class") is string c && c.Contains("vn-resultados")), "la lista");
        pagina.EnLaCaptura = "cuader";

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Atributos.GetValueOrDefault("class") is string c && c.Contains("vn-resultados"));
        Assert.Contains("cuader", await Texto(v));
        Assert.Empty(await v.Cliente.VentasDelDiaAsync(v.Caja.Id));
    }

    [Fact]
    public async Task En_el_piloto_lo_que_se_esta_escribiendo_detiene_el_paso_al_siguiente_turno()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        await v.Contingencia.RecordarPilotoEleventaAsync(true);
        await VentaConCopia.EsperarAsync(() => v.Contingencia.PilotoEleventa, "el piloto");
        await CerrarElTurnoAllaAsync(servicio);
        var siguio = await AlCerrarElTurnoAsync(v);

        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("cuader"));
        await v.Pantalla.Dispatcher.InvokeAsync(v.Venta.RevisarElTurnoAsync);
        Assert.False(siguio());
        Assert.DoesNotContain("ya se cerró", await Texto(v));

        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo(""));
        await v.Pantalla.Dispatcher.InvokeAsync(v.Venta.RevisarElTurnoAsync);
        Assert.True(siguio());
    }

    /// <summary>
    /// Un ticket que vuelve con renglones que ya no se pueden armar: lo guardado se escribía encima sin apartarlo, y si no quedaba ningún
    /// renglón, el ticket desaparecía sin decir nada.
    /// </summary>
    [Fact]
    public async Task Un_ticket_que_vuelve_sin_poder_armar_sus_renglones_se_aparta_y_lo_dice()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using (var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal))
        {
            var (a, _) = v.DosDeLaCopia();
            await v.EscanearAsync(a.Codigo!);
        }
        var guardados = System.Text.Json.Nodes.JsonNode.Parse(LeerAjuste(_baseLocal, "espera")!)!.AsArray();
        foreach (var guardado in guardados)
        {
            var ticket = guardado!.AsObject();
            ticket[ticket.Select(k => k.Key).Single(k => k.Equals("Productos", StringComparison.OrdinalIgnoreCase))] = new System.Text.Json.Nodes.JsonArray();
        }
        var sinProductos = guardados.ToJsonString();
        using (var c = new SqliteConnection(ArchivosDePrueba.Cadena(_baseLocal)))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE Ajustes SET Valor = $v WHERE Clave = 'espera'";
            cmd.Parameters.AddWithValue("$v", sinProductos);
            cmd.ExecuteNonQuery();
        }

        await using var otra = await VentaConCopia.AbrirAsync(servicio, _baseLocal);

        Assert.Contains("no se pudo armar", await Texto(otra));
        Assert.Equal(sinProductos, LeerAjuste(_baseLocal, "espera-sin-leer-%"));
    }

    [Fact]
    public async Task Esc_a_medio_corte_en_el_turno_de_otro_no_tira_lo_contado()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        var sesion = await v.Cliente.EntrarAsync("encargado", "2222");
        await using var otra = new PantallaSinNavegador(v.Servicios);
        var volvio = false;
        var pantalla = await otra.AbrirAsync<TurnoDeOtro>(new()
        {
            [nameof(TurnoDeOtro.Caja)] = v.Caja,
            [nameof(TurnoDeOtro.Turno)] = turno,
            [nameof(TurnoDeOtro.Sesion)] = sesion,
            [nameof(TurnoDeOtro.AlVolver)] = EventCallback.Factory.Create(this, () => { volvio = true; }),
        });

        await otra.EsperarAsync(t => t.Contains("Hacer el corte de"), "el botón del corte");
        await otra.OprimirAsync(await otra.BotonAsync("Hacer el corte de"));
        await otra.Dispatcher.InvokeAsync(() => pantalla.Tecla("Escape"));
        Assert.False(volvio);

        await otra.Dispatcher.InvokeAsync(() => pantalla.Tecla("Ctrl+U"));
        Assert.True(volvio);
    }
}
