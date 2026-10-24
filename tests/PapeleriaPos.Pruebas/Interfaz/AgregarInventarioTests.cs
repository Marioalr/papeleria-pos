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
/// Inventario › «Agregar inventario» (como en eleventa, 2026-10-24), sin navegador: la tarjeta junto a «Recibir mercancía»; por
/// teclado, el código y Enter, cuántos llegaron y Enter, y queda listo para el siguiente con el cursor en el código; lo que hay
/// se ve; el costo, ya puesto con el de ahora, solo para quien ve los costos; lo agregado hoy abajo; y lo que no es una cantidad
/// se dice antes de mandarlo.
/// </summary>
public sealed class AgregarInventarioTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, PaginaQueContesta Pagina, SesionDto Cajero, SesionDto Encargado);

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
        return new Caja(cliente, caja, servicios, pagina, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("encargado", "2222"));
    }

    /// <summary>
    /// Un producto nuevo por pieza con su código, y <paramref name="existencia"/> piezas a $10 como inventario inicial. Con
    /// <paramref name="codigoDeLaCaja"/>, también su caja de 12 con ese código.
    /// </summary>
    private async Task<(Guid Variante, string Codigo)> ProductoAsync(Caja c, string nombre, int existencia, string? codigoDeLaCaja = null)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        var codigo = $"AG{Guid.NewGuid():N}"[..14];
        List<PresentacionFichaDto> presentaciones = [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])];
        if (codigoDeLaCaja is not null)
            presentaciones.Add(new PresentacionFichaDto(null, "Caja 12", 12_000, false, [new CodigoFichaDto(codigoDeLaCaja)]));
        var ficha = await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, opciones.Departamentos[0].Id, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true, presentaciones, [new PrecioFichaDto(null, 1000, 2000)]),
            ])));
        if (existencia > 0)
        {
            await using var db = servicio.Base();
            var variante = await db.Variantes.SingleAsync(v => v.Id == ficha.Variantes[0].Id);
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(c.Datos.SucursalId, variante, TipoMovimiento.InventarioInicial,
                Cantidad.Piezas(existencia), existencia * 1000L, DateTime.UtcNow, documentoTipo: OrigenDelMovimiento.ImportacionEleventa));
        }
        // El código se busca en el catálogo en memoria del servicio, que se pone al día en segundo plano.
        await servicio.CatalogoAlDiaAsync();
        return (ficha.Variantes[0].Id!.Value, codigo);
    }

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

    /// <summary>Escanea un código en el cuadro del código: lo escribe y Enter.</summary>
    private static async Task EscanearAsync(PantallaSinNavegador p, string codigo)
    {
        await p.EscribirAsync("codigo-compra", codigo);
        await p.TeclaAsync("codigo-compra", "Enter");
    }

    /// <summary>A dónde mandó el cursor la última vez.</summary>
    private static string? UltimoFoco(Caja c) =>
        c.Pagina.Llamadas.LastOrDefault(l => l.Funcion == "enfocar").Argumentos?.FirstOrDefault()?.ToString();

    /// <summary>Espera a que la página mande el cursor ahí (lo manda al terminar de dibujar).</summary>
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

    private static DateOnly HoyEnMexico => DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));

    [Fact]
    public async Task La_tarjeta_va_junto_a_recibir_y_abre_la_pagina_con_el_cursor_en_el_codigo()
    {
        var c = await PrepararAsync();

        await using var p = await InventarioAsync(c, c.Cajero);
        await p.EsperarElementoAsync("tarea-Agregar");
        var tarjetas = (await p.LeerAsync()).Elementos.Where(e => e.Id?.StartsWith("tarea-", StringComparison.Ordinal) == true).Select(e => e.Id).ToList();
        Assert.Equal(["tarea-Recibir", "tarea-Agregar"], tarjetas.Take(2));
        Assert.Contains("Lo que llegó de un producto: cuántos y su costo, sin proveedor ni nota.",
            PantallaSinNavegador.Limpio((await p.PorIdAsync("tarea-Agregar")).Texto));

        await p.OprimirAsync(await p.PorIdAsync("tarea-Agregar"));
        await p.EsperarElementoAsync("codigo-compra");
        await p.EsperarAsync(t => t.Contains("Lo agregado hoy"), "lo agregado hoy");
        Assert.Contains("Agregar inventario", await p.TextoAsync());
        Assert.Contains("Lo agregado no se deshace: si te equivocaste, corrígelo con un ajuste (Inventario › Ajustes), como en eleventa.", await p.TextoAsync());
        await FocoEnAsync(c, "#codigo-compra");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Por_teclado_codigo_cuantos_y_Enter_y_queda_listo_para_el_siguiente()
    {
        var c = await PrepararAsync();
        var (variante, codigo) = await ProductoAsync(c, "Agregar por teclado de prueba", 6);
        var (otraVariante, otroCodigo) = await ProductoAsync(c, "Agregar el siguiente de prueba", 1);
        var dia = HoyEnMexico;
        await using var p = await InventarioAsync(c, c.Cajero, TareaInventario.Agregar);
        await p.EsperarElementoAsync("codigo-compra");

        await EscanearAsync(p, codigo);
        await p.EsperarElementoAsync("agregar-cantidad");
        // El producto y lo que hay (no es a ciegas), y el cursor en «Cuántos llegaron». El cajero no ve el costo.
        Assert.Equal("Agregar por teclado de prueba", PantallaSinNavegador.Limpio((await p.PorIdAsync("agregar-producto")).Texto));
        Assert.Equal("6", PantallaSinNavegador.Limpio((await p.PorIdAsync("agregar-hay")).Texto));
        Assert.Contains(codigo, await p.TextoAsync());
        await FocoEnAsync(c, "#agregar-cantidad");
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "agregar-costo");
        Assert.Contains("Entra al costo de ahora", await p.TextoAsync());

        await p.EscribirAsync("agregar-cantidad", "24");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: +24 «Agregar por teclado de prueba». Ahora hay 30."), "lo agregado");

        // Listo para el siguiente: sin producto, con el cursor otra vez en el código.
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "agregar-cantidad");
        await FocoEnAsync(c, "#codigo-compra");
        await using (var db = servicio.Base())
            Assert.Equal(Cantidad.Piezas(30), (await db.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == variante)).Cantidad);
        if (HoyEnMexico == dia)
        {
            await p.EsperarAsync(t => t.Contains("Agregar por teclado de prueba") && t.Contains("Cajero de prueba"), "lo agregado hoy");
            Assert.Contains("+24", await p.TextoAsync());
        }

        // El siguiente, igual.
        await EscanearAsync(p, otroCodigo);
        await p.EsperarAsync(t => t.Contains("Agregar el siguiente de prueba"), "el siguiente producto");
        await p.EsperarElementoAsync("agregar-cantidad");
        await p.EscribirAsync("agregar-cantidad", "3");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: +3 «Agregar el siguiente de prueba». Ahora hay 4."), "el siguiente agregado");
        await using (var db = servicio.Base())
            Assert.Equal(Cantidad.Piezas(4), (await db.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == otraVariante)).Cantidad);
        Assert.Empty(p.Errores);
    }

    /// <summary>Lo que llega va en la unidad del producto, como en eleventa: el código de una caja dice cuántas piezas trae.</summary>
    [Fact]
    public async Task Con_el_codigo_de_una_caja_dice_que_cuantos_llegaron_va_en_piezas()
    {
        var c = await PrepararAsync();
        var codigoDeLaCaja = $"AC{Guid.NewGuid():N}"[..14];
        var (variante, _) = await ProductoAsync(c, "Agregar por caja de prueba", 0, codigoDeLaCaja);
        await using var p = await InventarioAsync(c, c.Cajero, TareaInventario.Agregar);
        await p.EsperarElementoAsync("codigo-compra");

        await EscanearAsync(p, codigoDeLaCaja);
        await p.EsperarElementoAsync("agregar-cantidad");
        Assert.Contains("Escaneaste «Caja 12» (12 piezas): «Cuántos llegaron» va en piezas.", await p.TextoAsync());
        await p.EscribirAsync("agregar-cantidad", "24");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: +24 «Agregar por caja de prueba». Ahora hay 24."), "lo agregado");

        await using (var db = servicio.Base())
            Assert.Equal(Cantidad.Piezas(24), (await db.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == variante)).Cantidad);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Lo_que_no_es_una_cantidad_se_dice_antes_de_mandarlo()
    {
        var c = await PrepararAsync();
        var (variante, codigo) = await ProductoAsync(c, "Agregar mal escrito de prueba", 2);
        await using var p = await InventarioAsync(c, c.Cajero, TareaInventario.Agregar);
        await p.EsperarElementoAsync("codigo-compra");
        await EscanearAsync(p, codigo);
        await p.EsperarElementoAsync("agregar-cantidad");

        async Task<string> IntentarAsync(string texto)
        {
            await p.EscribirAsync("agregar-cantidad", texto);
            await p.TeclaAsync("agregar-cantidad", "Enter");
            return await p.TextoAsync();
        }

        Assert.Contains("Escribe cuántos llegaron.", await IntentarAsync(""));
        Assert.Contains("«Agregar mal escrito de prueba» se cuenta en piezas enteras: escribe cuántos, sin punto ni coma.", await IntentarAsync("2.5"));
        Assert.Contains("«Agregar mal escrito de prueba» se cuenta en piezas enteras", await IntentarAsync("1,000"));
        Assert.Contains("«0» no es una cantidad: escribe cuántos llegaron, más de cero y hasta 99,999, enteros.", await IntentarAsync("0"));
        // Un código escaneado con el cursor aquí no se toma como cantidad.
        Assert.Contains("«7501234567890» no es una cantidad", await IntentarAsync("7501234567890"));
        await FocoEnAsync(c, "#agregar-cantidad");
        await using (var db = servicio.Base())
            Assert.False(await db.EntradasInventario.AnyAsync(x => x.VarianteId == variante));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Quien_ve_los_costos_tiene_el_costo_de_ahora_puesto_y_lo_puede_cambiar()
    {
        var c = await PrepararAsync();
        var (variante, codigo) = await ProductoAsync(c, "Agregar con costo de prueba", 10);
        var dia = HoyEnMexico;
        await using var p = await InventarioAsync(c, c.Encargado, TareaInventario.Agregar);
        await p.EsperarElementoAsync("codigo-compra");

        await EscanearAsync(p, codigo);
        await p.EsperarElementoAsync("agregar-costo");
        // Ya puesto con el costo de ahora (el promedio: $10.00), y dice de dónde sale.
        Assert.Equal("10.00", (await p.PorIdAsync("agregar-costo")).Valor);
        Assert.Contains("Ahora: $10.00 (su costo promedio).", await p.TextoAsync());

        await p.EscribirAsync("agregar-cantidad", "10");
        await p.EscribirAsync("agregar-costo", "$12.50");
        await p.TeclaAsync("agregar-costo", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: +10 «Agregar con costo de prueba». Ahora hay 20."), "lo agregado");

        // El promedio queda como el de una compra: (10 × $10 + 10 × $12.50) / 20.
        await using (var db = servicio.Base())
        {
            var existencia = await db.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == variante);
            Assert.Equal((Cantidad.Piezas(20), 22_500L), (existencia.Cantidad, existencia.CostoCentavos));
        }
        if (HoyEnMexico == dia)
        {
            // Lo agregado hoy, con el costo de cada uno (lo ve).
            await p.EsperarAsync(t => t.Contains("Costo de cada uno") && t.Contains("$12.50"), "el costo en lo agregado hoy");
        }
        // Y con un costo que no es dinero, se dice antes de mandarlo.
        await EscanearAsync(p, codigo);
        await p.EsperarElementoAsync("agregar-costo");
        await p.EscribirAsync("agregar-cantidad", "1");
        await p.EscribirAsync("agregar-costo", "doce");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        Assert.Contains("Revisa el costo de cada uno: «doce» no es dinero de $0.00 a $100,000.00.", await p.TextoAsync());
        await FocoEnAsync(c, "#agregar-costo");
        Assert.Empty(p.Errores);
    }

    [Theory]
    [InlineData("24", UnidadBase.Pieza, 24_000L)]
    [InlineData(" 7 ", UnidadBase.Pieza, 7_000L)]
    [InlineData("99999", UnidadBase.Pieza, 99_999_000L)]
    [InlineData("2.5", UnidadBase.Metro, 2_500L)]
    [InlineData("2,5", UnidadBase.Kilogramo, 2_500L)]
    [InlineData("0.125", UnidadBase.Litro, 125L)]
    [InlineData("2.5", UnidadBase.Pieza, null)]
    [InlineData("1,000", UnidadBase.Pieza, null)]
    [InlineData("0", UnidadBase.Pieza, null)]
    [InlineData("0.0001", UnidadBase.Metro, null)]
    [InlineData("100000", UnidadBase.Pieza, null)]
    [InlineData("-3", UnidadBase.Pieza, null)]
    [InlineData("", UnidadBase.Pieza, null)]
    [InlineData("tres", UnidadBase.Pieza, null)]
    [InlineData("7501234567890", UnidadBase.Pieza, null)]
    public void Cuantos_llegaron_se_lee_como_las_cantidades_de_la_venta_con_su_unidad_y_su_limite(string texto, UnidadBase unidad, long? milesimas)
    {
        var leida = AgregarInventarioEnPantalla.Cantidad(texto, unidad, "Producto", out var problema);

        Assert.Equal(milesimas, leida);
        Assert.Equal(milesimas is null, problema is not null);
    }

    [Theory]
    [InlineData("12.50", 1250L)]
    [InlineData("$1,234.5", 123_450L)]
    [InlineData("0", 0L)]
    [InlineData("100000", 10_000_000L)]
    [InlineData("100000.01", null)]
    [InlineData("-1", null)]
    [InlineData("doce", null)]
    public void El_costo_se_lee_como_el_dinero_de_la_caja(string texto, long? centavos) =>
        Assert.Equal(centavos, AgregarInventarioEnPantalla.Costo(texto));
}
