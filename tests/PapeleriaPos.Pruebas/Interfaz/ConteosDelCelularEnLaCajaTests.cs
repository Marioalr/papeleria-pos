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
/// y su chip lleva ahí. Solo para quien tiene «Conteo completo de un departamento».
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
        SesionDto Cajero, SesionDto Encargado, HttpClient Celular, Guid CelularId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Servicios.DisposeAsync();
            await Servicio.DisposeAsync();
        }
    }

    private static async Task<Caja> PrepararAsync()
    {
        var servicio = await ServicioDePrueba.CrearAsync();
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
        var encargado = await cliente.EntrarAsync("encargado", "2222");
        await cliente.AbrirTurnoAsync(caja.Id, encargado.UsuarioId, 0);
        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        return new Caja(servicio, cliente, caja, servicios, pagina, await cliente.EntrarAsync("cajero", "1111"), encargado, celular, alta.Caja.Id);
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

    private static async Task MoverAsync(Caja c, Guid varianteId, int piezas, DateTime fechaUtc, TipoMovimiento tipo)
    {
        await using var db = c.Servicio.Base();
        var variante = await db.Variantes.SingleAsync(v => v.Id == varianteId);
        var inventario = new RegistroInventario(db);
        await inventario.RegistrarAsync(piezas > 0
            ? Kardex.Entrada(c.Datos.SucursalId, variante, tipo, Cantidad.Piezas(piezas), piezas * 1000L, fechaUtc, documentoTipo: OrigenDelMovimiento.Ficticios)
            : Kardex.Salida(c.Datos.SucursalId, variante, tipo, Cantidad.Piezas(-piezas), await inventario.ExistenciaAsync(c.Datos.SucursalId, varianteId), 1000,
                fechaUtc, documentoTipo: OrigenDelMovimiento.Ficticios, documentoId: Guid.NewGuid()));
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
