using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El celular cobra (dueño, 2026-11-22; docs/celular-como-caja.md, «Cobrar en el celular»): en una sola llamada, con los precios y las
/// promociones del servicio, en el turno abierto de la caja principal (su folio, su corte, su inventario, su bitácora y su eleventa), sin
/// cobrar dos veces el mismo ticket. Cotizar es el mismo cálculo sin cobrar.
/// </summary>
public sealed class CobroDelCelularTests
{
    private const string Codigo = "36000291452";
    private const long Precio = 2350;

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ErrorDto> Error(HttpResponseMessage r, HttpStatusCode estado)
    {
        Assert.True(estado == r.StatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!;
    }

    private static FichaProductoDto Ficha(Guid departamento, string nombre, string codigo, long precio) =>
        new(null, nombre, departamento, "Marca de Prueba", TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
        [
            new VarianteFichaDto(null, "", UnidadBase.Pieza, true, [new PresentacionFichaDto(null, "Pieza", 1000, true, [new CodigoFichaDto(codigo)])],
                [new PrecioFichaDto(null, 1000, precio)]),
        ]);

    /// <summary>La caja principal con su turno abierto (del encargado, con $500 de fondo), el celular dado de alta y un producto.</summary>
    private sealed record Tienda(ServicioDePrueba Servicio, HttpClient Principal, CajaDto Caja, TurnoDto Turno, HttpClient Celular, Guid CelularId,
        ProductoVentaDto Producto, SesionDto Encargado, SesionDto Dueno, Guid Departamento) : IAsyncDisposable
    {
        public Guid SucursalId => Caja.SucursalId;
        public async ValueTask DisposeAsync() => await Servicio.DisposeAsync();

        public Task<HttpResponseMessage> Cobrar(SolicitudCobroDelCelular s) => Celular.PostAsJsonAsync($"api/cajas/{CelularId}/cobrar", s);
        public Task<HttpResponseMessage> Cotizar(SolicitudCobroDelCelular s) => Celular.PostAsJsonAsync($"api/cajas/{CelularId}/cotizar", s);

        public async Task<ProductoVentaDto> ProductoAsync(string nombre, string codigo, long precio)
        {
            await Leer<FichaProductoDto>(await Principal.PostAsJsonAsync("api/catalogo/productos",
                new SolicitudGuardarProducto(Encargado.UsuarioId, SucursalId, Ficha(Departamento, nombre, codigo, precio))));
            return await Leer<ProductoVentaDto>(await Celular.GetAsync($"api/productos/codigo/{codigo}?sucursalId={SucursalId}"));
        }
    }

    private static async Task<Tienda> TiendaAsync(params (string, string)[] ajustes)
    {
        var servicio = await ServicioDePrueba.CrearAsync(ajustes);
        var principal = servicio.Fabrica.CreateClient();
        var caja = (await principal.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var encargado = await Leer<SesionDto>(await principal.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var dueno = await Leer<SesionDto>(await principal.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var turno = await Leer<TurnoDto>(await principal.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, encargado.UsuarioId, 50000)));

        var celular = servicio.Fabrica.CreateClient();
        var alta = await Leer<AltaCajaDto>(await celular.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, null, "Celular de Luis", "CEL", "Pixel 8")));
        celular.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);

        var departamento = (await principal.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        var t = new Tienda(servicio, principal, caja, turno, celular, alta.Caja.Id, null!, encargado, dueno, departamento);
        return t with { Producto = await t.ProductoAsync("Corrector de cinta", Codigo, Precio) };
    }

    private static SolicitudCobroDelCelular Efectivo(Guid id, IReadOnlyList<RenglonMandado> renglones, long recibido, long? visto = null) =>
        new(id, renglones, [new CobroSolicitado(FormaPago.Efectivo, recibido)], TotalVisto: visto);

    private static async Task<long> ExistenciaAsync(Tienda t, Guid varianteId)
    {
        await using var db = t.Servicio.Base();
        return (await new RegistroInventario(db).ExistenciaAsync(t.SucursalId, varianteId))?.Cantidad.Milesimas ?? 0;
    }

