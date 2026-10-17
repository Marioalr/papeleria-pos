using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
/// Productos (F3) › Cambiar precios, sin navegador, contra el servicio con datos ficticios: se eligen los de una marca (o lo
/// que se buscaba en la lista, o los marcados en ella: un toque o Enter en la búsqueda), la regla y el redondeo; Enter enseña
/// cómo quedan y «Aplicar» o Ctrl+Enter los cambia todos; Esc cierra la vista y luego regresa al producto. El margen y su
/// aviso, solo para quien ve la utilidad; quien no tiene el permiso no ve el botón; en el piloto con eleventa, la nota y lo
/// de eleventa sin cambiar.
/// </summary>
public sealed class PreciosDeVariosTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(ClienteCaja Cliente, CajaDto Datos, ServiceProvider Servicios, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

    /// <summary>La caja contra ese servicio; con <paramref name="piloto"/>, lo que dice del piloto llega hasta que se suelte.</summary>
    private static async Task<Caja> PrepararAsync(WebApplicationFactory<Program> fabrica, PilotoDetenido? piloto = null)
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/") };
        HttpMessageHandler manejador = fabrica.Server.CreateHandler();
        if (piloto is not null)
        {
            piloto.InnerHandler = manejador;
            manejador = piloto;
        }
        var cliente = new ClienteCaja(new HttpClient(manejador), conexion);
        var caja = (await cliente.CajasAsync()).Single();
        conexion.CajaId = caja.Id;
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        return new Caja(cliente, caja, servicios, await cliente.EntrarAsync("cajero", "1111"), await cliente.EntrarAsync("encargado", "2222"),
            await cliente.EntrarAsync("dueno", "3333"));
    }

    private static string Ficha() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Detiene lo que dice el servicio del piloto (lo que pide «Cambiar precios» al abrirse) hasta <see cref="Soltar"/>: así
    /// se prueba lo que pasa si se marca en la lista antes de que llegue, sin depender de cuánto tarde.
    /// </summary>
    private sealed class PilotoDetenido : DelegatingHandler
    {
        private readonly TaskCompletionSource _suelto = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Soltar() => _suelto.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/api/catalogo/piloto", StringComparison.Ordinal) == true)
                await _suelto.Task.WaitAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Un producto con su menudeo y, si se da, su escala desde 12.</summary>
    private static async Task<FichaProductoDto> ProductoAsync(Caja c, string nombre, long menudeo, string? marca = null, Guid? departamento = null,
        long? desde12 = null)
    {
        var opciones = await c.Cliente.OpcionesCatalogoAsync();
        List<PrecioFichaDto> precios = [new PrecioFichaDto(null, 1000, menudeo)];
        if (desde12 is { } escala)
            precios.Add(new PrecioFichaDto(null, 12000, escala));
        return await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            new FichaProductoDto(null, nombre, departamento ?? opciones.Departamentos[0].Id, marca, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
            [
                new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                    [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto($"PVP{Guid.NewGuid():N}"[..16])])],
                    precios),
            ])));
    }

    private static async Task<Guid> DepartamentoAsync(Caja c, string nombre) =>
        (await c.Cliente.DarDeAltaDepartamentoAsync(new SolicitudDepartamento(c.Encargado.UsuarioId, c.Datos.SucursalId, nombre)))
            .Departamentos.Single(d => d.Nombre == nombre).Id;

    private static async Task<long> MenudeoAsync(Caja c, FichaProductoDto producto) =>
        (await c.Cliente.FichaProductoAsync(producto.Id!.Value, c.Datos.SucursalId, c.Encargado.UsuarioId))!.Variantes[0].Precios
        .Single(p => p.Presentacion is null && p.CantidadMinimaMilesimas == 1000).PrecioCentavos;

    /// <summary>La página de Productos de quien entró, como la abre F3, con lo que se buscaba en ella (y su departamento).</summary>
    private static async Task<(PantallaSinNavegador Pantalla, Productos Productos)> ProductosAsync(Caja c, SesionDto quien, string texto, Guid? departamento = null)
    {
        var p = new PantallaSinNavegador(c.Servicios);
        var productos = await p.AbrirAsync<Productos>(new()
        {
            [nameof(Productos.Caja)] = c.Datos,
            [nameof(Productos.Sesion)] = quien,
            [nameof(Productos.Acceso)] = new PermisosDePagina(quien, null),
            [nameof(Productos.Estado)] = new ProductosVistos { Texto = texto, Departamento = departamento },
            [nameof(Productos.Etiquetas)] = new EtiquetasPorImprimir(),
        });
        await p.EsperarElementoAsync("prod-0");
        return (p, productos);
    }

    /// <summary>«Cambiar precios», abajo de la lista: se abre a la derecha.</summary>
    private static async Task CambiarPreciosAsync(PantallaSinNavegador p)
    {
        await p.OprimirAsync(await p.BotonAsync("Cambiar precios"));
        await p.EsperarElementoAsync("precios-cuanto");
    }

    /// <summary>Lo que se escribe y Enter: cómo quedan.</summary>
    private static async Task VerAsync(PantallaSinNavegador p, string cuanto)
    {
        await p.EscribirAsync("precios-cuanto", cuanto);
        await p.TeclaAsync("precios-cuanto", "Enter");
        await p.EsperarAsync(t => t.Contains("Cómo quedan") || t.Contains("Escribe"), "la vista");
    }

    private static Task<bool> EscAsync(PantallaSinNavegador p, Productos productos) => p.Dispatcher.InvokeAsync(productos.CerrarAsync);

    private static string Limpio(string texto) => PantallaSinNavegador.Limpio(texto);

    [Fact]
    public async Task El_encargado_cambia_los_de_una_marca_viendo_antes_como_quedan()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var ficha = Ficha();
        var marca = $"Marca {ficha}";
        var cuaderno = await ProductoAsync(c, $"Cuaderno {ficha}", 1250, marca);
        var libreta = await ProductoAsync(c, $"Libreta {ficha}", 3000, marca);
        var (p, _) = await ProductosAsync(c, c.Encargado, ficha);
        await using var __ = p;

        await CambiarPreciosAsync(p);
        // Empieza con lo que se buscaba en la lista; se cambia a la marca.
        Assert.Equal("true", (await p.PorIdAsync("precios-cuales-Busqueda")).Atributos.GetValueOrDefault("aria-pressed"));
        Assert.Equal(ficha, (await p.PorIdAsync("precios-texto")).Valor);
        await p.OprimirAsync(await p.PorIdAsync("precios-cuales-Marca"));
        await p.DispararAsync(await p.PorIdAsync("precios-marca"), "onchange", new ChangeEventArgs { Value = marca });
        await VerAsync(p, "10");

        var texto = Limpio(await p.TextoAsync());
        Assert.Contains($"Cambian 2 de 2 · la marca «{marca}»: subir 10 %, con el mayoreo en la misma proporción, redondeado a los 50 centavos más cercanos.", texto);
        Assert.Contains($"Cuaderno {ficha} $12.50 $14.00", texto);
        Assert.Contains($"Libreta {ficha} $30.00 $33.00", texto);
        // El encargado no ve la utilidad: ni el margen de cada uno.
        Assert.DoesNotContain(" % → ", texto);
        Assert.Contains("El margen lo ve quien tiene el permiso «Ver costos, utilidad y margen».", texto);
        Assert.Equal(1250, await MenudeoAsync(c, cuaderno));

        await p.OprimirAsync(await p.BotonAsync("Aplicar a 2 productos"));
        await p.EsperarAsync(t => t.Contains("Listo: cambió el precio de 2 productos."), "lo aplicado");
        // La lista, con los precios nuevos.
        await p.EsperarAsync(t => Limpio(t).Contains("$14.00") && Limpio(t).Contains("$33.00"), "la lista al día");
        Assert.Equal((1400L, 3300L), (await MenudeoAsync(c, cuaderno), await MenudeoAsync(c, libreta)));
        Assert.DoesNotContain("Cómo quedan", await p.TextoAsync());
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Se_marcan_en_la_lista_con_un_toque_o_con_Enter_y_Ctrl_Enter_aplica()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var ficha = Ficha();
        var regla = await ProductoAsync(c, $"Regla {ficha}", 1000);
        var escuadra = await ProductoAsync(c, $"Escuadra {ficha}", 1000);
        var compas = await ProductoAsync(c, $"Compás {ficha}", 1000);
        var (p, productos) = await ProductosAsync(c, c.Encargado, ficha);
        await using var __ = p;
        Assert.True((await p.ElementoAsync(e => e.Clase == "vn-items", "la lista")).Atributos.ContainsKey("data-lista"));

        await CambiarPreciosAsync(p);
        // Mientras se cambian precios, la lista marca: cada renglón dice si está marcado y las flechas no tocan renglones.
        Assert.False((await p.ElementoAsync(e => e.Clase == "vn-items", "la lista")).Atributos.ContainsKey("data-lista"));
        Assert.Equal("false", (await p.PorIdAsync("prod-0")).Atributos.GetValueOrDefault("aria-pressed"));
        var primero = Limpio((await p.PorIdAsync("prod-0")).Texto);
        (string Nombre, FichaProductoDto Producto)[] todos = [($"Regla {ficha}", regla), ($"Escuadra {ficha}", escuadra), ($"Compás {ficha}", compas)];
        var tocado = todos.Single(x => primero.StartsWith(x.Nombre, StringComparison.Ordinal));
        await p.OprimirAsync(await p.PorIdAsync("prod-0"));
        Assert.Equal("true", (await p.PorIdAsync("prod-0")).Atributos.GetValueOrDefault("aria-pressed"));
        Assert.Equal("true", (await p.PorIdAsync("precios-cuales-Marcados")).Atributos.GetValueOrDefault("aria-pressed"));
        Assert.Contains("Los marcados · 1", Limpio((await p.PorIdAsync("precios-cuales-Marcados")).Texto));

        // Con el cursor en la búsqueda: lo escrito busca y Enter marca el elegido.
        var conEnter = todos.First(x => x.Nombre != tocado.Nombre);
        var sinMarcar = todos.Single(x => x.Nombre != tocado.Nombre && x.Nombre != conEnter.Nombre);
        await p.EscribirAsync("busca-prod", conEnter.Nombre);
        await p.EsperarAsync(t => (Limpio(t).Contains("1 producto.")), "la lista con lo escrito");
        await p.TeclaAsync("busca-prod", "Enter");
        await p.EsperarAsync(t => Limpio(t).Contains("Los marcados · 2"), "el segundo marcado");
        Assert.Equal("true", (await p.PorIdAsync("prod-0")).Atributos.GetValueOrDefault("aria-pressed"));

        // Un precio fijo: Enter enseña cómo quedan; Esc cierra la vista; Ctrl+Enter aplica.
        await p.DispararAsync(await p.PorIdAsync("precios-como"), "onchange", new ChangeEventArgs { Value = nameof(ComoCambiarPrecios.PrecioFijo) });
        await VerAsync(p, "15");
        Assert.Contains("Cambian 2 de 2 · los marcados en la lista: ponerlos a $15.00", Limpio(await p.TextoAsync()));
        Assert.True(await EscAsync(p, productos));
        Assert.DoesNotContain("Cómo quedan", await p.TextoAsync());
        Assert.NotNull(await p.PorIdAsync("precios-cuanto"));
        await p.TeclaAsync("precios-cuanto", "Enter");
        await p.EsperarAsync(t => t.Contains("Cómo quedan"), "la vista otra vez");
        await p.DispararAsync(await p.PorIdAsync("precios-cuanto"), "onkeydown", new KeyboardEventArgs { Key = "Enter", CtrlKey = true });
        await p.EsperarAsync(t => t.Contains("Listo: cambió el precio de 2 productos."), "lo aplicado con Ctrl+Enter");
        await p.EsperarAsync(t => Limpio(t).Contains("Los marcados · 0"), "las marcas quitadas");
        Assert.Equal((1500L, 1500L, 1000L),
            (await MenudeoAsync(c, tocado.Producto), await MenudeoAsync(c, conEnter.Producto), await MenudeoAsync(c, sinMarcar.Producto)));

        // Esc sin la vista regresa al producto, y la lista vuelve a elegir.
        Assert.True(await EscAsync(p, productos));
        await p.EsperarAsync(t => t.Contains("Precio al público"), "el producto");
        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Id == "precios-cuanto");
        Assert.True((await p.ElementoAsync(e => e.Clase == "vn-items", "la lista")).Atributos.ContainsKey("data-lista"));
        Assert.Null((await p.PorIdAsync("prod-0")).Atributos.GetValueOrDefault("aria-pressed"));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Lo_que_se_marca_antes_de_que_termine_de_abrir_tambien_cuenta()
    {
        var piloto = new PilotoDetenido();
        var c = await PrepararAsync(servicio.Fabrica, piloto);
        var ficha = Ficha();
        await ProductoAsync(c, $"Transportador {ficha}", 1000);
        await ProductoAsync(c, $"Escalímetro {ficha}", 1000);
        var (p, _) = await ProductosAsync(c, c.Encargado, ficha);
        await using var __ = p;
        try
        {
            // «Cambiar precios» se dibuja mientras todavía pregunta por el piloto; se marca en la lista en ese rato.
            await CambiarPreciosAsync(p);
            await p.OprimirAsync(await p.PorIdAsync("prod-1"));

            Assert.Equal("true", (await p.PorIdAsync("precios-cuales-Marcados")).Atributos.GetValueOrDefault("aria-pressed"));
            Assert.Contains("Los marcados · 1", Limpio((await p.PorIdAsync("precios-cuales-Marcados")).Texto));
        }
        finally
        {
            piloto.Soltar();
        }
        // Ya con la respuesta, sigue igual.
        await p.EsperarAsync(t => Limpio(t).Contains("Los marcados · 1") && Limpio(t).Contains("1 marcado."), "lo marcado");
        Assert.Equal("true", (await p.PorIdAsync("precios-cuales-Marcados")).Atributos.GetValueOrDefault("aria-pressed"));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task El_dueno_ve_el_margen_y_el_aviso_de_debajo_del_minimo()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var ficha = Ficha();
        var departamento = (await c.Cliente.DarDeAltaDepartamentoAsync(new SolicitudDepartamento(c.Encargado.UsuarioId, c.Datos.SucursalId, $"Margen {ficha}")))
            .Departamentos.Single(d => d.Nombre == $"Margen {ficha}").Id;
        var carpeta = await ProductoAsync(c, $"Carpeta {ficha}", 1000, departamento: departamento);
        await using (var db = servicio.Base())
        {
            (await db.Departamentos.SingleAsync(d => d.Id == departamento)).MargenMinimoCentesimas = 3000;
            await db.SaveChangesAsync();
            var variante = await db.Variantes.SingleAsync(v => v.Id == carpeta.Variantes[0].Id);
            await new RegistroInventario(db).RegistrarAsync(Kardex.Entrada(c.Datos.SucursalId, variante, TipoMovimiento.InventarioInicial, Cantidad.Piezas(10), 8000,
                DateTime.UtcNow));
        }
        var (p, _) = await ProductosAsync(c, c.Dueno, ficha);
        await using var __ = p;

        await CambiarPreciosAsync(p);
        await VerAsync(p, "10");

        // La columna del margen: el de ahora y el que queda, con el aviso si queda debajo del mínimo de su departamento.
        var texto = Limpio(await p.TextoAsync());
        Assert.Contains($"Carpeta {ficha} $10.00 $11.00 20 % → 27.3 % debajo del mínimo 30 %", texto);
        Assert.DoesNotContain("El margen lo ve quien tiene el permiso", texto);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Si_algo_cambio_desde_la_vista_lo_dice_y_enseña_la_de_ahora()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var ficha = Ficha();
        var sobre = await ProductoAsync(c, $"Sobre {ficha}", 1000);
        var (p, _) = await ProductosAsync(c, c.Encargado, ficha);
        await using var __ = p;
        await CambiarPreciosAsync(p);
        await VerAsync(p, "10");
        Assert.Contains($"Sobre {ficha} $10.00 $11.00", Limpio(await p.TextoAsync()));

        // Mientras, otro le cambia el precio en su ficha: al aplicar, no se aplica y la vista es la de ahora.
        var suya = (await c.Cliente.FichaProductoAsync(sobre.Id!.Value, c.Datos.SucursalId, c.Encargado.UsuarioId))!;
        await c.Cliente.GuardarProductoAsync(new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Datos.SucursalId,
            suya with { Variantes = [suya.Variantes[0] with { Precios = [new PrecioFichaDto(null, 1000, 2000)] }] }));
        await p.OprimirAsync(await p.BotonAsync("Aplicar a 1 producto"));
        await p.EsperarAsync(t => t.Contains(RegistroPreciosDeVarios.CambioDesdeLaVista) && Limpio(t).Contains($"Sobre {ficha} $20.00 $22.00"),
            "el aviso y la vista de ahora");
        Assert.Equal(2000, await MenudeoAsync(c, sobre));

        await p.OprimirAsync(await p.BotonAsync("Aplicar a 1 producto"));
        await p.EsperarAsync(t => t.Contains("Listo: cambió el precio de 1 producto."), "lo aplicado");
        Assert.DoesNotContain(RegistroPreciosDeVarios.CambioDesdeLaVista, await p.TextoAsync());
        Assert.Equal(2200, await MenudeoAsync(c, sobre));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Lo_que_coincide_toma_el_departamento_de_la_lista()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var ficha = Ficha();
        var escolar = await DepartamentoAsync(c, $"Escolar {ficha}");
        var oficina = await DepartamentoAsync(c, $"Oficina {ficha}");
        var deEscolar = await ProductoAsync(c, $"Folder {ficha} escolar", 1000, departamento: escolar);
        var deOficina = await ProductoAsync(c, $"Folder {ficha} oficina", 1000, departamento: oficina);
        // La lista con lo que se busca y el departamento del filtro: «Cambiar precios» empieza con los dos.
        var (p, _) = await ProductosAsync(c, c.Encargado, ficha, escolar);
        await using var __ = p;

        await CambiarPreciosAsync(p);
        Assert.Equal((ficha, escolar.ToString()), ((await p.PorIdAsync("precios-texto")).Valor, (await p.PorIdAsync("precios-texto-depto")).Valor));
        await VerAsync(p, "10");

        Assert.Contains($"Cambian 1 de 1 · lo que coincide con «{ficha}» en «Escolar {ficha}»", Limpio(await p.TextoAsync()));
        // En todos los departamentos, los dos.
        await p.DispararAsync(await p.PorIdAsync("precios-texto-depto"), "onchange", new ChangeEventArgs { Value = "" });
        await p.TeclaAsync("precios-cuanto", "Enter");
        await p.EsperarAsync(t => Limpio(t).Contains($"Cambian 2 de 2 · lo que coincide con «{ficha}»:"), "los dos");
        await p.OprimirAsync(await p.BotonAsync("Aplicar a 2 productos"));
        await p.EsperarAsync(t => t.Contains("Listo: cambió el precio de 2 productos."), "lo aplicado");
        Assert.Equal((1100L, 1100L), (await MenudeoAsync(c, deEscolar), await MenudeoAsync(c, deOficina)));
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task El_mayoreo_que_no_cabe_mas_barato_que_el_menudeo_se_dice_en_la_vista()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var ficha = Ficha();
        var conEscala = await ProductoAsync(c, $"Con escala {ficha}", 1000, desde12: 900);
        var fijo = await ProductoAsync(c, $"Fijo {Ficha()}", 2000, desde12: 1930);
        var (p, _) = await ProductosAsync(c, c.Encargado, ficha);
        await using var __ = p;
        await CambiarPreciosAsync(p);

        // Bajar 20 % sin el mayoreo: su escala de $9.00 quedaría más cara que el menudeo de $8.00; no cambia, y dice cómo.
        await p.DispararAsync(await p.PorIdAsync("precios-como"), "onchange", new ChangeEventArgs { Value = nameof(ComoCambiarPrecios.BajarPorcentaje) });
        await p.DispararAsync(await p.PorIdAsync("precios-mayoreo"), "onchange", new ChangeEventArgs { Value = false });
        await VerAsync(p, "20");
        var texto = Limpio(await p.TextoAsync());
        Assert.Contains("No cambia ninguno", texto);
        Assert.Contains($"1 no cambia: su mayoreo quedaría igual o más caro que el menudeo; cámbialo con «También el mayoreo»: Con escala {ficha}.", texto);
        Assert.Equal(1000, await MenudeoAsync(c, conEscala));

        // Un precio fijo de $10.90 con el mayoreo, al peso: su escala queda al peso de abajo del menudeo, «ajustado».
        await p.EscribirAsync("precios-texto", fijo.Nombre);
        await p.DispararAsync(await p.PorIdAsync("precios-mayoreo"), "onchange", new ChangeEventArgs { Value = true });
        await p.DispararAsync(await p.PorIdAsync("precios-como"), "onchange", new ChangeEventArgs { Value = nameof(ComoCambiarPrecios.PrecioFijo) });
        await p.DispararAsync(await p.PorIdAsync("precios-redondeo"), "onchange", new ChangeEventArgs { Value = nameof(RedondeoDePrecios.UnPeso) });
        await VerAsync(p, "10.90");
        texto = Limpio(await p.TextoAsync());
        Assert.Contains($"{fijo.Nombre} Mayoreo: desde 12: $19.30 → $10.00 ajustado $20.00 $10.90", texto);
        Assert.Contains("«Ajustado»: para que su mayoreo siga más barato que el menudeo", texto);
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task Quien_no_tiene_el_permiso_no_ve_el_boton()
    {
        var c = await PrepararAsync(servicio.Fabrica);
        var ficha = Ficha();
        await ProductoAsync(c, $"Goma {ficha}", 500);
        var (p, _) = await ProductosAsync(c, c.Cajero, ficha);
        await using var __ = p;

        Assert.DoesNotContain((await p.LeerAsync()).Elementos, e => e.Etiqueta == "button" && Limpio(e.Texto) == "Cambiar precios");
        Assert.Empty(p.Errores);
    }

    [Fact]
    public async Task En_el_piloto_dice_que_lo_de_eleventa_se_cambia_alla_y_cambia_lo_de_aqui()
    {
        await using var piloto = servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));
        var c = await PrepararAsync(piloto);
        var ficha = Ficha();
        var deEleventa = await ProductoAsync(c, $"Lápiz de eleventa {ficha}", 1500);
        var deAqui = await ProductoAsync(c, $"Lápiz de aquí {ficha}", 1500);
        await using (var db = servicio.Base())
        {
            db.MapeosImportacion.Add(new MapeoImportacion
            {
                Origen = "eleventa", Entidad = nameof(Variante), IdOrigen = Random.Shared.Next(100_000, 999_999).ToString(), IdNuevo = deEleventa.Variantes[0].Id!.Value,
            });
            await db.SaveChangesAsync();
        }
        var (p, _) = await ProductosAsync(c, c.Encargado, ficha);
        await using var __ = p;

        await CambiarPreciosAsync(p);
        await p.EsperarAsync(t => t.Contains(TrastiendaEnElPiloto.ProductosEnEleventa), "la nota del piloto");
        await VerAsync(p, "10");

        var texto = Limpio(await p.TextoAsync());
        Assert.Contains($"Cambian 1 de 2", texto);
        Assert.Contains($"Lápiz de aquí {ficha} $15.00 $16.50", texto);
        Assert.Contains($"1 es de eleventa: su precio se cambia allá: Lápiz de eleventa {ficha}.", texto);
        await p.OprimirAsync(await p.BotonAsync("Aplicar a 1 producto"));
        await p.EsperarAsync(t => t.Contains("Listo: cambió el precio de 1 producto."), "lo aplicado");
        Assert.Equal((1500L, 1650L), (await MenudeoAsync(c, deEleventa), await MenudeoAsync(c, deAqui)));
        Assert.Empty(p.Errores);
    }
}
