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
using PapeleriaPos.Pruebas.Datos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Inventario › «Recibir con factura» (2026-11-27), sin navegador y solo con lo que se hace con el teclado (Enter en un botón es
/// oprimirlo; Enter en un campo, su tecla): la tarjeta (también en el piloto), cargar el XML con el cursor ya en el botón, el cursor
/// en el primer «Emparejar», emparejar escaneando (la caja de 12, con el código de la caja) y que el cursor pase al siguiente, Enter
/// en «Llegó» al siguiente renglón, que el cajero solo pueda dejarla marcada para revisión y que el encargado la reciba con los
/// cambios, preguntando antes.
/// </summary>
public sealed class RecibirConFacturaTests(ServicioDePrueba matriz) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(
        ServicioDePrueba Servicio, ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, AbiertoDePrueba Abierto, PaginaQueContesta Pagina,
        SesionDto Cajero, SesionDto Encargado);

    private static async Task<Caja> PrepararAsync(ServicioDePrueba servicio)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var (abierto, pagina) = (new AbiertoDePrueba(), new PaginaQueContesta());
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IJSRuntime>(pagina)
            .AddSingleton<IAbrirArchivo>(abierto)
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(servicio, cliente, caja, servicios, abierto, pagina, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("encargado", "2222"));
    }

    /// <summary>Un producto por pieza con ese código (y su caja de 12 con el otro, si se dice), con 2 piezas a $10.</summary>
    private static async Task<Guid> ProductoAsync(Caja c, string nombre, string codigo, string? codigoDeLaCaja = null)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        List<PresentacionFichaDto> presentaciones = [new(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])];
        if (codigoDeLaCaja is not null)
            presentaciones.Add(new(null, "Caja 12", 12_000, false, [new CodigoFichaDto(codigoDeLaCaja)]));
        var ficha = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
                [new VarianteFichaDto(null, "", UnidadBase.Pieza, true, presentaciones, [new PrecioFichaDto(null, 1000, 2500)])])));
        var id = ficha.Variantes[0].Id!.Value;
        await using (var db = c.Servicio.Base())
        {
            var variante = await db.Variantes.SingleAsync(v => v.Id == id);
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(c.Datos.SucursalId, variante, TipoMovimiento.InventarioInicial, Cantidad.Piezas(2), 2000,
                DateTime.UtcNow, documentoTipo: OrigenDelMovimiento.ImportacionEleventa));
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

    private static async Task<PantallaSinNavegador> FacturasAsync(Caja c, SesionDto quien) => (await ConInventarioAsync(c, quien)).Pantalla;

    /// <summary>La página, y su Inventario (para mandarle Esc, como lo hace la caja).</summary>
    private static async Task<(PantallaSinNavegador Pantalla, Inventario Inventario)> ConInventarioAsync(Caja c, SesionDto quien)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        var inventario = await p.AbrirAsync<Inventario>(new()
        {
            [nameof(Inventario.Caja)] = c.Datos,
            [nameof(Inventario.Sesion)] = quien,
            [nameof(Inventario.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Inventario.EtiquetasPorImprimir)] = new EtiquetasPorImprimir(),
            [nameof(Inventario.TareaInicial)] = TareaInventario.Factura,
        });
        await p.EsperarElementoAsync("factura-cargar");
        return (p, inventario);
    }

    /// <summary>Dónde se puso el cursor la última vez.</summary>
    private static string? Cursor(Caja c) => c.Pagina.Llamadas.LastOrDefault(l => l.Funcion == "enfocar").Argumentos?[0] as string;

    private static Task CursorEnAsync(PantallaSinNavegador p, Caja c, Func<string, bool> donde, string que) =>
        p.EsperarAsync(_ => Cursor(c) is { } cursor && donde(cursor), $"el cursor en {que} (está en «{Cursor(c)}»)");

    /// <summary>El elemento donde está el cursor (el selector es «#id»).</summary>
    private static Task<ElementoDibujado> EnElCursorAsync(PantallaSinNavegador p, Caja c) => p.PorIdAsync(Cursor(c)!.TrimStart('#'));

    private static async Task EscanearAsync(PantallaSinNavegador p, string codigo)
    {
        await p.EscribirAsync("codigo-compra", codigo);
        await p.TeclaAsync("codigo-compra", "Enter");
    }

    private static Task DiceAsync(PantallaSinNavegador p, string texto) =>
        p.EsperarAsync(t => PantallaSinNavegador.Limpio(t).Contains(texto, StringComparison.Ordinal), $"«{texto}»");

    [Fact]
    public void La_tarjeta_la_ve_quien_tiene_el_permiso_y_tambien_en_el_piloto()
    {
        var tarea = PaginasDeAtras.De(TareaInventario.Factura);
        Assert.Equal((PapeleriaPos.Nucleo.Control.AccionControlada.RecibirConFactura, false), (tarea.Regla!.Value, tarea.Consulta));
        // En el piloto con eleventa se ofrece: ahí es solo el documento, y no mueve existencias.
        Assert.DoesNotContain(TareaInventario.Factura, PaginasDeAtras.MuevenExistencias);
        // Junto a «Recibir mercancía».
        Assert.Equal(TareaInventario.Factura, PaginasDeAtras.Tareas.SkipWhile(t => t.Id != TareaInventario.Recibir).Skip(1).First().Id);
    }

    [Fact]
    public async Task Con_el_teclado_se_carga_se_empareja_se_marca_y_el_encargado_la_recibe()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await PrepararAsync(servicio);
        var (pistolas, cartulina, marcador, caja12) = ($"FP{Guid.NewGuid():N}"[..14], $"FC{Guid.NewGuid():N}"[..14], $"FM{Guid.NewGuid():N}"[..14], $"FK{Guid.NewGuid():N}"[..14]);
        var enPistola = await ProductoAsync(c, "Pistola de silicón", pistolas);
        var enCartulina = await ProductoAsync(c, "Cartulina verde", cartulina);
        var enMarcador = await ProductoAsync(c, "Marcador negro", marcador, caja12);
        c.Abierto.Elegido = new ArchivoAbierto("PGF20431.xml", CfdiDePrueba.Factura(Guid.NewGuid().ToString(),
        [
            new(pistolas, "PISTOLA P/SILICON CHICA", 25, 40m),
            new("02080025", "CARTULINA FLUOR 66X48C VERDE", 50, 4m, Descuento: 20m),
            new("08350095", "MARCADOR P/PIZARRON NEG CJA/12", 1, 184.31m),
            new("08000087", "FLETE", 1, 125.01m, ClaveUnidad: "E48", ClaveProdServ: "78121502"),
        ]), @"E:\PGF20431.xml");

        // ── El cajero: al abrir, el cursor ya está en «Cargar»; Enter la carga ──
        await using (var p = await FacturasAsync(c, c.Cajero))
        {
            await CursorEnAsync(p, c, x => x == "#factura-cargar", "«Cargar el XML de la factura…»");
            await p.OprimirAsync(await p.PorIdAsync("factura-cargar"));
            await DiceAsync(p, "Se cargó «PGF20431.xml»: 3 renglones, 2 sin emparejar. Todavía no entra nada: revisa lo que llegó y recíbela.");
            Assert.Contains("«FLETE» es un servicio, no mercancía", PantallaSinNavegador.Limpio(await p.TextoAsync()));
            Assert.Equal("Por recibir", PantallaSinNavegador.Limpio((await p.ElementoAsync(e => e.Clase?.Contains("vn-etiq azul") == true, "el estado")).Texto));

            // El cursor, en el primer «Emparejar» (la cartulina); Enter, y al código: se escanea la cartulina.
            await CursorEnAsync(p, c, x => x.StartsWith("#factura-emparejar-", StringComparison.Ordinal), "el primer «Emparejar»");
            await p.OprimirAsync(await EnElCursorAsync(p, c));
            await CursorEnAsync(p, c, x => x == "#codigo-compra", "el código");
            await DiceAsync(p, "Escanea o busca (F10) el producto de aquí que es «CARTULINA FLUOR 66X48C VERDE» (clave 02080025).");
            await EscanearAsync(p, cartulina);
            await DiceAsync(p, "Cartulina verde");

            // Pasa solo al siguiente sin emparejar (el marcador): se escanea el código de su caja de 12.
            await CursorEnAsync(p, c, x => x.StartsWith("#factura-emparejar-", StringComparison.Ordinal), "el siguiente «Emparejar»");
            await p.OprimirAsync(await EnElCursorAsync(p, c));
            await CursorEnAsync(p, c, x => x == "#codigo-compra", "el código");
            await EscanearAsync(p, caja12);
            await DiceAsync(p, "Caja 12 (= 12");

            // Ya no falta nada: el cursor, en lo que llegó del marcador. De las pistolas llegaron 20; Enter pasa al siguiente.
            await CursorEnAsync(p, c, x => x.StartsWith("#factura-llego-", StringComparison.Ordinal), "un «Llegó»");
            var llegos = (await p.LeerAsync()).Elementos.Where(e => e.Id?.StartsWith("factura-llego-", StringComparison.Ordinal) == true).ToList();
            Assert.Equal(["25", "50", "1"], llegos.Select(e => e.Valor));
            await p.DispararAsync(llegos[0], "onchange", new ChangeEventArgs { Value = "veinte" });
            await DiceAsync(p, "no es una cantidad");
            await p.DispararAsync(await p.PorIdAsync(llegos[0].Id!), "onchange", new ChangeEventArgs { Value = "20" });
            await DiceAsync(p, "Lo que escribiste está guardado; nada entra hasta recibirla.");
            await p.TeclaAsync(llegos[0].Id!, "Enter");
            await CursorEnAsync(p, c, x => x == $"#{llegos[1].Id}", "el «Llegó» de la cartulina");

            // Con cambios, el cajero no la recibe: la deja marcada para revisión.
            Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "factura-recibir");
            await p.OprimirAsync(await p.PorIdAsync("factura-revision"));
            await DiceAsync(p, "Quedó marcada para revisión");
            Assert.Contains("En revisión", await p.TextoAsync());
            Assert.Equal((2m, 2m, 2m), (await ExistenciaAsync(c, enPistola), await ExistenciaAsync(c, enCartulina), await ExistenciaAsync(c, enMarcador)));
            Assert.Empty(p.Errores);
        }

        // ── El encargado: la abre de la lista, ve los cambios y la recibe así, preguntando antes ──
        await using (var p = await FacturasAsync(c, c.Encargado))
        {
            await DiceAsync(p, "PAPELERA DEL GOLFO");
            Assert.Contains("3 cambios", await p.TextoAsync());
            await p.OprimirAsync(await p.BotonAsync("Abrir"));
            await DiceAsync(p, "Cajero de prueba marcó cambios para revisión");
            // Él sí ve los costos de la factura.
            Assert.Contains("Total de la factura: $1,727.61", PantallaSinNavegador.Limpio(await p.TextoAsync()));
            Assert.Equal("Recibir con 3 cambios", PantallaSinNavegador.Limpio((await p.PorIdAsync("factura-recibir")).Texto));
            await p.OprimirAsync(await p.PorIdAsync("factura-recibir"));
            await DiceAsync(p, "¿Recibir con 3 cambios? Entra a la existencia de esta sucursal con el costo de la factura y ya no se cambia.");
            await CursorEnAsync(p, c, x => x == "#factura-si", "«Sí, recibir»");
            Assert.Equal(2m, await ExistenciaAsync(c, enPistola));
            await p.OprimirAsync(await p.PorIdAsync("factura-si"));
            await DiceAsync(p, "Recibida: la mercancía ya está en la existencia de esta sucursal, con el costo de la factura.");
            Assert.Equal((22m, 52m, 14m), (await ExistenciaAsync(c, enPistola), await ExistenciaAsync(c, enCartulina), await ExistenciaAsync(c, enMarcador)));
            Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id?.StartsWith("factura-llego-", StringComparison.Ordinal) == true);
            Assert.Empty(p.Errores);
        }
    }

    [Fact]
    public async Task Lo_que_no_esta_se_da_de_alta_o_como_variante_sin_perder_la_lista()
    {
        // 2026-11-28 (el dueño): desde la factura se da de alta un producto nuevo o una variante de uno que ya existe y, al terminar, se
        // vuelve a la lista como estaba, con ese renglón ya emparejado.
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await PrepararAsync(servicio);
        var (pistolas, tijeras) = ($"FP{Guid.NewGuid():N}"[..14], $"FT{Guid.NewGuid():N}"[..14]);
        var enPistola = await ProductoAsync(c, "Pistola de silicón", pistolas);
        var enTijera = await ProductoAsync(c, "Tijera de oficina", tijeras);
        var departamento = (await c.Cliente.OpcionesCatalogoAsync()).Departamentos[0].Id;
        c.Abierto.Elegido = new ArchivoAbierto("PGF20600.xml", CfdiDePrueba.Factura(Guid.NewGuid().ToString(),
        [
            new(pistolas, "PISTOLA P/SILICON CHICA", 25, 40m),
            new("02720010", "PEGAMENTO BLANCO 1KG FIX", 6, 46.25m, Unidad: "PIEZA"),
            new("07430010", "TIJERA OFICINA SOFT AZUL", 10, 27.80m),
        ], folio: "20600"), @"E:\PGF20600.xml");

        var (p, inventario) = await ConInventarioAsync(c, c.Encargado);
        await using var _ = p;
        await p.OprimirAsync(await p.PorIdAsync("factura-cargar"));
        await DiceAsync(p, "3 renglones, 2 sin emparejar");
        // Algo escrito antes de irse: de las pistolas llegaron 20.
        var llegos = (await p.LeerAsync()).Elementos.Where(e => e.Id?.StartsWith("factura-llego-", StringComparison.Ordinal) == true).ToList();
        await p.DispararAsync(llegos[0], "onchange", new ChangeEventArgs { Value = "20" });
        await DiceAsync(p, "Lo que escribiste está guardado");

        // ── «Nuevo producto»: la ficha de Productos aquí mismo, con el nombre y el costo de la factura y sin «¿Cuántas hay?» ──
        await p.OprimirAsync(await p.BotonAsync("Nuevo producto"));
        await p.EsperarElementoAsync("ficha-nombre");
        await DiceAsync(p, "Nuevo producto para «PEGAMENTO BLANCO 1KG FIX» (clave 02720010)");
        Assert.Equal("Pegamento blanco 1kg fix", (await p.PorIdAsync("ficha-nombre")).Valor);
        // $46.25 + IVA = $53.65 cada uno.
        Assert.Equal("53.65", (await p.PorIdAsync("ficha-costo")).Valor);
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "ficha-hay");
        Assert.Equal("Volver a la factura", PantallaSinNavegador.Limpio((await p.BotonAsync("Volver a la factura")).Texto));
        await p.DispararAsync(await p.PorIdAsync("ficha-departamento"), "onchange", new ChangeEventArgs { Value = departamento.ToString() });
        await p.DispararAsync(await p.PorIdAsync("ficha-precio"), "onchange", new ChangeEventArgs { Value = "75" });
        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        await DiceAsync(p, "Se dio de alta «Pegamento blanco 1kg fix» y quedó emparejado con «PEGAMENTO BLANCO 1KG FIX». La factura sigue como la dejaste.");
        // La lista como estaba: lo escrito sigue ahí, y el renglón ya está emparejado.
        llegos = (await p.LeerAsync()).Elementos.Where(e => e.Id?.StartsWith("factura-llego-", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(["20", "6", "10"], llegos.Select(e => e.Valor));
        Assert.Contains("Pegamento blanco 1kg fix", await p.TextoAsync());
        // El cursor, al siguiente que falta emparejar.
        await CursorEnAsync(p, c, x => x.StartsWith("#factura-emparejar-", StringComparison.Ordinal), "el siguiente «Emparejar»");

        // ── «Variante de…»: se escanea el producto, su ficha abre con la variante nueva ya precargada; Esc ahí regresa a la factura ──
        await p.OprimirAsync(await p.BotonAsync("Variante de…"));
        await CursorEnAsync(p, c, x => x == "#codigo-compra", "el código");
        await DiceAsync(p, "Escanea o busca (F10) el producto al que «TIJERA OFICINA SOFT AZUL» se le agrega como variante");
        await EscanearAsync(p, tijeras);
        await p.EsperarElementoAsync("var-nombre-1");
        Assert.Equal("Tijera oficina soft azul", (await p.PorIdAsync("var-nombre-1")).Valor);
        // Sin tocarla (solo lo precargado), Esc regresa a la factura, con el cursor en «Variante de…».
        Assert.True(await p.Dispatcher.InvokeAsync(inventario.CerrarAsync));
        await p.EsperarElementoAsync("factura-recibir");
        await CursorEnAsync(p, c, x => x.StartsWith("#factura-variante-", StringComparison.Ordinal), "«Variante de…»");
        await p.OprimirAsync(await EnElCursorAsync(p, c));
        await EscanearAsync(p, tijeras);
        await p.EsperarElementoAsync("var-nombre-1");
        // Con algo escrito sin guardar, Esc lo dice y se queda en la ficha.
        await p.DispararAsync(await p.PorIdAsync("var-nombre-1"), "onchange", new ChangeEventArgs { Value = "Azul" });
        Assert.True(await p.Dispatcher.InvokeAsync(inventario.CerrarAsync));
        await DiceAsync(p, "Hay cambios sin guardar en la ficha");
        // Con dos variantes, cada una lleva nombre (la regla de Productos): la que ya estaba también.
        await p.DispararAsync(await p.PorIdAsync("var-nombre-0"), "onchange", new ChangeEventArgs { Value = "Negra" });
        await p.OprimirAsync(await p.PorIdAsync("ficha-guardar"));
        await DiceAsync(p, "Se agregó la variante «Tijera de oficina Azul» y quedó emparejada con «TIJERA OFICINA SOFT AZUL».");
        Assert.Equal(["20", "6", "10"], (await p.LeerAsync()).Elementos.Where(e => e.Id?.StartsWith("factura-llego-", StringComparison.Ordinal) == true).Select(e => e.Valor));

        // Esc en la factura regresa a la lista de facturas (no cierra la tarea); con todo emparejado se recibe.
        Assert.Equal("Recibir con 3 cambios", PantallaSinNavegador.Limpio((await p.PorIdAsync("factura-recibir")).Texto));
        await p.OprimirAsync(await p.PorIdAsync("factura-recibir"));
        await p.OprimirAsync(await p.PorIdAsync("factura-si"));
        await DiceAsync(p, "Recibida: la mercancía ya está en la existencia de esta sucursal");
        Assert.Equal(22m, await ExistenciaAsync(c, enPistola));
        await using (var db = servicio.Base())
        {
            var pegamento = await db.Variantes.AsNoTracking().SingleAsync(v => v.Producto!.Nombre == "Pegamento blanco 1kg fix");
            Assert.Equal(6m, await ExistenciaAsync(c, pegamento.Id));
            var azul = await db.Variantes.AsNoTracking().SingleAsync(v => v.Descripcion == "Azul" && v.Producto!.Variantes.Any(x => x.Id == enTijera));
            Assert.Equal(10m, await ExistenciaAsync(c, azul.Id));
        }
        Assert.True(await p.Dispatcher.InvokeAsync(inventario.CerrarAsync));
        await p.EsperarElementoAsync("factura-cargar");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Un_pdf_o_algo_que_no_es_factura_se_dice_y_no_se_carga()
    {
        var c = await PrepararAsync(matriz);
        await using var p = await FacturasAsync(c, c.Cajero);
        c.Abierto.Elegido = new ArchivoAbierto("factura.pdf", DocumentoProveedorTests.Pdf((700, [(50, "Factura")])), @"E:\factura.pdf");
        await p.OprimirAsync(await p.PorIdAsync("factura-cargar"));
        await DiceAsync(p, RegistroFacturas.SoloElXml);
        // Y si se cancela «Abrir», nada.
        c.Abierto.Elegido = null;
        await p.OprimirAsync(await p.PorIdAsync("factura-cargar"));
        await DiceAsync(p, "No se cargó nada: se canceló.");
        Assert.Equal(".xml", c.Abierto.Pedidos[^1].Extension);
        Assert.Empty(p.Errores);
    }

    [Theory]
    [InlineData("12", 12_000L)]
    [InlineData("0", 0L)]
    [InlineData("1.5", null)]
    [InlineData("-1", null)]
    [InlineData("", null)]
    public void En_lo_que_llego_el_cero_vale_y_las_piezas_van_enteras(string texto, long? milesimas)
    {
        Assert.Equal(milesimas, FacturasEnPantalla.Llego(texto, UnidadBase.Pieza, "Lápiz", out var problema));
        Assert.Equal(milesimas is null, problema is not null);
        Assert.Equal(1500, FacturasEnPantalla.Llego("1.5", UnidadBase.Metro, "Listón", out _));
    }
}
