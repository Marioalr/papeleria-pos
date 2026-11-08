using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Las pantallas que no son la venta, con el teclado (auditoría, 2026-11-04), sin navegador: las teclas de «sin vender», mermas y
/// ajustes de corrido (y lo que avisan), abrir un paquete, la ficha del producto (el cursor, Ctrl+Enter y lo que no se pierde al
/// tocar otro producto) y verificar transferencias con la sesión.
/// </summary>
public sealed class PantallasConTecladoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, PaginaQueContesta Pagina, SesionDto Cajero, SesionDto Dueno);

    private async Task<Caja> PrepararAsync()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var pagina = new PaginaQueContesta();
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(pagina)
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, pagina, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("dueno", "3333"));
    }

    /// <summary>Un producto nuevo por pieza con su código y <paramref name="existencia"/> piezas a $10.</summary>
    private async Task<(Guid Producto, Guid Variante, string Codigo)> ProductoAsync(Caja c, string nombre, int existencia)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var codigo = $"PT{Guid.NewGuid():N}"[..14];
        var ficha = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Dueno.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])],
                    [new PrecioFichaDto(null, 1000, 2000)]),
            ])));
        if (existencia > 0)
        {
            await using var db = servicio.Base();
            var variante = await db.Variantes.SingleAsync(v => v.Id == ficha.Variantes[0].Id);
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(c.Datos.SucursalId, variante, TipoMovimiento.InventarioInicial,
                Cantidad.Piezas(existencia), existencia * 1000L, DateTime.UtcNow, documentoTipo: OrigenDelMovimiento.ImportacionEleventa));
        }
        await servicio.CatalogoAlDiaAsync();
        return (ficha.Id!.Value, ficha.Variantes[0].Id!.Value, codigo);
    }

    private async Task<decimal> ExistenciaAsync(Caja c, string codigo) =>
        (await c.Cliente.PorCodigoAsync(codigo, c.Datos.SucursalId))!.Existencia ?? 0;

    private static string? UltimoFoco(Caja c) =>
        c.Pagina.Llamadas.LastOrDefault(l => l.Funcion == "enfocar").Argumentos?.FirstOrDefault()?.ToString();

    private static async Task FocoEnAsync(Caja c, string selector)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (UltimoFoco(c) != selector)
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"El cursor no fue a {selector}: está en {UltimoFoco(c) ?? "ningún lado"}.");
            await Task.Delay(20);
        }
    }

    private static async Task EscanearAsync(PantallaSinNavegador p, string codigo)
    {
        await p.EsperarElementoAsync("codigo-compra");
        await p.EscribirAsync("codigo-compra", codigo);
        await p.TeclaAsync("codigo-compra", "Enter");
    }

    private static async Task<PantallaSinNavegador> AjustesAsync(Caja c, bool merma)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<AjustesMermas>(new()
        {
            [nameof(AjustesMermas.Caja)] = c.Datos,
            [nameof(AjustesMermas.Sesion)] = c.Dueno,
            [nameof(AjustesMermas.EnMerma)] = merma,
        });
        return p;
    }

    // ── Las teclas de «Productos e inventario, sin vender» ─────────────────

    /// <summary>
    /// caja.js llama a <c>Tecla</c> con la tecla, el texto y la hora; un método con menos parámetros lo rechaza el marco y ninguna
    /// tecla hace nada (sin error a la vista). Se revisa cada pantalla que registra sus teclas con caja.js.
    /// </summary>
    [Fact]
    public void Las_pantallas_con_las_teclas_de_caja_js_aceptan_lo_que_manda()
    {
        var carpeta = new DirectoryInfo(AppContext.BaseDirectory);
        while (carpeta is not null && !Directory.Exists(Path.Combine(carpeta.FullName, "src", "PapeleriaPos.Interfaz", "Componentes")))
            carpeta = carpeta.Parent;
        var componentes = Directory.GetFiles(Path.Combine(carpeta!.FullName, "src", "PapeleriaPos.Interfaz", "Componentes"), "*.razor")
            .Where(a => File.ReadAllText(a).Contains("RegistrarTeclasAsync", StringComparison.Ordinal))
            .Select(a => Path.GetFileNameWithoutExtension(a))
            .ToList();

        Assert.Contains(nameof(Administracion), componentes);
        Assert.Contains(nameof(PantallaVenta), componentes);
        foreach (var componente in componentes)
        {
            var tipo = typeof(Administracion).Assembly.GetType($"{typeof(Administracion).Namespace}.{componente}")!;
            var tecla = tipo.GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(m => m.Name == "Tecla" && m.GetCustomAttribute<JSInvokableAttribute>() is not null);
            Assert.Equal([typeof(string), typeof(string), typeof(double?)], tecla.GetParameters().Select(a => a.ParameterType));
        }
    }

    // ── Mermas y ajustes ───────────────────────────────────────────────────

    [Fact]
    public async Task Una_merma_mayor_a_la_existencia_avisa_y_Enter_no_la_registra_de_un_solo_golpe()
    {
        var c = await PrepararAsync();
        var (_, _, codigo) = await ProductoAsync(c, "Merma de más", 27);
        await using var p = await AjustesAsync(c, merma: true);

        await EscanearAsync(p, codigo);
        await FocoEnAsync(c, "#cantidad-ajuste");

        // Con lo que se puede, dice cómo queda, sin el aviso.
        await p.EscribirAsync("cantidad-ajuste", "10");
        var texto = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.Contains("Quedarían 17 piezas", texto);
        Assert.DoesNotContain("Es más de lo que hay", texto);

        await p.EscribirAsync("cantidad-ajuste", "100");
        Assert.Contains("Es más de lo que hay: quedarían -73 piezas", PantallaSinNavegador.Limpio(await p.TextoAsync()));

        // Un cero de más y Enter: no entra; el cursor pasa al botón, y ahí sí se puede registrar si de verdad es eso.
        await p.TeclaAsync("cantidad-ajuste", "Enter");
        await FocoEnAsync(c, "#guardar-ajuste");
        Assert.Equal(27, await ExistenciaAsync(c, codigo));

        await p.OprimirAsync(await p.PorIdAsync("guardar-ajuste"));
        await p.EsperarAsync(t => t.Contains("Merma registrada"), "la merma");
        Assert.Equal(-73, await ExistenciaAsync(c, codigo));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Una_merma_se_registra_de_corrido_con_el_teclado_y_queda_lista_para_la_siguiente()
    {
        var c = await PrepararAsync();
        var (_, _, codigo) = await ProductoAsync(c, "Merma de corrido", 27);
        await using var p = await AjustesAsync(c, merma: true);

        await EscanearAsync(p, codigo);
        await FocoEnAsync(c, "#cantidad-ajuste");
        await p.EscribirAsync("cantidad-ajuste", "2");
        await p.TeclaAsync("cantidad-ajuste", "Enter");

        await p.EsperarAsync(t => t.Contains("Merma registrada"), "la merma");
        await FocoEnAsync(c, "#codigo-compra");
        Assert.Equal(25, await ExistenciaAsync(c, codigo));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Un_ajuste_pasa_al_motivo_con_Enter_y_ahi_guarda()
    {
        var c = await PrepararAsync();
        var (_, _, codigo) = await ProductoAsync(c, "Ajuste de corrido", 27);
        await using var p = await AjustesAsync(c, merma: false);

        await EscanearAsync(p, codigo);
        await FocoEnAsync(c, "#cantidad-ajuste");
        await p.EscribirAsync("cantidad-ajuste", "20");
        await p.TeclaAsync("cantidad-ajuste", "Enter");

        // Falta el motivo: no es un error, es el siguiente campo.
        await FocoEnAsync(c, "#motivo-ajuste");
        Assert.DoesNotContain("class=\"error\"", await p.TextoAsync());
        Assert.Equal(27, await ExistenciaAsync(c, codigo));

        await p.EscribirAsync("motivo-ajuste", "Lo conté");
        await p.TeclaAsync("motivo-ajuste", "Enter");
        await p.EsperarAsync(t => t.Contains("Existencia ajustada"), "el ajuste");
        Assert.Equal(20, await ExistenciaAsync(c, codigo));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Un_codigo_escaneado_en_la_cantidad_de_una_merma_no_es_un_numero_es_el_siguiente_producto()
    {
        var c = await PrepararAsync();
        var (_, _, primero) = await ProductoAsync(c, "Merma con ráfaga", 27);
        var (_, _, siguiente) = await ProductoAsync(c, "Merma la siguiente", 9);
        await using var p = await AjustesAsync(c, merma: true);
        await EscanearAsync(p, primero);
        await FocoEnAsync(c, "#cantidad-ajuste");
        Assert.True((await p.PorIdAsync("cantidad-ajuste")).Atributos.ContainsKey("data-rafaga"));
        var pagina = (DotNetObjectReference<AjustesMermas>)c.Pagina.Llamadas.Last(l => l.Funcion == "rafagasDe").Argumentos![0]!;

        await p.Dispatcher.InvokeAsync(() => pagina.Value.RafagaEnCampo("NO-EXISTE-77", "cantidad-ajuste", "3"));
        var texto = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.Contains("Merma con ráfaga", texto);
        Assert.Contains("NO-EXISTE-77", texto);
        Assert.Equal("3", (await p.PorIdAsync("cantidad-ajuste")).Valor);

        await p.Dispatcher.InvokeAsync(() => pagina.Value.RafagaEnCampo(siguiente, "cantidad-ajuste", "3"));
        texto = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.Contains("Merma la siguiente", texto);
        Assert.Equal("", (await p.PorIdAsync("cantidad-ajuste")).Valor);
        Assert.Equal(27, await ExistenciaAsync(c, primero));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Abrir_un_paquete_de_corrido_con_el_teclado()
    {
        var c = await PrepararAsync();
        var (_, _, paquete) = await ProductoAsync(c, "Paquete de corrido", 5);
        var (_, _, pieza) = await ProductoAsync(c, "Pieza de corrido", 0);
        await using var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<AbrirPaquete>(new() { [nameof(AbrirPaquete.Caja)] = c.Datos, [nameof(AbrirPaquete.Sesion)] = c.Dueno });

        await EscanearAsync(p, paquete);
        await p.EsperarAsync(t => t.Contains("¿Qué producto trae adentro"), "la pieza");
        await EscanearAsync(p, pieza);
        await FocoEnAsync(c, "#cantidad-paquetes");

        // Enter en «¿Cuántos se abren?» sin las piezas de cada uno: ahí pasa, sin error.
        await p.TeclaAsync("cantidad-paquetes", "Enter");
        await FocoEnAsync(c, "#piezas-paquete");
        Assert.DoesNotContain("Escribe cuántos paquetes", await p.TextoAsync());

        await p.EscribirAsync("piezas-paquete", "12");
        await p.TeclaAsync("piezas-paquete", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: salió"), "abrir el paquete");
        Assert.Equal(12, await ExistenciaAsync(c, pieza));
        Assert.Empty(p.Errores);
    }

    // ── La ficha del producto ──────────────────────────────────────────────

    private static async Task<PantallaSinNavegador> ProductosAsync(Caja c, string buscar)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = c.Dueno,
            [nameof(Productos.Acceso)] = new PermisosDePagina(c.Dueno, null),
            [nameof(Productos.Estado)] = new ProductosVistos { Texto = buscar },
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarElementoAsync("prod-1");
        await p.EsperarAsync(t => t.Contains("Editar"), "el detalle");
        return p;
    }

    private static async Task EditarAsync(PantallaSinNavegador p)
    {
        await p.OprimirAsync(await p.BotonAsync("Editar"));
        await p.EsperarElementoAsync("ficha-nombre");
    }

    private static async Task CambiarNombreAsync(PantallaSinNavegador p, string nombre) =>
        await p.DispararAsync(await p.PorIdAsync("ficha-nombre"), "onchange", new ChangeEventArgs { Value = nombre });

    [Fact]
    public async Task La_ficha_abre_con_el_cursor_en_su_primer_campo_y_Ctrl_Enter_guarda()
    {
        var c = await PrepararAsync();
        var (uno, _, _) = await ProductoAsync(c, "Ficha teclado uno", 1);
        var (dos, _, _) = await ProductoAsync(c, "Ficha teclado dos", 1);
        await using var p = await ProductosAsync(c, "Ficha teclado");
        await EditarAsync(p);
        await FocoEnAsync(c, FichaProducto.PrimerCampo);
        var titulo = (await p.ElementoAsync(e => e.Etiqueta == "h2", "el título")).Texto;
        var producto = titulo.Contains("uno") ? uno : dos;

        await CambiarNombreAsync(p, "Ficha teclado guardada");
        await p.DispararAsync(await p.PorIdAsync("ficha-campos"), "onkeydown", new KeyboardEventArgs { Key = "Enter", CtrlKey = true });
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "lo guardado");

        Assert.Equal("Ficha teclado guardada", (await c.Cliente.FichaProductoAsync(producto, c.Datos.SucursalId, c.Dueno.UsuarioId))!.Nombre);
        // Antes de guardar, el cursor salió del campo: lo escrito (que se toma al salir) va en lo que se guarda.
        Assert.Contains(c.Pagina.Llamadas, l => l.Funcion == "enfocar" && Equals(l.Argumentos?[0], "#ficha-guardar"));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Tocar_otro_producto_con_la_ficha_cambiada_no_tira_lo_escrito()
    {
        var c = await PrepararAsync();
        await ProductoAsync(c, "Ficha sin perder uno", 1);
        await ProductoAsync(c, "Ficha sin perder dos", 1);
        await using var p = await ProductosAsync(c, "Ficha sin perder");
        await EditarAsync(p);
        await CambiarNombreAsync(p, "Lo que se escribía");

        // El otro renglón: no cambia de producto, y lo dice arriba de la ficha.
        await p.OprimirAsync(await p.PorIdAsync("prod-1"));
        await p.EsperarAsync(t => t.Contains("Tienes cambios sin guardar"), "el aviso");
        Assert.Equal("Lo que se escribía", (await p.PorIdAsync("ficha-nombre")).Valor);
        await FocoEnAsync(c, "#ficha-guardar-cambios");

        // ↓ en la búsqueda tampoco.
        await p.TeclaAsync("busca-prod", "ArrowDown");
        Assert.Equal("Lo que se escribía", (await p.PorIdAsync("ficha-nombre")).Valor);

        // «Descartar» sigue a lo que se tocó.
        await p.OprimirAsync(await p.PorIdAsync("ficha-descartar"));
        await p.EsperarAsync(t => !t.Contains("Tienes cambios sin guardar") && !t.Contains("regresa sin guardar"), "el detalle");
        Assert.DoesNotContain("Lo que se escribía", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Guardar_desde_el_aviso_guarda_y_sigue_a_lo_que_se_toco()
    {
        var c = await PrepararAsync();
        var (uno, _, _) = await ProductoAsync(c, "Ficha aviso uno", 1);
        var (dos, _, _) = await ProductoAsync(c, "Ficha aviso dos", 1);
        await using var p = await ProductosAsync(c, "Ficha aviso");
        await EditarAsync(p);
        var titulo = (await p.ElementoAsync(e => e.Etiqueta == "h2", "el título")).Texto;
        var editado = titulo.Contains("uno") ? uno : dos;
        await CambiarNombreAsync(p, "Ficha aviso guardada");

        await p.OprimirAsync(await p.PorIdAsync("prod-1"));
        await p.EsperarElementoAsync("ficha-guardar-cambios");
        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar-cambios"));
        await p.EsperarAsync(t => !t.Contains("Tienes cambios sin guardar") && !t.Contains("regresa sin guardar"), "el detalle");

        Assert.Equal("Ficha aviso guardada", (await c.Cliente.FichaProductoAsync(editado, c.Datos.SucursalId, c.Dueno.UsuarioId))!.Nombre);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Sin_cambios_tocar_otro_producto_cambia_como_siempre()
    {
        var c = await PrepararAsync();
        await ProductoAsync(c, "Ficha igual uno", 1);
        await ProductoAsync(c, "Ficha igual dos", 1);
        await using var p = await ProductosAsync(c, "Ficha igual");
        await EditarAsync(p);
        await p.OprimirAsync(await p.PorIdAsync("prod-1"));
        await p.EsperarAsync(t => !t.Contains("regresa sin guardar"), "el detalle del otro");
        Assert.DoesNotContain("Tienes cambios sin guardar", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    // ── Verificar transferencias ───────────────────────────────────────────

    [Fact]
    public async Task Verificar_transferencias_usa_la_sesion_sin_pedir_usuario_ni_PIN()
    {
        var c = await PrepararAsync();
        var http = servicio.Fabrica.CreateClient();
        var turno = await c.Cliente.TurnoAsync(c.Datos.Id) ?? await c.Cliente.AbrirTurnoAsync(c.Datos.Id, c.Dueno.UsuarioId, 50000);
        var referencia = $"REF-{Guid.NewGuid():N}"[..14];
        var cobro = await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, turno.Id, turno.CajeroId,
            [new LineaSolicitada(null, null, 1000, "Recuerdo", 50000)], [new CobroSolicitado(FormaPago.Transferencia, 50000, referencia)]));
        Assert.True(cobro.IsSuccessStatusCode, await cobro.Content.ReadAsStringAsync());

        // Quien no tiene el permiso no pasa, aunque lleve la sesión.
        var transferencia = (await c.Cliente.TransferenciasAsync(c.Datos.SucursalId)).Single(t => t.Referencia == referencia);
        var error = await Assert.ThrowsAsync<ErrorCaja>(() => c.Cliente.VerificarTransferenciaAsync(transferencia.CobroId, new SolicitudVerificacion(c.Cajero.UsuarioId, true)));
        Assert.Contains("Verificar", error.Message, StringComparison.OrdinalIgnoreCase);

        await using var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<VerificarTransferencias>(new() { [nameof(VerificarTransferencias.Caja)] = c.Datos, [nameof(VerificarTransferencias.Sesion)] = c.Dueno });
        await p.EsperarAsync(t => t.Contains(referencia), "la transferencia");
        var (texto, elementos) = await p.LeerAsync();
        Assert.DoesNotContain("Usuario de quien", texto);
        Assert.DoesNotContain(elementos, e => e.Etiqueta == "input");
        Assert.Contains("Entraste como", texto);

        var llego = await p.ElementoAsync(e => e.Id == $"llego-{transferencia.CobroId}", "«Llegó»");
        await p.OprimirAsync(llego);
        await p.EsperarAsync(t => !t.Contains(transferencia.Folio + "Por verificar") && (t.Contains("Llegó")), "ya verificada");

        await using var db = servicio.Base();
        var guardado = await db.Cobros.SingleAsync(x => x.Id == transferencia.CobroId);
        Assert.Equal(Verificacion.Verificada, guardado.Verificacion);
        Assert.Equal(c.Dueno.UsuarioId, guardado.VerificadoPorId);
        Assert.Empty(p.Errores);
    }

    // ── Enter pasa al campo que falta ──────────────────────────────────────

    [Fact]
    public async Task Enter_en_articulo_comun_pasa_al_precio_sin_error_y_con_todo_agrega()
    {
        var c = await PrepararAsync();
        await using var p = new PantallaSinNavegador(c.Servicios);
        var agregado = false;
        await p.AbrirAsync<ArticuloComun>(new()
        {
            [nameof(ArticuloComun.AlAgregar)] = EventCallback.Factory.Create<(string, long, Cantidad)>(this, _ => agregado = true),
        });
        await p.EscribirAsync("comun-descripcion", "Recuerdo");
        await p.TeclaAsync("comun-descripcion", "Enter");
        await FocoEnAsync(c, "#comun-precio");
        Assert.DoesNotContain("Escribe", await p.TextoAsync());

        await p.EscribirAsync("comun-precio", "20");
        await p.TeclaAsync("comun-precio", "Enter");
        Assert.True(agregado);
    }

    [Fact]
    public async Task Enter_en_la_salida_de_efectivo_pasa_al_motivo_y_el_boton_dice_solo_lo_que_falta()
    {
        var c = await PrepararAsync();
        await using var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<MovimientoEfectivo>(new()
        {
            [nameof(MovimientoEfectivo.Tipo)] = TipoMovimientoCaja.Salida,
            [nameof(MovimientoEfectivo.TurnoId)] = Guid.NewGuid(),
            [nameof(MovimientoEfectivo.Caja)] = c.Datos,
            [nameof(MovimientoEfectivo.Sesion)] = c.Dueno,
        });
        await p.EscribirAsync("movimiento-monto", "50");
        await p.TeclaAsync("movimiento-monto", "Enter");
        await FocoEnAsync(c, "#movimiento-motivo");
        Assert.DoesNotContain("Escribe", await p.TextoAsync());

        await p.OprimirAsync(await p.BotonAsync("Registrar"));
        Assert.Contains("Escribe el motivo.", await p.TextoAsync());
    }

    [Fact]
    public async Task Enter_en_el_descuento_pasa_al_motivo_sin_error()
    {
        var c = await PrepararAsync();
        await using var p = new PantallaSinNavegador(c.Servicios);
        var venta = new Venta { SucursalId = c.Datos.SucursalId, CajaId = c.Datos.Id, TurnoId = Guid.NewGuid(), CajeroId = c.Dueno.UsuarioId, Serie = "A" };
        venta.AgregarArticuloComun("Recuerdo", 5000, Cantidad.Piezas(1));
        await p.AbrirAsync<Descuento>(new()
        {
            [nameof(Descuento.Venta)] = venta,
            [nameof(Descuento.Caja)] = c.Datos,
            [nameof(Descuento.Sesion)] = c.Dueno,
        });
        await p.EscribirAsync("descuento-valor", "5");
        await p.TeclaAsync("descuento-valor", "Enter");
        await FocoEnAsync(c, "#descuento-motivo");
        Assert.DoesNotContain("Escribe", await p.TextoAsync());
    }

    [Fact]
    public async Task Enter_en_la_alta_rapida_pasa_al_precio_sin_error()
    {
        var c = await PrepararAsync();
        await using var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<AltaRapida>(new()
        {
            [nameof(AltaRapida.Codigo)] = "750000000",
            [nameof(AltaRapida.Caja)] = c.Datos,
            [nameof(AltaRapida.Sesion)] = c.Dueno,
        });
        await p.EsperarElementoAsync("alta-nombre");
        await p.EscribirAsync("alta-nombre", "Lo nuevo");
        await p.TeclaAsync("alta-nombre", "Enter");
        await FocoEnAsync(c, "#alta-precio");
        Assert.DoesNotContain("Escribe", await p.TextoAsync());
    }

    [Fact]
    public async Task Enter_con_el_verificador_vacio_agrega_lo_que_se_ve()
    {
        var c = await PrepararAsync();
        var (_, _, codigo) = await ProductoAsync(c, "Verificado con Enter", 3);
        await using var p = new PantallaSinNavegador(c.Servicios);
        ProductoVentaDto? agregado = null;
        await p.AbrirAsync<Verificador>(new()
        {
            [nameof(Verificador.SucursalId)] = c.Datos.SucursalId,
            [nameof(Verificador.AlAgregar)] = EventCallback.Factory.Create<ProductoVentaDto>(this, x => agregado = x),
        });
        // Sin nada a la vista, Enter no hace nada.
        await p.TeclaAsync("verificador", "Enter");
        Assert.Null(agregado);

        await p.EscribirAsync("verificador", codigo);
        await p.TeclaAsync("verificador", "Enter");
        await p.EsperarAsync(t => t.Contains("Verificado con Enter"), "el producto");
        Assert.Null(agregado);

        await p.TeclaAsync("verificador", "Enter");
        Assert.Equal("Verificado con Enter", agregado?.Descripcion);
        Assert.Contains("Enter", (await p.BotonAsync("Agregarlo al ticket")).Texto);
    }

    // ── Lo demás de teclado ────────────────────────────────────────────────

    [Fact]
    public async Task Esc_en_abrir_turno_regresa_como_Volver()
    {
        var c = await PrepararAsync();
        await using var p = new PantallaSinNavegador(c.Servicios);
        var salio = false;
        var abrir = await p.AbrirAsync<AbrirTurno>(new()
        {
            [nameof(AbrirTurno.Caja)] = c.Datos,
            [nameof(AbrirTurno.Sesion)] = c.Dueno,
            [nameof(AbrirTurno.AlSalir)] = EventCallback.Factory.Create(this, () => salio = true),
        });
        await p.Dispatcher.InvokeAsync(() => abrir.Tecla("Escape"));
        Assert.True(salio);
    }

    /// <summary>
    /// Revisión de la rama junta (2026-11-06): en el piloto la pantalla pasa sola a «Abrir turno» cuando eleventa corta, y quien escanea al
    /// siguiente cliente sin mirar abría el turno con un fondo de billones. Ninguna caja tiene un fondo de 8 cifras sin centavos.
    /// </summary>
    [Fact]
    public async Task Un_codigo_escaneado_en_el_fondo_no_abre_el_turno()
    {
        var c = await PrepararAsync();
        await using var p = new PantallaSinNavegador(c.Servicios);
        var abrio = false;
        await p.AbrirAsync<AbrirTurno>(new()
        {
            [nameof(AbrirTurno.Caja)] = c.Datos,
            [nameof(AbrirTurno.Sesion)] = c.Dueno,
            [nameof(AbrirTurno.AlAbrir)] = EventCallback.Factory.Create<TurnoDto>(this, _ => abrio = true),
        });

        await p.EscribirAsync("fondo", "7501234567890");
        await p.TeclaAsync("fondo", "Enter");

        Assert.False(abrio);
        Assert.Contains("código", await p.TextoAsync());
        Assert.True(string.IsNullOrEmpty((await p.PorIdAsync("fondo")).Valor as string));
    }

    /// <summary>Un archivo de wwwroot, como se publica.</summary>
    private static string DeWwwroot(string archivo)
    {
        var carpeta = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(carpeta!.FullName, "src", "PapeleriaPos.Interfaz", "wwwroot", archivo)))
            carpeta = carpeta.Parent;
        return File.ReadAllText(Path.Combine(carpeta.FullName, "src", "PapeleriaPos.Interfaz", "wwwroot", archivo));
    }

    [Fact]
    public void Entrada_js_manda_Esc_fuera_de_un_campo_en_abrir_turno() =>
        Assert.Contains("tecla === 'Escape' && !escribe(e.target) && !hayVentana()", DeWwwroot("entrada.js"));

    /// <summary>
    /// El aviso del corte del piloto va en «¿Quién atiende?», sin «Entendido»: la letra de la ficha sirve desde el primer momento y el
    /// aviso se queda a la vista.
    /// </summary>
    [Fact]
    public async Task El_aviso_del_corte_no_estorba_a_la_letra_de_la_ficha()
    {
        var c = await PrepararAsync();
        await using var p = new PantallaSinNavegador(c.Servicios);
        var entrada = await p.AbrirAsync<Entrada>(new()
        {
            [nameof(Entrada.Caja)] = c.Datos,
            [nameof(Entrada.Turno)] = null,
            [nameof(Entrada.Aviso)] = PilotoConEleventa.CorteEnEleventa,
        });
        static bool EsLaFicha(ElementoDibujado e) => e.Etiqueta == "button" && e.Clase?.Contains("ficha-entrada") == true && e.Texto.Contains("Cajero");
        await p.ElementoAsync(EsLaFicha, "la ficha");
        var (texto, elementos) = await p.LeerAsync();
        Assert.Contains(PilotoConEleventa.CorteEnEleventa, texto);
        var letra = elementos.Skip(elementos.FindIndex(EsLaFicha)).First(e => e.Etiqueta == "kbd").Texto.Trim();

        await p.Dispatcher.InvokeAsync(() => entrada.Tecla(letra));
        await p.EsperarAsync(t => t.Contains("Entrar"), "el PIN de la ficha");
        Assert.Contains(PilotoConEleventa.CorteEnEleventa, await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    /// <summary>Lo de caja.css que no se ve sin navegador (auditoría, 2026-11-04): reglas que antes se medían a mano en la pantalla.</summary>
    [Fact]
    public void La_hoja_de_estilos_no_tapa_ni_recorta()
    {
        var css = DeWwwroot("caja.css");
        string Regla(string selector) => css.Split('\n').First(l => l.StartsWith(selector + " {", StringComparison.Ordinal));

        // Con 5 o 6 rápidos, o 5 o más fichas en espera, bajan de renglón en lugar de meterse bajo el total.
        Assert.Contains("flex-wrap: wrap", Regla(".vn-rapidos"));
        Assert.Contains("flex-wrap: wrap", Regla(".vn-espera"));
        Assert.Contains("min-width: 0", Regla(".vn-rapidos"));
        // Lo que se está por cobrar: «Pagó con» y sus formas no se salen de la tarjeta.
        Assert.Contains("flex-wrap: wrap", Regla(".vn-pago-cab"));
        // La lista de búsqueda (20) y los menús (30) quedan abajo de una ventana.
        Assert.Contains("z-index: 40", Regla(".fondo-ventana"));
        // Los paneles entran de lado: sin barra de un instante.
        Assert.Contains("overflow-x: hidden", Regla(".vn-der"));
        // Lo elegido no queda bajo las barras fijas.
        Assert.Contains("scroll-margin-bottom", Regla(".vn-res"));
        Assert.Contains("scroll-margin-top", Regla(".vn-ticket tbody tr"));
        // El gris que informa ya pasa AA en sus cuatro copias, y el teclado del PIN no se recorre con el mensaje.
        Assert.DoesNotContain("#8693a3", css);
        Assert.Contains("width: 260px", Regla(".pin-izq"));
        // El foco se ve aunque el contenedor recorte, y en los botones de elegir archivo.
        Assert.Contains("outline-offset: -2px", Regla(".vn-corte-segmento button:focus-visible"));
        Assert.Contains(".vn-cfg .vn-archivo:focus-within", css);
    }

    [Fact]
    public async Task Proveedores_se_edita_con_el_teclado()
    {
        var c = await PrepararAsync();
        var nombre = $"Proveedor {Guid.NewGuid():N}"[..20];
        await using (var db = servicio.Base())
        {
            db.Proveedores.Add(new Proveedor { Id = Ids.Nuevo(), Nombre = nombre });
            await db.SaveChangesAsync();
        }
        await using var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Proveedores>(new() { [nameof(Proveedores.Caja)] = c.Datos, [nameof(Proveedores.Sesion)] = c.Dueno });
        await p.EsperarElementoAsync("prov-0");
        await FocoEnAsync(c, "#prov-0");

        var renglon = await p.ElementoAsync(e => e.Etiqueta == "tr" && e.Texto.Contains(nombre), "su renglón");
        Assert.Equal("0", renglon.Atributos["tabindex"]?.ToString());
        await p.DispararAsync(renglon, "onkeydown", new KeyboardEventArgs { Key = "Enter" });
        Assert.Contains((await p.LeerAsync()).Elementos, e => e.Etiqueta == "input" && e.Valor == nombre);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Nuevo_producto_pide_el_nombre_y_el_departamento_y_al_guardar_cambia_su_titulo()
    {
        var c = await PrepararAsync();
        await ProductoAsync(c, "Para abrir nuevo uno", 1);
        await ProductoAsync(c, "Para abrir nuevo dos", 1);
        await using var p = await ProductosAsync(c, "Para abrir nuevo");
        await p.OprimirAsync(await p.BotonAsync("Nuevo producto"));
        await p.EsperarElementoAsync("ficha-nombre");
        await FocoEnAsync(c, FichaProducto.PrimerCampo);

        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        Assert.Contains("Escribe el nombre del producto.", await p.TextoAsync());

        var nombre = $"Nuevo con título {Guid.NewGuid():N}"[..24];
        await CambiarNombreAsync(p, nombre);
        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        Assert.Contains("Elige el departamento.", await p.TextoAsync());

        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        await p.DispararAsync(await p.PorIdAsync("ficha-departamento"), "onchange", new ChangeEventArgs { Value = opciones.Departamentos[0].Id.ToString() });
        var precio = (await p.LeerAsync()).Elementos.Last(e => e.Etiqueta == "input" && e.Clase == "umbral" && e.Valor.Length == 0);
        await p.DispararAsync(precio, "onchange", new ChangeEventArgs { Value = "12.50" });
        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas ya lo venden así."), "lo guardado");
        Assert.Contains($"Editar «{nombre}»", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    /// <summary>Lo que un producto nuevo necesita para guardarse: nombre, departamento y precio.</summary>
    private static async Task LlenarNuevoAsync(Caja c, PantallaSinNavegador p, string nombre)
    {
        await CambiarNombreAsync(p, nombre);
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        await p.DispararAsync(await p.PorIdAsync("ficha-departamento"), "onchange", new ChangeEventArgs { Value = opciones.Departamentos[0].Id.ToString() });
        var precio = (await p.LeerAsync()).Elementos.Last(e => e.Etiqueta == "input" && e.Clase == "umbral" && string.IsNullOrEmpty(e.Valor));
        await p.DispararAsync(precio, "onchange", new ChangeEventArgs { Value = "12.50" });
    }

    private async Task<int> ProductosConNombreAsync(string nombre)
    {
        await using var db = servicio.Base();
        return await db.Productos.CountAsync(x => x.Nombre == nombre);
    }

    /// <summary>Antes la segunda seguía con el producto guardado bajo el título «Nuevo producto»: escribir encima lo renombraba.</summary>
    [Fact]
    public async Task Cada_Nuevo_producto_abre_la_ficha_vacia_aunque_el_anterior_ya_se_haya_guardado()
    {
        var c = await PrepararAsync();
        await ProductoAsync(c, "Ficha seguidos uno", 1);
        await ProductoAsync(c, "Ficha seguidos dos", 1);
        await using var p = await ProductosAsync(c, "Ficha seguidos");
        var primero = $"Primero{Guid.NewGuid():N}"[..20];
        var segundo = $"Segundo{Guid.NewGuid():N}"[..20];

        await p.OprimirAsync(await p.BotonAsync("Nuevo producto"));
        await p.EsperarElementoAsync("ficha-nombre");
        await LlenarNuevoAsync(c, p, primero);
        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas"), "lo guardado");

        await p.OprimirAsync(await p.BotonAsync("Nuevo producto"));
        await p.EsperarAsync(t => !t.Contains("Guardado: las cajas"), "la ficha vacía");
        Assert.Equal("", (await p.PorIdAsync("ficha-nombre")).Valor);
        Assert.Contains("Nuevo producto", (await p.ElementoAsync(e => e.Etiqueta == "h2", "el título")).Texto);

        await LlenarNuevoAsync(c, p, segundo);
        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        await p.EsperarAsync(t => t.Contains("Guardado: las cajas"), "el segundo guardado");
        Assert.Equal(1, await ProductosConNombreAsync(primero));
        Assert.Equal(1, await ProductosConNombreAsync(segundo));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Descartar_con_Nuevo_producto_vacia_la_ficha_y_la_sigue_cuidando()
    {
        var c = await PrepararAsync();
        await ProductoAsync(c, "Ficha descarta uno", 1);
        await ProductoAsync(c, "Ficha descarta dos", 1);
        await using var p = await ProductosAsync(c, "Ficha descarta");
        await p.OprimirAsync(await p.BotonAsync("Nuevo producto"));
        await p.EsperarElementoAsync("ficha-nombre");
        await CambiarNombreAsync(p, "Lo que sobra");

        await p.OprimirAsync(await p.BotonAsync("Nuevo producto"));
        await p.EsperarElementoAsync("ficha-descartar");
        await p.OprimirAsync(await p.PorIdAsync("ficha-descartar"));
        await p.EsperarAsync(t => !t.Contains("Tienes cambios sin guardar"), "sin el aviso");
        Assert.Equal("", (await p.PorIdAsync("ficha-nombre")).Valor);

        // Lo que se escriba en la ficha nueva también se cuida.
        await CambiarNombreAsync(p, "Lo que se escribe después");
        await p.OprimirAsync(await p.PorIdAsync("prod-0"));
        await p.EsperarAsync(t => t.Contains("Tienes cambios sin guardar"), "el aviso");
        Assert.Equal("Lo que se escribe después", (await p.PorIdAsync("ficha-nombre")).Valor);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Dos_clics_en_el_Guardar_del_aviso_guardan_una_vez()
    {
        var c = await PrepararAsync();
        await ProductoAsync(c, "Ficha doble uno", 1);
        await ProductoAsync(c, "Ficha doble dos", 1);
        await using var p = await ProductosAsync(c, "Ficha doble");
        await p.OprimirAsync(await p.BotonAsync("Nuevo producto"));
        await p.EsperarElementoAsync("ficha-nombre");
        var nombre = $"Doble{Guid.NewGuid():N}"[..20];
        await LlenarNuevoAsync(c, p, nombre);
        await p.OprimirAsync(await p.PorIdAsync("prod-0"));
        await p.EsperarElementoAsync("ficha-guardar-cambios");

        var guardar = await p.PorIdAsync("ficha-guardar-cambios");
        await Task.WhenAll(p.OprimirAsync(guardar), p.OprimirAsync(guardar));
        await p.EsperarAsync(t => !t.Contains("Tienes cambios sin guardar") && !t.Contains("regresa sin guardar"), "el detalle");

        Assert.Equal(1, await ProductosConNombreAsync(nombre));
        Assert.Empty(p.Errores);
    }

    /// <summary>Lo que se escribía tampoco se pierde con F4, las pestañas o Ctrl+U: sale el mismo aviso y al contestarlo se hace lo que se pidió.</summary>
    [Fact]
    public async Task Sin_vender_dejar_Productos_con_la_ficha_cambiada_avisa_y_luego_sigue()
    {
        var c = await PrepararAsync();
        await using var p = new PantallaSinNavegador(c.Servicios);
        var salidas = 0;
        var administracion = await p.AbrirAsync<Administracion>(new()
        {
            [nameof(Administracion.Caja)] = c.Datos,
            [nameof(Administracion.Sesion)] = c.Dueno,
            [nameof(Administracion.AlSalir)] = new EventCallback(null, (Action)(() => salidas++)),
        });
        await p.Dispatcher.InvokeAsync(() => administracion.Tecla("F3", null));
        await p.EsperarElementoAsync("prod-0");
        await p.OprimirAsync(await p.BotonAsync("Nuevo producto"));
        await p.EsperarElementoAsync("ficha-nombre");
        var nombre = $"Cuidada{Guid.NewGuid():N}"[..20];
        await LlenarNuevoAsync(c, p, nombre);

        // F4: se queda, con lo escrito y el cursor en «Guardar».
        await p.Dispatcher.InvokeAsync(() => administracion.Tecla("F4", null));
        await p.EsperarAsync(t => t.Contains("Tienes cambios sin guardar"), "el aviso de F4");
        Assert.Equal(nombre, (await p.PorIdAsync("ficha-nombre")).Valor);
        await FocoEnAsync(c, "#ficha-guardar-cambios");

        // Esc sigue en la ficha.
        await p.Dispatcher.InvokeAsync(() => administracion.Tecla("Escape", null));
        await p.EsperarAsync(t => !t.Contains("Tienes cambios sin guardar"), "sin el aviso");
        Assert.Equal(nombre, (await p.PorIdAsync("ficha-nombre")).Valor);

        // Ctrl+U: no sale hasta contestar; «Guardar» guarda y sale.
        await p.Dispatcher.InvokeAsync(() => administracion.Tecla("Ctrl+U", null));
        await p.EsperarAsync(t => t.Contains("Tienes cambios sin guardar"), "el aviso de Ctrl+U");
        Assert.Equal(0, salidas);
        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar-cambios"));
        await p.EsperarAsync(_ => salidas == 1, "la salida");
        Assert.Equal(1, await ProductosConNombreAsync(nombre));

        // Lo mismo con la pestaña: «Descartar» sigue a Inventario y lo escrito se tira.
        await p.OprimirAsync(await p.BotonAsync("Nuevo producto"));
        await p.EsperarAsync(t => !t.Contains("Guardado: las cajas"), "la ficha vacía");
        await CambiarNombreAsync(p, "Se va a tirar");
        await p.OprimirAsync(await p.BotonAsync("Inventario"));
        await p.EsperarElementoAsync("ficha-descartar");
        Assert.Equal("Se va a tirar", (await p.PorIdAsync("ficha-nombre")).Valor);
        await p.OprimirAsync(await p.PorIdAsync("ficha-descartar"));
        await p.EsperarAsync(t => t.Contains("Solo las tareas que tienes"), "Inventario");
        Assert.Equal(0, await ProductosConNombreAsync("Se va a tirar"));

        // Sin nada escrito no estorba: ni en Inventario, que ya no tiene ficha (F3 regresa sin preguntar).
        await p.Dispatcher.InvokeAsync(() => administracion.Tecla("F3", null));
        await p.EsperarElementoAsync("prod-0");
        await p.Dispatcher.InvokeAsync(() => administracion.Tecla("Ctrl+U", null));
        Assert.Equal(2, salidas);
        Assert.Empty(p.Errores);
    }

    /// <summary>Lo que el panel «Verificar transferencias» pide a la hoja: el monto, lo que se compara contra el banco, resalta; los botones, abajo.</summary>
    [Fact]
    public void Verificar_transferencias_tiene_el_monto_a_la_derecha_y_los_botones_abajo()
    {
        var css = DeWwwroot("caja.css");
        string Regla(string selector) => css.Split('\n').First(l => l.StartsWith(selector + " {", StringComparison.Ordinal));

        var monto = Regla(".vn-panel.tarjetas .resultados .cifra-derecha");
        Assert.Contains("text-align: right", monto);
        Assert.Contains("font-weight: 700", monto);
        Assert.Contains("grid-column: 1 / -1", Regla(".vn-panel.tarjetas .resultados .botones-abajo"));
    }
}
