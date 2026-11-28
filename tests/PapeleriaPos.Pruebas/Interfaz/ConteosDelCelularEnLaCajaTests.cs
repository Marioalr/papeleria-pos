using System.Net.Http.Json;
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
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La caja que recibe un conteo del celular (docs/celular-como-caja.md, «Mandar un conteo a una caja»), sin navegador y solo con el
/// teclado: Inventario › Conteos del celular abre la lista con Enter, se corrige una cantidad escribiéndola (Enter pasa al siguiente),
/// Ctrl+Supr quita un producto y Ctrl+Enter la aplica; «Rechazar la lista» pide el porqué y Enter la rechaza. La venta abierta lo avisa
/// y su chip lleva ahí. Solo para quien tiene «Conteo completo de un departamento». En el piloto con eleventa (2026-11-27) la tarea
/// también se ofrece: Ctrl+Enter la acepta sin mover existencias y queda «por capturar en eleventa», donde se imprime, se saca a Excel
/// y se cierra con «Ya lo capturé en eleventa», todo con el teclado. Con «Mandar los conteos a eleventa» prendido, la lista aceptada
/// queda en «Mandados a eleventa» y dice cómo va cada producto.
/// </summary>
public sealed class ConteosDelCelularEnLaCajaTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-conteos-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>La caja principal con un turno del encargado, su página que contesta, y el celular dado de alta.</summary>
    private sealed record Caja(ServicioDePrueba Servicio, ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, PaginaQueContesta Pagina,
        SesionDto Cajero, SesionDto Encargado, HttpClient Celular, Guid CelularId, GuardadoDePrueba Guardado) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Servicios.DisposeAsync();
            await Servicio.DisposeAsync();
        }
    }

    /// <param name="piloto">Con ella, el piloto con eleventa: su lectura prendida y esta de mentiras en lugar de eleventa.</param>
    /// <param name="escribe">Además, el puente escribiendo (<c>Eleventa:Escribir</c>), sin eleventa que conteste: lo que entra a la bandeja espera.</param>
    private static async Task<Caja> PrepararAsync(LecturaDePrueba? piloto = null, bool escribe = false)
    {
        (string, string)[] ajustes = escribe ? [("Eleventa:Activo", "true"), ("Eleventa:Escribir", "true")] : [("Eleventa:Activo", "true")];
        var servicio = piloto is null
            ? await ServicioDePrueba.CrearAsync()
            : await ServicioDePrueba.CrearAsync(s => s.AddSingleton<PapeleriaPos.Servicio.ILecturaFresca>(piloto), ajustes);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var pagina = new PaginaQueContesta();
        var guardado = new GuardadoDePrueba();
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IGuardarArchivo>(guardado)
            .AddSingleton<IJSRuntime>(pagina)
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        var encargado = await cliente.EntrarAsync("encargado", "2222");
        await cliente.AbrirTurnoAsync(caja.Id, encargado.UsuarioId, 0);
        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        return new Caja(servicio, cliente, caja, servicios, pagina, await cliente.EntrarAsync("cajero", "1111"), encargado, celular, alta.Caja.Id, guardado);
    }

    private sealed record Producto(Guid VarianteId, Guid Pieza, Guid? CajaDe12, string Nombre);

    /// <summary>Un producto por pieza (con su caja de 12, si se pide) con <paramref name="existencia"/> piezas de hace una hora.</summary>
    private static async Task<Producto> ProductoAsync(Caja c, string nombre, int existencia, bool conCaja = false)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        List<PresentacionFichaDto> presentaciones = [new(null, "Pieza", 1000, true, [new CodigoFichaDto($"CO{Guid.NewGuid():N}"[..14])])];
        if (conCaja)
            presentaciones.Add(new PresentacionFichaDto(null, "Caja 12", 12_000, false, [new CodigoFichaDto($"CJ{Guid.NewGuid():N}"[..14])]));
        var ficha = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
                [new VarianteFichaDto(null, "", UnidadBase.Pieza, true, presentaciones, [new PrecioFichaDto(null, 1000, 2000)])])));
        var variante = ficha.Variantes[0];
        await MoverAsync(c, variante.Id!.Value, existencia, DateTime.UtcNow.AddHours(-1), TipoMovimiento.InventarioInicial);
        await c.Servicio.CatalogoAlDiaAsync();
        return new Producto(variante.Id!.Value, variante.Presentaciones.Single(p => p.EsBase).Id!.Value,
            variante.Presentaciones.SingleOrDefault(p => !p.EsBase)?.Id, nombre);
    }

    /// <summary>El producto, como los que llegan con la lectura: con su ID de eleventa.</summary>
    private static async Task<Producto> DeEleventaAsync(Caja c, Producto p)
    {
        await using var db = c.Servicio.Base();
        db.MapeosImportacion.Add(new MapeoImportacion
        {
            Origen = "eleventa", Entidad = nameof(Variante), IdOrigen = Random.Shared.Next(100_000, 999_999).ToString(), IdNuevo = p.VarianteId,
        });
        await db.SaveChangesAsync();
        return p;
    }

    private static async Task MoverAsync(Caja c, Guid varianteId, int piezas, DateTime fechaUtc, TipoMovimiento tipo, string documento = OrigenDelMovimiento.Ficticios)
    {
        await using var db = c.Servicio.Base();
        var variante = await db.Variantes.SingleAsync(v => v.Id == varianteId);
        var inventario = new RegistroInventario(db);
        await inventario.RegistrarAsync(piezas > 0
            ? Kardex.Entrada(c.Datos.SucursalId, variante, tipo, Cantidad.Piezas(piezas), piezas * 1000L, fechaUtc, documentoTipo: documento)
            : Kardex.Salida(c.Datos.SucursalId, variante, tipo, Cantidad.Piezas(-piezas), await inventario.ExistenciaAsync(c.Datos.SucursalId, varianteId), 1000,
                fechaUtc, documentoTipo: documento, documentoId: Guid.NewGuid()));
    }

    private static async Task<decimal> HayAsync(Caja c, Producto p)
    {
        await using var db = c.Servicio.Base();
        return (await db.Existencias.AsNoTracking().SingleAsync(e => e.SucursalId == c.Datos.SucursalId && e.VarianteId == p.VarianteId)).Cantidad.EnUnidades;
    }

    /// <summary>El celular manda la lista a la caja principal.</summary>
    private static async Task<EstadoConteoDto> MandarAsync(Caja c, Guid? usuario, params RenglonMandado[] renglones) =>
        await Leer<EstadoConteoDto>(await c.Celular.PostAsJsonAsync($"api/cajas/{c.CelularId}/mandar-conteo",
            new SolicitudMandarConteo(Guid.NewGuid(), c.Datos.Id, DateTime.UtcNow.AddMinutes(-5), usuario, renglones)));

    private static async Task<EstadoConteoDto> ComoVaAsync(Caja c, Guid conteo) =>
        await Leer<EstadoConteoDto>(await c.Celular.GetAsync($"api/cajas/{c.CelularId}/conteos/{conteo}"));

    private static async Task<PantallaSinNavegador> InventarioAsync(Caja c, SesionDto quien, TareaInventario? inicial = null)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Inventario>(new()
        {
            [nameof(Inventario.Caja)] = c.Datos,
            [nameof(Inventario.Sesion)] = quien,
            [nameof(Inventario.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Inventario.EtiquetasPorImprimir)] = new EtiquetasPorImprimir(),
            [nameof(Inventario.TareaInicial)] = inicial,
        });
        return p;
    }

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

    private static async Task<string> TextoDeAsync(PantallaSinNavegador p, string id) => PantallaSinNavegador.Limpio((await p.PorIdAsync(id)).Texto);

    /// <summary>Una tecla con Ctrl en un campo (Ctrl+Enter, Ctrl+Supr).</summary>
    private static async Task CtrlAsync(PantallaSinNavegador p, string id, string tecla) =>
        await p.DispararAsync(await p.PorIdAsync(id), "onkeydown", new KeyboardEventArgs { Key = tecla, CtrlKey = true });

    [Fact]
    public async Task Con_el_teclado_se_abre_se_corrige_se_quita_y_se_aplica()
    {
        await using var c = await PrepararAsync();
        var lapiz = await ProductoAsync(c, "Lápiz de la caja", 30, conCaja: true);
        var goma = await ProductoAsync(c, "Goma de la caja", 4);
        var regla = await ProductoAsync(c, "Regla de la caja", 10);
        var lista = await MandarAsync(c, c.Cajero.UsuarioId,
            new RenglonMandado(lapiz.VarianteId, lapiz.CajaDe12, null, 2000), new RenglonMandado(lapiz.VarianteId, lapiz.Pieza, null, 5000),
            new RenglonMandado(goma.VarianteId, goma.Pieza, null, 3000), new RenglonMandado(regla.VarianteId, regla.Pieza, null, 10_000));
        // Después de contar se vendió un lápiz.
        await MoverAsync(c, lapiz.VarianteId, -1, DateTime.UtcNow, TipoMovimiento.Venta);

        await using var p = await InventarioAsync(c, c.Encargado, TareaInventario.ConteosDelCelular);
        await p.EsperarElementoAsync("cel-lista-0");
        Assert.Matches(@"^▸ Celular de Luis · contó Cajero de prueba · contado a las \d\d:\d\d · 3 productos$", await TextoDeAsync(p, "cel-lista-0"));
        await FocoEnAsync(c, "#cel-lista-0");

        // Enter en la lista (el clic de su botón): se abre con el cursor en el primer producto.
        await p.OprimirAsync(await p.PorIdAsync("cel-lista-0"));
        await p.EsperarElementoAsync("cel-cant-0");
        await FocoEnAsync(c, "#cel-cant-0");
        Assert.Contains("Mandó: 2 Caja 12 + 5 Pieza", await p.TextoAsync());
        Assert.Equal("29", (await p.PorIdAsync("cel-cant-0")).Valor);
        // Contó 29, se vendió 1: quedarán 28, y hoy hay 29.
        Assert.Equal(("28", "-1"), (await TextoDeAsync(p, "cel-queda-0"), await TextoDeAsync(p, "cel-dif-0")));

        // Se corrige a 27 (quedarán 26) y Enter pasa al siguiente.
        await p.EscribirAsync("cel-cant-0", "27");
        Assert.Equal(("26", "-3"), (await TextoDeAsync(p, "cel-queda-0"), await TextoDeAsync(p, "cel-dif-0")));
        await p.TeclaAsync("cel-cant-0", "Enter");
        await FocoEnAsync(c, "#cel-cant-1");

        // Ctrl+Supr quita la goma (no cambia) y el cursor sigue en la regla.
        await CtrlAsync(p, "cel-cant-1", "Delete");
        await FocoEnAsync(c, "#cel-cant-2");
        Assert.Contains("Quitado: no cambia.", PantallaSinNavegador.Limpio(await TextoDeAsync(p, "cel-renglon-1")));
        Assert.Equal("Regresar", await TextoDeAsync(p, "cel-quitar-1"));

        // Lo que no es una cantidad se dice antes de mandarlo.
        await p.EscribirAsync("cel-cant-2", "diez");
        await CtrlAsync(p, "cel-cant-2", "Enter");
        await p.EsperarAsync(t => t.Contains("Escribe cuánto hay de «Regla de la caja» (0 si no hay), o quítalo."), "el error de la cantidad");
        Assert.Equal("recibida", (await ComoVaAsync(c, lista.Id)).Estado);

        // Ctrl+Enter la aplica.
        await p.EscribirAsync("cel-cant-2", "10");
        await CtrlAsync(p, "cel-cant-2", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: se aplicó la lista de Celular de Luis: 2 productos, 1 cambiado, 1 quitado."), "lo aplicado");
        Assert.Contains("No hay conteos del celular por revisar.", await p.TextoAsync());
        Assert.Equal((26m, 4m, 10m), (await HayAsync(c, lapiz), await HayAsync(c, goma), await HayAsync(c, regla)));
        var estado = await ComoVaAsync(c, lista.Id);
        Assert.Equal(("aceptada-con-cambios", "Encargado de prueba"), (estado.Estado, estado.Resolvio));
        Assert.Equal([27m, null, 10m], estado.Renglones.Select(r => r.Aceptado));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Rechazarla_pide_el_porque_y_solo_la_ve_quien_puede()
    {
        await using var c = await PrepararAsync();
        var marcador = await ProductoAsync(c, "Marcador de la caja", 6);
        var lista = await MandarAsync(c, null, new RenglonMandado(marcador.VarianteId, null, null, 2000));

        // El cajero no tiene «Conteo completo de un departamento»: ni la tarjeta.
        await using (var cajero = await InventarioAsync(c, c.Cajero))
        {
            await cajero.EsperarElementoAsync("tarea-Agregar");
            Assert.DoesNotContain((await cajero.LeerAsync()).Elementos, e => e.Id == "tarea-ConteosDelCelular");
        }

        // El encargado la ve con cuántas esperan, y su tarjeta la abre.
        await using var p = await InventarioAsync(c, c.Encargado);
        await p.EsperarAsync(t => t.Contains("1 por revisar"), "lo que espera del celular");
        Assert.Contains("1 por revisar", await TextoDeAsync(p, "tarea-ConteosDelCelular"));
        await p.OprimirAsync(await p.PorIdAsync("tarea-ConteosDelCelular"));
        await p.EsperarElementoAsync("cel-lista-0");
        Assert.Contains("Celular de Luis · contado a las", await TextoDeAsync(p, "cel-lista-0"));
        await p.OprimirAsync(await p.PorIdAsync("cel-lista-0"));
        await p.EsperarElementoAsync("cel-rechazar");

        // «Rechazar la lista» y luego «No»: vuelve a «Rechazar».
        await p.OprimirAsync(await p.PorIdAsync("cel-rechazar"));
        await FocoEnAsync(c, "#cel-motivo");
        Assert.Contains("¿Rechazar la lista de Celular de Luis? Nada cambia en el inventario.", await p.TextoAsync());
        await p.OprimirAsync(await p.PorIdAsync("cel-rechazar-no"));
        await FocoEnAsync(c, "#cel-rechazar");

        // Otra vez, con el porqué, y Enter.
        await p.OprimirAsync(await p.PorIdAsync("cel-rechazar"));
        await FocoEnAsync(c, "#cel-motivo");
        await p.EscribirAsync("cel-motivo", "Faltó la bodega");
        await p.TeclaAsync("cel-motivo", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: se rechazó la lista de Celular de Luis. El inventario no cambió."), "lo rechazado");
        Assert.Equal(6m, await HayAsync(c, marcador));
        var estado = await ComoVaAsync(c, lista.Id);
        Assert.Equal(("rechazada", "Faltó la bodega"), (estado.Estado, estado.Motivo));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_el_piloto_con_el_teclado_se_acepta_sin_mover_existencias_y_la_lista_por_capturar_se_imprime_se_exporta_y_se_cierra()
    {
        var lectura = new LecturaDePrueba();
        await using var c = await PrepararAsync(lectura);
        var lapiz = await DeEleventaAsync(c, await ProductoAsync(c, "Lápiz del piloto", 30));
        var goma = await DeEleventaAsync(c, await ProductoAsync(c, "Goma del piloto", 4));
        var lista = await MandarAsync(c, c.Cajero.UsuarioId,
            new RenglonMandado(lapiz.VarianteId, lapiz.Pieza, null, 27_000), new RenglonMandado(goma.VarianteId, goma.Pieza, null, 4000));
        // Después de contar, eleventa vendió 2 lápices: llegan con la lectura que se pide al abrir la lista.
        lectura.AlLeer = () => MoverAsync(c, lapiz.VarianteId, -2, DateTime.UtcNow, TipoMovimiento.Conteo, OrigenDelMovimiento.LecturaEleventa);

        // En el piloto la tarea se ofrece (el conteo completo, que sí mueve existencias, no), con lo que espera.
        await using var p = await InventarioAsync(c, c.Encargado);
        await p.EsperarAsync(t => t.Contains("1 por revisar"), "lo que espera del celular");
        Assert.Contains(TrastiendaEnElPiloto.InventarioEnEleventa, await p.TextoAsync());
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "tarea-ConteoCompleto");
        await p.OprimirAsync(await p.PorIdAsync("tarea-ConteosDelCelular"));
        await p.EsperarElementoAsync("cel-lista-0");
        await FocoEnAsync(c, "#cel-lista-0");
        Assert.Contains("al aceptarla aquí no cambia ninguna existencia", PantallaSinNavegador.Limpio(await p.TextoAsync()));

        // Enter la abre: se compara contra lo que dice eleventa ahora, y lo que vendió cuenta como movido.
        await p.OprimirAsync(await p.PorIdAsync("cel-lista-0"));
        await p.EsperarElementoAsync("cel-cant-0");
        await FocoEnAsync(c, "#cel-cant-0");
        Assert.Equal(PapeleriaPos.Servicio.ApiConteosDelCelular.SeCapturaEnEleventa, await TextoDeAsync(p, "cel-eleventa"));
        var abierta = await p.TextoAsync();
        Assert.Contains("En eleventa hay", abierta);
        Assert.Contains("Debe quedar", abierta);
        Assert.DoesNotContain("Aplicar al inventario", abierta);
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "cel-sin-lectura");
        // Contó 27 y eleventa vendió 2: deben quedar 25, y eleventa dice 28.
        Assert.Equal(("25", "-3"), (await TextoDeAsync(p, "cel-queda-0"), await TextoDeAsync(p, "cel-dif-0")));
        Assert.Equal("Aceptar: queda por capturar en eleventa (Ctrl+Enter)", await TextoDeAsync(p, "cel-aplicar"));

        // Enter pasa al siguiente y Ctrl+Enter la acepta: aquí no cambia nada, y se abre su lista por capturar con el cursor en «Imprimir».
        await p.TeclaAsync("cel-cant-0", "Enter");
        await FocoEnAsync(c, "#cel-cant-1");
        await CtrlAsync(p, "cel-cant-1", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: se aceptó la lista de Celular de Luis: 1 producto con diferencia por capturar en eleventa, 1 cuadró. "
            + "Aquí no cambió ninguna existencia."), "lo aceptado");
        await FocoEnAsync(c, "#cel-imprimir");
        Assert.Equal((28m, 4m), (await HayAsync(c, lapiz), await HayAsync(c, goma)));
        Assert.Contains("No hay conteos del celular por revisar.", await p.TextoAsync());
        Assert.Matches(@"^▾ Celular de Luis · contó Cajero de prueba · contado a las \d\d:\d\d · 1 por capturar$", await TextoDeAsync(p, "cel-capturar-0"));
        Assert.Contains("Lápiz del piloto", await TextoDeAsync(p, "cel-pc-renglon-0"));
        Assert.Equal(("-3", "25"), (await TextoDeAsync(p, "cel-pc-dif-0"), await TextoDeAsync(p, "cel-pc-queda-0")));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "cel-pc-renglon-1");
        Assert.Contains("1 producto cuadró", await p.TextoAsync());
        Assert.Equal("por-capturar", (await ComoVaAsync(c, lista.Id)).Renglones[0].Eleventa);

        // La tarjeta de Inventario lo dice.
        await using (var tarjetas = await InventarioAsync(c, c.Encargado))
        {
            await tarjetas.EsperarAsync(t => t.Contains("1 por capturar en eleventa"), "lo que falta capturar");
            Assert.DoesNotContain("por revisar", await TextoDeAsync(tarjetas, "tarea-ConteosDelCelular"));
        }

        // «Imprimir la lista» (Enter en su botón): la hoja va a su marco y se abre la impresión; el cursor sigue ahí.
        await p.OprimirAsync(await p.PorIdAsync("cel-imprimir"));
        var hoja = c.Pagina.Llamadas.Last(l => l.Funcion == "mostrarEnMarco").Argumentos!;
        Assert.Equal("#marco-por-capturar", hoja[0]);
        var html = hoja[1]!.ToString()!;
        Assert.Contains("Conteo del celular por capturar en eleventa", html);
        Assert.Contains("Lápiz del piloto", html);
        Assert.Contains("<td class=\"num\">28</td><td class=\"num dif\">-3</td><td class=\"num\">25</td>", html);
        Assert.DoesNotContain("Goma del piloto", html);
        Assert.Equal("#marco-por-capturar", c.Pagina.Llamadas.Last(l => l.Funcion == "imprimirMarco").Argumentos![0]);
        await FocoEnAsync(c, "#cel-imprimir");

        // «Exportar a Excel»: se guarda con su nombre y lo dice al lado.
        await p.OprimirAsync(await p.PorIdAsync("cel-excel"));
        await p.EsperarElementoAsync("cel-excel-estado");
        Assert.Matches(@"^Conteo por capturar en eleventa Celular de Luis \d{4}-\d\d-\d\d \d\d\.\d\d\.xlsx$", Assert.Single(c.Guardado.Guardados).Nombre);
        await FocoEnAsync(c, "#cel-excel");

        // «Ya lo capturé en eleventa» pregunta; «No» regresa al botón.
        await p.OprimirAsync(await p.PorIdAsync("cel-capturado"));
        await FocoEnAsync(c, "#cel-capturado-si");
        Assert.Contains("¿Ya capturaste en eleventa la diferencia de esta lista?", await TextoDeAsync(p, "cel-capturado-pregunta"));
        await p.OprimirAsync(await p.PorIdAsync("cel-capturado-no"));
        await FocoEnAsync(c, "#cel-capturado");
        Assert.Single(await c.Cliente.ConteosPorCapturarAsync(c.Datos.Id));

        // Otra vez, y Enter en «Sí»: se cierra, sin mover nada aquí; la existencia llegará con la lectura, que se pide.
        await p.OprimirAsync(await p.PorIdAsync("cel-capturado"));
        await FocoEnAsync(c, "#cel-capturado-si");
        await p.OprimirAsync(await p.PorIdAsync("cel-capturado-si"));
        await p.EsperarAsync(t => t.Contains("Listo: la lista de Celular de Luis quedó como capturada en eleventa (1 diferencia)."), "lo capturado");
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "cel-capturar-0");
        Assert.DoesNotContain("Por capturar en eleventa", await p.TextoAsync());
        var estado = await ComoVaAsync(c, lista.Id);
        Assert.Equal([("capturado", true), (null, true)], estado.Renglones.Select(r => (r.Eleventa, r.Aplicado)));
        Assert.Equal((28m, 4m), (await HayAsync(c, lapiz), await HayAsync(c, goma)));
        Assert.Equal(1, lectura.Pedidas);
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Con «Mandar los conteos a eleventa» prendido y el puente escribiendo (2026-11-27), solo con el teclado: Ctrl+Enter acepta, la
    /// existencia de aquí toma la diferencia y la lista queda abajo, en «Mandados a eleventa», con el cursor en «Actualizar»; cada
    /// producto dice «Va a eleventa», «Ya quedó en eleventa» o «Eleventa no lo tomó: …» (aquí no hay eleventa: lo que hace el puente se
    /// pone a mano en la base, como lo deja él).
    /// </summary>
    [Fact]
    public async Task Con_mandar_a_eleventa_prendido_se_acepta_con_el_teclado_y_la_pantalla_dice_como_va_cada_producto()
    {
        var lectura = new LecturaDePrueba();
        await using var c = await PrepararAsync(lectura, escribe: true);
        var dueno = await c.Cliente.EntrarAsync("dueno", "3333");
        Assert.Equal(new MandarConteosDto(EnElPiloto: true, Mandar: true, PuenteEscribe: true),
            await c.Cliente.GuardarMandarConteosAsync(c.Datos.Id, new SolicitudMandarConteos(dueno.UsuarioId, true)));
        var lapiz = await DeEleventaAsync(c, await ProductoAsync(c, "Lápiz del piloto", 30));
        var goma = await DeEleventaAsync(c, await ProductoAsync(c, "Goma del piloto", 4));
        var regla = await DeEleventaAsync(c, await ProductoAsync(c, "Regla del piloto", 10));
        var lista = await MandarAsync(c, c.Cajero.UsuarioId, new RenglonMandado(lapiz.VarianteId, lapiz.Pieza, null, 27_000),
            new RenglonMandado(goma.VarianteId, goma.Pieza, null, 4000), new RenglonMandado(regla.VarianteId, regla.Pieza, null, 12_000));
        lectura.AlLeer = () => MoverAsync(c, lapiz.VarianteId, -2, DateTime.UtcNow, TipoMovimiento.Conteo, OrigenDelMovimiento.LecturaEleventa);

        await using var p = await InventarioAsync(c, c.Encargado, TareaInventario.ConteosDelCelular);
        await p.EsperarElementoAsync("cel-lista-0");
        await FocoEnAsync(c, "#cel-lista-0");
        Assert.Contains("cada diferencia se le manda a eleventa como un ajuste de inventario", PantallaSinNavegador.Limpio(await p.TextoAsync()));

        // Enter la abre: dice que lo aceptado se manda, y el botón también.
        await p.OprimirAsync(await p.PorIdAsync("cel-lista-0"));
        await p.EsperarElementoAsync("cel-cant-0");
        await FocoEnAsync(c, "#cel-cant-0");
        Assert.Equal(PapeleriaPos.Servicio.ApiConteosDelCelular.SeMandaAEleventa, await TextoDeAsync(p, "cel-eleventa"));
        Assert.Equal(("25", "-3"), (await TextoDeAsync(p, "cel-queda-0"), await TextoDeAsync(p, "cel-dif-0")));
        Assert.Equal("Aceptar: se manda a eleventa (Ctrl+Enter)", await TextoDeAsync(p, "cel-aplicar"));

        // Ctrl+Enter la acepta, sin otra pregunta: la existencia de aquí toma la diferencia y se abre su lista «mandada a eleventa».
        await CtrlAsync(p, "cel-cant-0", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: se aceptó la lista de Celular de Luis: 2 productos con diferencia van a eleventa como ajuste, 1 cuadró. "
            + "Abajo dice cómo va cada uno."), "lo aceptado");
        await FocoEnAsync(c, "#cel-eleventa-actualizar");
        Assert.Equal((25m, 4m, 12m), (await HayAsync(c, lapiz), await HayAsync(c, goma), await HayAsync(c, regla)));
        Assert.Contains("No hay conteos del celular por revisar.", await p.TextoAsync());
        Assert.DoesNotContain("Por capturar en eleventa", await p.TextoAsync());
        Assert.Matches(@"^▾ Celular de Luis · contó Cajero de prueba · contado a las \d\d:\d\d · 2 van a eleventa$", await TextoDeAsync(p, "cel-eleventa-0"));
        Assert.Contains("Lápiz del piloto", await TextoDeAsync(p, "cel-el-renglon-0"));
        Assert.Equal(("-3", "Va a eleventa", "+2", "Va a eleventa"), (await TextoDeAsync(p, "cel-el-dif-0"), await TextoDeAsync(p, "cel-el-estado-0"),
            await TextoDeAsync(p, "cel-el-dif-1"), await TextoDeAsync(p, "cel-el-estado-1")));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "cel-el-renglon-2");
        // Lo que ve el celular: va en camino, y todavía no cuenta como aplicado.
        Assert.Equal([("pendiente", false, "Va a eleventa: -3."), (null, true, null), ("pendiente", false, "Va a eleventa: +2.")],
            (await ComoVaAsync(c, lista.Id)).Renglones.Select(r => (r.Eleventa, r.Aplicado, r.Nota)));

        // El puente: el lápiz entró (ajuste #412) y la regla no (alguien la ajustó allá). Como lo deja él en la base.
        await using (var db = c.Servicio.Base())
        {
            foreach (var envio in await db.EnviosAEleventa.ToListAsync())
            {
                var renglon = await db.ConteosDelCelularRenglones.SingleAsync(r => r.Id == envio.ConteoRenglonId);
                if (renglon.VarianteId == lapiz.VarianteId)
                    (envio.Estado, envio.IdEleventa, envio.EnviadaUtc) = (PapeleriaPos.Nucleo.Ventas.EstadoEnvioEleventa.Enviada, 412, DateTime.UtcNow);
                else
                    (envio.Estado, envio.Error) = (PapeleriaPos.Nucleo.Ventas.EstadoEnvioEleventa.PorRevisar,
                        "«Regla del piloto» se ajustó en eleventa el 27/11 a las 10:42, después de contarlo: manda ese ajuste y este conteo no se aplica.");
                await PapeleriaPos.Eleventa.ConteosParaEleventa.AlQuedarAsync(db, envio, DateTime.UtcNow);
            }
            await db.SaveChangesAsync();
        }

        // Enter en «Actualizar»: cada producto dice cómo quedó, y el cursor sigue ahí.
        await p.OprimirAsync(await p.PorIdAsync("cel-eleventa-actualizar"));
        await p.EsperarAsync(t => t.Contains("Ya quedó en eleventa (ajuste #412)"), "lo que ya quedó");
        await FocoEnAsync(c, "#cel-eleventa-actualizar");
        Assert.Equal(("Ya quedó en eleventa (ajuste #412)",
            "Eleventa no lo tomó: «Regla del piloto» se ajustó en eleventa el 27/11 a las 10:42, después de contarlo: manda ese ajuste y este conteo no se aplica."),
            (await TextoDeAsync(p, "cel-el-estado-0"), await TextoDeAsync(p, "cel-el-estado-1")));
        Assert.Equal(("enviado", "por-revisar"), ((await p.PorIdAsync("cel-el-estado-0")).Atributos.GetValueOrDefault("data-estado"),
            (await p.PorIdAsync("cel-el-estado-1")).Atributos.GetValueOrDefault("data-estado")));
        Assert.EndsWith("· 1 ya quedó · 1 no lo tomó eleventa", await TextoDeAsync(p, "cel-eleventa-0"));
        // Lo que eleventa no tomó regresó aquí a lo de antes; lo que sí, se queda.
        Assert.Equal((25m, 10m), (await HayAsync(c, lapiz), await HayAsync(c, regla)));
        var estado = await ComoVaAsync(c, lista.Id);
        Assert.Equal([("enviado", true), (null, true), ("por-revisar", false)], estado.Renglones.Select(r => (r.Eleventa, r.Aplicado)));
        Assert.Equal("Ya quedó en eleventa: -3 (ajuste #412).", estado.Renglones[0].Nota);
        Assert.StartsWith("Eleventa no lo tomó: «Regla del piloto» se ajustó en eleventa", estado.Renglones[2].Nota);

        // Enter en la lista la cierra y el cursor regresa a ella.
        await p.OprimirAsync(await p.PorIdAsync("cel-eleventa-0"));
        await FocoEnAsync(c, "#cel-eleventa-0");
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "cel-eleventa-actualizar");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_el_piloto_si_eleventa_no_contesta_la_pantalla_lo_dice_y_la_lista_sigue_esperando()
    {
        var lectura = new LecturaDePrueba { Falla = "La PC de eleventa no contesta." };
        await using var c = await PrepararAsync(lectura);
        var regla = await DeEleventaAsync(c, await ProductoAsync(c, "Regla del piloto", 10));
        var lista = await MandarAsync(c, null, new RenglonMandado(regla.VarianteId, regla.Pieza, null, 8000));

        await using var p = await InventarioAsync(c, c.Encargado, TareaInventario.ConteosDelCelular);
        await p.EsperarElementoAsync("cel-lista-0");
        await p.OprimirAsync(await p.PorIdAsync("cel-lista-0"));
        await p.EsperarElementoAsync("cel-cant-0");
        Assert.Equal("Eleventa no contesta: lo que se ve es de su última lectura. Para aceptarla hace falta que conteste.", await TextoDeAsync(p, "cel-sin-lectura"));

        await CtrlAsync(p, "cel-cant-0", "Enter");
        await p.EsperarAsync(t => t.Contains(PapeleriaPos.Servicio.ApiConteosDelCelular.EleventaNoContesta), "que eleventa no contesta");
        Assert.Equal("recibida", (await ComoVaAsync(c, lista.Id)).Estado);
        // La lista sigue abierta, con lo escrito: cuando eleventa contesta, Ctrl+Enter otra vez la acepta.
        lectura.Falla = null;
        await CtrlAsync(p, "cel-cant-0", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: se aceptó la lista de Celular de Luis: 1 producto con diferencia por capturar en eleventa."), "lo aceptado");
        await FocoEnAsync(c, "#cel-imprimir");
        Assert.Equal(10m, await HayAsync(c, regla));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task La_venta_avisa_cuando_llega_y_su_chip_lleva_a_la_lista()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        // El turno de la caja es del encargado, que puede aceptarla (la venta usa el que ya está abierto).
        var principal = servicio.Fabrica.CreateClient();
        var caja = (await principal.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var encargado = await Leer<SesionDto>(await principal.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        await Leer<TurnoDto>(await principal.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, encargado.UsuarioId, 0)));
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal, quien: ("encargado", "2222"),
            conteosDelCelularCada: TimeSpan.FromMilliseconds(100));
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        var atendido = await VentaConCopia.RenglonesAsync(v.Pantalla);
        Assert.DoesNotContain((await v.Pantalla.LeerAsync()).Elementos, e => e.Id == "chip-conteos-celular");

        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", v.Caja.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        await Leer<EstadoConteoDto>(await celular.PostAsJsonAsync($"api/cajas/{alta.Caja.Id}/mandar-conteo",
            new SolicitudMandarConteo(Guid.NewGuid(), v.Caja.Id, null, null, [new RenglonMandado(a.VarianteId, a.Presentacion.Id, null, 7000)])));

        await v.Pantalla.EsperarAsync(t => t.Contains("Llegó un conteo de Celular de Luis: 1 producto. Revísalo en Inventario (F4) › Conteos del celular."),
            "el aviso del conteo");
        Assert.Equal("Conteos del celular 1", PantallaSinNavegador.Limpio((await v.Pantalla.PorIdAsync("chip-conteos-celular")).Texto));
        // El ticket que se atendía sigue igual.
        Assert.Equal(atendido, await VentaConCopia.RenglonesAsync(v.Pantalla));

        await v.Pantalla.OprimirAsync(await v.Pantalla.PorIdAsync("chip-conteos-celular"));
        await v.Pantalla.EsperarElementoAsync("cel-lista-0");
        Assert.Contains("Lo que se contó con el celular: revisarlo y aplicarlo.", await v.Pantalla.TextoAsync());
        Assert.Empty(v.Pantalla.Errores);
    }
}
