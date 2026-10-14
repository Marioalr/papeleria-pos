using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Servicio;

/// <summary>
/// El crédito de los clientes registrados (fase 3, entrega 2): su cuenta, sus condiciones y ajustes (el dueño), los
/// abonos en cualquier caja con su comprobante, su cancelación y la verificación de las transferencias; y desde la parte
/// 3, su estado de cuenta y devolverle en efectivo lo que tiene a favor.
/// </summary>
public static class ApiCredito
{
    public static void MapearApiCredito(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        api.MapGet("/clientes/{clienteId:guid}/credito", (Guid clienteId, PosDbContext db) => new RegistroCredito(db).MovimientosAsync(clienteId));

        api.MapPut("/clientes/{clienteId:guid}/credito", (Guid clienteId, SolicitudCreditoCliente s, PosDbContext db) =>
            new RegistroCredito(db).GuardarCondicionesAsync(clienteId, s));

        api.MapPost("/clientes/{clienteId:guid}/ajustes", (Guid clienteId, SolicitudAjusteCredito s, PosDbContext db) =>
            new RegistroCredito(db).AjustarAsync(clienteId, s));

        api.MapGet("/sucursales/{sucursalId:guid}/creditos", (Guid sucursalId, PosDbContext db) => new RegistroCredito(db).CuentasDeLaSucursalAsync(sucursalId));

        // El estado de cuenta (parte 3), con el encabezado del ticket de la sucursal de la caja que lo pide.
        api.MapGet("/clientes/{clienteId:guid}/estado-de-cuenta", async (Guid clienteId, Guid sucursalId, DateOnly? desde, PosDbContext db,
            IOptions<OpcionesTicket> opciones) =>
        {
            var estado = await new RegistroCredito(db).EstadoDeCuentaAsync(clienteId, desde);
            return estado with { Encabezado = (await ApiConfiguracion.ConfiguracionAsync(db, sucursalId, opciones.Value)).RenglonesEncabezado };
        });

        // Devolverle en efectivo lo que tiene a favor (parte 3): sale el mismo comprobante que el de un abono, con su tipo.
        // En el piloto con eleventa, ni esto ni los abonos nuevos: eleventa no los tiene y su corte no cuadraría. Lo que
        // llega otra vez con el mismo Id ya pasó: regresa su comprobante (SoloEfectivoEnElPiloto.RevisarAbonoNuevoAsync).
        api.MapPost("/clientes/{clienteId:guid}/saldo-devuelto", async (Guid clienteId, SolicitudDevolverSaldo s, PosDbContext db,
            IOptions<OpcionesTicket> opciones, OpcionesEleventa eleventa) =>
        {
            if (eleventa.SoloEfectivo)
                await SoloEfectivoEnElPiloto.RevisarAbonoNuevoAsync(db, s.Id, SoloEfectivoEnElPiloto.NoSeDevuelveSaldo);
            return await ComprobanteAsync(db, await new RegistroCredito(db).DevolverSaldoAsync(clienteId, s), opciones.Value);
        });

        api.MapPost("/abonos", async (SolicitudAbono s, PosDbContext db, IOptions<OpcionesTicket> opciones, OpcionesEleventa eleventa) =>
        {
            if (eleventa.SoloEfectivo)
                await SoloEfectivoEnElPiloto.RevisarAbonoNuevoAsync(db, s.Id, SoloEfectivoEnElPiloto.NoSeAbona);
            return await ComprobanteAsync(db, await new RegistroCredito(db).AbonarAsync(s), opciones.Value);
        });

        // En el piloto con eleventa, solo el abono que fue en efectivo: lo devuelto sale en efectivo.
        api.MapPost("/abonos/{abonoId:guid}/cancelacion", async (Guid abonoId, SolicitudCancelacion s, PosDbContext db, IOptions<OpcionesTicket> opciones,
            OpcionesEleventa eleventa) =>
        {
            if (eleventa.SoloEfectivo)
                await SoloEfectivoEnElPiloto.RevisarCancelacionDeAbonoAsync(db, abonoId);
            return await ComprobanteAsync(db, await new RegistroCredito(db).CancelarAbonoAsync(abonoId, s), opciones.Value);
        });

        // Reimprimir el comprobante: con motivo si la regla lo pide, y queda en la bitácora (como el ticket).
        api.MapPost("/abonos/{abonoId:guid}/reimpresion", async (Guid abonoId, SolicitudReimpresion s, PosDbContext db, IOptions<OpcionesTicket> opciones) =>
        {
            var abono = await db.Abonos.FindAsync(abonoId) ?? throw new InvalidOperationException("El abono no existe.");
            await new ServicioControl(db).RequerirAsync(s.UsuarioId, abono.SucursalId, s.CajaId, AccionControlada.Reimprimir, abono.MontoCentavos,
                $"Reimpresión {(abono.Tipo == TipoAbono.SaldoDevuelto ? "del saldo a favor devuelto" : "del abono")} {abono.Folio}", motivo: s.Motivo);
            await db.SaveChangesAsync();
            return await ComprobanteAsync(db, abono, opciones.Value);
        });

        api.MapPost("/abonos/{abonoId:guid}/verificacion", async (Guid abonoId, SolicitudVerificacion s, PosDbContext db) =>
        {
            var abono = await new RegistroCredito(db).VerificarAbonoAsync(abonoId, s.Usuario, s.Pin, s.Llego);
            return new { abono.Id, abono.Verificacion };
        });

        api.MapGet("/cajas/{cajaId:guid}/abonos-del-dia", (Guid cajaId, PosDbContext db) => new RegistroCredito(db).AbonosDelDiaAsync(cajaId));

        // Sin conexión (parte 2.4): la caja pide los saldos cuando cambia la versión del crédito y pregunta cuáles de los suyos ya están.
        api.MapPost("/cajas/{cajaId:guid}/creditos", async (Guid cajaId, SolicitudSaldosCredito s, PosDbContext db) =>
        {
            var caja = await db.Cajas.FindAsync(cajaId) ?? throw new InvalidOperationException("La caja no existe.");
            return await new RegistroCredito(db).SaldosAsync(caja.SucursalId, s.Pendientes);
        });

        api.MapGet("/sucursales/{sucursalId:guid}/creditos/por-revisar", (Guid sucursalId, PosDbContext db) =>
            new RegistroCredito(db).PorRevisarAsync(sucursalId));

        api.MapPost("/creditos/revisiones/{revisionId:guid}", async (Guid revisionId, SolicitudRevision s, PosDbContext db) =>
        {
            await new RegistroCredito(db).MarcarRevisadaAsync(revisionId, s);
            return Results.NoContent();
        });
    }

