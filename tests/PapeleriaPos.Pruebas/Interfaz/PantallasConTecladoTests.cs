using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
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
}
