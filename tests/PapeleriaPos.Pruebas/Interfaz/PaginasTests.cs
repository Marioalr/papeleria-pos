using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Herramientas.Prueba;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Productos, Inventario y Clientes ya no son ventanas encima de la venta: son páginas con lista y detalle (entrega 8, como
/// el prototipo). Sin navegador, contra el servicio con la base de <c>preparar-prueba</c> (los clientes con deuda y lo del
/// crédito por revisar): Productos abre llena, los filtros y los datos según los permisos, Inventario por tareas con lo
/// pendiente, el detalle de Clientes, y que F1 regresa a la venta como estaba.
/// </summary>
public sealed class PaginasTests(ServicioEnPrueba prueba) : IClassFixture<ServicioEnPrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

    private async Task<Caja> PrepararAsync()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5180/") };
        var cliente = new ClienteCaja(new HttpClient(prueba.Fabrica.Server.CreateHandler()), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("encargado", "2222"),
            await cliente.EntrarAsync("dueno", "3333"));
    }

    /// <summary>Deja abierto el turno de quien vende (las pruebas de esta clase comparten la base y no corren en orden fijo).</summary>
    private static async Task<TurnoDto> TurnoDeAsync(Caja c, SesionDto quien)
    {
        if (await c.Cliente.TurnoAsync(c.Datos.Id) is { } abierto)
        {
            if (abierto.CajeroId == quien.UsuarioId)
                return abierto;
            await c.Cliente.CorteZAsync(abierto.Id, new SolicitudCorteZ(c.Encargado.UsuarioId, 50000));
        }
        return await c.Cliente.AbrirTurnoAsync(c.Datos.Id, quien.UsuarioId, 50000);
    }

    private static async Task<(PantallaSinNavegador Pantalla, PantallaVenta Venta)> VentaAsync(Caja c, SesionDto quien)
    {
        var turno = await TurnoDeAsync(c, quien);
        var p = new PantallaSinNavegador(c.Servicios);
        var venta = await p.AbrirAsync<PantallaVenta>(new()
        {
            [nameof(PantallaVenta.Caja)] = c.Datos,
            [nameof(PantallaVenta.Sesion)] = quien,
            [nameof(PantallaVenta.Turno)] = turno,
        });
        await p.EsperarAsync(t => t.Contains("Copia B/N"), "los rápidos");
        return (p, venta);
    }

    private static Task TeclaAsync(PantallaSinNavegador p, PantallaVenta venta, string tecla) => p.Dispatcher.InvokeAsync(() => venta.Tecla(tecla, null));

    /// <summary>Lo que dicen los botones de la clase dada, en orden y sin espacios de más.</summary>
    private static async Task<List<string>> BotonesAsync(PantallaSinNavegador p, string clase) =>
        (await p.LeerAsync()).Elementos.Where(e => e.Etiqueta == "button" && e.Clase?.Split(' ').Contains(clase) == true)
            .Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();

    private static async Task<string> TituloAsync(PantallaSinNavegador p) =>
        PantallaSinNavegador.Limpio((await p.ElementoAsync(e => e.Etiqueta == "h2", "un título")).Texto);

    /// <summary>El nombre de un renglón de la lista (su primer texto en negritas).</summary>
    private static async Task<string> NombreDelRenglonAsync(PantallaSinNavegador p, string id)
    {
        var elementos = (await p.LeerAsync()).Elementos;
        return PantallaSinNavegador.Limpio(elementos.Skip(elementos.FindIndex(e => e.Id == id) + 1).First(e => e.Etiqueta == "strong").Texto);
    }

    /// <summary>Espera a que el detalle sea el de lo que se buscó (el de antes se queda mientras llega el nuevo).</summary>
    private static async Task EsperarDetalleAsync(PantallaSinNavegador p, string contiene)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!((await p.LeerAsync()).Elementos.FirstOrDefault(e => e.Etiqueta == "h2") is { } titulo
                 && titulo.Texto.Contains(contiene, StringComparison.OrdinalIgnoreCase)
                 && !(await p.TextoAsync()).Contains("Cargando…")))
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"El detalle no llegó a «{contiene}»:\n{await p.TextoAsync()}");
            await Task.Delay(20);
        }
    }

    /// <summary>¿Se ve la venta? En una página se oculta, como estaba.</summary>
    private static async Task<bool> VentaALaVistaAsync(PantallaSinNavegador p) =>
        (await p.ElementoAsync(e => e.Clase?.Split(' ').Contains("vn-cuerpo") == true, "el cuerpo de la venta")).Clase?.Contains("oculto") == false;

    [Fact]
    public async Task Productos_abre_con_la_lista_llena_el_primero_en_el_detalle_y_sin_el_margen_para_quien_no_lo_ve()
    {
        var c = await PrepararAsync();
        var (p, venta) = await VentaAsync(c, c.Encargado);
        await using var _ = p;

        await TeclaAsync(p, venta, "F3");
        var primero = await p.EsperarElementoAsync("prod-0");
        await p.EsperarAsync(t => t.Contains("Últimos movimientos") || t.Contains("Es un servicio"), "el detalle del primero");

        Assert.False(await VentaALaVistaAsync(p));
        var texto = await p.TextoAsync();
        // Llena desde que abre, lo más vendido primero y diciendo cuántos hay (antes abría vacía hasta buscar).
        Assert.Matches(@"Lo más vendido en 90 días primero · 50 de [\d,]+\.", texto);
        Assert.Contains("on", primero.Clase);
        Assert.Equal(await NombreDelRenglonAsync(p, "prod-0"), await TituloAsync(p));
        // Los filtros del prototipo; «Margen bajo», solo con «Ver costos, utilidad y margen» (el encargado no lo tiene).
        var filtros = await BotonesAsync(p, "vn-filtro");
        Assert.Equal("Todos", filtros[0]);
        Assert.StartsWith("Por completar", filtros[1]);
        Assert.Equal("Poca existencia", filtros[2]);
        Assert.DoesNotContain("Margen bajo", filtros);
        // Sus datos: el costo para quien captura las compras; el margen no, y lo dice.
        var (_, elementos) = await p.LeerAsync();
        var datos = elementos.Where(e => e.Clase?.StartsWith("vn-dato", StringComparison.Ordinal) == true)
            .Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        Assert.Contains(datos, d => d.StartsWith("Precio al público", StringComparison.Ordinal));
        Assert.Contains(datos, d => d.StartsWith("Existencia", StringComparison.Ordinal));
        Assert.Contains(datos, d => d.StartsWith("Costo promedio", StringComparison.Ordinal));
        Assert.DoesNotContain(datos, d => d.StartsWith("Margen", StringComparison.Ordinal));
        Assert.Contains("El margen y la utilidad los ve quien tiene el permiso «Ver costos, utilidad y margen».", texto);
        Assert.NotNull(await p.BotonAsync("Editar"));
        Assert.Contains(await BotonesAsync(p, "vn-boton"), b => b == "Nuevo producto");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_productos_la_busqueda_las_flechas_enter_y_esc()
    {
        var c = await PrepararAsync();
        var (p, venta) = await VentaAsync(c, c.Encargado);
        await using var _ = p;
        await TeclaAsync(p, venta, "F3");
        await p.EsperarElementoAsync("prod-0");

        // Lo que se escribe busca; Enter busca ya y deja elegido el primero.
        await p.EscribirAsync("busca-prod", "cuaderno");
        await p.TeclaAsync("busca-prod", "Enter");
        await p.EsperarAsync(t => t.Contains("El código exacto y lo que empieza así primero"), "la búsqueda");
        await EsperarDetalleAsync(p, "cuaderno");
        // ↓ elige el siguiente (el detalle cambia con él) y Enter abre su ficha, ahí mismo.
        var segundo = await NombreDelRenglonAsync(p, "prod-1");
        await p.TeclaAsync("busca-prod", "ArrowDown");
        Assert.Contains("on", (await p.PorIdAsync("prod-1")).Clase);
        await EsperarDetalleAsync(p, segundo);
        await p.TeclaAsync("busca-prod", "Enter");
        await p.EsperarAsync(t => t.Contains("Presentaciones y códigos"), "la ficha del producto");
        Assert.StartsWith("Editar «", await TituloAsync(p));
        Assert.NotNull(await p.BotonAsync("Guardar"));
        // Con la guía de la prueba encima, Esc no toca la página de abajo, que no se ve: la ficha sigue abierta.
        var modulo = c.Servicios.GetRequiredService<ModuloCaja>();
        await modulo.VentanaDeLaFranjaAsync(true);
        await TeclaAsync(p, venta, "Escape");
        Assert.Contains("Presentaciones y códigos", await p.TextoAsync());
        await modulo.VentanaDeLaFranjaAsync(false);
        // Esc cierra la ficha y regresa al producto; otro Esc, sin nada abierto, regresa a la venta, como en el prototipo.
        await TeclaAsync(p, venta, "Escape");
        await p.EsperarAsync(t => !t.Contains("Presentaciones y códigos"), "el producto otra vez");
        Assert.False(await VentaALaVistaAsync(p));
        await TeclaAsync(p, venta, "Escape");
        Assert.True(await VentaALaVistaAsync(p));
        Assert.DoesNotContain("Nombre, marca o código", (await p.LeerAsync()).Elementos.Select(e => e.Atributos.GetValueOrDefault("placeholder")?.ToString()));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Quien_ve_la_utilidad_tiene_el_margen_bajo_y_el_margen_del_producto()
    {
        var c = await PrepararAsync();
        var (p, venta) = await VentaAsync(c, c.Dueno);
        await using var _ = p;

        await TeclaAsync(p, venta, "F3");
        await p.EsperarElementoAsync("prod-0");
        await p.EscribirAsync("busca-prod", "cuaderno");
        await p.TeclaAsync("busca-prod", "Enter");
        await EsperarDetalleAsync(p, "cuaderno");

        Assert.Contains("Margen bajo", await BotonesAsync(p, "vn-filtro"));
        var margen = (await p.LeerAsync()).Elementos.Single(e => e.Clase == "vn-dato restringido");
        Assert.StartsWith("Margen", PantallaSinNavegador.Limpio(margen.Texto));
        Assert.Contains("utilidad $", margen.Texto);
        Assert.DoesNotContain("los ve quien tiene el permiso", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Etiqueta_lo_agrega_a_las_etiquetas_por_imprimir_del_inventario()
    {
        var c = await PrepararAsync();
        var (p, venta) = await VentaAsync(c, c.Encargado);
        await using var _ = p;
        await TeclaAsync(p, venta, "F3");
        await p.EsperarElementoAsync("prod-0");
        await p.EscribirAsync("busca-prod", "cuaderno");
        await p.TeclaAsync("busca-prod", "Enter");
        await EsperarDetalleAsync(p, "cuaderno");
        var producto = await TituloAsync(p);

        await p.OprimirAsync(await p.BotonAsync("Etiqueta"));
        // Con el nombre con que sale en la etiqueta (el del producto con su variante).
        Assert.Matches($"«{System.Text.RegularExpressions.Regex.Escape(producto)}[^»]*» va en las etiquetas por imprimir \\(1\\)\\.", await p.TextoAsync());
        await p.OprimirAsync(await p.BotonAsync("Ir a Etiquetas"));

        // A Inventario › Etiquetas, con la lista que dejó Productos.
        await p.EsperarAsync(t => t.Contains("← Inventario") && t.Contains("Qué imprimir"), "las etiquetas");
        Assert.Equal("Etiquetas", await TituloAsync(p));
        Assert.Contains(producto, await p.TextoAsync());
        await TeclaAsync(p, venta, "Escape");
        await p.EsperarAsync(t => t.Contains("Solo las tareas que tienes"), "las tarjetas");
        Assert.Contains((await BotonesAsync(p, "vn-tarea")), t => t.StartsWith("Etiquetas", StringComparison.Ordinal) && t.Contains("1 por imprimir"));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Inventario_por_tareas_solo_las_de_quien_entro_con_su_limite_y_quien_autoriza()
    {
        var c = await PrepararAsync();
        var (p, venta) = await VentaAsync(c, c.Cajero);
        await using var _ = p;

        await TeclaAsync(p, venta, "F4");
        await p.EsperarAsync(t => t.Contains("por contar") || t.Contains("contado"), "lo pendiente del conteo del día");

        var tareas = await BotonesAsync(p, "vn-tarea");
        // El cajero: lo de los permisos de defecto («Agregar inventario», como recibir). Las demás no aparecen.
        Assert.Equal(["Recibir mercancía", "Agregar inventario", "Conteo del día", "Mermas", "Ajustes", "Abrir un paquete", "Proveedores"],
            tareas.Select(t => PaginasDeAtras.Tareas.First(x => t.StartsWith(x.Nombre, StringComparison.Ordinal)).Nombre));
        Assert.Contains("tú: hasta $200.00", tareas.Single(t => t.StartsWith("Mermas", StringComparison.Ordinal)));
        Assert.Contains("lo autoriza Encargado de prueba o Dueño de prueba", tareas.Single(t => t.StartsWith("Ajustes", StringComparison.Ordinal)));
        Assert.Contains("solo consulta", tareas.Single(t => t.StartsWith("Proveedores", StringComparison.Ordinal)));
        Assert.Matches(@"\d+ por contar|contado", tareas.Single(t => t.StartsWith("Conteo del día", StringComparison.Ordinal)));

        // Cada tarjeta abre su pantalla de siempre, aquí mismo, con «← Inventario»; Esc regresa a las tarjetas y luego a vender.
        await p.OprimirAsync(await p.ElementoAsync(e => e.Id == "tarea-Mermas", "la tarjeta de mermas"));
        await p.EsperarAsync(t => t.Contains("Escanea o busca el producto."), "la merma");
        Assert.Equal("Mermas", await TituloAsync(p));
        Assert.NotNull(await p.BotonAsync("← Inventario"));
        await TeclaAsync(p, venta, "Escape");
        Assert.Contains("Solo las tareas que tienes", await p.TextoAsync());
        await TeclaAsync(p, venta, "Escape");
        Assert.True(await VentaALaVistaAsync(p));
        Assert.Empty(p.Errores);
    }

    /// <summary>«Agregar inventario» (como en eleventa, 2026-10-24): su tarjeta abre su página aquí mismo y Esc regresa, como las demás.</summary>
    [Fact]
    public async Task Agregar_inventario_abre_con_su_tarjeta_y_Esc_regresa_a_las_tareas()
    {
        var c = await PrepararAsync();
        var (p, venta) = await VentaAsync(c, c.Cajero);
        await using var _ = p;

        await TeclaAsync(p, venta, "F4");
        await p.EsperarElementoAsync("tarea-Agregar");
        await p.OprimirAsync(await p.PorIdAsync("tarea-Agregar"));
        await p.EsperarAsync(t => t.Contains("Lo agregado hoy"), "agregar inventario");
        Assert.Equal("Agregar inventario", await TituloAsync(p));
        Assert.NotNull(await p.PorIdAsync("codigo-compra"));

        await TeclaAsync(p, venta, "Escape");
        Assert.Contains("Solo las tareas que tienes", await p.TextoAsync());
        await TeclaAsync(p, venta, "Escape");
        Assert.True(await VentaALaVistaAsync(p));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task El_encargado_ve_las_compras_las_ordenes_y_lo_que_se_esta_recibiendo()
    {
        var c = await PrepararAsync();
        var proveedor = (await c.Cliente.ProveedoresAsync()).First();
        await c.Cliente.AbrirCompraAsync(new SolicitudAbrirCompra(c.Cajero.UsuarioId, c.Datos.SucursalId, proveedor.Id!.Value, null));
        var (p, venta) = await VentaAsync(c, c.Encargado);
        await using var _ = p;

        await TeclaAsync(p, venta, "F4");
        await p.EsperarAsync(t => t.Contains("contando"), "lo que se está recibiendo");

        var tareas = await BotonesAsync(p, "vn-tarea");
        // Todas menos lo que vale el inventario: es costo, y el encargado no tiene «Ver costos, utilidad y margen».
        Assert.Equal(PaginasDeAtras.Tareas.Where(t => t.Id != TareaInventario.Valor).Select(t => t.Nombre),
            tareas.Select(t => PaginasDeAtras.Tareas.First(x => t.StartsWith(x.Nombre, StringComparison.Ordinal)).Nombre));
        Assert.Matches(@"\d+ contando", tareas.Single(t => t.StartsWith("Recibir mercancía", StringComparison.Ordinal)));
        Assert.DoesNotContain("solo consulta", tareas.Single(t => t.StartsWith("Proveedores", StringComparison.Ordinal)));
        // «Recibir mercancía» abre ya en elegir el proveedor.
        await p.OprimirAsync(await p.ElementoAsync(e => e.Id == "tarea-Recibir", "la tarjeta de recibir"));
        await p.EsperarAsync(t => t.Contains("Empezar a contar"), "elegir el proveedor");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Clientes_el_detalle_con_su_cuenta_y_lo_que_puede_quien_entro()
    {
        var c = await PrepararAsync();
        var (p, venta) = await VentaAsync(c, c.Cajero);
        await using var _ = p;

        await p.OprimirAsync(await p.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Clientes", "la pestaña Clientes"));
        await p.EsperarElementoAsync("cli-0");
        Assert.False(await VentaALaVistaAsync(p));
        // «Solo los que deben»: el de la deuda, con lo que debe en rojo porque ya venció.
        await p.OprimirAsync(await p.BotonAsync("Solo los que deben"));
        var conDeuda = (await p.LeerAsync()).Elementos.First(e => e.Id?.StartsWith("cli-", StringComparison.Ordinal) == true
            && e.Texto.Contains(PreparacionPrueba.ConDeuda));
        Assert.Contains((await p.LeerAsync()).Elementos, e => e.Clase == "num rojo" && e.Texto == "$350.00");
        await p.OprimirAsync(conDeuda);
        // Su cuenta, y quién cambia su límite (se pide después).
        await p.EsperarAsync(t => t.Contains("venció el") && t.Contains("lo cambia Dueño de prueba"), "su cuenta");

        Assert.Equal(PreparacionPrueba.ConDeuda, await TituloAsync(p));
        var datos = (await p.LeerAsync()).Elementos.Where(e => e.Clase == "vn-dato").Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        Assert.Contains(datos, d => d.StartsWith("Debe $350.00", StringComparison.Ordinal) && d.Contains("vencido $350.00"));
        Assert.Contains(datos, d => d.StartsWith("Límite de crédito", StringComparison.Ordinal) && d.Contains("lo cambia Dueño de prueba"));
        Assert.Contains(datos, d => d.StartsWith("Mayoreo y descuento", StringComparison.Ordinal));
        Assert.Contains(datos, d => d.StartsWith("Compra al mes", StringComparison.Ordinal));
        // El cajero los consulta: sin «Editar», sin «Límite y plazo» ni «Nuevo cliente»; su cuenta sí (recibe abonos).
        var botones = await BotonesAsync(p, "vn-boton");
        Assert.Contains("Estado de cuenta", botones);
        Assert.Contains("Recibir abono", botones);
        Assert.DoesNotContain("Editar", botones);
        Assert.DoesNotContain("Límite y plazo", botones);
        Assert.DoesNotContain("Nuevo cliente", botones);
        Assert.Contains("Movimientos de la cuenta", await p.TextoAsync());
        // «Estado de cuenta» se abre ahí mismo y Esc lo cierra; otro Esc regresa a la venta.
        await p.OprimirAsync(await p.BotonAsync("Estado de cuenta"));
        await p.EsperarAsync(t => t.Contains("Copiar para WhatsApp"), "el estado de cuenta");
        await TeclaAsync(p, venta, "Escape");
        Assert.DoesNotContain("Copiar para WhatsApp", await p.TextoAsync());
        await TeclaAsync(p, venta, "Escape");
        Assert.True(await VentaALaVistaAsync(p));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Quien_administra_los_clientes_los_edita_ahi_mismo_y_quien_da_credito_cambia_el_limite()
    {
        var c = await PrepararAsync();
        var (p, venta) = await VentaAsync(c, c.Dueno);
        await using var _ = p;

        await p.OprimirAsync(await p.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Clientes", "la pestaña Clientes"));
        await p.EsperarElementoAsync("cli-0");
        await p.EscribirAsync("busca-cli", PreparacionPrueba.ConDeuda);
        await p.TeclaAsync("busca-cli", "Enter");
        await p.EsperarAsync(t => t.Contains("venció el") && t.Contains("Límite y plazo"), "su cuenta");

        var botones = await BotonesAsync(p, "vn-boton");
        Assert.Contains("Editar", botones);
        Assert.Contains("Límite y plazo", botones);
        Assert.Contains("Nuevo cliente", botones);
        Assert.Contains("puedes cambiarlo", await p.TextoAsync());
        // «Por revisar»: lo del crédito sin conexión de este cliente, en su detalle. La página lo pide aparte de la lista, así
        // que puede llegar después de su cuenta (pasó en el CI de Windows): se espera.
        await p.EsperarAsync(t => t.Contains("Del crédito sin conexión, por revisar"), "lo del crédito sin conexión por revisar");
        // Editar, en el mismo lado; Esc regresa sin guardar.
        await p.OprimirAsync(await p.BotonAsync("Editar"));
        Assert.Equal($"Editar «{PreparacionPrueba.ConDeuda}»", await TituloAsync(p));
        Assert.NotNull(await p.BotonAsync("Guardar"));
        await TeclaAsync(p, venta, "Escape");
        Assert.Equal(PreparacionPrueba.ConDeuda, await TituloAsync(p));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task F1_regresa_a_la_venta_con_el_mismo_ticket_su_espera_y_su_panel()
    {
        var c = await PrepararAsync();
        var (p, venta) = await VentaAsync(c, c.Cajero);
        await using var _ = p;
        // Un ticket en espera y otro con dos copias, y el verificador abierto en el panel.
        await TeclaAsync(p, venta, "Alt+2");
        await TeclaAsync(p, venta, "F6");
        await TeclaAsync(p, venta, "Alt+1");
        await TeclaAsync(p, venta, "Alt+1");
        await TeclaAsync(p, venta, "F9");
        await p.EsperarAsync(t => t.Contains("Verificador de precios"), "el verificador");
        // Con el panel abierto no se ve el total: el ticket, sus importes y la espera.
        async Task<(string Importes, int Fichas, string Cantidades)> LaVentaAsync()
        {
            var (_, elementos) = await p.LeerAsync();
            return (string.Join(",", elementos.Where(e => e.Clase?.Contains("vn-importe") == true).Select(e => PantallaSinNavegador.Limpio(e.Texto))),
                elementos.Count(e => e.Clase == "vn-ficha"),
                string.Join(",", elementos.Where(e => e.Clase == "vn-paso").Select(e => PantallaSinNavegador.Limpio(e.Texto))));
        }
        var antes = await LaVentaAsync();
        Assert.Equal(1, antes.Fichas);
        Assert.Equal("− 2 +", antes.Cantidades);
        Assert.NotEmpty(antes.Importes);

        // A Inventario (F4) y a Clientes: la venta se oculta, no se deja.
        await TeclaAsync(p, venta, "F4");
        Assert.False(await VentaALaVistaAsync(p));
        Assert.Contains("Solo las tareas que tienes", await p.TextoAsync());
        await p.OprimirAsync(await p.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Clientes", "la pestaña Clientes"));
        await p.EsperarElementoAsync("cli-0");
        // F12 o F2 son de la venta: aquí no hacen nada.
        await TeclaAsync(p, venta, "F12");
        await TeclaAsync(p, venta, "F2");
        Assert.False(await VentaALaVistaAsync(p));

        // F1: la misma venta, como estaba: el ticket, su espera y el panel abierto.
        await TeclaAsync(p, venta, "F1");
        Assert.True(await VentaALaVistaAsync(p));
        Assert.Equal(antes, await LaVentaAsync());
        Assert.Contains("Verificador de precios", await p.TextoAsync());
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "busca-cli");
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// La barra de arriba (2026-10-25: a 1366 la del dueño no cabía y cortaba el total). Lo que caja.css necesita para que quepa:
    /// los chips juntos en .vn-chips, lo único que se encoge, entre las pestañas y quien vende; lo que dice cada chip, completo en
    /// su aria-label (caja.css acorta o quita su texto si no cabe; «No imprimirlo» no se encoge); y las pestañas con su tecla en
    /// la página (caja.css solo la oculta cuando la barra se llena: F1, F3 y F4 siguen sirviendo). Igual en una página, sin la
    /// temporada.
    /// </summary>
    [Fact]
    public async Task La_barra_de_arriba_junta_los_chips_y_su_texto_completo_queda_en_aria_label()
    {
        var c = await PrepararAsync();
        await using var p = (await VentaAsync(c, c.Dueno)).Pantalla;
        await p.EsperarAsync(t => t.Contains("Configuración"), "las pestañas del dueño");
        // «Impresiones por cobrar: 4» y «Todos Santos (Ctrl+M)» dicen lo mismo que el chip.
        static string SinSignos(string? texto) => PantallaSinNavegador.Limpio(Regex.Replace(texto ?? "", "[:()]", ""));

        static bool EsChip(ElementoDibujado e) => e.Clase?.Split(' ').Contains("vn-chip-estado") == true;

        async Task RevisarAsync(string donde)
        {
            // El chip de la conexión sale cuando la caja ya sabe dónde está el servicio (lo pregunta en segundo plano): a veces,
            // justo al entrar, solo se ve el de la impresora (así falló en el CI de Linux y de Windows, 2026-10-28 y 29).
            var limite = DateTime.UtcNow.AddSeconds(20);
            while ((await p.LeerAsync()).Elementos.Count(EsChip) < 2 && DateTime.UtcNow < limite)
                await Task.Delay(20);
            var elementos = (await p.LeerAsync()).Elementos;
            var pestanas = elementos.FindIndex(e => e.Clase == "vn-pestanas");
            var juntos = elementos.FindIndex(e => e.Clase == "vn-chips");
            var usuario = elementos.FindIndex(e => e.Clase == "vn-usuario-caja");
            Assert.True(pestanas >= 0 && pestanas < juntos && juntos < usuario, $"{donde}: la barra no tiene sus pestañas, sus chips y su usuario");
            Assert.Equal(["Venta F1", "Productos F3", "Inventario F4", "Clientes", "Corte", "Reportes", "Configuración"],
                elementos.Take(juntos).Skip(pestanas).Where(e => e.Etiqueta == "button").Select(e => PantallaSinNavegador.Limpio(e.Texto)));
            var chips = Enumerable.Range(0, usuario).Where(i => EsChip(elementos[i])).ToList();
            // Por lo menos la conexión y la impresora; todos dentro de .vn-chips.
            Assert.True(chips.Count >= 2, $"{donde}: {chips.Count} chips: "
                + string.Join(" | ", chips.Select(i => $"«{PantallaSinNavegador.Limpio(elementos[i].Texto)}» ({elementos[i].Clase})"))
                + $"\nLa barra: {PantallaSinNavegador.Limpio(string.Concat(elementos.Take(usuario).Skip(juntos).Select(e => e.Texto + " ")))}");
            Assert.All(chips, i => Assert.True(i > juntos, $"{donde}: «{PantallaSinNavegador.Limpio(elementos[i].Texto)}» fuera de .vn-chips"));
            foreach (var chip in chips.Select(i => elementos[i]).Where(e => PantallaSinNavegador.Limpio(e.Texto) != "No imprimirlo"))
                Assert.Equal(SinSignos(chip.Texto), SinSignos(chip.Atributos.GetValueOrDefault("aria-label")?.ToString()));
        }

        await RevisarAsync("en la venta");
        await p.OprimirAsync(await p.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Reportes", "la pestaña Reportes"));
        await p.EsperarElementoAsync("rep-kpi-venta");
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Clase?.Contains("vn-chip-temporada", StringComparison.Ordinal) == true);
        await RevisarAsync("en Reportes");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Sin_cortar_van_las_mismas_paginas_con_su_barra()
    {
        var c = await PrepararAsync();
        await using var p = new PantallaSinNavegador(c.Servicios);
        var administracion = await p.AbrirAsync<Administracion>(new()
        {
            [nameof(Administracion.Caja)] = c.Datos,
            [nameof(Administracion.Sesion)] = c.Encargado,
        });

        // Empieza en Inventario, con sus tarjetas; F3, Productos con la lista llena; sin «Volver a la venta».
        await p.EsperarAsync(t => t.Contains("Solo las tareas que tienes"), "las tarjetas");
        Assert.DoesNotContain("Volver a la venta", await p.TextoAsync());
        await p.Dispatcher.InvokeAsync(() => administracion.Tecla("F3", null));
        await p.EsperarElementoAsync("prod-0");
        Assert.Matches(@"Lo más vendido en 90 días primero · 50 de [\d,]+\.", await p.TextoAsync());
        Assert.NotNull(await p.BotonAsync("Ctrl+U"));
        Assert.Empty(p.Errores);
    }
}