    private static async Task<long> EsperadoAsync(Tienda t) =>
        (await Leer<EfectivoCajaDto>(await t.Principal.GetAsync($"api/turnos/{t.Turno.Id}/efectivo"))).EsperadoCentavos;

    [Fact]
    public async Task Cobra_en_efectivo_en_el_turno_de_la_caja_principal_al_peso_con_cambio_y_otra_vez_no_lo_duplica()
    {
        await using var t = await TiendaAsync();
        // Una venta de la caja antes: la del celular toma el folio que sigue de la misma serie.
        var deLaCaja = await Leer<TicketDto>(await t.Principal.PostAsJsonAsync("api/ventas", new SolicitudCobro(t.Caja.Id, t.Turno.Id, t.Encargado.UsuarioId,
            [new LineaSolicitada(t.Producto.VarianteId, null, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 2400)], VentaId: Ids.Nuevo())));
        var existencia = await ExistenciaAsync(t, t.Producto.VarianteId);
        var esperado = await EsperadoAsync(t);

        // Por variante y por código (con la regla del UPC): una sola línea de 3 a $23.50 = $70.50; en efectivo, $71.
        var renglones = new[] { new RenglonMandado(t.Producto.VarianteId, t.Producto.Presentacion.Id, null, 2000), new RenglonMandado(null, null, "0" + Codigo, 1000) };
        var cotizado = await Leer<CotizacionDelCelularDto>(await t.Cotizar(Efectivo(Guid.Empty, renglones, 10000)));
        Assert.Equal((1, 3m, 7050L, 7100L, 50L), (cotizado.Renglones, cotizado.Articulos, cotizado.TotalCentavos, cotizado.EfectivoCentavos, cotizado.RedondeoCentavos));
        Assert.Equal(new PagoCotizadoDto(0, 7100, 50, 10000, 0, 2900), cotizado.Pago);
        var linea = Assert.Single(cotizado.Lineas);
        Assert.Equal((t.Producto.VarianteId, 3000L, Precio, 7050L, false), (linea.VarianteId!.Value, linea.CantidadMilesimas, linea.PrecioCentavos, linea.ImporteCentavos, linea.ArticuloComun));
        // Con menos de lo que se cobra, la cotización dice cuánto falta.
        Assert.Equal(1100, (await Leer<CotizacionDelCelularDto>(await t.Cotizar(Efectivo(Guid.Empty, renglones, 6000)))).Pago!.FaltanCentavos);

        var id = Ids.Nuevo();
        var cobrado = await Leer<CobroDelCelularDto>(await t.Cobrar(Efectivo(id, renglones, 10000, visto: 7050)));
        Assert.False(cobrado.YaCobrada);
        Assert.Equal((id, t.Caja.Id, t.Caja.Nombre, 7050L, 50L, 7100L, 10000L, 2900L), (cobrado.Id, cobrado.CajaId, cobrado.Caja, cobrado.TotalCentavos,
            cobrado.RedondeoCentavos, cobrado.CobradoCentavos, cobrado.RecibidoCentavos, cobrado.CambioCentavos));
        Assert.StartsWith("Encargado", cobrado.Cajero);
        Assert.Equal(cobrado.Folio, cobrado.Ticket.Folio);
        var efectivo = Assert.Single(cobrado.Cobros);
        Assert.Equal((FormaPago.Efectivo, 7100L, 10000L, 2900L), (efectivo.Forma, efectivo.MontoCentavos, efectivo.RecibidoCentavos, efectivo.CambioCentavos));

        // Otra vez el mismo (se perdió la respuesta): la misma venta, sin cobrar otra.
        var otraVez = await Leer<CobroDelCelularDto>(await t.Cobrar(Efectivo(id, renglones, 10000)));
        Assert.True(otraVez.YaCobrada);
        Assert.Equal((cobrado.Folio, cobrado.CambioCentavos, cobrado.CobradoCentavos), (otraVez.Folio, otraVez.CambioCentavos, otraVez.CobradoCentavos));

        await using var db = t.Servicio.Base();
        var venta = await db.Ventas.AsNoTracking().SingleAsync(v => v.Id == id);
        var anterior = await db.Ventas.AsNoTracking().SingleAsync(v => v.Id == deLaCaja.VentaId);
        // En el turno de la caja principal, con el folio que sigue de su serie (el mismo camino que sus ventas en línea).
        Assert.Equal((t.Caja.Id, t.Turno.Id, anterior.Serie, anterior.Folio + 1), (venta.CajaId, venta.TurnoId, venta.Serie, venta.Folio));
        Assert.Equal(venta.FolioTexto, cobrado.Folio);
        // En línea, sin letra (2026-11-22, folios sin letra): lo que cobra el celular también.
        Assert.Equal(Folios.SinLetra, venta.Serie);
        Assert.Matches(@"^[1-9]\d*$", cobrado.Folio);
        Assert.True(venta.TicketSinImprimir);
        Assert.Equal(1, await db.Ventas.CountAsync(v => v.TurnoId == t.Turno.Id && v.Id != anterior.Id));
        // El inventario baja una vez; el efectivo cuenta en el corte de ese turno.
        Assert.Equal(existencia - 3000, await ExistenciaAsync(t, t.Producto.VarianteId));
        Assert.Equal(esperado + 7100, await EsperadoAsync(t));
        // En «Ventas del día» de la caja principal y en la bitácora, con el celular.
        var delDia = await Leer<List<VentaDelDiaDto>>(await t.Principal.GetAsync($"api/cajas/{t.Caja.Id}/ventas-del-dia"));
        Assert.Contains(delDia, v => v.Id == id && v.Folio == cobrado.Folio && v.TotalCentavos == 7050);
        var bitacora = Assert.Single(await db.Bitacora.AsNoTracking().Where(b => b.VentaId == id).ToListAsync());
        Assert.Equal((AccionControlada.CobrarSinTicket, (Guid?)t.CelularId, (long?)7050), (bitacora.Accion, bitacora.CajaId, bitacora.MontoCentavos));
        Assert.Equal($"Cobró en Celular de Luis, en el turno de la {t.Caja.Nombre}, sin imprimir el ticket", bitacora.Detalle);
        // Su primera impresión, desde la caja, no pide motivo.
        var impreso = await Leer<TicketDto>(await t.Principal.PostAsJsonAsync($"api/ventas/{id}/primera-impresion",
            new SolicitudPrimeraImpresion(t.Encargado.UsuarioId, t.Caja.Id)));
        Assert.Equal(cobrado.Folio, impreso.Folio);
    }

