using System.Text;
using Microsoft.AspNetCore.Components;
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
/// Inventario › «Traspasos» (2026-11-26, provisional mientras no exista el servidor), sin navegador y con dos cajas, cada una con
/// su servicio y su base: en la que lo tramita, la tarjeta, la lista escaneando (cada escaneo suma uno; la caja de 12, doce),
/// exportar preguntando antes y el archivo guardado con su clave; en la que recibe, importar ese archivo, escribir lo que llegó,
/// que el cajero solo pueda dejarlo marcado para revisión y que el encargado lo reciba con el cambio. Y lo que se escribe en
/// «Cuántos van» y en «Llegó».
/// </summary>
public sealed class TraspasosTests(ServicioDePrueba matriz) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(
        ServicioDePrueba Servicio, ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, GuardadoDePrueba Guardado, AbiertoDePrueba Abierto,
        SesionDto Cajero, SesionDto Encargado);

    private static async Task<Caja> PrepararAsync(ServicioDePrueba servicio)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var (guardado, abierto) = (new GuardadoDePrueba(), new AbiertoDePrueba());
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddSingleton<IGuardarArchivo>(guardado)
            .AddSingleton<IAbrirArchivo>(abierto)
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(servicio, cliente, caja, servicios, guardado, abierto, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("encargado", "2222"));
    }

    /// <summary>Un producto por pieza con ese código y su caja de 12 con el otro, con <paramref name="existencia"/> piezas a $10.</summary>
    private static async Task<Guid> ProductoAsync(Caja c, string nombre, string codigo, string codigoDeLaCaja, int existencia)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var ficha = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                [
                    new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)]),
                    new PresentacionFichaDto(null, "Caja 12", 12_000, false, [new CodigoFichaDto(codigoDeLaCaja)]),
                ], [new PrecioFichaDto(null, 1000, 2000)]),
            ])));
        var id = ficha.Variantes[0].Id!.Value;
        if (existencia > 0)
        {
            await using var db = c.Servicio.Base();
            var variante = await db.Variantes.SingleAsync(v => v.Id == id);
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(c.Datos.SucursalId, variante, TipoMovimiento.InventarioInicial,
                Cantidad.Piezas(existencia), existencia * 1000L, DateTime.UtcNow, documentoTipo: OrigenDelMovimiento.ImportacionEleventa));
        }
        // El código se busca en el catálogo en memoria del servicio, que se pone al día en segundo plano.
        await c.Servicio.CatalogoAlDiaAsync();
        return id;
    }

    private static async Task<decimal> ExistenciaAsync(Caja c, Guid variante)
    {
        await using var db = c.Servicio.Base();
        return (await db.Existencias.AsNoTracking().SingleOrDefaultAsync(x => x.VarianteId == variante))?.Cantidad.EnUnidades ?? 0;
    }

    private static async Task<PantallaSinNavegador> TraspasosAsync(Caja c, SesionDto quien)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        await p.AbrirAsync<Inventario>(new()
        {
            [nameof(Inventario.Caja)] = c.Datos,
            [nameof(Inventario.Sesion)] = quien,
            [nameof(Inventario.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Inventario.EtiquetasPorImprimir)] = new EtiquetasPorImprimir(),
            [nameof(Inventario.TareaInicial)] = TareaInventario.Traspasos,
        });
        await p.EsperarElementoAsync("traspaso-nuevo");
        return p;
    }

    private static async Task EscanearAsync(PantallaSinNavegador p, string codigo)
    {
        await p.EscribirAsync("codigo-compra", codigo);
        await p.TeclaAsync("codigo-compra", "Enter");
    }

    /// <summary>El campo cuyo id empieza así (los de cada renglón llevan el de su renglón).</summary>
    private static Task<ElementoDibujado> CampoAsync(PantallaSinNavegador p, string prefijo) =>
        p.ElementoAsync(e => e.Id?.StartsWith(prefijo, StringComparison.Ordinal) == true, $"un campo «{prefijo}…»");

    /// <summary>Escribir en un campo y salir de él (su «onchange» con lo escrito).</summary>
    private static async Task PonerAsync(PantallaSinNavegador p, ElementoDibujado campo, string valor) =>
        await p.DispararAsync(campo, "onchange", new ChangeEventArgs { Value = valor });

    private static Task DiceAsync(PantallaSinNavegador p, string texto) =>
        p.EsperarAsync(t => PantallaSinNavegador.Limpio(t).Contains(texto, StringComparison.Ordinal), $"«{texto}»");

    [Fact]
    public async Task La_tarjeta_solo_la_ve_quien_tiene_el_permiso_y_tambien_en_el_piloto()
    {
        var tarea = PaginasDeAtras.De(TareaInventario.Traspasos);
        Assert.Equal((PapeleriaPos.Nucleo.Control.AccionControlada.Traspaso, false), (tarea.Regla!.Value, tarea.Consulta));
        // En el piloto con eleventa se ofrece: ahí es solo el documento, y no mueve existencias.
        Assert.DoesNotContain(TareaInventario.Traspasos, PaginasDeAtras.MuevenExistencias);

        var c = await PrepararAsync(matriz);
        var p = new PantallaSinNavegador(c.Servicios);
        await using (p)
        {
            await p.AbrirAsync<Inventario>(new()
            {
                [nameof(Inventario.Caja)] = c.Datos, [nameof(Inventario.Sesion)] = c.Cajero, [nameof(Inventario.Acceso)] = new PermisosDePagina(c.Cajero, null),
                [nameof(Inventario.EtiquetasPorImprimir)] = new EtiquetasPorImprimir(),
            });
            await p.EsperarElementoAsync("tarea-Traspasos");
            Assert.Contains("Mandar mercancía a otra sucursal con un archivo, y recibir la que llega.", PantallaSinNavegador.Limpio((await p.PorIdAsync("tarea-Traspasos")).Texto));
            Assert.Empty(p.Errores);
        }
    }

    [Fact]
    public async Task Se_arma_escaneando_se_exporta_y_en_la_otra_sucursal_se_importa_se_marca_y_se_recibe()
    {
        await using var sucursal2 = await ServicioDePrueba.CrearAsync();
        var (a, b) = (await PrepararAsync(matriz), await PrepararAsync(sucursal2));
        var (codigo, caja12) = ($"TP{Guid.NewGuid():N}"[..14], $"TC{Guid.NewGuid():N}"[..14]);
        var enA = await ProductoAsync(a, "Marcador de la pantalla", codigo, caja12, 40);
        var enB = await ProductoAsync(b, "Marcador de la pantalla", codigo, caja12, 2);

        // ── La matriz: nuevo, escanear (uno, otro y la caja de 12: catorce), corregir a 13 y exportar ──
        ArchivoPorGuardar archivo;
        string clave;
        await using (var p = await TraspasosAsync(a, a.Cajero))
        {
            await p.OprimirAsync(await p.PorIdAsync("traspaso-nuevo"));
            await p.EsperarElementoAsync("codigo-compra");
            await EscanearAsync(p, codigo);
            await DiceAsync(p, "Marcador de la pantalla");
            await EscanearAsync(p, codigo);
            await EscanearAsync(p, caja12);
            await p.EsperarAsync(_ => true, "que se dibuje");
            var van = await CampoAsync(p, "traspaso-van-");
            Assert.Equal("14", van.Valor);
            Assert.Contains("40", await p.TextoAsync());

            // Lo que no es una cantidad se dice y no se guarda; corregido, sí.
            await PonerAsync(p, van, "1.5");
            await DiceAsync(p, "se cuenta en piezas enteras");
            await PonerAsync(p, await CampoAsync(p, "traspaso-van-"), "13");
            await DiceAsync(p, "Borrador guardado.");

            // Sin destino no se exporta; con él, pregunta antes y nada sale hasta el «Sí».
            await p.OprimirAsync(await p.PorIdAsync("traspaso-exportar"));
            await DiceAsync(p, "Escribe a qué sucursal va el traspaso.");
            await PonerAsync(p, await p.PorIdAsync("traspaso-destino"), "Sucursal 2");
            await DiceAsync(p, "Borrador guardado.");
            await p.OprimirAsync(await p.PorIdAsync("traspaso-exportar"));
            await DiceAsync(p, "¿Exportar 1 producto a «Sucursal 2»? Sale de la existencia de esta sucursal y ya no se cambia.");
            Assert.Equal(40m, await ExistenciaAsync(a, enA));
            await p.OprimirAsync(await p.PorIdAsync("traspaso-si"));
            await DiceAsync(p, "Llévalo a «Sucursal 2».");

            archivo = Assert.Single(a.Guardado.Guardados);
            clave = PantallaSinNavegador.Limpio((await p.PorIdAsync("traspaso-clave")).Texto);
            Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}$", clave);
            Assert.Equal($"Traspaso a Sucursal 2 {DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow)):yyyy-MM-dd} {clave}.traspaso.json", archivo.Nombre);
            Assert.Contains("Exportado", await p.TextoAsync());
            Assert.Contains("La mercancía ya salió de la existencia de esta sucursal.", PantallaSinNavegador.Limpio(await p.TextoAsync()));
            Assert.Equal(27m, await ExistenciaAsync(a, enA));

            // El archivo se guarda otra vez, igual; y en la lista queda como exportado.
            await p.OprimirAsync(await p.PorIdAsync("traspaso-archivo"));
            await p.EsperarAsync(_ => a.Guardado.Guardados.Count == 2, "que se guarde otra vez");
            Assert.Equal(archivo.Datos, a.Guardado.Guardados[1].Datos);
            await p.OprimirAsync(await p.BotonAsync("← Traspasos"));
            await DiceAsync(p, "Sale a Sucursal 2");
            Assert.Empty(p.Errores);
        }

        // ── La sucursal 2, el cajero: importa, ve la misma clave y los dos nombres; llegaron 11 y solo puede marcarlo ──
        b.Abierto.Elegido = new ArchivoAbierto(archivo.Nombre, archivo.Datos, $@"E:\{archivo.Nombre}");
        await using (var p = await TraspasosAsync(b, b.Cajero))
        {
            await p.OprimirAsync(await p.PorIdAsync("traspaso-importar"));
            await DiceAsync(p, "Todavía no entra nada: revisa lo que llegó y recíbelo.");
            Assert.Equal(clave, PantallaSinNavegador.Limpio((await p.PorIdAsync("traspaso-clave")).Texto));
            Assert.Contains("Por recibir", await p.TextoAsync());
            // Como dice el archivo, lo recibiría él mismo.
            Assert.Equal("Recibir: llegó lo que dice", PantallaSinNavegador.Limpio((await p.PorIdAsync("traspaso-recibir")).Texto));

            await PonerAsync(p, await CampoAsync(p, "traspaso-llego-"), "once");
            await DiceAsync(p, "no es una cantidad");
            await PonerAsync(p, await CampoAsync(p, "traspaso-llego-"), "11");
            await DiceAsync(p, "Lo que escribiste está guardado; nada entra hasta recibirlo.");
            Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "traspaso-recibir");
            await p.OprimirAsync(await p.PorIdAsync("traspaso-revision"));
            await DiceAsync(p, "Quedó marcado para revisión");
            Assert.Contains("En revisión", await p.TextoAsync());
            Assert.Equal(2m, await ExistenciaAsync(b, enB));

            // El mismo archivo otra vez no se importa dos veces: es el que ya estaba.
            await p.OprimirAsync(await p.BotonAsync("← Traspasos"));
            await p.EsperarElementoAsync("traspaso-importar");
            await p.OprimirAsync(await p.PorIdAsync("traspaso-importar"));
            await DiceAsync(p, "Ese archivo ya se había importado: es este traspaso (en revisión). No se importó otra vez.");
            Assert.Empty(p.Errores);
        }

        // ── La sucursal 2, el encargado: lo abre de la lista, ve el cambio marcado y lo recibe así ──
        await using (var p = await TraspasosAsync(b, b.Encargado))
        {
            await DiceAsync(p, "Llega de Matriz");
            Assert.Contains("1 cambio", await p.TextoAsync());
            await p.OprimirAsync(await p.BotonAsync("Abrir"));
            await DiceAsync(p, "Cajero de prueba marcó cambios para revisión");
            Assert.Equal("11", (await CampoAsync(p, "traspaso-llego-")).Valor);
            Assert.Equal("Recibir con 1 cambio", PantallaSinNavegador.Limpio((await p.PorIdAsync("traspaso-recibir")).Texto));
            await p.OprimirAsync(await p.PorIdAsync("traspaso-recibir"));
            await DiceAsync(p, "¿Recibir con 1 cambio? Entra a la existencia de esta sucursal y ya no se cambia.");
            Assert.Equal(2m, await ExistenciaAsync(b, enB));
            await p.OprimirAsync(await p.PorIdAsync("traspaso-si"));
            await DiceAsync(p, "Recibido: la mercancía ya está en la existencia de esta sucursal.");
            Assert.Equal(13m, await ExistenciaAsync(b, enB));
            Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id?.StartsWith("traspaso-llego-", StringComparison.Ordinal) == true);
            Assert.Empty(p.Errores);
        }
    }

    [Fact]
    public async Task Un_archivo_que_no_es_un_traspaso_se_dice_y_no_se_importa()
    {
        var c = await PrepararAsync(matriz);
        c.Abierto.Elegido = new ArchivoAbierto("otra cosa.json", Encoding.UTF8.GetBytes("{\"productos\": []}"), @"E:\otra cosa.json");
        await using var p = await TraspasosAsync(c, c.Cajero);
        await p.OprimirAsync(await p.PorIdAsync("traspaso-importar"));
        await DiceAsync(p, ArchivoDeTraspaso.NoEs);
        // Y si se cancela «Abrir», nada.
        c.Abierto.Elegido = null;
        await p.OprimirAsync(await p.PorIdAsync("traspaso-importar"));
        await DiceAsync(p, "No se importó nada: se canceló.");
        Assert.Empty(p.Errores);
    }

    [Theory]
    [InlineData("12", 12_000L)]
    [InlineData("1,000", 1_000_000L)]
    [InlineData("0", null)]
    [InlineData("1.5", null)]
    [InlineData("doce", null)]
    [InlineData("", null)]
    public void Cuantos_van_es_mas_de_cero_y_en_piezas_enteras(string texto, long? milesimas)
    {
        Assert.Equal(milesimas, TraspasosEnPantalla.Van(texto, UnidadBase.Pieza, "Lápiz", out var problema));
        Assert.Equal(milesimas is null, problema is not null);
        Assert.DoesNotContain("llegaron", problema ?? "");
    }

    [Fact]
    public void En_lo_que_llego_el_cero_vale_y_lo_demas_como_siempre()
    {
        Assert.Equal(0, TraspasosEnPantalla.Llego("0", UnidadBase.Pieza, "Lápiz", out var problema));
        Assert.Null(problema);
        Assert.Equal(1500, TraspasosEnPantalla.Llego("1.5", UnidadBase.Metro, "Listón", out _));
        Assert.Null(TraspasosEnPantalla.Llego("-1", UnidadBase.Pieza, "Lápiz", out problema));
        Assert.EndsWith("Si no llegó, escribe 0.", problema);
        Assert.Equal("Sale a Sucursal 2", TraspasosEnPantalla.Sentido(SentidoTraspaso.Salida, "Sucursal 2"));
        Assert.Equal("Llega de Matriz", TraspasosEnPantalla.Sentido(SentidoTraspaso.Entrada, "Matriz"));
        Assert.Equal("En revisión", TraspasosEnPantalla.Estado(EstadoTraspaso.EnRevision));
    }
}
