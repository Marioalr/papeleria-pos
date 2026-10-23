using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Productos › Promociones contra el servicio (fase 3): darlas de alta, verlas y terminarlas con «Productos, precios y
/// etiquetas»; nunca debajo del costo sin autorización; «Dejar al costo» con «Ver costos, utilidad y margen»; las de un
/// departamento o una marca; el cobro con el día de la caja, que no cobra una promoción que el cajero no vio (409) y lo que
/// subió sin conexión, como se cobró; la copia de las cajas con las promociones y su huella (también cuando una termina sin
/// que cambie nada más); y el piloto con eleventa, donde no se aplican ni se dan de alta.
/// </summary>
public sealed class PromocionesApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno)
    {
        public Guid Sucursal => Datos.SucursalId;
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ErrorDto> RechazoAsync(HttpResponseMessage r)
    {
        Assert.False(r.IsSuccessStatusCode, "Se esperaba que no se pudiera.");
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!;
    }

    private static async Task<Caja> CajaAsync(HttpClient http)
    {
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Caja(http, caja, await Entrar("cajero", "1111"), await Entrar("encargado", "2222"), await Entrar("dueno", "3333"));
    }

    private static DateOnly HoyEnLaPc => DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));

    private static string Ficha() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<Guid> DepartamentoAsync(Caja c, string nombre) =>
        (await Leer<DepartamentosDto>(await c.Http.PostAsJsonAsync("api/catalogo/departamentos", new SolicitudDepartamento(c.Encargado.UsuarioId, c.Sucursal, nombre))))
        .Departamentos.Single(d => d.Nombre == nombre).Id;

    /// <summary>Un producto con su menudeo (y su escala desde 12, si se da) y su código; regresa su variante.</summary>
    private static async Task<(Guid Variante, Guid Producto, string Codigo)> ProductoAsync(Caja c, string nombre, Guid departamento, long menudeo,
        string? marca = null, long? desde12 = null)
    {
        var codigo = $"PR{Guid.NewGuid():N}"[..16];
        List<PrecioFichaDto> precios = [new(null, 1000, menudeo)];
        if (desde12 is { } escala)
            precios.Add(new PrecioFichaDto(null, 12000, escala));
        var ficha = await Leer<FichaProductoDto>(await c.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(c.Encargado.UsuarioId, c.Sucursal,
            new FichaProductoDto(null, nombre, departamento, marca, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
                [new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])], precios)]))));
        return (ficha.Variantes[0].Id!.Value, ficha.Id!.Value, codigo);
    }

    /// <summary>Su costo: el último de su proveedor (no tiene existencia).</summary>
    private static async Task CostoAsync(ServicioDePrueba s, Guid variante, long centavos)
    {
        await using var db = s.Base();
        var proveedor = new Proveedor { Nombre = $"Proveedor {Ficha()}" };
        db.Proveedores.Add(proveedor);
        db.ProveedoresVariantes.Add(new ProveedorVariante { ProveedorId = proveedor.Id, VarianteId = variante, UltimoCostoCentavos = centavos, UltimaCompraUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private static SolicitudPromocion Promocion(Caja c, SesionDto quien, string nombre, TipoPromocion tipo, IReadOnlyList<AlcanceSolicitadoDto> alcance,
        int lleva = 0, int paga = 0, int centesimas = 0, DateOnly? desde = null, DateOnly? hasta = null, Guid? id = null, Guid? autorizacion = null,
        DiasDeLaSemana dias = DiasDeLaSemana.Todos, bool soloEstaSucursal = false) =>
        new(quien.UsuarioId, c.Sucursal, c.Datos.Id, nombre, tipo, lleva, paga, centesimas, desde ?? HoyEnLaPc.AddDays(-1), hasta ?? HoyEnLaPc.AddDays(30), dias,
            soloEstaSucursal, alcance, autorizacion, id ?? Ids.Nuevo());

    private static Task<HttpResponseMessage> GuardarAsync(Caja c, SolicitudPromocion s) => c.Http.PostAsJsonAsync("api/promociones", s);

    private static async Task<TurnoDto> TurnoAsync(Caja c)
    {
        var abierto = await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/turno");
        return abierto.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await c.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(c.Datos.Id, c.Cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(abierto);
    }

    /// <summary>Lo que manda la caja al cobrar: con el día con que puso las promociones (o sin él, una caja de antes).</summary>
    private static async Task<HttpResponseMessage> CobrarAsync(Caja c, IReadOnlyList<LineaSolicitada> lineas, DateOnly? dia, long? version = null) =>
        await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, (await TurnoAsync(c)).Id, c.Cajero.UsuarioId, lineas,
            [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)], VentaId: Ids.Nuevo(), VersionCatalogo: version, DiaPromociones: dia));

    private static async Task<List<Bitacora>> BitacoraAsync(ServicioDePrueba s, string contiene)
    {
        await using var db = s.Base();
        return await db.Bitacora.AsNoTracking().Where(b => b.Detalle.Contains(contiene)).OrderBy(b => b.FechaUtc).ToListAsync();
    }

    [Fact]
    public async Task El_encargado_da_de_alta_un_tres_por_dos_que_corre_hoy_y_se_cobra_solo_hasta_que_lo_termina()
    {
        var c = await CajaAsync(servicio.Fabrica.CreateClient());
        var ficha = Ficha();
        var departamento = await DepartamentoAsync(c, $"Cuadernos {ficha}");
        var cuaderno = await ProductoAsync(c, $"Cuaderno {ficha}", departamento, 4500);
        var libreta = await ProductoAsync(c, $"Libreta {ficha}", departamento, 3000);
        var nombre = $"3×2 {ficha}";

        var guardada = await Leer<PromocionFichaDto>(await GuardarAsync(c, Promocion(c, c.Encargado, nombre, TipoPromocion.LlevaNPagaM,
            [new(VarianteId: cuaderno.Variante), new(VarianteId: libreta.Variante)], lleva: 3, paga: 2)));
        await servicio.CatalogoAlDiaAsync();

        Assert.Equal((EstadoPromocion.Hoy, "3×2", 2), (guardada.Estado, guardada.QueHace, guardada.CuantosToca));
        Assert.StartsWith("Encargado de prueba, el ", guardada.Creo);
        var lista = await Leer<ListaPromocionesDto>(await c.Http.GetAsync($"api/promociones?sucursalId={c.Sucursal}&usuarioId={c.Encargado.UsuarioId}"));
        var enLista = lista.Promociones.Single(p => p.Id == guardada.Id);
        Assert.Equal(("3×2", "2 productos", EstadoPromocion.Hoy), (enLista.QueHace, enLista.AQue, enLista.Estado));
        Assert.Null(lista.EnElPiloto);
        Assert.False(lista.VeCostos);

        // La caja que la aplicó (con el día de hoy) cobra 3 al precio de 2, y el ticket lo dice.
        var tresPorDos = new LineaSolicitada(cuaderno.Variante, null, 3000, PromocionId: guardada.Id, AhorroPromocionCentavos: 4500);
        var ticket = await Leer<TicketDto>(await CobrarAsync(c, [tresPorDos], HoyEnLaPc));
        var linea = Assert.Single(ticket.Lineas);
        Assert.Equal((9000L, 4500L, nombre), (linea.ImporteCentavos, linea.AhorroPromocionCentavos, linea.Promocion));
        Assert.Equal(4500, ticket.AhorroCentavos);
        await using (var db = servicio.Base())
        {
            var guardadaEnLaVenta = await db.VentasLineas.AsNoTracking().SingleAsync(l => l.VentaId == ticket.VentaId);
            Assert.Equal((guardada.Id, 4500L, 13500L - 4500L), (guardadaEnLaVenta.PromocionId!.Value, guardadaEnLaVenta.AhorroPromocionCentavos,
                guardadaEnLaVenta.ImporteCentavos));
        }

        // Terminada, ya no se aplica: una caja que todavía la aplicaba recibe los precios de ahora y su ticket se pone al día.
        var terminada = await Leer<PromocionFichaDto>(await c.Http.PostAsJsonAsync($"api/promociones/{guardada.Id}/terminar",
            new SolicitudTerminarPromocion(c.Encargado.UsuarioId, c.Sucursal, c.Datos.Id)));
        await servicio.CatalogoAlDiaAsync();
        Assert.Equal(EstadoPromocion.Terminada, terminada.Estado);
        Assert.StartsWith("Encargado de prueba, el ", terminada.Termino);
        var conflicto = await CobrarAsync(c, [tresPorDos], HoyEnLaPc);
        Assert.Equal(HttpStatusCode.Conflict, conflicto.StatusCode);
        var cambiados = (await conflicto.Content.ReadFromJsonAsync<PreciosCambiadosDto>())!;
        Assert.Equal(cuaderno.Variante, Assert.Single(cambiados.Productos).VarianteId);
        Assert.DoesNotContain(cambiados.Promociones!, p => p.Id == guardada.Id);
        var sinPromocion = await Leer<TicketDto>(await CobrarAsync(c, [new LineaSolicitada(cuaderno.Variante, null, 3000)], HoyEnLaPc));
        Assert.Equal((13500L, (string?)null), (sinPromocion.TotalCentavos, sinPromocion.Lineas[0].Promocion));

        // Cada cambio quedó en la bitácora.
        Assert.Equal(2, (await BitacoraAsync(servicio, $"Promoción «{nombre}»")).Count);
    }

    [Fact]
    public async Task Quien_no_tiene_productos_precios_y_etiquetas_no_las_ve_ni_las_cambia()
    {
        var c = await CajaAsync(servicio.Fabrica.CreateClient());
        var ficha = Ficha();
        var producto = await ProductoAsync(c, $"Regla {ficha}", await DepartamentoAsync(c, $"Reglas {ficha}"), 1500);

        var alta = await RechazoAsync(await GuardarAsync(c, Promocion(c, c.Cajero, $"Del cajero {ficha}", TipoPromocion.SegundoAMitad,
            [new(VarianteId: producto.Variante)])));
        var lista = await c.Http.GetAsync($"api/promociones?sucursalId={c.Sucursal}&usuarioId={c.Cajero.UsuarioId}");

        Assert.Equal("no-permitido", alta.Codigo);
        Assert.Equal(HttpStatusCode.Forbidden, lista.StatusCode);
        await using var db = servicio.Base();
        Assert.False(await db.Promociones.AnyAsync(p => p.Nombre == $"Del cajero {ficha}"));
    }

    /// <summary>
    /// Nunca debajo del costo sin autorización, como Ctrl+D: quien puede vender debajo del costo la guarda (y queda en la
    /// bitácora); quien lo tiene «con autorización», solo con la de quien sí puede; al costo exacto no hace falta. Con su propio
    /// servicio: se le cambia un permiso al encargado.
    /// </summary>
    [Fact]
    public async Task Nunca_queda_debajo_del_costo_sin_autorizacion()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var c = await CajaAsync(propio.Fabrica.CreateClient());
        var ficha = Ficha();
        var departamento = await DepartamentoAsync(c, $"Pegamentos {ficha}");
        var pegamento = await ProductoAsync(c, $"Pegamento {ficha}", departamento, 1000);
        await CostoAsync(propio, pegamento.Variante, 700);

        // Al costo: no pide nada.
        var alCosto = Promocion(c, c.Encargado, $"Al costo {ficha}", TipoPromocion.PrecioEspecial, [new(VarianteId: pegamento.Variante, PrecioCentavos: 700)]);
        var vistaAlCosto = await Leer<VistaPromocionDto>(await c.Http.PostAsJsonAsync("api/promociones/vista", alCosto));
        Assert.Equal(0, vistaAlCosto.BajoCosto.FaltanteCentavos);
        await Leer<PromocionFichaDto>(await GuardarAsync(c, alCosto));

        // Debajo: el encargado puede vender debajo del costo; queda en la bitácora.
        var debajo = alCosto with { Nombre = $"Debajo {ficha}", Id = Ids.Nuevo(), Alcance = [new(VarianteId: pegamento.Variante, PrecioCentavos: 600)] };
        var vista = await Leer<VistaPromocionDto>(await c.Http.PostAsJsonAsync("api/promociones/vista", debajo));
        // El encargado no ve la utilidad: sabe cuáles quedan debajo, no cuánto falta (con un producto, diría su costo: revisión del
        // PR #132). Tampoco el costo ni el margen; el dueño sí.
        Assert.Equal((0L, $"Pegamento {ficha}"), (vista.BajoCosto.FaltanteCentavos, Assert.Single(vista.BajoCosto.Lineas)));
        Assert.True(Assert.Single(vista.Productos).DebajoDelCosto);
        Assert.Null(vista.Productos[0].CostoCentavos);
        var delDueno = await Leer<VistaPromocionDto>(await c.Http.PostAsJsonAsync("api/promociones/vista", debajo with { UsuarioId = c.Dueno.UsuarioId }));
        Assert.Equal(100, delDueno.BajoCosto.FaltanteCentavos);
        Assert.Equal((700m, Margen.Calcular(600, 700m)), (delDueno.Productos[0].CostoCentavos!.Value, delDueno.Productos[0].MargenCentesimas!.Value));
        await Leer<PromocionFichaDto>(await GuardarAsync(c, debajo));
        var bitacora = Assert.Single(await BitacoraAsync(propio, $"La promoción «Debajo {ficha}» deja debajo del costo"));
        Assert.Equal(AccionControlada.VenderBajoCosto, bitacora.Accion);

        // Con «Vender debajo del costo» con autorización: sin ella no se guarda; con la del dueño, sí.
        await Leer<UsuariosYPermisosDto>(await c.Http.PutAsJsonAsync($"api/cajas/{c.Datos.Id}/usuarios-y-permisos/{c.Encargado.UsuarioId}",
            new SolicitudPermisoUsuario(c.Dueno.UsuarioId, AccionControlada.VenderBajoCosto, NivelPermiso.RequiereAutorizacion)));
        var otra = debajo with { Nombre = $"Otra debajo {ficha}", Id = Ids.Nuevo(), Alcance = [new(VarianteId: pegamento.Variante, PrecioCentavos: 650)] };
        var sinAutorizacion = await RechazoAsync(await GuardarAsync(c, otra));
        Assert.Equal("requiere-autorizacion", sinAutorizacion.Codigo);
        var autorizacion = await Leer<AutorizacionDto>(await c.Http.PostAsJsonAsync("api/autorizaciones", new SolicitudAutorizacion(
            AccionControlada.VenderBajoCosto, c.Sucursal, c.Datos.Id, c.Encargado.UsuarioId, "dueno", "3333", 50, "Otra promoción debajo del costo")));
        await Leer<PromocionFichaDto>(await GuardarAsync(c, otra with { AutorizacionBajoCostoId = autorizacion.Id }));
        var autorizada = Assert.Single(await BitacoraAsync(propio, $"La promoción «Otra debajo {ficha}» deja debajo del costo"));
        Assert.Equal(ResultadoBitacora.Autorizado, autorizada.Resultado);
        // Si la respuesta se pierde y se manda otra vez, ya está: no se gasta otra autorización ni queda otro renglón.
        await Leer<PromocionFichaDto>(await GuardarAsync(c, otra with { AutorizacionBajoCostoId = autorizacion.Id }));
        Assert.Single(await BitacoraAsync(propio, $"Promoción «Otra debajo {ficha}»"));
        await using var db = propio.Base();
        Assert.Equal(3, await db.Promociones.CountAsync(p => p.Nombre.EndsWith(ficha)));
    }

    /// <summary>
    /// «Dejar al costo» (el F11 del regreso a clases): cada producto marcado con el precio de su costo, redondeado hacia arriba al
    /// centavo; lo que no tiene costo no se agrega. Solo para quien ve la utilidad.
    /// </summary>
    [Fact]
    public async Task Dejar_al_costo_pone_cada_uno_a_su_costo_y_es_de_quien_ve_la_utilidad()
    {
        var c = await CajaAsync(servicio.Fabrica.CreateClient());
        var ficha = Ficha();
        var departamento = await DepartamentoAsync(c, $"Estrategicos {ficha}");
        var lapiz = await ProductoAsync(c, $"Lápiz {ficha}", departamento, 800);
        var goma = await ProductoAsync(c, $"Goma {ficha}", departamento, 600);
        await CostoAsync(servicio, lapiz.Variante, 437);
        var pedido = new SolicitudProductosPromocion(c.Dueno.UsuarioId, c.Sucursal, [lapiz.Producto, goma.Producto], AlCosto: true);

        var delEncargado = await c.Http.PostAsJsonAsync("api/promociones/productos", pedido with { UsuarioId = c.Encargado.UsuarioId });
        var alCosto = await Leer<ProductosPromocionDto>(await c.Http.PostAsJsonAsync("api/promociones/productos", pedido));

        Assert.Equal(HttpStatusCode.Forbidden, delEncargado.StatusCode);
        var renglon = Assert.Single(alCosto.Productos);
        Assert.Equal((lapiz.Variante, 437L, 800L, "pieza"), (renglon.VarianteId, renglon.PrecioCentavos!.Value, renglon.MenudeoCentavos, renglon.Unidad));
        Assert.Equal([$"Goma {ficha}"], alCosto.SinCosto);
        // Sin «al costo», cualquiera con «Productos, precios y etiquetas» agrega los marcados (sin precio todavía).
        var marcados = await Leer<ProductosPromocionDto>(await c.Http.PostAsJsonAsync("api/promociones/productos",
            pedido with { UsuarioId = c.Encargado.UsuarioId, AlCosto = false }));
        Assert.Equal(2, marcados.Productos.Count);
        Assert.All(marcados.Productos, p => Assert.Null(p.PrecioCentavos));
    }

    /// <summary>
    /// Una de un departamento toca lo que se vende en él, también lo que se da de alta después; una de una marca, igual. No se
    /// juntan con el mayoreo: con poco, la promoción; con mucho, el mayoreo, si deja menos.
    /// </summary>
    [Fact]
    public async Task Una_de_departamento_o_de_marca_toca_lo_que_se_vende_en_ellos_y_no_se_junta_con_el_mayoreo()
    {
        var c = await CajaAsync(servicio.Fabrica.CreateClient());
        var ficha = Ficha();
        var departamento = await DepartamentoAsync(c, $"Colores {ficha}");
        var marca = $"Marca {ficha}";
        var color = await ProductoAsync(c, $"Color {ficha}", departamento, 1000, desde12: 800);
        var deLaMarca = await ProductoAsync(c, $"Plumón {ficha}", await DepartamentoAsync(c, $"Plumones {ficha}"), 2000, marca: marca);

        var porDepartamento = await Leer<PromocionFichaDto>(await GuardarAsync(c, Promocion(c, c.Encargado, $"Colores 10 % {ficha}", TipoPromocion.Porcentaje,
            [new(DepartamentoId: departamento)], centesimas: 1000)));
        var porMarca = await Leer<PromocionFichaDto>(await GuardarAsync(c, Promocion(c, c.Encargado, $"Marca 20 % {ficha}", TipoPromocion.Porcentaje,
            [new(Marca: marca.ToUpperInvariant())], centesimas: 2000)));
        var nuevo = await ProductoAsync(c, $"Color nuevo {ficha}", departamento, 1500);
        await servicio.CatalogoAlDiaAsync();

        Assert.Equal(("10 % menos", 1), (porDepartamento.QueHace, porDepartamento.CuantosToca));
        Assert.Equal([marca], porMarca.Marcas);
        var copia = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));
        Assert.Equal(new[] { color.Variante, nuevo.Variante }.Order(), copia.Promociones!.Single(p => p.Id == porDepartamento.Id).Variantes.Select(v => v.VarianteId));
        Assert.Equal(deLaMarca.Variante, Assert.Single(copia.Promociones!.Single(p => p.Id == porMarca.Id).Variantes).VarianteId);

        // 2 colores: con la promoción, $18. 12: el mayoreo ($96) le conviene más que la promoción ($108).
        var dos = await Leer<TicketDto>(await CobrarAsync(c,
            [new LineaSolicitada(color.Variante, null, 2000, PromocionId: porDepartamento.Id, AhorroPromocionCentavos: 200)], HoyEnLaPc));
        Assert.Equal(1800, dos.TotalCentavos);
        var doce = await Leer<TicketDto>(await CobrarAsync(c, [new LineaSolicitada(color.Variante, null, 12000)], HoyEnLaPc));
        Assert.Equal((9600L, (string?)null, 2400L), (doce.TotalCentavos, doce.Lineas[0].Promocion, doce.Lineas[0].AhorroMayoreoCentavos));
    }

    /// <summary>
    /// El cobro aplica las promociones con el día de la caja: el de aquí o, cerca de la medianoche de aquí (una hora), el de al
    /// lado (un reloj unos minutos atrás o adelante); fuera de eso, con el de aquí, y la respuesta lo dice. No cobra una que el
    /// cajero no vio: contesta (409) con los precios, las promociones de ahora y su día. Una caja de antes (sin el día) cobra como
    /// siempre, sin promociones. Con su propio servicio: su reloj se mueve (revisión del PR #132: antes aceptaba un día de
    /// diferencia a cualquier hora).
    /// </summary>
    [Fact]
    public async Task El_cobro_no_cobra_una_promocion_que_el_cajero_no_vio()
    {
        var hoy = new DateOnly(2027, 8, 20);
        var reloj = new RelojFijo(new DateTimeOffset(HoraMexico.AUtc(hoy.ToDateTime(new TimeOnly(23, 30))), TimeSpan.Zero));
        await using var propio = await ServicioDePrueba.CrearAsync(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(reloj);
        });
        var c = await CajaAsync(propio.Fabrica.CreateClient());
        var ficha = Ficha();
        var mochila = await ProductoAsync(c, $"Mochila {ficha}", await DepartamentoAsync(c, $"Mochilas {ficha}"), 30000);
        await propio.CatalogoAlDiaAsync();
        var antes = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));
        // Empieza mañana.
        var manana = await Leer<PromocionFichaDto>(await GuardarAsync(c, Promocion(c, c.Encargado, $"Mañana {ficha}", TipoPromocion.Porcentaje,
            [new(VarianteId: mochila.Variante)], centesimas: 1500, desde: hoy.AddDays(1), hasta: hoy.AddDays(10))));
        await propio.CatalogoAlDiaAsync();
        Assert.Equal(EstadoPromocion.PorVenir, manana.Estado);
        var sinPromocion = new LineaSolicitada(mochila.Variante, null, 1000);
        var conPromocion = sinPromocion with { PromocionId = manana.Id, AhorroPromocionCentavos = 4500 };

        // Hoy no corre: una caja que la aplicara hoy recibe un 409 con las promociones de ahora (la de mañana, con sus fechas) y el día.
        var hoyNo = await CobrarAsync(c, [conPromocion], hoy, antes.Version);
        Assert.Equal(HttpStatusCode.Conflict, hoyNo.StatusCode);
        var dicho = (await hoyNo.Content.ReadFromJsonAsync<PreciosCambiadosDto>())!;
        Assert.Contains(dicho.Promociones!, p => p.Id == manana.Id && p.Desde == hoy.AddDays(1));
        Assert.Equal(hoy, dicho.DiaPromociones);
        Assert.Equal(30000, (await Leer<TicketDto>(await CobrarAsync(c, [sinPromocion], hoy, antes.Version))).TotalCentavos);
        // A las 23:30, una caja con el reloj unos minutos adelantado (para ella ya es mañana) la cobra; sin verla, 409. Una de
        // antes (sin el día), sin ella.
        Assert.Equal(25500, (await Leer<TicketDto>(await CobrarAsync(c, [conPromocion], hoy.AddDays(1)))).TotalCentavos);
        Assert.Equal(HttpStatusCode.Conflict, (await CobrarAsync(c, [sinPromocion], hoy.AddDays(1))).StatusCode);
        Assert.Equal(30000, (await Leer<TicketDto>(await CobrarAsync(c, [sinPromocion], null))).TotalCentavos);

        // A mediodía, el día de mañana es un reloj descompuesto: se cobra con el de aquí, y el 409 lo dice para que la caja
        // ponga su ticket al día con él (si no, se repetiría).
        reloj.Ahora = new DateTimeOffset(HoraMexico.AUtc(hoy.ToDateTime(new TimeOnly(12, 0))), TimeSpan.Zero);
        var adelantada = await CobrarAsync(c, [conPromocion], hoy.AddDays(1));
        Assert.Equal(HttpStatusCode.Conflict, adelantada.StatusCode);
        var conSuDia = (await adelantada.Content.ReadFromJsonAsync<PreciosCambiadosDto>())!;
        Assert.Equal(hoy, conSuDia.DiaPromociones);
        Assert.Equal(30000, (await Leer<TicketDto>(await CobrarAsync(c, [sinPromocion], hoy.AddDays(1)))).TotalCentavos);
        // Ni con varios días de diferencia.
        Assert.Equal(hoy, (await (await CobrarAsync(c, [conPromocion], hoy.AddDays(5))).Content.ReadFromJsonAsync<PreciosCambiadosDto>())!.DiaPromociones);
    }

    /// <summary>
    /// Revisión del PR #132: a mediodía del día siguiente, una caja con el reloj un día atrás ya no cobra la promoción que terminó
    /// ayer, ni el 409 se la da; justo después de medianoche (su reloj unos minutos atrás), sí.
    /// </summary>
    [Fact]
    public async Task A_mediodia_una_caja_con_el_reloj_atrasado_no_cobra_la_promocion_que_ya_termino()
    {
        var dia = new DateOnly(2027, 8, 20);
        var reloj = new RelojFijo(new DateTimeOffset(HoraMexico.AUtc(dia.ToDateTime(new TimeOnly(12, 0))), TimeSpan.Zero));
        await using var propio = await ServicioDePrueba.CrearAsync(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(reloj);
        });
        var c = await CajaAsync(propio.Fabrica.CreateClient());
        var ficha = Ficha();
        var calculadora = await ProductoAsync(c, $"Calculadora {ficha}", await DepartamentoAsync(c, $"Calculadoras {ficha}"), 10000);
        var guardada = await Leer<PromocionFichaDto>(await GuardarAsync(c, Promocion(c, c.Encargado, $"Mitad {ficha}", TipoPromocion.Porcentaje,
            [new(VarianteId: calculadora.Variante)], centesimas: 5000, desde: dia.AddDays(-5), hasta: dia)));
        await propio.CatalogoAlDiaAsync();
        var sinPromocion = new LineaSolicitada(calculadora.Variante, null, 1000);
        var conPromocion = sinPromocion with { PromocionId = guardada.Id, AhorroPromocionCentavos = 5000 };

        reloj.Ahora = reloj.Ahora.AddDays(1);
        var copia = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));
        var sinElla = await Leer<TicketDto>(await CobrarAsync(c, [sinPromocion], dia));
        var conElla = await CobrarAsync(c, [conPromocion], dia);

        Assert.DoesNotContain(copia.Promociones!, p => p.Id == guardada.Id);
        Assert.Equal(10000, sinElla.TotalCentavos);
        Assert.Equal(HttpStatusCode.Conflict, conElla.StatusCode);
        var dicho = (await conElla.Content.ReadFromJsonAsync<PreciosCambiadosDto>())!;
        Assert.DoesNotContain(dicho.Promociones!, p => p.Id == guardada.Id);
        Assert.Equal(dia.AddDays(1), dicho.DiaPromociones);
        // A las 00:30, una caja con el reloj unos minutos atrás todavía va en su último día: se cobra con ella.
        reloj.Ahora = new DateTimeOffset(HoraMexico.AUtc(dia.AddDays(1).ToDateTime(new TimeOnly(0, 30))), TimeSpan.Zero);
        Assert.Equal(5000, (await Leer<TicketDto>(await CobrarAsync(c, [conPromocion], dia))).TotalCentavos);
    }

    /// <summary>
    /// Lo que se cobró sin conexión con una promoción entra como se cobró (la bandeja), aunque mientras tanto la terminaran: el
    /// precio de la línea, lo que se ahorró y su nombre; sin quedar como «precio cambiado».
    /// </summary>
    [Fact]
    public async Task Lo_que_se_cobro_sin_conexion_entra_con_su_promocion_aunque_ya_haya_terminado()
    {
        var c = await CajaAsync(servicio.Fabrica.CreateClient());
        var ficha = Ficha();
        var tijeras = await ProductoAsync(c, $"Tijeras {ficha}", await DepartamentoAsync(c, $"Tijeras {ficha}"), 2500);
        var promocion = await Leer<PromocionFichaDto>(await GuardarAsync(c, Promocion(c, c.Encargado, $"Segunda a mitad {ficha}", TipoPromocion.SegundoAMitad,
            [new(VarianteId: tijeras.Variante)])));
        await Leer<PromocionFichaDto>(await c.Http.PostAsJsonAsync($"api/promociones/{promocion.Id}/terminar",
            new SolicitudTerminarPromocion(c.Encargado.UsuarioId, c.Sucursal)));
        await servicio.CatalogoAlDiaAsync();
        var turno = await TurnoAsync(c);
        int folio;
        await using (var db = servicio.Base())
            folio = await db.Ventas.Where(v => v.Serie == c.Datos.Serie).MaxAsync(v => (int?)v.Folio) ?? 0;
        // Como la manda la caja: con la presentación de la línea.
        var pieza = (await c.Http.GetFromJsonAsync<ProductoVentaDto>($"api/productos/{tijeras.Variante}?sucursalId={c.Sucursal}"))!.Presentacion.Id;
        var venta = new VentaContingenciaDto(Ids.Nuevo(), c.Datos.Id, turno.Id, c.Cajero.UsuarioId, folio + 1, DateTime.UtcNow,
            [new LineaContingenciaDto(tijeras.Variante, pieza, $"Tijeras {ficha}", 2000, 2500, PromocionId: promocion.Id, Promocion: promocion.Nombre,
                AhorroPromocionCentavos: 1250)],
            [new CobroSolicitado(FormaPago.Efectivo, 4000)], RedondeoCentavos: 0);

        async Task SubirAsync()
        {
            var subida = await c.Http.PostAsJsonAsync("api/bandeja/ventas", venta);
            Assert.True(subida.IsSuccessStatusCode, $"{subida.StatusCode}: {await subida.Content.ReadAsStringAsync()}");
        }
        await SubirAsync();
        // Si sube otra vez, es la misma.
        await SubirAsync();

        await using var lectura = servicio.Base();
        var guardada = await lectura.Ventas.AsNoTracking().Include(v => v.Lineas).SingleAsync(v => v.Id == venta.Id);
        var linea = Assert.Single(guardada.Lineas);
        Assert.Equal((3750L, 2500L, 1250L, promocion.Nombre), (guardada.TotalCentavos, linea.PrecioCentavos, linea.AhorroPromocionCentavos, linea.Promocion));
        Assert.Null(linea.PrecioOriginalCentavos);
    }

    /// <summary>
    /// La copia de una caja lleva las promociones de su sucursal (y las de todas) que no han terminado, también las que vienen, con
    /// su huella; la que pasó su último día sale de la copia completa y de los cambios desde la de antes, aunque no haya cambiado
    /// nada más, y la copia puesta al día queda igual que la completa. Con su propio servicio: su día se adelanta.
    /// </summary>
    [Fact]
    public async Task La_que_paso_su_ultimo_dia_sale_de_la_copia_sin_que_cambie_nada_mas()
    {
        var hoy = new DateOnly(2027, 8, 20);
        var reloj = new RelojFijo(new DateTimeOffset(HoraMexico.AUtc(hoy.ToDateTime(new TimeOnly(20, 0))), TimeSpan.Zero));
        await using var propio = await ServicioDePrueba.CrearAsync(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(reloj);
        });
        var c = await CajaAsync(propio.Fabrica.CreateClient());
        var ficha = Ficha();
        var departamento = await DepartamentoAsync(c, $"Temporada {ficha}");
        var termina = await ProductoAsync(c, $"Forro {ficha}", departamento, 1200);
        var sigue = await ProductoAsync(c, $"Etiqueta {ficha}", departamento, 500);
        Task<HttpResponseMessage> Alta(string nombre, Guid variante, DateOnly desde, DateOnly hasta, bool soloAqui = false) =>
            GuardarAsync(c, Promocion(c, c.Encargado, nombre, TipoPromocion.LlevaNPagaM, [new(VarianteId: variante)], lleva: 3, paga: 2, desde: desde,
                hasta: hasta, soloEstaSucursal: soloAqui));
        var deHoy = await Leer<PromocionFichaDto>(await Alta($"Termina hoy {ficha}", termina.Variante, hoy.AddDays(-5), hoy));
        var deManana = await Leer<PromocionFichaDto>(await Alta($"Empieza mañana {ficha}", sigue.Variante, hoy.AddDays(1), hoy.AddDays(9), soloAqui: true));
        await propio.CatalogoAlDiaAsync();
        var antes = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));
        Assert.Equal(CopiaAlDia.Formato, antes.Formato);
        // La de hoy y la de mañana (que empieza a medianoche: la caja la aplica sin bajar nada).
        Assert.Contains(antes.Promociones!, p => p.Id == deHoy.Id && p.Hasta == hoy);
        Assert.Contains(antes.Promociones!, p => p.Id == deManana.Id && p.Desde == hoy.AddDays(1));
        Assert.Equal(PromocionDto.Huella(antes.Promociones!), antes.HuellaPromociones);
        async Task<CambiosCopiaDto> CambiosAsync(PaqueteContingenciaDto desde) => await Leer<CambiosCopiaDto>(await c.Http.GetAsync(
            $"api/cajas/{c.Datos.Id}/cambios?desde={desde.Version}&existencias={desde.VersionExistencias}&base={desde.Base}&formato={CopiaAlDia.Formato}"
            + $"&vendidos={desde.VersionVendidos}&elegidos={desde.VersionElegidos}&promociones={desde.HuellaPromociones}"));
        // Sin cambios, nada: la caja ya tiene las de ahora.
        Assert.Null((await CambiosAsync(antes)).Promociones);

        // Al día siguiente, sin que cambie nada más: la que terminó ayer ya no va, la de hoy sí.
        reloj.Ahora = reloj.Ahora.AddDays(1);
        var despues = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));
        var cambios = await CambiosAsync(antes);

        Assert.Equal(antes.Version, despues.Version);
        Assert.DoesNotContain(despues.Promociones!, p => p.Id == deHoy.Id);
        Assert.Contains(despues.Promociones!, p => p.Id == deManana.Id);
        Assert.NotNull(cambios.Promociones);
        Assert.True(cambios.CambiaCatalogo);
        var alDia = CopiaAlDia.Aplicar(antes, cambios);
        Assert.Equal(JsonSerializer.Serialize(despues.Promociones, JsonSerializerOptions.Web), JsonSerializer.Serialize(alDia.Promociones, JsonSerializerOptions.Web));
        Assert.Equal(despues.HuellaPromociones, alDia.HuellaPromociones);
        Assert.Null((await CambiosAsync(alDia)).Promociones);
        // En la lista, la que terminó dice cuándo.
        var lista = await Leer<ListaPromocionesDto>(await c.Http.GetAsync($"api/promociones?sucursalId={c.Sucursal}&usuarioId={c.Encargado.UsuarioId}"));
        Assert.Equal(hoy.AddDays(1), lista.Hoy);
        Assert.Equal(EstadoPromocion.Termino, lista.Promociones.Single(p => p.Id == deHoy.Id).Estado);
        Assert.Equal((EstadoPromocion.Hoy, c.Datos.Sucursal), (lista.Promociones.Single(p => p.Id == deManana.Id).Estado,
            lista.Promociones.Single(p => p.Id == deManana.Id).Sucursal));
    }

    /// <summary>
    /// El piloto con eleventa (su lectura prendida): los precios se ponen en eleventa. Las promociones no se dan de alta aquí (la
    /// lista dice por qué), no van en la copia de la caja y el cobro no las aplica, aunque haya una en la base; sí se pueden
    /// terminar.
    /// </summary>
    [Fact]
    public async Task En_el_piloto_no_se_aplican_ni_se_dan_de_alta()
    {
        var fuera = await CajaAsync(servicio.Fabrica.CreateClient());
        var ficha = Ficha();
        var compas = await ProductoAsync(fuera, $"Compás {ficha}", await DepartamentoAsync(fuera, $"Geometría {ficha}"), 4000);
        var deAntes = await Leer<PromocionFichaDto>(await GuardarAsync(fuera, Promocion(fuera, fuera.Encargado, $"De antes del piloto {ficha}",
            TipoPromocion.Porcentaje, [new(VarianteId: compas.Variante)], centesimas: 1000)));
        await using var piloto = servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));
        var c = await CajaAsync(piloto.CreateClient());

        var lista = await Leer<ListaPromocionesDto>(await c.Http.GetAsync($"api/promociones?sucursalId={c.Sucursal}&usuarioId={c.Encargado.UsuarioId}"));
        var alta = await RechazoAsync(await GuardarAsync(c, Promocion(c, c.Encargado, $"En el piloto {ficha}", TipoPromocion.Porcentaje,
            [new(VarianteId: compas.Variante)], centesimas: 1000)));
        var copia = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));
        var ticket = await Leer<TicketDto>(await CobrarAsync(c, [new LineaSolicitada(compas.Variante, null, 1000)], HoyEnLaPc));

        Assert.Equal(RegistroPromociones.EnElPiloto, lista.EnElPiloto);
        Assert.Contains(lista.Promociones, p => p.Id == deAntes.Id);
        Assert.Equal(RegistroPromociones.EnElPiloto, alta.Mensaje);
        Assert.Empty(copia.Promociones!);
        Assert.Equal(PromocionDto.Huella([]), copia.HuellaPromociones);
        Assert.Equal((4000L, (string?)null), (ticket.TotalCentavos, ticket.Lineas[0].Promocion));
        // Terminar sí se puede (solo quita).
        var terminada = await Leer<PromocionFichaDto>(await c.Http.PostAsJsonAsync($"api/promociones/{deAntes.Id}/terminar",
            new SolicitudTerminarPromocion(c.Encargado.UsuarioId, c.Sucursal)));
        Assert.Equal(EstadoPromocion.Terminada, terminada.Estado);
    }

    // ── Revisión del PR #132 ────────────────────────────────────────────────

    /// <summary>Una autorización del dueño para una acción, como la pide la caja (sin <paramref name="para"/>, para el cajero).</summary>
    private static async Task<Guid> AutorizacionAsync(Caja c, AccionControlada accion, long? monto, string detalle, SesionDto? para = null) =>
        (await Leer<AutorizacionDto>(await c.Http.PostAsJsonAsync("api/autorizaciones", new SolicitudAutorizacion(accion, c.Sucursal, c.Datos.Id,
            (para ?? c.Cajero).UsuarioId, "dueno", "3333", monto, detalle)))).Id;

    /// <summary>Que quien entró como encargado necesite la autorización de otro para vender debajo del costo.</summary>
    private static async Task BajoCostoConAutorizacionAsync(Caja c) =>
        await Leer<UsuariosYPermisosDto>(await c.Http.PutAsJsonAsync($"api/cajas/{c.Datos.Id}/usuarios-y-permisos/{c.Encargado.UsuarioId}",
            new SolicitudPermisoUsuario(c.Dueno.UsuarioId, AccionControlada.VenderBajoCosto, NivelPermiso.RequiereAutorizacion)));

    /// <summary>
    /// Un precio cambiado con Ctrl+E justo al de la lista se cobra como precio cambiado (con su autorización), sin promoción,
    /// también con la versión y el día de la caja: antes daba 409 una y otra vez. Y en el piloto con eleventa, donde no hay
    /// promociones.
    /// </summary>
    [Fact]
    public async Task Un_precio_cambiado_al_de_la_lista_se_cobra_como_precio_cambiado_tambien_en_el_piloto()
    {
        var c = await CajaAsync(servicio.Fabrica.CreateClient());
        var ficha = Ficha();
        var departamento = await DepartamentoAsync(c, $"Escolares {ficha}");
        var cuaderno = await ProductoAsync(c, $"Cuaderno {ficha}", departamento, 2500);
        var conPromocion = await ProductoAsync(c, $"Carpeta {ficha}", departamento, 2000);
        await Leer<PromocionFichaDto>(await GuardarAsync(c, Promocion(c, c.Encargado, $"10 % {ficha}", TipoPromocion.Porcentaje,
            [new(VarianteId: conPromocion.Variante)], centesimas: 1000)));
        await servicio.CatalogoAlDiaAsync();
        var copia = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));

        var ticket = await Leer<TicketDto>(await CobrarAsync(c,
        [
            new LineaSolicitada(cuaderno.Variante, null, 1000, PrecioCentavos: 2500,
                AutorizacionId: await AutorizacionAsync(c, AccionControlada.CambiarPrecio, 2500, $"Precio de «Cuaderno {ficha}»")),
            new LineaSolicitada(conPromocion.Variante, null, 1000, PrecioCentavos: 2000,
                AutorizacionId: await AutorizacionAsync(c, AccionControlada.CambiarPrecio, 2000, $"Precio de «Carpeta {ficha}»")),
        ], HoyEnLaPc, copia.Version));

        Assert.Equal(4500, ticket.TotalCentavos);
        Assert.All(ticket.Lineas, l => Assert.Null(l.Promocion));
        await using (var db = servicio.Base())
        {
            var lineas = await db.VentasLineas.AsNoTracking().Where(l => l.VentaId == ticket.VentaId).ToListAsync();
            Assert.All(lineas, l => Assert.Equal((l.PrecioCentavos, (Guid?)null), (l.PrecioOriginalCentavos!.Value, l.PromocionId)));
        }
        Assert.Equal(2, (await BitacoraAsync(servicio, ficha)).Count(b => b.Accion == AccionControlada.CambiarPrecio && b.Resultado == ResultadoBitacora.Autorizado));

        await using var piloto = servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));
        var enElPiloto = await CajaAsync(piloto.CreateClient());
        var delPiloto = await Leer<TicketDto>(await CobrarAsync(enElPiloto, [new LineaSolicitada(cuaderno.Variante, null, 1000, PrecioCentavos: 2500,
            AutorizacionId: await AutorizacionAsync(enElPiloto, AccionControlada.CambiarPrecio, 2500, $"Precio de «Cuaderno {ficha}» en el piloto"))], HoyEnLaPc));
        Assert.Equal(2500, delPiloto.TotalCentavos);
    }

    /// <summary>
    /// Una caja de 12 con precio propio ($120, $10 la pieza) de un producto de $12 la pieza que cuesta $7.50: con un 3×2, tres
    /// cajas salen en $240 ($6.67 la pieza). El costo se revisa con eso, como lo cobra la venta (antes, con los $8 de la pieza,
    /// y se vendía debajo del costo sin autorización).
    /// </summary>
    [Fact]
    public async Task Un_tres_por_dos_se_revisa_contra_el_costo_con_la_presentacion_de_precio_propio()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var c = await CajaAsync(propio.Fabrica.CreateClient());
        var ficha = Ficha();
        var producto = await Leer<FichaProductoDto>(await c.Http.PostAsJsonAsync("api/catalogo/productos", new SolicitudGuardarProducto(c.Encargado.UsuarioId,
            c.Sucursal, new FichaProductoDto(null, $"Lápices {ficha}", await DepartamentoAsync(c, $"Lápices {ficha}"), null, TipoProducto.Mercancia,
                Impuesto.Iva16, null, true, false,
                [
                    new VarianteFichaDto(null, "", UnidadBase.Pieza, true,
                    [
                        new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto($"PL{Guid.NewGuid():N}"[..16])]),
                        new PresentacionFichaDto(null, "Caja", 12000, false, [new CodigoFichaDto($"PC{Guid.NewGuid():N}"[..16])]),
                    ],
                    [new PrecioFichaDto(null, 1000, 1200), new PrecioFichaDto("Caja", 1000, 12000)]),
                ]))));
        var variante = producto.Variantes[0].Id!.Value;
        var caja = producto.Variantes[0].Presentaciones.Single(p => p.Nombre == "Caja").Id!.Value;
        await CostoAsync(propio, variante, 750);
        await BajoCostoConAutorizacionAsync(c);
        var solicitud = Promocion(c, c.Encargado, $"3×2 {ficha}", TipoPromocion.LlevaNPagaM, [new(VarianteId: variante)], lleva: 3, paga: 2);

        var vista = await Leer<VistaPromocionDto>(await c.Http.PostAsJsonAsync("api/promociones/vista", solicitud with { UsuarioId = c.Dueno.UsuarioId }));
        var sinAutorizacion = await RechazoAsync(await GuardarAsync(c, solicitud));
        var guardada = await Leer<PromocionFichaDto>(await GuardarAsync(c, solicitud with
        {
            AutorizacionBajoCostoId = await AutorizacionAsync(c, AccionControlada.VenderBajoCosto, null, $"3×2 {ficha} debajo del costo", c.Encargado),
        }));
        await propio.CatalogoAlDiaAsync();
        var ticket = await Leer<TicketDto>(await CobrarAsync(c, [new LineaSolicitada(variante, caja, 3000, PromocionId: guardada.Id, AhorroPromocionCentavos: 12000)],
            HoyEnLaPc));

        // $7.50 − $6.67 por pieza.
        Assert.Equal((84L, $"Lápices {ficha}"), (vista.BajoCosto.FaltanteCentavos, Assert.Single(vista.BajoCosto.Lineas)));
        Assert.Equal("requiere-autorizacion", sinAutorizacion.Codigo);
        Assert.Equal(24000, ticket.TotalCentavos);
        Assert.Equal(ResultadoBitacora.Autorizado, Assert.Single(await BitacoraAsync(propio, $"La promoción «3×2 {ficha}» deja debajo del costo")).Resultado);
    }

    /// <summary>
    /// Un 50 % a un departamento; después llega a él un producto de $10 que cuesta $7. Sin autorización se queda fuera: la copia de
    /// las cajas no se la lleva, el cobro no la aplica (la caja que la aplicara recibe un 409) y el detalle lo dice («Fuera por el
    /// costo»). Igual el que ya estaba, si le sube el costo. Guardarla otra vez con la autorización de quien puede se la da.
    /// </summary>
    [Fact]
    public async Task Lo_que_despues_queda_debajo_del_costo_se_queda_fuera_hasta_que_se_autoriza()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var c = await CajaAsync(propio.Fabrica.CreateClient());
        var ficha = Ficha();
        var departamento = await DepartamentoAsync(c, $"Mitad {ficha}");
        var conMargen = await ProductoAsync(c, $"Con margen {ficha}", departamento, 1000);
        await CostoAsync(propio, conMargen.Variante, 300);
        await BajoCostoConAutorizacionAsync(c);
        var solicitud = Promocion(c, c.Encargado, $"Mitad {ficha}", TipoPromocion.Porcentaje, [new(DepartamentoId: departamento)], centesimas: 5000);
        var guardada = await Leer<PromocionFichaDto>(await GuardarAsync(c, solicitud));

        var nuevo = await ProductoAsync(c, $"Nuevo {ficha}", departamento, 1000);
        await CostoAsync(propio, nuevo.Variante, 700);
        await propio.CatalogoAlDiaAsync();
        async Task<IReadOnlyList<Guid>?> EnLaCopiaAsync() => (await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete")))
            .Promociones!.SingleOrDefault(p => p.Id == guardada.Id)?.Variantes.Select(v => v.VarianteId).ToList();
        async Task<PromocionFichaDto> FichaAsync() =>
            await Leer<PromocionFichaDto>(await c.Http.GetAsync($"api/promociones/{guardada.Id}?sucursalId={c.Sucursal}&usuarioId={c.Encargado.UsuarioId}"));
        var sinElla = new LineaSolicitada(nuevo.Variante, null, 1000);

        Assert.Equal([conMargen.Variante], await EnLaCopiaAsync());
        Assert.Equal(1000, (await Leer<TicketDto>(await CobrarAsync(c, [sinElla], HoyEnLaPc))).TotalCentavos);
        Assert.Equal(HttpStatusCode.Conflict,
            (await CobrarAsync(c, [sinElla with { PromocionId = guardada.Id, AhorroPromocionCentavos = 500 }], HoyEnLaPc)).StatusCode);
        var conUnoFuera = await FichaAsync();
        Assert.Equal([$"Nuevo {ficha}"], conUnoFuera.FueraPorElCosto!);
        Assert.Equal(1, conUnoFuera.CuantosToca);

        // Al que ya estaba le sube el costo: también se queda fuera (ya no le queda ninguno: no va en la copia).
        await CostoAsync(propio, conMargen.Variante, 600);
        Assert.Null(await EnLaCopiaAsync());
        Assert.Equal(2, (await FichaAsync()).FueraPorElCosto!.Count);

        // Guardarla otra vez, sin cambiarla, con la autorización del dueño: la llevan los dos.
        var sinAutorizacion = await RechazoAsync(await GuardarAsync(c, solicitud));
        await Leer<PromocionFichaDto>(await GuardarAsync(c, solicitud with
        {
            AutorizacionBajoCostoId = await AutorizacionAsync(c, AccionControlada.VenderBajoCosto, null, $"Mitad {ficha} debajo del costo", c.Encargado),
        }));
        Assert.Equal("requiere-autorizacion", sinAutorizacion.Codigo);
        Assert.Equal(new[] { conMargen.Variante, nuevo.Variante }.Order(), await EnLaCopiaAsync());
        Assert.Empty((await FichaAsync()).FueraPorElCosto!);
        Assert.Equal(500, (await Leer<TicketDto>(await CobrarAsync(c, [sinElla with { PromocionId = guardada.Id, AhorroPromocionCentavos = 500 }],
            HoyEnLaPc))).TotalCentavos);
    }

    /// <summary>
    /// El descuento fijo del cliente registrado no va encima de la promoción: se queda lo que más le conviene al cliente. «Al
    /// costo» ($7 de $10) le gana a su 5 %: se cobra a $7, sin descuento ni autorización por el costo, aunque la caja mande los dos
    /// (antes se cobraba $6.65 y pedía autorización). Con 20 %, su descuento le gana a un 10 %: sin la promoción.
    /// </summary>
    [Fact]
    public async Task El_descuento_del_cliente_registrado_no_va_encima_de_la_promocion()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var c = await CajaAsync(propio.Fabrica.CreateClient());
        var ficha = Ficha();
        var departamento = await DepartamentoAsync(c, $"Clientes {ficha}");
        var alCosto = await ProductoAsync(c, $"Al costo {ficha}", departamento, 1000);
        await CostoAsync(propio, alCosto.Variante, 700);
        var conDiez = await ProductoAsync(c, $"Con 10 % {ficha}", departamento, 1000);
        await CostoAsync(propio, conDiez.Variante, 300);
        async Task<ClienteRegistradoDto> ClienteAsync(string nombre, int descuento) =>
            await Leer<ClienteRegistradoDto>(await c.Http.PostAsJsonAsync("api/clientes", new SolicitudGuardarCliente(c.Encargado.UsuarioId, c.Sucursal,
                new ClienteRegistradoDto(Guid.Empty, nombre, descuento, null, true))));
        var maestra = await ClienteAsync($"Maestra {ficha}", 500);
        var escuela = await ClienteAsync($"Escuela {ficha}", 2000);
        var especial = await Leer<PromocionFichaDto>(await GuardarAsync(c, Promocion(c, c.Encargado, $"Al costo {ficha}", TipoPromocion.PrecioEspecial,
            [new(VarianteId: alCosto.Variante, PrecioCentavos: 700)])));
        var diez = await Leer<PromocionFichaDto>(await GuardarAsync(c, Promocion(c, c.Encargado, $"10 % {ficha}", TipoPromocion.Porcentaje,
            [new(VarianteId: conDiez.Variante)], centesimas: 1000)));
        await propio.CatalogoAlDiaAsync();
        async Task<HttpResponseMessage> CobrarConClienteAsync(ClienteRegistradoDto cliente, LineaSolicitada linea) =>
            await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, (await TurnoAsync(c)).Id, c.Cajero.UsuarioId, [linea],
                [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)], VentaId: Ids.Nuevo(), ClienteId: cliente.Id, DiaPromociones: HoyEnLaPc));

        var conLosDos = await Leer<TicketDto>(await CobrarConClienteAsync(maestra, new LineaSolicitada(alCosto.Variante, null, 1000, PromocionId: especial.Id,
            AhorroPromocionCentavos: 300, DescuentoCentesimas: 500, MotivoDescuento: maestra.MotivoDescuento())));
        var conSuDescuento = await Leer<TicketDto>(await CobrarConClienteAsync(escuela, new LineaSolicitada(conDiez.Variante, null, 1000,
            DescuentoCentesimas: 2000, MotivoDescuento: escuela.MotivoDescuento())));
        var conLaPromocion = await CobrarConClienteAsync(escuela, new LineaSolicitada(conDiez.Variante, null, 1000, PromocionId: diez.Id,
            AhorroPromocionCentavos: 100, DescuentoCentesimas: 2000, MotivoDescuento: escuela.MotivoDescuento()));

        Assert.Equal((700L, 0L, $"Al costo {ficha}"), (conLosDos.TotalCentavos, conLosDos.Lineas[0].DescuentoCentavos, conLosDos.Lineas[0].Promocion));
        Assert.Equal((800L, 200L, (string?)null), (conSuDescuento.TotalCentavos, conSuDescuento.Lineas[0].DescuentoCentavos, conSuDescuento.Lineas[0].Promocion));
        // Una caja que pusiera los dos donde le gana su descuento: no se cobra con la promoción que no aplica (409).
        Assert.Equal(HttpStatusCode.Conflict, conLaPromocion.StatusCode);
        Assert.DoesNotContain(await BitacoraAsync(propio, ficha), b => b.Accion == AccionControlada.VenderBajoCosto);
    }

    /// <summary>
    /// Un descuento en monto de $60 a un renglón de $100 que aquí lleva una promoción de 50 % que la caja no vio: primero el 409
    /// (la caja pone su ticket al día y el cajero ve el total), no el error del descuento. Sin promoción de por medio, el error.
    /// </summary>
    [Fact]
    public async Task Un_descuento_mas_grande_que_el_renglon_con_su_promocion_da_409_antes_que_el_error()
    {
        var c = await CajaAsync(servicio.Fabrica.CreateClient());
        var ficha = Ficha();
        var lampara = await ProductoAsync(c, $"Lámpara {ficha}", await DepartamentoAsync(c, $"Lámparas {ficha}"), 10000);
        var mitad = await Leer<PromocionFichaDto>(await GuardarAsync(c, Promocion(c, c.Encargado, $"Mitad {ficha}", TipoPromocion.Porcentaje,
            [new(VarianteId: lampara.Variante)], centesimas: 5000)));
        await servicio.CatalogoAlDiaAsync();
        var conDescuento = new LineaSolicitada(lampara.Variante, null, 1000, DescuentoCentavos: 6000, MotivoDescuento: "Cliente frecuente");

        var sinVerla = await CobrarAsync(c, [conDescuento], HoyEnLaPc);
        var viendola = await RechazoAsync(await CobrarAsync(c, [conDescuento with { PromocionId = mitad.Id, AhorroPromocionCentavos = 5000 }], HoyEnLaPc));

        Assert.Equal(HttpStatusCode.Conflict, sinVerla.StatusCode);
        Assert.Equal(lampara.Variante, Assert.Single((await sinVerla.Content.ReadFromJsonAsync<PreciosCambiadosDto>())!.Productos).VarianteId);
        Assert.Contains("va de más de $0 a", viendola.Mensaje);
    }
}