    [Fact]
    public async Task Los_precios_y_las_promociones_los_pone_el_servicio_y_un_total_distinto_no_se_cobra()
    {
        await using var t = await TiendaAsync();
        var cuaderno = await t.ProductoAsync("Cuaderno profesional", "7509876500011", 4500);
        var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
        await Leer<PromocionFichaDto>(await t.Principal.PostAsJsonAsync("api/promociones", new SolicitudPromocion(t.Encargado.UsuarioId, t.SucursalId,
            t.Caja.Id, "Tres por dos", TipoPromocion.LlevaNPagaM, 3, 2, 0, hoy.AddDays(-1), hoy.AddDays(30), DiasDeLaSemana.Todos, false,
            [new AlcanceSolicitadoDto(VarianteId: cuaderno.VarianteId)], Id: Ids.Nuevo())));
        await t.Servicio.CatalogoAlDiaAsync();

        // 3 cuadernos con el 3×2: se pagan 2 ($90); el celular no manda precio.
        var renglones = new[] { new RenglonMandado(cuaderno.VarianteId, null, null, 3000) };
        var cotizado = await Leer<CotizacionDelCelularDto>(await t.Cotizar(new SolicitudCobroDelCelular(Guid.Empty, renglones)));
        Assert.Equal((9000L, 9000L, 0L, (PagoCotizadoDto?)null), (cotizado.TotalCentavos, cotizado.EfectivoCentavos, cotizado.RedondeoCentavos, cotizado.Pago));
        var linea = Assert.Single(cotizado.Lineas);
        Assert.Equal((4500L, "Tres por dos"), (linea.AhorroPromocionCentavos, linea.Promocion));

        // El celular vio otro total (su copia era de antes): no se cobra.
        var distinto = await Error(await t.Cobrar(Efectivo(Ids.Nuevo(), renglones, 20000, visto: 13500)), HttpStatusCode.Conflict);
        Assert.Equal(("total-distinto", "El total cambió: ahora es $90.00 (el celular decía $135.00). Revísalo con el cliente y vuelve a cobrar."),
            (distinto.Codigo, distinto.Mensaje));
        var cobrado = await Leer<CobroDelCelularDto>(await t.Cobrar(Efectivo(Ids.Nuevo(), renglones, 10000, visto: 9000)));
        Assert.Equal((9000L, 0L, 1000L), (cobrado.TotalCentavos, cobrado.RedondeoCentavos, cobrado.CambioCentavos));
        Assert.Equal("Tres por dos", Assert.Single(cobrado.Ticket.Lineas).Promocion);

        // Un producto del catálogo con precio del celular: no.
        var conPrecio = await Error(await t.Cotizar(new SolicitudCobroDelCelular(Guid.Empty, [renglones[0] with { PrecioCentavos = 100 }])), HttpStatusCode.BadRequest);
        Assert.Equal(("comun", ApiTicketsDelCelularTextos.ProductoConPrecio), (conPrecio.Codigo, conPrecio.Mensaje));
    }

