using System.Globalization;
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
/// se dice antes de mandarlo. De la revisión del PR #138: los dos campos con la regla de los números del POS (la coma solo de los
/// miles), lo que se mandó y no contestó se repite con el mismo Id, Esc mientras se guarda espera la respuesta, y un código
/// escaneado en un campo de números es el siguiente producto.
/// </summary>
public sealed class AgregarInventarioTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, PaginaQueContesta Pagina, SesionDto Cajero, SesionDto Encargado);

    /// <param name="red">Lo que se pone entre la caja y el servicio (la red que pierde una respuesta, o la lenta).</param>
    private async Task<Caja> PrepararAsync(Func<HttpMessageHandler, HttpMessageHandler>? red = null)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        HttpMessageHandler alServicio = servicio.Fabrica.Server.CreateHandler();
        var cliente = new ClienteCaja(new HttpClient(red?.Invoke(alServicio) ?? alServicio), conexion);
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
    private async Task<(Guid Variante, string Codigo)> ProductoAsync(Caja c, string nombre, int existencia, string? codigoDeLaCaja = null, string? codigo = null)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        codigo ??= $"AG{Guid.NewGuid():N}"[..14];
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

    private static async Task<PantallaSinNavegador> InventarioAsync(Caja c, SesionDto quien, TareaInventario? inicial = null) =>
        (await AbrirAsync(c, quien, inicial)).Pantalla;

    /// <param name="visto">Lo que se recuerda de la tarea al salir y volver (el de la venta); sin él, el de esta página.</param>
    private static async Task<(PantallaSinNavegador Pantalla, Inventario Inventario)> AbrirAsync(
        Caja c, SesionDto quien, TareaInventario? inicial = null, AgregarInventarioVisto? visto = null)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        var inventario = await p.AbrirAsync<Inventario>(new()
        {
            [nameof(Inventario.Caja)] = c.Datos,
            [nameof(Inventario.Sesion)] = quien,
            [nameof(Inventario.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Inventario.EtiquetasPorImprimir)] = new EtiquetasPorImprimir(),
            [nameof(Inventario.TareaInicial)] = inicial,
            [nameof(Inventario.AgregarVisto)] = visto ?? new AgregarInventarioVisto(),
        });
        return (p, inventario);
    }

    /// <summary>Esc en la página (lo que hace la venta con su tecla): cierra la tarea abierta, si la deja.</summary>
    private static Task<bool> EscAsync(PantallaSinNavegador p, Inventario inventario) => p.Dispatcher.InvokeAsync(inventario.CerrarAsync);

    private static bool EsAgregar(HttpRequestMessage solicitud) =>
        solicitud.Method == HttpMethod.Post && solicitud.RequestUri!.AbsolutePath.EndsWith("/api/inventario/agregar", StringComparison.Ordinal);

    /// <summary>La red que se cae a media respuesta: «agregar» llega al servicio y se guarda, pero la respuesta se pierde.</summary>
    private sealed class PierdeLaRespuesta(HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        private int _porPerder;

        /// <summary>La respuesta del siguiente «agregar» se pierde.</summary>
        public void PerderLaSiguiente() => Interlocked.Exchange(ref _porPerder, 1);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var respuesta = await base.SendAsync(request, cancellationToken);
            if (EsAgregar(request) && Interlocked.Exchange(ref _porPerder, 0) == 1)
            {
                respuesta.Dispose();
                throw new HttpRequestException("Se cayó la red al regresar la respuesta.");
            }
            return respuesta;
        }
    }

    /// <summary>La red lenta: «agregar» se detiene aquí hasta que la prueba lo suelte (mientras, la página se está guardando).</summary>
    private sealed class DetieneElAgregar(HttpMessageHandler interno) : DelegatingHandler(interno)
    {
        public TaskCompletionSource Llego { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Soltar { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (EsAgregar(request))
            {
                Llego.TrySetResult();
                await Soltar.Task.WaitAsync(cancellationToken);
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Hasta que se cumpla algo que no es solo el texto de la pantalla (lo que se recuerda al salir, un renglón), con un límite.</summary>
    private static async Task HastaQueAsync(Func<Task<bool>> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!await condicion())
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No pasó: {que}.");
            await Task.Delay(20);
        }
    }

    private static Task HastaQueAsync(Func<bool> condicion, string que) => HastaQueAsync(() => Task.FromResult(condicion()), que);

    /// <summary>Lo que hay del producto, como lo dice la página («Hay 30 piezas en la sucursal»).</summary>
    private static Task HayAsync(PantallaSinNavegador p, string hay) =>
        p.EsperarAsync(t => PantallaSinNavegador.Limpio(t).Contains($"Hay {hay} piezas en la sucursal", StringComparison.Ordinal), $"«Hay {hay}»");

    /// <summary>
    /// La página que recibe los códigos escaneados en sus campos de números: la que se registró en caja.js (rafagasDe) al abrir
    /// «Agregar inventario».
    /// </summary>
    private static async Task<DotNetObjectReference<AgregarInventario>> RafagasAsync(Caja c)
    {
        DotNetObjectReference<AgregarInventario>? Registrada() =>
            c.Pagina.Llamadas.LastOrDefault(l => l.Funcion == "rafagasDe").Argumentos?.FirstOrDefault() as DotNetObjectReference<AgregarInventario>;
        await HastaQueAsync(() => Registrada() is not null, "que la página se registre en caja.js para los códigos de sus campos");
        return Registrada()!;
    }

    /// <summary>Un código de cinco cifras que nadie tiene, como los de 15 productos de la tienda.</summary>
    private static async Task<string> CodigoCortoAsync(Caja c)
    {
        while (true)
        {
            var codigo = Random.Shared.Next(90_000, 100_000).ToString(CultureInfo.InvariantCulture);
            if (await c.Cliente.PorCodigoAsync(codigo, c.Datos.SucursalId) is null)
                return codigo;
        }
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
        Assert.Contains("«Agregar mal escrito de prueba» se cuenta en piezas enteras: escribe cuántos, sin punto.", await IntentarAsync("2.5"));
        // La coma solo separa los miles: «2,5» no se adivina.
        Assert.Contains("«Agregar mal escrito de prueba» se cuenta en piezas enteras: escribe cuántos, sin decimales (la coma solo separa los miles: 1,000).",
            await IntentarAsync("2,5"));
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
        // «12,50» no entra como $1,250.00 (revisión del PR #138, R4): se pide con punto.
        await p.EscribirAsync("agregar-costo", "12,50");
        await p.TeclaAsync("agregar-costo", "Enter");
        Assert.Contains("Los centavos van con punto: 12.50 (la coma solo separa los miles: 1,250.00).", await p.TextoAsync());
        await using (var db = servicio.Base())
            Assert.Equal(1, await db.EntradasInventario.CountAsync(x => x.VarianteId == variante));
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// «Cuántos llegaron» con la regla de los números del POS (la de los productos en Excel; revisión del PR #138, R4b): el punto
    /// es el decimal y la coma solo separa los miles, así «1,000» son mil (también en metros) y «2,5» no se adivina. Con su unidad
    /// (lo que va por pieza, entero) y su límite.
    /// </summary>
    [Theory]
    [InlineData("24", UnidadBase.Pieza, 24_000L)]
    [InlineData(" 7 ", UnidadBase.Pieza, 7_000L)]
    [InlineData("99999", UnidadBase.Pieza, 99_999_000L)]
    [InlineData("1,000", UnidadBase.Pieza, 1_000_000L)]
    [InlineData("1,000", UnidadBase.Metro, 1_000_000L)]
    [InlineData("1,250.5", UnidadBase.Metro, 1_250_500L)]
    [InlineData("2.5", UnidadBase.Metro, 2_500L)]
    [InlineData("0.125", UnidadBase.Litro, 125L)]
    [InlineData("2,5", UnidadBase.Kilogramo, null)]
    [InlineData("1,5", UnidadBase.Metro, null)]
    [InlineData("0,500", UnidadBase.Litro, null)]
    [InlineData("2.5", UnidadBase.Pieza, null)]
    [InlineData("0", UnidadBase.Pieza, null)]
    [InlineData("0.0001", UnidadBase.Metro, null)]
    [InlineData("100000", UnidadBase.Pieza, null)]
    [InlineData("-3", UnidadBase.Pieza, null)]
    [InlineData("$5", UnidadBase.Pieza, null)]
    [InlineData("", UnidadBase.Pieza, null)]
    [InlineData("tres", UnidadBase.Pieza, null)]
    [InlineData("7501234567890", UnidadBase.Pieza, null)]
    public void Cuantos_llegaron_va_con_la_regla_de_los_numeros_del_POS_su_unidad_y_su_limite(string texto, UnidadBase unidad, long? milesimas)
    {
        var leida = AgregarInventarioEnPantalla.Cantidad(texto, unidad, "Producto", out var problema);

        Assert.Equal(milesimas, leida);
        Assert.Equal(milesimas is null, problema is not null);
    }

    /// <summary>
    /// «Costo de cada uno» con la misma regla (revisión del PR #138, R4): «12,50» ya no entra como $1,250.00; «1,250» sí son mil
    /// doscientos cincuenta pesos.
    /// </summary>
    [Theory]
    [InlineData("12.50", 1250L)]
    [InlineData("$1,234.5", 123_450L)]
    [InlineData("1,250", 125_000L)]
    [InlineData("0", 0L)]
    [InlineData("100000", 10_000_000L)]
    [InlineData("12,50", null)]
    [InlineData("1,5", null)]
    // Tres decimales no se adivinan (revisión de la coma): antes «12.505» entraba como $12.51 y «1.500», como $1.50.
    [InlineData("12.505", null)]
    [InlineData("1.500", null)]
    [InlineData("100000.01", null)]
    [InlineData("-1", null)]
    [InlineData("doce", null)]
    public void El_costo_va_con_la_regla_de_los_numeros_del_POS(string texto, long? centavos)
    {
        var leido = AgregarInventarioEnPantalla.Costo(texto, out var problema);

        Assert.Equal(centavos, leido);
        Assert.Equal(centavos is null, problema is not null);
    }

    /// <summary>Una coma que podría ser de decimales se pide con punto, con lo escrito ya corregido (revisión del PR #138).</summary>
    [Fact]
    public void La_coma_que_podria_ser_de_decimales_se_pide_con_punto()
    {
        AgregarInventarioEnPantalla.Costo("12,50", out var costo);
        AgregarInventarioEnPantalla.Costo("$1.234,50", out var europeo);
        AgregarInventarioEnPantalla.Cantidad("2,5", UnidadBase.Metro, "Listón", out var metros);
        AgregarInventarioEnPantalla.Cantidad("1,5", UnidadBase.Pieza, "Pluma", out var piezas);

        Assert.Equal("Los centavos van con punto: 12.50 (la coma solo separa los miles: 1,250.00).", costo);
        Assert.Equal("Los centavos van con punto: 12.50 (la coma solo separa los miles: 1,250.00).", europeo);
        Assert.Equal("Los decimales van con punto: 2.5 (la coma solo separa los miles: 1,000).", metros);
        Assert.Equal("«Pluma» se cuenta en piezas enteras: escribe cuántos, sin decimales (la coma solo separa los miles: 1,000).", piezas);
    }

    /// <summary>
    /// Lo mandado sin respuesta (revisión del PR #138): lo mismo otra vez (el producto, cuántos, el costo, quién y la caja) va con
    /// el mismo Id hasta que el servicio conteste; otra cosa va con el suyo; y ya contestado, lo mismo es otra entrada.
    /// </summary>
    [Fact]
    public void Lo_mismo_que_no_contesto_va_con_el_mismo_Id_hasta_que_contesta()
    {
        var visto = new AgregarInventarioVisto();
        var (usuario, sucursal, variante, caja) = (Ids.Nuevo(), Ids.Nuevo(), Ids.Nuevo(), Ids.Nuevo());
        SolicitudAgregarInventario Nueva(long milesimas, long? costo = null) => new(Ids.Nuevo(), usuario, sucursal, variante, milesimas, costo, caja);

        var primera = visto.Mandar(Nueva(24_000));
        var otraVez = visto.Mandar(Nueva(24_000));
        var otraCantidad = visto.Mandar(Nueva(25_000));
        var conCosto = visto.Mandar(Nueva(24_000, 1200));
        var otraVezLaOtra = visto.Mandar(Nueva(25_000));
        visto.Contestada(otraVez);
        var yaContestada = visto.Mandar(Nueva(24_000));

        Assert.Equal(primera.Id, otraVez.Id);
        Assert.Equal(otraCantidad.Id, otraVezLaOtra.Id);
        Assert.Equal(4, new[] { primera.Id, otraCantidad.Id, conCosto.Id, yaContestada.Id }.Distinct().Count());
    }

    /// <summary>
    /// Se guardó pero se perdió la respuesta (revisión del PR #138, R5): la página dice que no se sabe si se guardó y que se revise
    /// «Lo agregado hoy», que se vuelve a leer y ya lo trae. Repetirlo igual (escaneando otra vez el mismo producto, o saliendo con
    /// Esc y volviendo a entrar) va con el mismo Id: no se agrega dos veces. Otra cantidad sí es otra entrada.
    /// </summary>
    [Fact]
    public async Task Si_se_pierde_la_respuesta_repetirlo_igual_no_lo_agrega_dos_veces()
    {
        PierdeLaRespuesta? red = null;
        var c = await PrepararAsync(interno => red = new PierdeLaRespuesta(interno));
        var (variante, codigo) = await ProductoAsync(c, "Agregar respuesta perdida de prueba", 6);
        var dia = HoyEnMexico;
        var (p, inventario) = await AbrirAsync(c, c.Cajero, TareaInventario.Agregar);
        await using var pantalla = p;
        await p.EsperarElementoAsync("codigo-compra");

        // Escanea, 24 y Enter: se guarda, pero la respuesta no llega.
        red!.PerderLaSiguiente();
        await EscanearAsync(p, codigo);
        await HayAsync(p, "6");
        await p.EscribirAsync("agregar-cantidad", "24");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await p.EsperarAsync(t => t.Contains("No se sabe si se guardó +24 «Agregar respuesta perdida de prueba»."), "el aviso de la respuesta perdida");
        var texto = await p.TextoAsync();
        Assert.Contains("No hay conexión", texto);
        Assert.Contains("Revisa «Lo agregado hoy» antes de repetir: si ya aparece, sí se guardó.", texto);
        if (HoyEnMexico == dia)
        {
            // «Lo agregado hoy» se leyó otra vez y ya lo trae.
            await HastaQueAsync(async () => (await p.LeerAsync()).Elementos.Any(e => e.Etiqueta == "tr"
                && e.Texto.Contains("Agregar respuesta perdida de prueba") && PantallaSinNavegador.Limpio(e.Texto).Contains("+24")),
                "lo agregado hoy, con lo que sí se guardó");
        }

        // Lo repite igual: escanea otra vez el mismo producto, 24 y Enter. Va con el mismo Id: ya estaba.
        await EscanearAsync(p, codigo);
        await HayAsync(p, "30");
        await p.EscribirAsync("agregar-cantidad", "24");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await p.EsperarAsync(t => t.Contains("Ya se había agregado: +24 «Agregar respuesta perdida de prueba». Ahora hay 30."), "que ya estaba");

        // Otra vez se pierde la respuesta; ahora sale con Esc y vuelve a entrar, y lo repite igual: tampoco se agrega dos veces.
        red.PerderLaSiguiente();
        await EscanearAsync(p, codigo);
        await HayAsync(p, "30");
        await p.EscribirAsync("agregar-cantidad", "5");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await p.EsperarAsync(t => t.Contains("No se sabe si se guardó +5 «Agregar respuesta perdida de prueba»."), "el aviso de la segunda respuesta perdida");
        Assert.True(await EscAsync(p, inventario));
        await p.OprimirAsync(await p.EsperarElementoAsync("tarea-Agregar"));
        await p.EsperarElementoAsync("codigo-compra");
        await EscanearAsync(p, codigo);
        await HayAsync(p, "35");
        await p.EscribirAsync("agregar-cantidad", "5");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await p.EsperarAsync(t => t.Contains("Ya se había agregado: +5 «Agregar respuesta perdida de prueba». Ahora hay 35."), "que ya estaba, al volver");

        // Otra cantidad sí es otra entrada.
        await EscanearAsync(p, codigo);
        await HayAsync(p, "35");
        await p.EscribirAsync("agregar-cantidad", "3");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: +3 «Agregar respuesta perdida de prueba». Ahora hay 38."), "otra entrada");

        await using var db = servicio.Base();
        Assert.Equal(3, await db.EntradasInventario.CountAsync(x => x.VarianteId == variante));
        Assert.Equal(Cantidad.Piezas(38), (await db.Existencias.AsNoTracking().SingleAsync(x => x.VarianteId == variante)).Cantidad);
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Esc mientras se guarda ya no regresa «sin agregar nada» (revisión del PR #138): se queda, dice que espere y enseña la
    /// respuesta al llegar; luego Esc ya regresa a las tareas. «← Inventario», igual.
    /// </summary>
    [Fact]
    public async Task Esc_mientras_se_guarda_espera_la_respuesta_y_la_ensena()
    {
        DetieneElAgregar? red = null;
        var c = await PrepararAsync(interno => red = new DetieneElAgregar(interno));
        var (_, codigo) = await ProductoAsync(c, "Agregar con Esc de prueba", 2);
        var (p, inventario) = await AbrirAsync(c, c.Cajero, TareaInventario.Agregar);
        await using var pantalla = p;
        await p.EsperarElementoAsync("codigo-compra");
        await EscanearAsync(p, codigo);
        await HayAsync(p, "2");
        await p.EscribirAsync("agregar-cantidad", "4");
        var enter = p.TeclaAsync("agregar-cantidad", "Enter");
        await red!.Llego.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // Esc y «← Inventario» mientras se guarda: se queda, y dice que espere.
        Assert.True(await EscAsync(p, inventario));
        await p.OprimirAsync(await p.BotonAsync("← Inventario"));
        Assert.Contains("Todavía se está guardando: espera la respuesta para ver si se agregó (luego Esc regresa a las tareas).", await p.TextoAsync());
        Assert.True((await p.PorIdAsync("agregar-guardar")).Desactivado);

        // Llega la respuesta: se ve; y Esc ya regresa a las tareas.
        red.Soltar.SetResult();
        await enter;
        await p.EsperarAsync(t => t.Contains("Listo: +4 «Agregar con Esc de prueba». Ahora hay 6."), "lo agregado");
        Assert.True(await EscAsync(p, inventario));
        await p.EsperarElementoAsync("tarea-Agregar");
        Assert.Empty(p.Errores);
    }

    /// <summary>
    /// Si la tarea se va mientras se guarda (F1 a la venta, otra página, o cambia el usuario), lo que contestó se dice al volver a
    /// entrar (revisión del PR #138): lo recuerda la venta, no la tarea.
    /// </summary>
    [Fact]
    public async Task Si_se_sale_mientras_se_guarda_lo_que_contesto_se_dice_al_volver()
    {
        DetieneElAgregar? red = null;
        var c = await PrepararAsync(interno => red = new DetieneElAgregar(interno));
        var (_, codigo) = await ProductoAsync(c, "Agregar al salir de prueba", 1);
        var visto = new AgregarInventarioVisto();
        var (p, _) = await AbrirAsync(c, c.Cajero, TareaInventario.Agregar, visto);
        await using (p)
        {
            await p.EsperarElementoAsync("codigo-compra");
            await EscanearAsync(p, codigo);
            await HayAsync(p, "1");
            await p.EscribirAsync("agregar-cantidad", "2");
            _ = p.TeclaAsync("agregar-cantidad", "Enter");
            await red!.Llego.Task.WaitAsync(TimeSpan.FromSeconds(20));
            // La página se va mientras se guarda (al salir de este bloque); luego llega la respuesta.
        }
        red.Soltar.SetResult();
        await HastaQueAsync(() => visto.AlVolver is not null, "que se recuerde lo que contestó");

        var (otra, _) = await AbrirAsync(c, c.Cajero, TareaInventario.Agregar, visto);
        await using var pantalla = otra;
        await otra.EsperarAsync(t => t.Contains("Listo: +2 «Agregar al salir de prueba». Ahora hay 3."), "lo que contestó, al volver");
        Assert.Null(visto.AlVolver);
        Assert.Empty(otra.Errores);
    }

    /// <summary>
    /// Un código escaneado en «Cuántos llegaron» o en el costo no es un número (revisión del PR #138): caja.js lo reconoce por el
    /// ritmo del lector (data-rafaga, de 4 caracteres o más; eso lo prueba CajaJsTests), deja el campo como estaba, no deja pasar
    /// su Enter y se lo pasa a la página. Aquí, lo que hace la página con él: lo toma como el siguiente producto (lo escrito para el
    /// de antes no se agrega, y se dice); si no es de ningún producto, el de antes se queda con lo escrito y el cursor en su campo.
    /// </summary>
    [Fact]
    public async Task Un_codigo_escaneado_en_un_campo_de_numeros_es_el_siguiente_producto()
    {
        var c = await PrepararAsync();
        var (anterior, codigoAnterior) = await ProductoAsync(c, "Agregar el de antes de prueba", 4);
        var corto = await CodigoCortoAsync(c);
        var (siguiente, _) = await ProductoAsync(c, "Agregar el escaneado de prueba", 2, codigo: corto);
        await using var p = await InventarioAsync(c, c.Encargado, TareaInventario.Agregar);
        await p.EsperarElementoAsync("codigo-compra");
        var pagina = await RafagasAsync(c);

        // Escribe 24 y, sin su Enter, el lector escanea el siguiente con el cursor en el costo (caja.js lo deja en «10.00»).
        await EscanearAsync(p, codigoAnterior);
        await HayAsync(p, "4");
        await p.EscribirAsync("agregar-cantidad", "24");
        await p.EscribirAsync("agregar-costo", "10.00" + corto);
        await p.Dispatcher.InvokeAsync(() => pagina.Value.RafagaEnCampo(corto, "agregar-costo", "10.00"));
        await p.EsperarAsync(t => t.Contains("No se agregó lo que escribiste de «Agregar el de antes de prueba» (24): se escaneó un código antes de su Enter."),
            "que lo del de antes no se agregó");
        Assert.Equal("Agregar el escaneado de prueba", PantallaSinNavegador.Limpio((await p.PorIdAsync("agregar-producto")).Texto));
        Assert.Equal("", (await p.PorIdAsync("agregar-cantidad")).Valor);
        await FocoEnAsync(c, "#agregar-cantidad");
        await p.EscribirAsync("agregar-cantidad", "3");
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: +3 «Agregar el escaneado de prueba». Ahora hay 5."), "el escaneado, agregado");

        // Un código que no es de ningún producto, con el cursor en «Cuántos llegaron»: el de antes se queda con lo escrito.
        await EscanearAsync(p, codigoAnterior);
        await HayAsync(p, "4");
        await p.EscribirAsync("agregar-cantidad", "6" + "QX4471");
        await p.Dispatcher.InvokeAsync(() => pagina.Value.RafagaEnCampo("QX4471", "agregar-cantidad", "6"));
        await p.EsperarAsync(t => t.Contains("El código «QX4471» no existe: dalo de alta en F3 Productos."), "que el código no existe");
        Assert.Equal("Agregar el de antes de prueba", PantallaSinNavegador.Limpio((await p.PorIdAsync("agregar-producto")).Texto));
        Assert.Equal("6", (await p.PorIdAsync("agregar-cantidad")).Valor);
        await FocoEnAsync(c, "#agregar-cantidad");
        // Y su Enter agrega lo escrito: 6, no el código.
        await p.TeclaAsync("agregar-cantidad", "Enter");
        await p.EsperarAsync(t => t.Contains("Listo: +6 «Agregar el de antes de prueba». Ahora hay 10."), "el de antes, agregado");

        await using (var db = servicio.Base())
        {
            Assert.Equal([6m], await db.EntradasInventario.Where(x => x.VarianteId == anterior).Select(x => x.Cantidad).ToListAsync() is var a
                ? a.Select(x => x.EnUnidades).ToList() : []);
            Assert.Equal(1, await db.EntradasInventario.CountAsync(x => x.VarianteId == siguiente));
        }
        Assert.Empty(p.Errores);
    }
}
