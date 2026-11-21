using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que pidió el dueño el 2026-11-21, del lado del servicio: el descuento sin motivo solo lo da quien lo puede dar sin
/// autorización (lo que pide autorización lleva motivo, como siempre), y «Cobrar sin imprimir el ticket» solo con ese permiso, sin
/// lo fiado, con la venta sin imprimir (su primera impresión no es reimpresión) y en la bitácora.
/// </summary>
public sealed class DescuentoSinMotivoYCobroSinTicketTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Dueno, TurnoDto Turno, ProductoVentaDto Producto)> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        var buscado = (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=cuaderno&sucursalId={caja.SucursalId}"))!
            .First(r => r.Codigo is not null && r.Existencia > 5);
        var producto = await Leer<ProductoVentaDto>(await http.GetAsync($"api/productos/codigo/{buscado.Codigo}?sucursalId={caja.SucursalId}"));
        return (http, caja, cajero, dueno, turno, producto);
    }

    private static SolicitudCobro Cobro(CajaDto caja, TurnoDto turno, SesionDto quien, LineaSolicitada linea, Guid? autorizacion = null,
        bool sinTicket = false, IReadOnlyList<CobroSolicitado>? cobros = null) =>
        new(caja.Id, turno.Id, quien.UsuarioId, [linea], cobros ?? [new CobroSolicitado(FormaPago.Efectivo, 1_000_000)], Guid.NewGuid(),
            AutorizacionDescuentoId: autorizacion, SinImprimirTicket: sinTicket);

    private T EnLaBase<T>(Func<PosDbContext, T> leer)
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        return leer(alcance.ServiceProvider.GetRequiredService<PosDbContext>());
    }

    // ── El descuento sin motivo ─────────────────────────────────────────────

    [Fact]
    public async Task El_cajero_da_sin_motivo_lo_que_puede_solo_y_lo_que_pide_autorizacion_lleva_motivo()
    {
        var (http, caja, cajero, _, turno, producto) = await PrepararAsync();

        // Hasta su 10 %, sin motivo: se cobra, y la bitácora lo dice.
        var cinco = new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000, DescuentoCentesimas: 500);
        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, cajero, cinco)));
        Assert.True(Assert.Single(ticket.Lineas).DescuentoCentavos > 0);
        var registro = EnLaBase(db => db.Bitacora.AsNoTracking().Single(b => b.VentaId == ticket.VentaId && b.Accion == AccionControlada.Descuento));
        Assert.EndsWith(": sin motivo", registro.Detalle);
        Assert.Null(EnLaBase(db => db.Ventas.Include(v => v.Lineas).AsNoTracking().Single(v => v.Id == ticket.VentaId)).Lineas.Single().MotivoDescuento);

        // El 15 % pide autorización: aun autorizado, sin motivo no se cobra (lo lee quien autoriza).
        var quince = new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000, DescuentoCentesimas: 1500);
        var autorizacion = await Leer<AutorizacionDto>(await http.PostAsJsonAsync("api/autorizaciones", new SolicitudAutorizacion(AccionControlada.Descuento,
            caja.SucursalId, caja.Id, cajero.UsuarioId, "encargado", "2222", 1500, "Descuento")));
        var sinMotivo = await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, cajero, quince, autorizacion.Id));
        Assert.Equal(HttpStatusCode.BadRequest, sinMotivo.StatusCode);
        Assert.Equal(Permisos.MotivoDelDescuento, (await sinMotivo.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);

        // Con motivo, como siempre.
        await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas",
            Cobro(caja, turno, cajero, quince with { MotivoDescuento = "Pasta doblada" }, autorizacion.Id)));
    }

    [Fact]
    public async Task El_dueno_da_cualquier_descuento_sin_motivo()
    {
        var (http, caja, _, dueno, turno, producto) = await PrepararAsync();
        var mitad = new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000, DescuentoCentesimas: 5000);

        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, dueno, mitad)));

        Assert.Equal("50 %", Assert.Single(ticket.Lineas).Descuento);
    }

    // ── Cobrar sin imprimir el ticket ───────────────────────────────────────

    [Fact]
    public async Task Sin_el_permiso_no_se_cobra_sin_ticket()
    {
        var (http, caja, cajero, _, turno, producto) = await PrepararAsync();
        var linea = new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000);

        var respuesta = await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, cajero, linea, sinTicket: true));

        Assert.False(respuesta.IsSuccessStatusCode);
        Assert.Contains("Cobrar sin imprimir el ticket", (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal(NivelPermiso.NoPermitido, EnLaBase(db => db.PermisosUsuario.AsNoTracking()
            .Single(p => p.UsuarioId == cajero.UsuarioId && p.Accion == AccionControlada.CobrarSinTicket)).Nivel);
    }

    [Fact]
    public async Task Con_el_permiso_la_venta_queda_sin_imprimir_en_la_bitacora_y_su_primera_impresion_no_pide_motivo()
    {
        var (http, caja, cajero, dueno, turno, producto) = await PrepararAsync();
        var linea = new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000);

        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, dueno, linea, sinTicket: true)));

        Assert.Equal(TicketAlCobrar.NoImprimir, ticket.LoQueSeHaceAlCobrar);
        Assert.True(EnLaBase(db => db.Ventas.AsNoTracking().Single(v => v.Id == ticket.VentaId)).TicketSinImprimir);
        var registro = EnLaBase(db => db.Bitacora.AsNoTracking().Single(b => b.VentaId == ticket.VentaId && b.Accion == AccionControlada.CobrarSinTicket));
        Assert.Equal(ResultadoBitacora.Hecho, registro.Resultado);
        Assert.Equal(dueno.UsuarioId, registro.UsuarioId);

        // La misma venta otra vez (se perdió la respuesta): tampoco se imprime.
        var otraVez = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas",
            Cobro(caja, turno, dueno, linea, sinTicket: true) with { VentaId = ticket.VentaId }));
        Assert.True(otraVez.YaCobrada);
        Assert.Equal(TicketAlCobrar.NoImprimir, otraVez.LoQueSeHaceAlCobrar);

        // Se imprime después, desde «Ventas del día»: la primera vez no es reimpresión (ni la cajera necesita el motivo).
        var impreso = await Leer<TicketDto>(await http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/primera-impresion",
            new SolicitudPrimeraImpresion(cajero.UsuarioId, caja.Id)));
        Assert.Equal(ticket.Folio, impreso.Folio);
        Assert.False(EnLaBase(db => db.Ventas.AsNoTracking().Single(v => v.Id == ticket.VentaId)).TicketSinImprimir);
    }

    [Fact]
    public async Task Lo_fiado_no_se_cobra_sin_ticket()
    {
        var (http, caja, _, dueno, turno, producto) = await PrepararAsync();
        var linea = new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000);

        var respuesta = await http.PostAsJsonAsync("api/ventas",
            Cobro(caja, turno, dueno, linea, sinTicket: true, cobros: [new CobroSolicitado(FormaPago.Credito, 100)]));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(TicketDto.FiadoConTicket, (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }
}