    [Fact]
    public async Task El_articulo_comun_se_cobra_sin_inventario_y_queda_en_la_bitacora()
    {
        await using var t = await TiendaAsync();
        var existencia = await ExistenciaAsync(t, t.Producto.VarianteId);
        var renglones = new[] { new RenglonMandado(null, null, null, 2000, "  Engargolado  ", 1525), new RenglonMandado(t.Producto.VarianteId, null, null, 1000) };
        var cotizado = await Leer<CotizacionDelCelularDto>(await t.Cotizar(new SolicitudCobroDelCelular(Guid.Empty, renglones)));
        Assert.Equal((2, 3050L + Precio, 5400L, 0L), (cotizado.Renglones, cotizado.TotalCentavos, cotizado.EfectivoCentavos, cotizado.RedondeoCentavos));
        var comun = cotizado.Lineas.Single(l => l.ArticuloComun);
        Assert.Equal(("Engargolado", 1525L, 3050L, (Guid?)null), (comun.Descripcion, comun.PrecioCentavos, comun.ImporteCentavos, comun.VarianteId));

        var id = Ids.Nuevo();
        var cobrado = await Leer<CobroDelCelularDto>(await t.Cobrar(new SolicitudCobroDelCelular(id, renglones, [new CobroSolicitado(FormaPago.Efectivo, 5400)],
            CajeroId: t.Dueno.UsuarioId)));
        Assert.Equal((5400L, 0L), (cobrado.CobradoCentavos, cobrado.CambioCentavos));
        // Cobra quien entró en el celular (el dueño), no el cajero del turno.
        Assert.StartsWith("Due", cobrado.Cajero);
        Assert.Equal(existencia - 1000, await ExistenciaAsync(t, t.Producto.VarianteId));
        await using var db = t.Servicio.Base();
        var comunEnBitacora = await db.Bitacora.AsNoTracking().SingleAsync(b => b.VentaId == id && b.Accion == AccionControlada.ArticuloComun);
        Assert.Equal((t.Dueno.UsuarioId, (long?)3050), (comunEnBitacora.UsuarioId!.Value, comunEnBitacora.MontoCentavos));
        Assert.Equal(t.Dueno.UsuarioId, (await db.Ventas.AsNoTracking().SingleAsync(v => v.Id == id)).CajeroId);

        // Mal armado: sin descripción, sin precio o con más de 80 letras.
        foreach (var malo in new[] { renglones[0] with { Descripcion = " " }, renglones[0] with { PrecioCentavos = 0 },
                     renglones[0] with { Descripcion = new string('x', 81) }, new RenglonMandado(null, null, null, 1000, "Sin precio") })
            Assert.Equal(("comun", ApiTicketsDelCelularTextos.ComunIncompleto),
                (await Error(await t.Cotizar(new SolicitudCobroDelCelular(Guid.Empty, [malo])), HttpStatusCode.BadRequest)) is var e ? (e.Codigo, e.Mensaje) : default);

        // Quien no tiene el artículo común en «Puede» no lo cobra (el cajero, con el permiso quitado).
        var cajero = await db.Usuarios.AsNoTracking().Where(u => u.NombreUsuario == "cajero").Select(u => u.Id).SingleAsync();
        await db.PermisosUsuario.Where(p => p.UsuarioId == cajero && p.Accion == AccionControlada.ArticuloComun)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.Nivel, NivelPermiso.NoPermitido));
        var sinPermiso = await Error(await t.Cobrar(new SolicitudCobroDelCelular(Ids.Nuevo(), renglones, [new CobroSolicitado(FormaPago.Efectivo, 5400)],
            CajeroId: cajero)), HttpStatusCode.Forbidden);
        Assert.Equal("no-permitido", sinPermiso.Codigo);
    }

    [Fact]
    public async Task Mandar_a_una_caja_tambien_lleva_el_articulo_comun()
    {
        await using var t = await TiendaAsync();
        // La caja 2, con la venta abierta, recoge lo que le manden.
        var alta2 = await Leer<AltaCajaDto>(await t.Principal.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", t.SucursalId, null, "Caja 2", "B", "PC-2")));
        var caja2 = t.Servicio.Fabrica.CreateClient();
        caja2.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta2.Llave);
        await Leer<TurnoDto>(await caja2.PostAsJsonAsync("api/turnos", new SolicitudTurno(alta2.Caja.Id, t.Encargado.UsuarioId, 0)));
        using var parar = new CancellationTokenSource();
        var recoge = Task.Run(async () =>
        {
            var recibidos = new List<TicketRecibidoDto>();
            while (!parar.IsCancellationRequested)
            {
                var llegaron = await Leer<List<TicketRecibidoDto>>(await caja2.GetAsync($"api/cajas/{alta2.Caja.Id}/recibidos?esperan=0&atiende={t.Encargado.UsuarioId}"));
                recibidos.AddRange(llegaron.Where(l => recibidos.All(r => r.Id != l.Id)));
                if (llegaron.Count > 0)
                    await caja2.PostAsJsonAsync($"api/cajas/{alta2.Caja.Id}/recibidos", new SolicitudRecibidos([.. llegaron.Select(l => l.Id)]));
                await Task.Delay(50);
            }
            return recibidos;
        });
        var limite = DateTime.UtcNow.AddSeconds(20);
        CajaParaMandarDto? lista = null;
        while ((lista = (await Leer<List<CajaParaMandarDto>>(await t.Celular.GetAsync($"api/cajas/{t.CelularId}/para-mandar"))).Single(c => c.Id == alta2.Caja.Id)) is { Recibe: false })
        {
            Assert.True(DateTime.UtcNow < limite, "La caja 2 no quedó lista para recibir.");
            await Task.Delay(20);
        }
        // Las dos cajas tienen turno abierto: el celular puede cobrar en cualquiera.
        Assert.True(lista.TurnoAbierto);

        var mandado = await Leer<TicketMandadoDto>(await t.Celular.PostAsJsonAsync($"api/cajas/{t.CelularId}/mandar", new SolicitudMandarTicket(Ids.Nuevo(),
            alta2.Caja.Id, [new RenglonMandado(t.Producto.VarianteId, null, null, 1000), new RenglonMandado(null, null, null, 1000, "Engargolado", 3000)])));
        Assert.Equal((2, Precio + 3000), (mandado.Renglones, mandado.TotalCentavos));
        await Task.Delay(200);
        await parar.CancelAsync();
        var recibido = Assert.Single(await recoge);
        var comun = recibido.Lineas.Single(l => l.VarianteId is null);
        Assert.Equal(("Engargolado", (long?)3000, 1000L), (comun.Descripcion, comun.PrecioCentavos, comun.CantidadMilesimas));
        // Así lo arma la caja en su espera: el artículo común con su precio.
        var venta = new Venta { Serie = "" };
        Assert.Equal(0, TicketsEnEspera.Rearmar(TicketsEnEspera.DelCelular(recibido), venta));
        Assert.Equal(Precio + 3000, venta.TotalCentavos);

        // Con dos turnos abiertos, el cobro dice en cuál; eligiendo la caja 2, entra en el suyo.
        var elige = await Error(await t.Cobrar(Efectivo(Ids.Nuevo(), [new RenglonMandado(t.Producto.VarianteId, null, null, 1000)], 3000)), HttpStatusCode.BadRequest);
        Assert.Equal("elige-caja", elige.Codigo);
        Assert.StartsWith("Hay 2 cajas con turno abierto (", elige.Mensaje);
        var enLa2 = await Leer<CobroDelCelularDto>(await t.Cobrar(new SolicitudCobroDelCelular(Ids.Nuevo(), [new RenglonMandado(t.Producto.VarianteId, null, null, 1000)],
            [new CobroSolicitado(FormaPago.Efectivo, 3000)], Caja: alta2.Caja.Id)));
        Assert.Equal((alta2.Caja.Id, "Caja 2"), (enLa2.CajaId, enLa2.Caja));
        // Una caja de Windows (abre turnos) no cobra por aquí: cobra en su pantalla de venta.
        var noEsCelular = await Error(await caja2.PostAsJsonAsync($"api/cajas/{alta2.Caja.Id}/cobrar",
            Efectivo(Ids.Nuevo(), [new RenglonMandado(t.Producto.VarianteId, null, null, 1000)], 3000)), HttpStatusCode.Forbidden);
        Assert.Equal("no-es-celular", noEsCelular.Codigo);
    }

    [Fact]
    public async Task Con_tarjeta_y_transferencia_va_exacto_la_transferencia_queda_por_verificar_y_el_corte_lo_cuenta()
    {
        await using var t = await TiendaAsync();
        var renglones = new[] { new RenglonMandado(t.Producto.VarianteId, null, null, 3000) };
        // $70.50: $20 con tarjeta, $30 por transferencia y lo demás ($20.50) en efectivo, al peso: $21.
        var cobros = new[] { new CobroSolicitado(FormaPago.Tarjeta, 2000, "4321"), new CobroSolicitado(FormaPago.Transferencia, 3000),
            new CobroSolicitado(FormaPago.Efectivo, 5000) };
        Assert.Equal(new PagoCotizadoDto(5000, 2100, 50, 5000, 0, 2900),
            (await Leer<CotizacionDelCelularDto>(await t.Cotizar(new SolicitudCobroDelCelular(Guid.Empty, renglones, cobros)))).Pago);
        var id = Ids.Nuevo();
        var cobrado = await Leer<CobroDelCelularDto>(await t.Cobrar(new SolicitudCobroDelCelular(id, renglones, cobros)));
        Assert.Equal((7050L, 50L, 7100L, 2900L), (cobrado.TotalCentavos, cobrado.RedondeoCentavos, cobrado.CobradoCentavos, cobrado.CambioCentavos));
        // Solo con tarjeta: exacto, sin redondeo ni cambio.
        var conTarjeta = await Leer<CobroDelCelularDto>(await t.Cobrar(new SolicitudCobroDelCelular(Ids.Nuevo(), renglones,
            [new CobroSolicitado(FormaPago.Tarjeta, 7050)])));
        Assert.Equal((7050L, 0L, 0L, 0L), (conTarjeta.CobradoCentavos, conTarjeta.RedondeoCentavos, conTarjeta.CambioCentavos, conTarjeta.RecibidoCentavos));

        await using var db = t.Servicio.Base();
        var deLaVenta = await db.Cobros.AsNoTracking().Where(c => c.VentaId == id).ToListAsync();
        Assert.Equal(("4321", Verificacion.NoAplica), deLaVenta.Single(c => c.Forma == FormaPago.Tarjeta) is var tj ? (tj.Referencia, tj.Verificacion) : default);
        Assert.Equal(Verificacion.PorVerificar, deLaVenta.Single(c => c.Forma == FormaPago.Transferencia).Verificacion);
        var corte = await Leer<CorteDto>(await t.Principal.PostAsync($"api/turnos/{t.Turno.Id}/corte-x?usuarioId={t.Dueno.UsuarioId}", null));
        Assert.Equal((2000L + 7050, 3000L, 2100L), (corte.TarjetaCentavos, corte.TransferenciaCentavos, corte.EfectivoVentasCentavos));

        // Lo que no cuadra, en español y con su código.
        async Task<(string?, string)> Rechazo(IReadOnlyList<CobroSolicitado>? c)
        {
            var e = await Error(await t.Cobrar(new SolicitudCobroDelCelular(Ids.Nuevo(), renglones, c)), HttpStatusCode.BadRequest);
            return (e.Codigo, e.Mensaje);
        }
        Assert.Equal(("pasa-del-total", "Tarjeta y transferencia no pueden pasar del total: no dan cambio."), await Rechazo([new CobroSolicitado(FormaPago.Tarjeta, 7100)]));
        Assert.Equal(("falta", "Faltan $1.00 por cobrar."), await Rechazo([new CobroSolicitado(FormaPago.Efectivo, 7000)]));
        Assert.Equal(("sin-cobro", "Falta cómo paga: efectivo, tarjeta o transferencia."), await Rechazo(null));
        Assert.Equal(("forma", "Desde el celular se cobra en efectivo, tarjeta o transferencia: lo fiado, en una caja."),
            await Rechazo([new CobroSolicitado(FormaPago.Credito, 7050)]));
        Assert.Equal(("cobro", "El efectivo va en un solo cobro."), await Rechazo([new CobroSolicitado(FormaPago.Efectivo, 5000), new CobroSolicitado(FormaPago.Efectivo, 5000)]));
        Assert.Equal(("cobro", "Cada cobro debe ser mayor a cero."), await Rechazo([new CobroSolicitado(FormaPago.Tarjeta, 0)]));
        // Nada de eso cobró.
        Assert.Equal(2, await db.Ventas.CountAsync(v => v.TurnoId == t.Turno.Id));
    }

    [Fact]
    public async Task Lo_que_no_se_puede_cobrar_lo_dice_en_espanol_con_su_codigo()
    {
        await using var t = await TiendaAsync();
        var bueno = new RenglonMandado(t.Producto.VarianteId, null, null, 1000);
        async Task<(string?, string)> Rechazo(SolicitudCobroDelCelular s, HttpStatusCode estado = HttpStatusCode.BadRequest, HttpClient? quien = null)
        {
            var e = await Error(await (quien ?? t.Celular).PostAsJsonAsync($"api/cajas/{t.CelularId}/cobrar", s), estado);
            return (e.Codigo, e.Mensaje);
        }

        Assert.Equal(("vacio", "El ticket está vacío."), await Rechazo(Efectivo(Ids.Nuevo(), [], 3000)));
        Assert.Equal(("sin-id", "El ticket no trae su número: actualiza la app."), await Rechazo(Efectivo(Guid.Empty, [bueno], 3000)));
        Assert.Equal(("cantidad", "Una cantidad del ticket no es válida: va de 1 a 9,999."), await Rechazo(Efectivo(Ids.Nuevo(), [bueno with { CantidadMilesimas = 0 }], 3000)));
        Assert.Equal(("no-esta", "«7501234567890» ya no está en el catálogo de la caja principal: quítalo del ticket."),
            await Rechazo(Efectivo(Ids.Nuevo(), [bueno, new RenglonMandado(null, null, "7501234567890", 1000)], 3000)));
        Assert.Equal(("otra-caja-destino", "Esa caja ya no está en la tienda: elige otra."),
            await Rechazo(new SolicitudCobroDelCelular(Ids.Nuevo(), [bueno], [new CobroSolicitado(FormaPago.Efectivo, 3000)], Caja: t.CelularId)));
        Assert.Equal(("cajero", "Ese usuario ya no cobra en esta sucursal: entra otra vez en el celular."),
            await Rechazo(new SolicitudCobroDelCelular(Ids.Nuevo(), [bueno], [new CobroSolicitado(FormaPago.Efectivo, 3000)], CajeroId: Ids.Nuevo())));
        // Sin la llave del celular (desde esta misma PC, sin alta), no.
        Assert.Equal(("sin-llave", "Solo un celular dado de alta cobra desde el celular."),
            await Rechazo(Efectivo(Ids.Nuevo(), [bueno], 3000), HttpStatusCode.Unauthorized, t.Servicio.Fabrica.CreateClient()));

        // Sin turno abierto en la caja principal (se cerró con su corte), no hay dónde cobrar.
        await Leer<CorteDto>(await t.Principal.PostAsJsonAsync($"api/turnos/{t.Turno.Id}/corte-z",
            new SolicitudCorteZ(t.Encargado.UsuarioId, 50000, Pin: "2222")));
        Assert.Equal(("sin-turno", "Ninguna caja tiene un turno abierto: abre el turno en la caja principal y vuelve a cobrar."),
            await Rechazo(Efectivo(Ids.Nuevo(), [bueno], 3000), HttpStatusCode.Conflict));
        Assert.Equal(("sin-turno", $"La {t.Caja.Nombre} no tiene un turno abierto: que lo abran ahí, o elige otra caja."),
            await Rechazo(new SolicitudCobroDelCelular(Ids.Nuevo(), [bueno], [new CobroSolicitado(FormaPago.Efectivo, 3000)], Caja: t.Caja.Id), HttpStatusCode.Conflict));
        await using var db = t.Servicio.Base();
        Assert.Equal(0, await db.Ventas.CountAsync());
    }

    [Fact]
    public async Task En_el_piloto_entra_a_la_bandeja_hacia_eleventa_como_las_de_la_caja_y_solo_en_efectivo()
    {
        await using var t = await TiendaAsync(("Eleventa:Escribir", "true"), ("Eleventa:Activo", "false"), ("Eleventa:Servidor", "127.0.0.1"),
            ("Eleventa:Puerto", "1"), ("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"), ("Eleventa:Usuario", "SYSDBA"), ("Eleventa:Clave", "masterkey"),
            ("Eleventa:Caja", "4"), ("Eleventa:Cajero", "7"), ("Eleventa:SegundosAlCobrar", "1"), ("Eleventa:SegundosEntreEnvios", "3600"),
            ("Eleventa:Minutos", "600"), ("Eleventa:RespaldoObligatorio", "false"));
        var renglones = new[] { new RenglonMandado(t.Producto.VarianteId, null, null, 2000) };
        var tarjeta = await Error(await t.Cobrar(new SolicitudCobroDelCelular(Ids.Nuevo(), renglones, [new CobroSolicitado(FormaPago.Tarjeta, 4700)])),
            HttpStatusCode.BadRequest);
        Assert.Equal(("solo-efectivo", SoloEfectivoEnElPilotoTexto), (tarjeta.Codigo, tarjeta.Mensaje));
        Assert.Equal("solo-efectivo", (await Error(await t.Cotizar(new SolicitudCobroDelCelular(Guid.Empty, renglones,
            [new CobroSolicitado(FormaPago.Transferencia, 4700)])), HttpStatusCode.BadRequest)).Codigo);

        var id = Ids.Nuevo();
        var cobrado = await Leer<CobroDelCelularDto>(await t.Cobrar(Efectivo(id, renglones, 5000)));
        Assert.Equal((4700L, 300L), (cobrado.CobradoCentavos, cobrado.CambioCentavos));
        await using var db = t.Servicio.Base();
        var envio = await db.EnviosAEleventa.AsNoTracking().SingleAsync(e => e.VentaId == id);
        Assert.Equal(TipoEnvioEleventa.Venta, envio.Tipo);
    }

    private const string SoloEfectivoEnElPilotoTexto =
        "Mientras dure el piloto con eleventa, esta caja solo cobra en efectivo: sin tarjeta, transferencia, crédito ni saldo a favor. Cóbralo todo en efectivo.";
}

/// <summary>Los textos del servicio que la prueba compara (el servicio es otro ensamblado; sus constantes son públicas).</summary>
file static class ApiTicketsDelCelularTextos
{
    public const string ComunIncompleto = PapeleriaPos.Servicio.ApiTicketsDelCelular.ComunIncompleto;
    public const string ProductoConPrecio = PapeleriaPos.Servicio.ApiTicketsDelCelular.ProductoConPrecio;
}
