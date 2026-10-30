using ClosedXML.Excel;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Productos (F3) › «Productos en Excel», sin navegador y contra el servicio con datos ficticios: exportar la lista con sus filtros
/// (con el «Guardar como» de mentiras) e importarla ya cambiada, eligiendo el archivo con el «Abrir» de mentiras (la ventana de
/// Windows) o con el selector del navegador de mentiras; primero se ve qué cambia (lo que conviene revisar primero, también un precio
/// que sube a más del doble), qué es nuevo y qué se ignora, y Ctrl+Enter (o «Aplicar») lo aplica todo junto; Esc cierra la vista y
/// luego regresa al producto. Quien no cambia precios solo exporta; en el piloto con eleventa no se importa y lo dice.
/// </summary>
public sealed class ExcelEnProductosTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(
        ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, PaginaQueContesta Pagina, GuardadoDePrueba Guardado, AbiertoDePrueba? Abierto,
        SesionDto Encargado, SesionDto Dueno);

    /// <summary>La caja contra ese servicio; con <paramref name="conAbrir"/>, la ventana de Windows (su «Abrir»); sin él, la del navegador.</summary>
    private static async Task<Caja> PrepararAsync(WebApplicationFactory<Program> fabrica, bool conAbrir = true)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        var cliente = new ClienteCaja(new HttpClient(fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var (pagina, guardado, abierto) = (new PaginaQueContesta(), new GuardadoDePrueba(), conAbrir ? new AbiertoDePrueba() : null);
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IGuardarArchivo>(guardado)
            .AddSingleton<IJSRuntime>(pagina)
            .AddScoped<ModuloCaja>();
        if (abierto is not null)
            servicios.AddSingleton<IAbrirArchivo>(abierto);
        return new Caja(cliente, caja, servicios.BuildServiceProvider(), pagina, guardado, abierto, await cliente.EntrarAsync("encargado", "2222"),
            await cliente.EntrarAsync("dueno", "3333"));
    }

    private static string Ficha() => Guid.NewGuid().ToString("N")[..8];

    private static string Codigo() => $"75{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";

    private static async Task<(Guid Id, string Nombre)> DepartamentoAsync(Caja c)
    {
        var nombre = $"Excel en pantalla {Ficha()}";
        return ((await c.Cliente.DarDeAltaDepartamentoAsync(new SolicitudDepartamento(c.Encargado.UsuarioId, c.Datos.SucursalId, nombre)))
            .Departamentos.Single(d => d.Nombre == nombre).Id, nombre);
    }

    private static Task<FichaProductoDto> ProductoAsync(Caja c, string nombre, Guid departamento, string codigo, long menudeo) =>
        c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, departamento, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])],
                    [new PrecioFichaDto(null, 1000, menudeo)]),
            ])));

    /// <summary>
    /// El Excel de ese departamento (como lo exporta la caja), con lo que se le cambie, listo para elegirlo: como lo deja Excel al
    /// guardarlo.
    /// </summary>
    private static async Task<ArchivoAbierto> ExcelAsync(Caja c, Guid departamento, Action<IXLWorksheet, int> cambiar, string nombre = "Productos cambiados.xlsx")
    {
        var exportado = await c.Cliente.ExcelProductosAsync(c.Datos.SucursalId, c.Encargado.UsuarioId, "", FiltroProductos.Todos, departamento, false);
        using var libro = new XLWorkbook(new MemoryStream(exportado.Datos));
        var hoja = libro.Worksheet(ProductosEnExcel.HojaProductos);
        var titulos = hoja.RowsUsed().First(r => r.Cell(1).GetString() == "Código").RowNumber();
        cambiar(hoja, titulos);
        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        return new ArchivoAbierto(nombre, flujo.ToArray(), $@"C:\Users\caja\Documents\{nombre}");
    }

    /// <summary>El precio de la fila con ese código.</summary>
    private static void Precio(IXLWorksheet hoja, int titulos, string codigo, double precio)
    {
        var columna = hoja.Row(titulos).CellsUsed().First(x => x.GetString() == "Precio").Address.ColumnNumber;
        hoja.RowsUsed().First(r => r.RowNumber() > titulos && r.Cell(1).GetString() == codigo).Cell(columna).Value = precio;
    }

    /// <summary>La página de Productos de quien entró, como la abre F3, con lo que se tenía en ella.</summary>
    private static async Task<(PantallaSinNavegador Pantalla, Productos Productos)> ProductosAsync(Caja c, SesionDto quien, ProductosVistos estado,
        PermisosDePagina? acceso = null)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        var productos = await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = quien,
            [nameof(Productos.Acceso)] = acceso ?? new PermisosDePagina(quien, null),
            [nameof(Productos.Estado)] = estado,
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarElementoAsync("prod-0");
        return (p, productos);
    }

    /// <summary>«Productos en Excel», abajo de la lista: se abre a la derecha.</summary>
    private static async Task ExcelEnProductosAsync(PantallaSinNavegador p)
    {
        await p.OprimirAsync(await p.PorIdAsync("ver-excel"));
        await p.EsperarElementoAsync("excel-exportar");
    }

    private static Task CtrlEnterAsync(PantallaSinNavegador p) =>
        p.ElementoAsync(e => e.Id == "excel-productos", "la vista de Productos en Excel")
            .ContinueWith(t => p.DispararAsync(t.Result, "onkeydown", new KeyboardEventArgs { Key = "Enter", CtrlKey = true })).Unwrap();

    private static string Limpio(string texto) => PantallaSinNavegador.Limpio(texto);

    private static async Task<string> TextoDeAsync(PantallaSinNavegador p, string id) => Limpio((await p.PorIdAsync(id)).Texto);

    [Fact]
    public async Task Exporta_la_lista_como_se_ve_y_dice_donde_quedo()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var (departamento, nombre) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var codigo = Codigo();
        await ProductoAsync(c, $"Cuaderno {ficha}", departamento, codigo, 2500);
        await ProductoAsync(c, $"Lápiz {ficha}", departamento, Codigo(), 650);
        var (p, _) = await ProductosAsync(c, c.Encargado, new ProductosVistos { Texto = $"cuaderno {ficha}", Departamento = departamento });
        await using var _ = p;

        await ExcelEnProductosAsync(p);

        // Dice qué se exporta: lo de la lista, con sus filtros; el encargado no ve costos.
        Assert.Contains($"El producto de la lista: los que coinciden con «cuaderno {ficha}» y de «{nombre}». Una fila por variante con sus códigos, su "
            + "precio y su mayoreo, su existencia, su mínimo y su máximo; sus cajas y paquetes, en otra hoja.", Limpio(await p.TextoAsync()));
        // El cursor, en «Exportar a Excel».
        Assert.Contains(c.Pagina.Llamadas, l => l.Funcion == "enfocar" && (string?)l.Argumentos![0] == "#excel-exportar");
        // El botón de importar, para quien cambia precios: con «Abrir» de Windows.
        Assert.Equal("Importar de Excel…", await TextoDeAsync(p, "excel-elegir"));

        await p.OprimirAsync(await p.PorIdAsync("excel-exportar"));
        await p.EsperarElementoAsync("excel-exportar-estado");

        var hoy = DateOnly.FromDateTime(PapeleriaPos.Nucleo.HoraMexico.ALocal(DateTime.UtcNow));
        var archivo = Assert.Single(c.Guardado.Guardados);
        Assert.Equal($"Productos {nombre} {hoy:yyyy-MM-dd}.xlsx", archivo.Nombre);
        Assert.Equal($@"Se guardó en {GuardadoDePrueba.Carpeta}\{archivo.Nombre}.", await TextoDeAsync(p, "excel-exportar-estado"));
        using var libro = new XLWorkbook(new MemoryStream(archivo.Datos));
        var hoja = libro.Worksheet(ProductosEnExcel.HojaProductos);
        Assert.Equal([codigo], hoja.Column(1).CellsUsed().Select(x => x.GetString()).Where(x => x.StartsWith("75", StringComparison.Ordinal)));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Con_el_abrir_de_windows_primero_se_ve_que_cambia_y_ctrl_enter_lo_aplica()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (cuaderno, lapiz, nuevo) = (Codigo(), Codigo(), Codigo());
        await ProductoAsync(c, $"Cuaderno {ficha}", departamento, cuaderno, 2500);
        await ProductoAsync(c, $"Lápiz {ficha}", departamento, lapiz, 650);
        c.Abierto!.Elegido = await ExcelAsync(c, departamento, (hoja, titulos) =>
        {
            Precio(hoja, titulos, cuaderno, 27.5);
            Precio(hoja, titulos, lapiz, 0);
            var fila = hoja.LastRowUsed()!.RowNumber() + 1;
            hoja.Cell(fila, 1).Value = nuevo;
            hoja.Cell(fila, 3).Value = $"Compás {ficha}";
            hoja.Cell(fila, 5).Value = hoja.Cell(titulos + 1, 5).GetString();
            hoja.Cell(fila, 8).Value = 45;
        });
        var estado = new ProductosVistos { Departamento = departamento };
        var (p, productos) = await ProductosAsync(c, c.Encargado, estado);
        await using var _ = p;
        await ExcelEnProductosAsync(p);

        await p.OprimirAsync(await p.PorIdAsync("excel-elegir"));
        await p.EsperarElementoAsync("excel-resumen");

        // «Abrir» de Windows pidió un Excel.
        Assert.Equal(new ArchivoPorAbrir("Elegir el Excel de los productos", ".xlsx", ProductosEnExcel.BytesMaximos), c.Abierto.Pedidos.Single());
        // Qué cambia, qué es nuevo y qué se ignora (y por qué); la exportación se esconde mientras.
        Assert.Equal("«Productos cambiados.xlsx»: cambia 1 · 1 nuevo · se ignora 1.", await TextoDeAsync(p, "excel-resumen"));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "excel-exportar");
        var cambian = await TextoDeAsync(p, "excel-cambian");
        Assert.Contains($"Cuaderno {ficha}", cambian);
        Assert.Contains("Precio: $25.00 → $27.50", cambian);
        Assert.Contains($"Compás {ficha}", await TextoDeAsync(p, "excel-nuevos"));
        Assert.Contains("«Precio» dice «0»: el precio va mayor que cero.", await TextoDeAsync(p, "excel-ignorados"));
        Assert.Equal("Aplicar: cambiar 1 producto y dar de alta 1 Ctrl+Enter", await TextoDeAsync(p, "excel-aplicar"));
        // El cursor, en la vista: un Enter de más no aplica; Ctrl+Enter, sí.
        Assert.Equal("#excel-productos", (string?)c.Pagina.Llamadas.Last(l => l.Funcion == "enfocar").Argumentos![0]);
        await p.DispararAsync(await p.PorIdAsync("excel-productos"), "onkeydown", new KeyboardEventArgs { Key = "Enter" });
        Assert.Equal(2500, (await c.Cliente.ListaProductosAsync(c.Datos.SucursalId, c.Encargado.UsuarioId, cuaderno, FiltroProductos.Todos, null, false, 5))
            .Productos.Single().PrecioCentavos);

        await CtrlEnterAsync(p);
        await p.EsperarAsync(t => t.Contains("Listo:", StringComparison.Ordinal), "lo aplicado");

        Assert.Equal("Listo: cambió 1 producto y se dio de alta 1 producto nuevo. Las cajas ya los tienen y quedó en la bitácora.",
            await TextoDeAsync(p, "excel-aviso"));
        // La lista ya trae el precio nuevo y el producto nuevo.
        await p.EsperarAsync(t => t.Contains("$27.50", StringComparison.Ordinal) && t.Contains($"Compás {ficha}", StringComparison.Ordinal), "la lista al día");
        // Vuelve a ofrecer exportar, y el cursor a «Importar de Excel…».
        await p.EsperarElementoAsync("excel-exportar");
        Assert.Equal("#excel-elegir", (string?)c.Pagina.Llamadas.Last(l => l.Funcion == "enfocar").Argumentos![0]);

        // Esc regresa al producto.
        Assert.True(await p.Dispatcher.InvokeAsync(productos.CerrarAsync));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "excel-productos");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Esc_cierra_la_vista_sin_aplicar_y_cancelar_el_abrir_no_importa_nada()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var (departamento, _) = await DepartamentoAsync(c);
        var codigo = Codigo();
        await ProductoAsync(c, $"Regla {Ficha()}", departamento, codigo, 1500);
        var (p, productos) = await ProductosAsync(c, c.Encargado, new ProductosVistos { Departamento = departamento });
        await using var _ = p;
        await ExcelEnProductosAsync(p);

        // Cancelar «Abrir»: no se importa nada.
        await p.OprimirAsync(await p.PorIdAsync("excel-elegir"));
        Assert.Equal("No se importó nada: se canceló.", await TextoDeAsync(p, "excel-aviso"));

        // Un archivo que no cambia nada: se dice, sin «Aplicar».
        c.Abierto!.Elegido = await ExcelAsync(c, departamento, (_, _) => { }, "Igual.xlsx");
        await p.OprimirAsync(await p.PorIdAsync("excel-elegir"));
        await p.EsperarElementoAsync("excel-resumen");
        Assert.Equal("«Igual.xlsx»: 1 sin cambios: no hay nada que aplicar.", await TextoDeAsync(p, "excel-resumen"));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "excel-aplicar");
        Assert.Equal("Cerrar Esc", await TextoDeAsync(p, "excel-cerrar"));

        // Otro que sí cambia: Esc cierra la vista (sin aplicar) y otro Esc regresa al producto.
        c.Abierto.Elegido = await ExcelAsync(c, departamento, (hoja, titulos) => Precio(hoja, titulos, codigo, 16));
        await p.OprimirAsync(await p.PorIdAsync("excel-elegir"));
        await p.EsperarElementoAsync("excel-aplicar");
        Assert.True(await p.Dispatcher.InvokeAsync(productos.CerrarAsync));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "excel-resumen");
        Assert.NotNull(await p.PorIdAsync("excel-exportar"));
        Assert.Equal(1500, (await c.Cliente.ListaProductosAsync(c.Datos.SucursalId, c.Encargado.UsuarioId, codigo, FiltroProductos.Todos, null, false, 5))
            .Productos.Single().PrecioCentavos);
        Assert.True(await p.Dispatcher.InvokeAsync(productos.CerrarAsync));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "excel-productos");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Si_algo_cambio_desde_la_vista_la_vuelve_a_ver_con_el_aviso()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var (departamento, _) = await DepartamentoAsync(c);
        var codigo = Codigo();
        var producto = await ProductoAsync(c, $"Escuadra {Ficha()}", departamento, codigo, 3000);
        c.Abierto!.Elegido = await ExcelAsync(c, departamento, (hoja, titulos) => Precio(hoja, titulos, codigo, 32));
        var (p, _) = await ProductosAsync(c, c.Encargado, new ProductosVistos { Departamento = departamento });
        await using var _ = p;
        await ExcelEnProductosAsync(p);
        await p.OprimirAsync(await p.PorIdAsync("excel-elegir"));
        await p.EsperarElementoAsync("excel-aplicar");
        Assert.Contains("Precio: $30.00 → $32.00", await TextoDeAsync(p, "excel-cambian"));

        // Mientras, alguien le cambió el precio en su ficha.
        await c.Cliente.CambiarMenudeoAsync(new SolicitudPrecioMenudeo(c.Encargado.UsuarioId, c.Datos.SucursalId, producto.Variantes[0].Id!.Value, 3100));
        await p.OprimirAsync(await p.PorIdAsync("excel-aplicar"));
        await p.EsperarAsync(t => t.Contains(ProductosEnExcel.CambioDesdeLaVista, StringComparison.Ordinal), "el aviso");

        // La vista otra vez, con lo de ahora; nada se aplicó. Como cambió después de exportar el Excel, va aparte: lo que cambió y lo que
        // haría el Excel, sin «Aplicar».
        await p.EsperarElementoAsync("excel-cambiaron-despues");
        Assert.Equal(ProductosEnExcel.CambioDesdeLaVista, await TextoDeAsync(p, "excel-error"));
        var aparte = await TextoDeAsync(p, "excel-cambiaron-despues");
        Assert.Contains("Precio: $30.00 → $31.00", aparte);
        Assert.Contains("Precio: $31.00 → $32.00", aparte);
        Assert.Equal("«Productos cambiados.xlsx»: 1 cambió después de exportar: no hay nada que aplicar.", await TextoDeAsync(p, "excel-resumen"));
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "excel-aplicar");
        Assert.Equal(3100, (await c.Cliente.ListaProductosAsync(c.Datos.SucursalId, c.Encargado.UsuarioId, codigo, FiltroProductos.Todos, null, false, 5))
            .Productos.Single().PrecioCentavos);

        // «Aplicarlos de todos modos»: va en lo que cambia, primero y con lo que cambió después; «No aplicarlos» lo regresa.
        await p.OprimirAsync(await p.PorIdAsync("excel-de-todos-modos"));
        await p.EsperarElementoAsync("excel-de-todos-modos-aviso");
        Assert.Contains("Cambió después de exportar: Precio: $30.00 → $31.00", await TextoDeAsync(p, "excel-cambian"));
        Assert.Equal("Aplicar: cambiar 1 producto Ctrl+Enter", await TextoDeAsync(p, "excel-aplicar"));
        await p.OprimirAsync(await p.PorIdAsync("excel-sin-los-cambiados"));
        await p.EsperarElementoAsync("excel-cambiaron-despues");
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "excel-aplicar");
        await p.OprimirAsync(await p.PorIdAsync("excel-de-todos-modos"));
        await p.EsperarElementoAsync("excel-aplicar");
        await CtrlEnterAsync(p);
        await p.EsperarAsync(t => t.Contains("Listo:", StringComparison.Ordinal), "lo aplicado");
        Assert.Equal(3200, (await c.Cliente.ListaProductosAsync(c.Datos.SucursalId, c.Encargado.UsuarioId, codigo, FiltroProductos.Todos, null, false, 5))
            .Productos.Single().PrecioCentavos);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_el_navegador_se_elige_con_su_selector_de_archivos()
    {
        var c = await PrepararAsync(servicio.Fabrica, conAbrir: false);
        var (departamento, _) = await DepartamentoAsync(c);
        var codigo = Codigo();
        await ProductoAsync(c, $"Transportador {Ficha()}", departamento, codigo, 1200);
        var archivo = await ExcelAsync(c, departamento, (hoja, titulos) => Precio(hoja, titulos, codigo, 13), "Desde el navegador.xlsx");
        var p = new PantallaSinNavegador(c.Servicios);
        await using var _ = p;
        var excel = await p.AbrirAsync<ExcelEnProductos>(new()
        {
            [nameof(ExcelEnProductos.Caja)] = c.Datos,
            [nameof(ExcelEnProductos.Sesion)] = c.Encargado,
            [nameof(ExcelEnProductos.Acceso)] = new PermisosDePagina(c.Encargado, null),
            [nameof(ExcelEnProductos.Estado)] = new ProductosVistos { Departamento = departamento },
            [nameof(ExcelEnProductos.EnLaLista)] = 1,
        });

        // Sin «Abrir» de Windows: el botón es el selector de archivos del navegador, que solo acepta un Excel.
        var elegir = await p.PorIdAsync("excel-elegir");
        Assert.Equal("label", elegir.Etiqueta);
        Assert.Contains((await p.LeerAsync()).Elementos, e => e.Etiqueta == "input" && (string?)e.Atributos.GetValueOrDefault("type") == "file"
            && (string?)e.Atributos.GetValueOrDefault("accept") == ".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        // Lo que hace el selector al elegir: avisa a la pantalla por su OnChange (y la pantalla se vuelve a dibujar).
        Task ElegirAsync(ArchivoDelNavegadorDePrueba elegido) => p.Dispatcher.InvokeAsync(() =>
            EventCallback.Factory.Create<InputFileChangeEventArgs>(excel, excel.ElegidoEnElNavegadorAsync).InvokeAsync(new InputFileChangeEventArgs([elegido])));

        // Uno que pesa demasiado: lo dice sin mandarlo.
        await ElegirAsync(new ArchivoDelNavegadorDePrueba("Enorme.xlsx", new byte[ProductosEnExcel.BytesMaximos + 1024 * 1024]));
        Assert.StartsWith("«Enorme.xlsx» pesa 9 MB: se importan archivos de hasta 8 MB.", await TextoDeAsync(p, "excel-error"));

        await ElegirAsync(new ArchivoDelNavegadorDePrueba(archivo.Nombre, archivo.Datos));
        await p.EsperarElementoAsync("excel-aplicar");
        Assert.Equal("«Desde el navegador.xlsx»: cambia 1.", await TextoDeAsync(p, "excel-resumen"));
        await p.OprimirAsync(await p.PorIdAsync("excel-aplicar"));
        await p.EsperarAsync(t => t.Contains("Listo: cambió 1 producto.", StringComparison.Ordinal), "lo aplicado");
        Assert.Equal(1300, (await c.Cliente.ListaProductosAsync(c.Datos.SucursalId, c.Encargado.UsuarioId, codigo, FiltroProductos.Todos, null, false, 5))
            .Productos.Single().PrecioCentavos);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Quien_no_cambia_precios_solo_exporta_y_en_el_piloto_no_se_importa()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var (departamento, _) = await DepartamentoAsync(c);
        await ProductoAsync(c, $"Compás {Ficha()}", departamento, Codigo(), 4500);
        // «Productos, precios y etiquetas» con autorización: ve Productos (y exporta), pero no importa.
        var conAutorizacion = new PermisosDePagina(c.Encargado, new Dictionary<AccionControlada, PermisoDeSesion>
        {
            [AccionControlada.EditarCatalogo] = new(AccionControlada.EditarCatalogo, NivelPermiso.RequiereAutorizacion, null, false, []),
        });
        var (p, _) = await ProductosAsync(c, c.Encargado, new ProductosVistos { Departamento = departamento }, conAutorizacion);
        await using (p)
        {
            await ExcelEnProductosAsync(p);
            Assert.Contains("Importa quien tiene el permiso «Productos, precios y etiquetas»: puede cambiar los precios de muchos a la vez.",
                Limpio(await p.TextoAsync()));
            Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "excel-elegir");
            await p.OprimirAsync(await p.PorIdAsync("excel-exportar"));
            await p.EsperarElementoAsync("excel-exportar-estado");
            Assert.Single(c.Guardado.Guardados);
        }

        // En el piloto con eleventa: lo dice, y no se ofrece; exportar, sí.
        var piloto = await PrepararAsync(servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true")));
        var (enPiloto, _) = await ProductosAsync(piloto, piloto.Encargado, new ProductosVistos { Departamento = departamento });
        await using (enPiloto)
        {
            await ExcelEnProductosAsync(enPiloto);
            await enPiloto.EsperarElementoAsync("excel-piloto");
            Assert.Equal(ProductosEnExcel.ImportarEnElPiloto, await TextoDeAsync(enPiloto, "excel-piloto"));
            Assert.DoesNotContain((await enPiloto.LeerAsync()).Elementos, e => e.Id == "excel-elegir");
            await enPiloto.OprimirAsync(await enPiloto.PorIdAsync("excel-exportar"));
            await enPiloto.EsperarElementoAsync("excel-exportar-estado");
            Assert.StartsWith("Se guardó en", await TextoDeAsync(enPiloto, "excel-exportar-estado"));
            Assert.Empty(enPiloto.Errores);
        }
    }

    [Fact]
    public async Task Las_listas_largas_se_ven_por_paginas_y_lo_que_conviene_revisar_va_primero()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        await using (var db = servicio.Base())
        {
            for (var i = 0; i < 60; i++)
            {
                var producto = new PapeleriaPos.Nucleo.Catalogo.Producto { Nombre = $"Hoja {ficha} {i:00}", DepartamentoId = departamento };
                var variante = new PapeleriaPos.Nucleo.Catalogo.Variante { ProductoId = producto.Id };
                var pieza = new PapeleriaPos.Nucleo.Catalogo.Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true };
                db.AddRange(producto, variante, pieza, new PapeleriaPos.Nucleo.Catalogo.CodigoBarras { Codigo = $"H{ficha}{i:00}", PresentacionId = pieza.Id },
                    new PapeleriaPos.Nucleo.Catalogo.Precio { VarianteId = variante.Id, PrecioCentavos = 1000 });
            }
            await db.SaveChangesAsync();
        }
        // Todos suben, menos el último, que baja.
        c.Abierto!.Elegido = await ExcelAsync(c, departamento, (hoja, titulos) =>
        {
            for (var i = 0; i < 60; i++)
                Precio(hoja, titulos, $"H{ficha}{i:00}", i == 59 ? 9 : 11);
        });
        var (p, _) = await ProductosAsync(c, c.Encargado, new ProductosVistos { Departamento = departamento });
        await using var _ = p;
        await ExcelEnProductosAsync(p);
        await p.OprimirAsync(await p.PorIdAsync("excel-elegir"));
        await p.EsperarElementoAsync("excel-resumen");

        // Los 60 cambian; se ven de 50 en 50, y el que baja va primero, resaltado.
        Assert.Equal("«Productos cambiados.xlsx»: cambian 60.", await TextoDeAsync(p, "excel-resumen"));
        Assert.Equal("1–50 de 60", await TextoDeAsync(p, "excel-cambian-pagina"));
        var resaltado = Assert.Single((await p.LeerAsync()).Elementos, e => e.Etiqueta == "tr" && e.Clase == "vn-excel-ojo");
        Assert.StartsWith($"Hoja {ficha} 59", Limpio(resaltado.Texto));
        Assert.Contains("Precio: $10.00 → $9.00 baja", Limpio(resaltado.Texto));
        Assert.Contains($"Hoja {ficha} 48", await TextoDeAsync(p, "excel-cambian"));
        Assert.DoesNotContain($"Hoja {ficha} 49", await TextoDeAsync(p, "excel-cambian"));
        Assert.True((await p.PorIdAsync("excel-cambian-anteriores")).Desactivado);

        await p.OprimirAsync(await p.PorIdAsync("excel-cambian-siguientes"));
        Assert.Equal("51–60 de 60", await TextoDeAsync(p, "excel-cambian-pagina"));
        Assert.Contains($"Hoja {ficha} 58", await TextoDeAsync(p, "excel-cambian"));
        Assert.DoesNotContain($"Hoja {ficha} 48", await TextoDeAsync(p, "excel-cambian"));
        Assert.True((await p.PorIdAsync("excel-cambian-siguientes")).Desactivado);
        await p.OprimirAsync(await p.PorIdAsync("excel-cambian-anteriores"));
        Assert.Equal("1–50 de 60", await TextoDeAsync(p, "excel-cambian-pagina"));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Un_precio_que_sube_a_mas_del_doble_va_primero_resaltado_con_lo_que_paso_y_se_aplica_igual()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var (agenda, regla) = (Codigo(), Codigo());
        await ProductoAsync(c, $"Agenda {ficha}", departamento, agenda, 6500);
        await ProductoAsync(c, $"Regla {ficha}", departamento, regla, 1250);
        // La agenda sube como siempre; en la regla se escribió «12,50», que Excel con la configuración de México guarda como 1250.
        c.Abierto!.Elegido = await ExcelAsync(c, departamento, (hoja, titulos) =>
        {
            Precio(hoja, titulos, agenda, 70);
            Precio(hoja, titulos, regla, 1250);
        });
        var (p, _) = await ProductosAsync(c, c.Encargado, new ProductosVistos { Departamento = departamento });
        await using var _ = p;
        await ExcelEnProductosAsync(p);
        await p.OprimirAsync(await p.PorIdAsync("excel-elegir"));
        await p.EsperarElementoAsync("excel-resumen");

        Assert.Equal("«Productos cambiados.xlsx»: cambian 2.", await TextoDeAsync(p, "excel-resumen"));
        Assert.Contains("Primero, resaltado, lo que conviene revisar: precios que bajan, que suben a más del doble o que regresan a como estaban,",
            Limpio(await p.TextoAsync()));
        // La regla va primero (en el archivo va después) y resaltada como lo demás que conviene revisar: el precio con «sube a más del
        // doble» y, debajo, lo que casi siempre pasó y qué hacer. La agenda, después y sin resaltar.
        var elementos = (await p.LeerAsync()).Elementos;
        var renglones = elementos.Where(e => e.Etiqueta == "tr" && e.Texto.Contains(ficha, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, renglones.Count);
        Assert.Equal(("vn-excel-ojo", null), (renglones[0].Clase, renglones[1].Clase));
        Assert.StartsWith($"Regla {ficha}", Limpio(renglones[0].Texto));
        Assert.StartsWith($"Agenda {ficha}", Limpio(renglones[1].Texto));
        Assert.Equal("Precio: $12.50 → $1,250.00 sube a más del doble", Limpio(Assert.Single(elementos, e => e.Etiqueta == "li" && e.Clase == "ojo").Texto));
        Assert.Equal("vn-etiq ambar", Assert.Single(elementos, e => e.Etiqueta == "span" && Limpio(e.Texto) == "sube a más del doble").Clase);
        var aviso = Assert.Single(elementos, e => e.Etiqueta == "small" && e.Clase == "vn-excel-aviso");
        Assert.Equal(ProductosEnExcel.AvisoSubeMucho, Limpio(aviso.Texto));
        Assert.Contains(ProductosEnExcel.AvisoSubeMucho, Limpio(renglones[0].Texto));
        Assert.Contains("Precio: $65.00 → $70.00", Limpio(renglones[1].Texto));

        // Se aplica con el mismo botón, todo junto: el resaltado es para que se vea, no detiene nada.
        Assert.Equal("Aplicar: cambiar 2 productos Ctrl+Enter", await TextoDeAsync(p, "excel-aplicar"));
        await p.OprimirAsync(await p.PorIdAsync("excel-aplicar"));
        await p.EsperarAsync(t => t.Contains("Listo:", StringComparison.Ordinal), "lo aplicado");
        Assert.Equal("Listo: cambiaron 2 productos. Las cajas ya los tienen y quedó en la bitácora.", await TextoDeAsync(p, "excel-aviso"));
        Assert.Equal(125_000, (await c.Cliente.ListaProductosAsync(c.Datos.SucursalId, c.Encargado.UsuarioId, regla, FiltroProductos.Todos, null, false, 5))
            .Productos.Single().PrecioCentavos);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Lo_que_cambio_una_importacion_se_pide_en_excel_desde_la_bitacora()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var (departamento, _) = await DepartamentoAsync(c);
        var ficha = Ficha();
        var codigo = Codigo();
        await ProductoAsync(c, $"Cartulina {ficha}", departamento, codigo, 800);
        var archivo = await ExcelAsync(c, departamento, (hoja, titulos) => Precio(hoja, titulos, codigo, 9), $"Cartulinas {ficha}.xlsx");
        var solicitud = new SolicitudImportarProductos(c.Encargado.UsuarioId, c.Datos.SucursalId, archivo.Nombre, Convert.ToBase64String(archivo.Datos));
        var vista = await c.Cliente.VistaImportacionAsync(solicitud);
        var hecho = await c.Cliente.ImportarProductosAsync(solicitud with { Huella = vista.Huella, Id = Guid.NewGuid() });

        var p = new PantallaSinNavegador(c.Servicios);
        await using var _ = p;
        await p.AbrirAsync<ConsultaBitacora>(new()
        {
            [nameof(ConsultaBitacora.Caja)] = c.Datos,
            [nameof(ConsultaBitacora.Sesion)] = c.Dueno,
        });
        await p.EsperarAsync(t => t.Contains(hecho.Bitacora, StringComparison.Ordinal), "el renglón de la importación");

        // Su renglón trae «Lo que cambió (Excel)»: cada producto con lo de antes y lo que quedó.
        var renglon = await p.ElementoAsync(e => e.Etiqueta == "td" && Limpio(e.Texto).StartsWith(hecho.Bitacora, StringComparison.Ordinal), "el detalle");
        Assert.EndsWith("Lo que cambió (Excel)", Limpio(renglon.Texto));
        await p.OprimirAsync(await p.BotonAsync("Lo que cambió (Excel)"));
        await p.EsperarAsync(t => t.Contains("Se guardó en", StringComparison.Ordinal), "el Excel guardado");
        var guardado = Assert.Single(c.Guardado.Guardados);
        Assert.StartsWith("Importación de Excel ", guardado.Nombre);
        using var libro = new XLWorkbook(new MemoryStream(guardado.Datos));
        Assert.Contains(libro.Worksheet("Lo que cambió").RowsUsed(), r => r.Cell(1).GetString() == $"Cartulina {ficha}" && r.Cell(4).GetString() == "Precio"
            && r.Cell(5).GetString() == "$8.00" && r.Cell(6).GetString() == "$9.00");
        Assert.Empty(p.Errores);
    }
}
