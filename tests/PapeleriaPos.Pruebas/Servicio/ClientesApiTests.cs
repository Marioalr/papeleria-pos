using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Los clientes registrados (fase 3, entrega 2): alta, el cliente en la venta, su mayoreo y su evaluación.</summary>
public sealed class ClientesApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-clientes-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado, TurnoDto Turno)> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        return (http, caja, cajero, encargado, turno);
    }

    private static Task<HttpResponseMessage> GuardarAsync(HttpClient http, SesionDto quien, CajaDto caja, ClienteRegistradoDto cliente) =>
        http.PostAsJsonAsync("api/clientes", new SolicitudGuardarCliente(quien.UsuarioId, caja.SucursalId, cliente));

    /// <summary>Un producto por pieza con escala de mayoreo: su variante, presentación base, menudeo, primera escala y su precio.</summary>
    private async Task<(Guid Variante, Guid Presentacion, long Menudeo, long Mayoreo)> ConMayoreoAsync()
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        var escalas = await (from p in db.Precios
                             join v in db.Variantes on p.VarianteId equals v.Id
                             where p.PresentacionId == null && p.SucursalId == null && v.Activa && v.UnidadBase == UnidadBase.Pieza
                             select new { p.VarianteId, p.CantidadMinima, p.PrecioCentavos }).ToListAsync();
        var conMayoreo = escalas.GroupBy(e => e.VarianteId).First(g => g.Count() > 1);
        var orden = conMayoreo.OrderBy(e => e.CantidadMinima.Milesimas).ToList();
        var presentacion = await db.Presentaciones.Where(p => p.VarianteId == conMayoreo.Key && p.EsBase).Select(p => p.Id).SingleAsync();
        return (conMayoreo.Key, presentacion, orden[0].PrecioCentavos, orden[1].PrecioCentavos);
    }

    [Fact]
    public async Task Los_dan_de_alta_el_dueno_y_el_encargado_sin_repetir_nombre_ni_telefono()
    {
        var (http, caja, cajero, encargado, _) = await PrepararAsync();
        var nuevo = new ClienteRegistradoDto(Guid.Empty, "Taller de costura Lupita", 500, null, true, "(55) 4444-1111");

        var delCajero = await GuardarAsync(http, cajero, caja, nuevo);
        var guardado = await Leer<ClienteRegistradoDto>(await GuardarAsync(http, encargado, caja, nuevo));
        var mismoNombre = await GuardarAsync(http, encargado, caja, nuevo with { Telefono = null, Nombre = "taller de costura LUPITA" });
        var mismoTelefono = await GuardarAsync(http, encargado, caja, nuevo with { Nombre = "Otro taller", Telefono = "5544441111" });
        var lista = await Leer<List<ClienteResumenDto>>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/clientes"));

        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal(("5544441111", false), (guardado.Telefono, guardado.PorRevisar));
        Assert.Contains("Ya existe", (await mismoNombre.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Contains("Taller de costura Lupita", (await mismoTelefono.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Contains(lista, r => r.Cliente.Id == guardado.Id);
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var bitacora = await alcance.ServiceProvider.GetRequiredService<PosDbContext>().Bitacora.AsNoTracking()
            .Where(b => b.Accion == AccionControlada.AdministrarClientes).Select(b => b.Detalle).ToListAsync();
        Assert.Contains("Privilegios de «Taller de costura Lupita» (alta): descuento 5 %", bitacora);
    }

    [Fact]
    public async Task El_cliente_queda_en_la_venta_aunque_no_tenga_descuento_y_su_ficha_dice_lo_que_compra()
    {
        var (http, caja, cajero, encargado, turno) = await PrepararAsync();
        var arquitecto = await Leer<ClienteRegistradoDto>(await GuardarAsync(http, encargado, caja,
            new ClienteRegistradoDto(Guid.Empty, "Arq. Ramírez (venta)", 0, null, true)));
        var (variante, presentacion, menudeo, _) = await ConMayoreoAsync();

        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(variante, presentacion, 2000, "x")], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)], Ids.Nuevo(),
            ClienteId: arquitecto.Id)));
        var reimpreso = await Leer<TicketDto>(await http.GetAsync($"api/ventas/{ticket.VentaId}/ticket"));
        var ficha = await Leer<FichaClienteDto>(await http.GetAsync($"api/clientes/{arquitecto.Id}"));

        Assert.Equal(("Arq. Ramírez (venta)", "Arq. Ramírez (venta)"), (ticket.Cliente, reimpreso.Cliente));
        Assert.Equal(2 * menudeo, ticket.TotalCentavos);
        Assert.Equal((1, ticket.TotalCentavos), (ficha.Meses[0].Tickets, ficha.Meses[0].TotalCentavos));
        Assert.Equal(6, ficha.Meses.Count);
        Assert.NotNull(ficha.UltimaCompraLocal);
    }

    [Fact]
    public async Task Con_mayoreo_siempre_el_servicio_cobra_el_mayoreo_desde_la_primera_pieza()
    {
        var (http, caja, cajero, encargado, turno) = await PrepararAsync();
        var revende = await Leer<ClienteRegistradoDto>(await GuardarAsync(http, encargado, caja,
            new ClienteRegistradoDto(Guid.Empty, "Papelería que revende", 0, null, true, MayoreoSiempre: true)));
        var (variante, presentacion, menudeo, mayoreo) = await ConMayoreoAsync();

        // La caja solo manda el precio si se cambió: el servicio tiene que llegar al mismo precio de mayoreo.
        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(variante, presentacion, 1000, "x")], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)], Ids.Nuevo(),
            ClienteId: revende.Id, MayoreoDeCliente: true)));
        var bajoCosto = await Leer<BajoCostoDto>(await http.PostAsJsonAsync("api/descuentos/bajo-costo",
            new SolicitudBajoCosto(caja.Id, [new LineaSolicitada(variante, presentacion, 1000)], revende.Id, MayoreoDeCliente: true)));

        var linea = ticket.Lineas.Single();
        Assert.Equal((mayoreo, mayoreo, menudeo - mayoreo, "Mayoreo de cliente"),
            (ticket.TotalCentavos, linea.PrecioCentavos, linea.AhorroMayoreoCentavos, linea.Mayoreo));
        Assert.Equal("Papelería que revende (mayoreo siempre)", ticket.Cliente);
        Assert.Equal(0, bajoCosto.FaltanteCentavos);
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var guardada = await alcance.ServiceProvider.GetRequiredService<PosDbContext>().Ventas.Include(v => v.Lineas).AsNoTracking()
            .SingleAsync(v => v.Id == ticket.VentaId);
        Assert.Null(guardada.Lineas.Single().PrecioOriginalCentavos);  // no quedó como precio cambiado
    }

    [Fact]
    public async Task Sin_conexion_el_cliente_sin_descuento_sube_con_la_venta_y_con_su_mayoreo()
    {
        var (local, caja, _, encargado, _) = await PrepararAsync();
        var revende = await Leer<ClienteRegistradoDto>(await GuardarAsync(local, encargado, caja,
            new ClienteRegistradoDto(Guid.Empty, "Revende sin conexión", 0, null, true, "5599887766", MayoreoSiempre: true)));
        var (variante, presentacion, menudeo, mayoreo) = await ConMayoreoAsync();

        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);
        await contingencia.LatidoAsync();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);

        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        var copia = await cliente.ClientesRegistradosAsync(caja.SucursalId);
        var ticket = await cliente.CobrarAsync(new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(variante, presentacion, 1000, "x")], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)], ClienteId: revende.Id,
            MayoreoDeCliente: true));
        interruptor.Caido = false;
        await contingencia.LatidoAsync();

        Assert.Contains(copia, c => c.Id == revende.Id && c.Telefono == "5599887766");
        Assert.Equal((mayoreo, "Revende sin conexión (mayoreo siempre)", "Mayoreo de cliente"),
            (ticket.TotalCentavos, ticket.Cliente, ticket.Lineas.Single().Mayoreo));
        Assert.Equal(0, contingencia.Pendientes);
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var subida = await alcance.ServiceProvider.GetRequiredService<PosDbContext>().Ventas.Include(v => v.Lineas).AsNoTracking()
            .SingleAsync(v => v.Id == ticket.VentaId);
        Assert.Equal((revende.Id, mayoreo, (long?)null, menudeo - mayoreo),
            (subida.ClienteId, subida.TotalCentavos, subida.Lineas.Single().PrecioOriginalCentavos, subida.Lineas.Single().AhorroMayoreoCentavos));
    }

    [Fact]
    public async Task Una_caja_de_antes_cobra_sin_mayoreo_y_el_servicio_le_cobra_lo_mismo()
    {
        var (http, caja, cajero, encargado, turno) = await PrepararAsync();
        var cliente = await Leer<ClienteRegistradoDto>(await GuardarAsync(http, encargado, caja,
            new ClienteRegistradoDto(Guid.Empty, "Cliente de una caja vieja", 1000, null, true, MayoreoSiempre: true)));
        var (variante, presentacion, menudeo, _) = await ConMayoreoAsync();
        var conDescuento = menudeo - Redondeo.Dividir(menudeo * 1000, 10000);

        // El JSON de una caja de antes: con clienteEspecialId y sin el mayoreo del cliente; paga con tarjeta lo que ella calculó.
        var vieja = await http.PostAsJsonAsync("api/ventas", new
        {
            cajaId = caja.Id, turnoId = turno.Id, cajeroId = cajero.UsuarioId, ventaId = Guid.NewGuid(), clienteEspecialId = cliente.Id,
            lineas = new[] { new { varianteId = variante, presentacionId = presentacion, cantidadMilesimas = 1000, descuentoCentesimas = 1000,
                motivoDescuento = $"Cliente especial: {cliente.Nombre}" } },
            cobros = new[] { new { forma = (int)FormaPago.Tarjeta, montoCentavos = conDescuento } },
        });
        var deAntes = await Leer<List<ClienteRegistradoDto>>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/clientes-especiales"));
        var paquete = (await http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete"))!;

        Assert.Equal(conDescuento, (await Leer<TicketDto>(vieja)).TotalCentavos);
        // Las cajas de antes solo ven a los que tienen descuento (uno sin descuento les daría error en CTRL+D).
        Assert.All(deAntes, c => Assert.True(c.DescuentoCentesimas > 0));
        Assert.All(paquete.Clientes!, c => Assert.True(c.DescuentoCentesimas > 0));
        Assert.Contains(paquete.Registrados!, c => c.DescuentoCentesimas == 0);
    }

    [Fact]
    public async Task Si_le_cambian_los_privilegios_con_el_ticket_abierto_el_cobro_dice_que_lo_vuelvan_a_elegir()
    {
        var (http, caja, cajero, encargado, turno) = await PrepararAsync();
        var cliente = await Leer<ClienteRegistradoDto>(await GuardarAsync(http, encargado, caja,
            new ClienteRegistradoDto(Guid.Empty, "Cliente que cambia", 1000, null, true, MayoreoSiempre: true)));
        var (variante, presentacion, _, _) = await ConMayoreoAsync();
        SolicitudCobro Cobro(bool mayoreo, int? descuento) => new(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(variante, presentacion, 1000, "x", DescuentoCentesimas: descuento, MotivoDescuento: descuento is null ? null : cliente.MotivoDescuento())],
            [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)], Ids.Nuevo(), ClienteId: cliente.Id, MayoreoDeCliente: mayoreo);

        await Leer<ClienteRegistradoDto>(await GuardarAsync(http, encargado, caja, cliente with { DescuentoCentesimas = 500, MayoreoSiempre = false }));
        var sinMayoreo = await http.PostAsJsonAsync("api/ventas", Cobro(mayoreo: true, descuento: null));
        var otroDescuento = await http.PostAsJsonAsync("api/ventas", Cobro(mayoreo: false, descuento: 1000));

        Assert.Equal(HttpStatusCode.BadRequest, sinMayoreo.StatusCode);
        Assert.Contains("ya no tiene mayoreo siempre", (await sinMayoreo.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal(HttpStatusCode.BadRequest, otroDescuento.StatusCode);
        Assert.Contains("El descuento del cliente cambió", (await otroDescuento.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }
}
