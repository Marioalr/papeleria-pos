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
        Assert.Equal((100L, $"Pegamento {ficha}"), (vista.BajoCosto.FaltanteCentavos, Assert.Single(vista.BajoCosto.Lineas)));
        Assert.True(Assert.Single(vista.Productos).DebajoDelCosto);
        // El encargado no ve el costo ni el margen; el dueño sí.
        Assert.Null(vista.Productos[0].CostoCentavos);
        var delDueno = await Leer<VistaPromocionDto>(await c.Http.PostAsJsonAsync("api/promociones/vista", debajo with { UsuarioId = c.Dueno.UsuarioId }));
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
    /// El cobro aplica las promociones con el día de la caja (el suyo, el de antes o el de después: casi a medianoche), y no
    /// cobra una que el cajero no vio: contesta con los precios y las promociones de ahora. Una caja de antes (sin el día) cobra
    /// como siempre, sin promociones.
    /// </summary>
    [Fact]
    public async Task El_cobro_no_cobra_una_promocion_que_el_cajero_no_vio()
    {
        var c = await CajaAsync(servicio.Fabrica.CreateClient());
        var ficha = Ficha();
        var mochila = await ProductoAsync(c, $"Mochila {ficha}", await DepartamentoAsync(c, $"Mochilas {ficha}"), 30000);
        await servicio.CatalogoAlDiaAsync();
        var antes = await Leer<PaqueteContingenciaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/paquete"));
        var hoy = HoyEnLaPc;
        // Empieza mañana.
        var manana = await Leer<PromocionFichaDto>(await GuardarAsync(c, Promocion(c, c.Encargado, $"Mañana {ficha}", TipoPromocion.Porcentaje,
            [new(VarianteId: mochila.Variante)], centesimas: 1500, desde: hoy.AddDays(1), hasta: hoy.AddDays(10))));
        await servicio.CatalogoAlDiaAsync();
        Assert.Equal(EstadoPromocion.PorVenir, manana.Estado);
        var sinPromocion = new LineaSolicitada(mochila.Variante, null, 1000);
        var conPromocion = sinPromocion with { PromocionId = manana.Id, AhorroPromocionCentavos = 4500 };

        // Hoy no corre: una caja que la aplicara hoy recibe un 409 con las promociones de ahora (la de mañana, con sus fechas).
        var hoyNo = await CobrarAsync(c, [conPromocion], hoy, antes.Version);
        Assert.Equal(HttpStatusCode.Conflict, hoyNo.StatusCode);
        var dicho = (await hoyNo.Content.ReadFromJsonAsync<PreciosCambiadosDto>())!;
        Assert.Contains(dicho.Promociones!, p => p.Id == manana.Id && p.Desde == hoy.AddDays(1));
        Assert.Equal(30000, (await Leer<TicketDto>(await CobrarAsync(c, [sinPromocion], hoy, antes.Version))).TotalCentavos);
        // Una caja que ya pasó la medianoche (su día es mañana) la cobra; y una de antes (sin el día), sin ella.
        Assert.Equal(25500, (await Leer<TicketDto>(await CobrarAsync(c, [conPromocion], hoy.AddDays(1)))).TotalCentavos);
        var mananaSinVerla = await CobrarAsync(c, [sinPromocion], hoy.AddDays(1));
        Assert.Equal(HttpStatusCode.Conflict, mananaSinVerla.StatusCode);
        Assert.Equal(30000, (await Leer<TicketDto>(await CobrarAsync(c, [sinPromocion], null))).TotalCentavos);
        // Un día que no es el del servicio, ni el de antes ni el de después (un reloj descompuesto): se cobra con el de hoy.
        Assert.Equal(HttpStatusCode.Conflict, (await CobrarAsync(c, [conPromocion], hoy.AddDays(5))).StatusCode);
        // Un precio cambiado en la caja (con su autorización) no lleva promoción, aunque sea el de la lista.
        var autorizado = await Leer<AutorizacionDto>(await c.Http.PostAsJsonAsync("api/autorizaciones", new SolicitudAutorizacion(
            AccionControlada.CambiarPrecio, c.Sucursal, c.Datos.Id, c.Cajero.UsuarioId, "dueno", "3333", 30000, $"Precio de «Mochila {ficha}»")));
        var cambiado = await Leer<TicketDto>(await CobrarAsync(c, [sinPromocion with { PrecioCentavos = 30000, AutorizacionId = autorizado.Id }], hoy.AddDays(1)));
        Assert.Equal((30000L, (string?)null), (cambiado.TotalCentavos, cambiado.Lineas[0].Promocion));
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
}
