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
        Assert.Contains("Cantidad inválida", await Texto(v));

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