    /// <summary>El comprobante del abono (o de su cancelación), con lo que debía y lo que quedó cuando se recibió.</summary>
    internal static async Task<ComprobanteAbonoDto> ComprobanteAsync(PosDbContext db, Abono a, OpcionesTicket opciones)
    {
        var caja = await db.Cajas.FindAsync(a.CajaId);
        var usuario = await db.Usuarios.FindAsync(a.UsuarioId);
        var cliente = await db.ClientesRegistrados.FindAsync(a.ClienteId);
        var encabezado = (await ApiConfiguracion.ConfiguracionAsync(db, a.SucursalId, opciones)).RenglonesEncabezado;
        // Al cancelarlo, lo que quedó a deber lo guarda su movimiento (el abono guarda lo de cuando se recibió).
        var trasCancelar = a.CanceladoUtc is null ? null
            : await db.MovimientosCredito.Where(m => m.AbonoId == a.Id && m.Tipo == TipoMovimientoCredito.AbonoCancelado)
                .Select(m => (long?)m.SaldoCentavos).SingleOrDefaultAsync();
        return new ComprobanteAbonoDto(a.Id, a.Folio, HoraMexico.ALocal(a.FechaUtc), caja!.Nombre, usuario!.Nombre, a.ClienteId, cliente!.Nombre, a.Forma,
            a.MontoCentavos, a.RedondeoCentavos, a.RecibidoCentavos, a.CambioCentavos, a.Referencia, a.SaldoAnteriorCentavos, a.SaldoCentavos, a.Vence,
            encabezado, a.CanceladoUtc is not null, a.EnContingencia, a.MotivoCancelacion, trasCancelar, a.Tipo);
    }
}
