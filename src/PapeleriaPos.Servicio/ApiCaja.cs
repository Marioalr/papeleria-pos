using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Servicio.Telegram;

namespace PapeleriaPos.Servicio;

/// <summary>Textos del ticket. Los mismos que tiene hoy el ticket de eleventa; se configuran en appsettings.</summary>
public sealed class OpcionesTicket
{
    public List<string> Encabezado { get; set; } = [];
    public List<string> Pie { get; set; } = [];
}

/// <summary>La API que usa la caja. Los errores de negocio regresan 400 con un mensaje para el cajero.</summary>
public static class ApiCaja
{
    public static void MapearApiCaja(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        api.MapGet("/cajas", async (PosDbContext db) =>
            await (from c in db.Cajas
                   join s in db.Sucursales on c.SucursalId equals s.Id
                   where c.Activa
                   orderby c.Serie
                   select new CajaDto(c.Id, c.Nombre, c.Serie, s.Id, s.Nombre, c.Equipo)).ToListAsync());

        api.MapPost("/sesion", async (SolicitudEntrada solicitud, PosDbContext db) =>
        {
            var usuario = await db.Usuarios.SingleOrDefaultAsync(u => u.NombreUsuario == solicitud.Usuario.Trim().ToLower());
            if (usuario is null)
                return Results.BadRequest(new ErrorDto("Usuario o PIN incorrecto."));
            var resultado = usuario.VerificarPin(solicitud.Pin, db.Reloj.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync();
            // Sin caja (el celular): quién autoriza sale de los de su sucursal; sin sucursal, de todos.
            return resultado switch
            {
                ResultadoPin.Correcto => Results.Ok(await new ServicioControl(db).SesionAsync(usuario, usuario.SucursalId)),
                ResultadoPin.Bloqueado => Results.BadRequest(new ErrorDto("Demasiados intentos: espera unos minutos.")),
                _ => Results.BadRequest(new ErrorDto("Usuario o PIN incorrecto.")),
            };
        });

        // La entrada con fichas (entrega 6): quién entra en esta caja, sin usuario, PIN ni hash; y entrar con la ficha y el PIN.
        // Las letras se reparten también entre los dados de baja: una baja no le cambia la letra a nadie.
        api.MapGet("/cajas/{cajaId:guid}/usuarios", async (Guid cajaId, PosDbContext db) =>
        {
            var caja = await db.Cajas.FindAsync(cajaId) ?? throw new InvalidOperationException("La caja no existe.");
            var control = new ServicioControl(db);
            var todos = await db.Usuarios.AsNoTracking().Where(u => u.SucursalId == null || u.SucursalId == caja.SucursalId)
                .Select(u => new { u.Id, u.Nombre }).ToListAsync();
            var turno = await new RegistroCaja(db).TurnoAbiertoAsync(cajaId);
            return FichasEntrada.Armar(await control.UsuariosQueEntranAsync(caja.SucursalId), todos.Select(u => (u.Id, u.Nombre)), await control.DefectoAsync(),
                turno?.CajeroId);
        });

        api.MapPost("/cajas/{cajaId:guid}/sesion", async (Guid cajaId, SolicitudEntradaFicha s, PosDbContext db) =>
        {
            var caja = await db.Cajas.FindAsync(cajaId) ?? throw new InvalidOperationException("La caja no existe.");
            // Solo quien tiene ficha en esta caja: los activos de su sucursal y el dueño. Uno dado de baja (con la ficha todavía en
            // pantalla) no suma intentos ni oye «PIN incorrecto»: lo mismo que dice la caja sin conexión.
            var usuario = await db.Usuarios.SingleOrDefaultAsync(u =>
                u.Id == s.UsuarioId && u.Activo && (u.SucursalId == null || u.SucursalId == caja.SucursalId));
            if (usuario is null)
                return Results.BadRequest(new ErrorDto("Ese usuario no entra en esta caja."));
            var resultado = usuario.VerificarPin(s.Pin, db.Reloj.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync();
            return resultado switch
            {
                ResultadoPin.Correcto => Results.Ok(await new ServicioControl(db).SesionAsync(usuario, caja.SucursalId)),
                ResultadoPin.Bloqueado => Results.BadRequest(new ErrorDto(FichasEntrada.DemasiadosIntentos)),
                _ => Results.BadRequest(new ErrorDto(FichasEntrada.PinIncorrecto(Usuario.IntentosPermitidos - usuario.IntentosFallidos))),
            };
        });

        api.MapGet("/cajas/{cajaId:guid}/turno", async (Guid cajaId, PosDbContext db) =>
            await new RegistroCaja(db).TurnoAbiertoAsync(cajaId) is { } turno ? Results.Ok(await TurnoDtoAsync(db, turno)) : Results.NoContent());

        api.MapPost("/turnos", async (SolicitudTurno s, PosDbContext db) =>
            await TurnoDtoAsync(db, await new RegistroCaja(db).AbrirTurnoAsync(s.CajaId, s.CajeroId, s.FondoCentavos, s.Id)));

        api.MapPost("/turnos/{turnoId:guid}/movimientos", async (Guid turnoId, SolicitudMovimiento s, PosDbContext db, IOptions<OpcionesTicket> ticket, EnvioVentasEleventa eleventa) =>
            await ApiControl.MovimientoAsync(turnoId, s, db, ticket.Value, eleventa));

        // La cuenta del turno, guardada como corte parcial. La pantalla ya no la enseña antes de cerrar (corte-informativo).
        api.MapPost("/turnos/{turnoId:guid}/corte-x", async (Guid turnoId, Guid usuarioId, PosDbContext db) =>
            await CorteDtoAsync(db, await new RegistroCaja(db).CorteXAsync(turnoId, usuarioId)));

        // El corte X de la pantalla (entrega 6): a ciegas, sin el efectivo; y si quien entró puede hacer el corte.
        api.MapGet("/turnos/{turnoId:guid}/corte-informativo", async (Guid turnoId, Guid usuarioId, PosDbContext db) =>
            await new RegistroCaja(db).InformativoAsync(turnoId, usuarioId));

        // El corte del turno (entrega 6, 2026-10-14): el usuario de la sesión, con el permiso, manda el total que contó.
        api.MapPost("/turnos/{turnoId:guid}/corte-z", async (Guid turnoId, SolicitudCorteZ s, PosDbContext db, ServicioRespaldos respaldos, EnvioVentasEleventa eleventa,
            OpcionesEleventa opcionesEleventa) =>
        {
            // El piloto en la caja 2: el corte se hace en eleventa, aunque la pantalla ya no lo ofrezca (una caja de antes).
            if (opcionesEleventa.CorteEnEleventa)
                throw new InvalidOperationException(CorteEnElPiloto.CorteEnEleventa);
            var corte = await CorteDtoAsync(db, await new RegistroCaja(db).CorteZAsync(turnoId, s.UsuarioId, s.ContadoCentavos, id: s.Id));
            // Si el turno pasó a eleventa, su cierre ya está en la bandeja: que vaya ya.
            eleventa.Despertar();
            // Con cada cierre, una copia de la base (además de la diaria); si falla, el corte ya quedó guardado.
            _ = Task.Run(() => respaldos.Hacer($"z-{corte.FechaLocal:HHmm}"));
            return corte;
        });

        // El corte del día (entrega 6): todas las cajas y turnos de la sucursal de esta caja, para quien tiene el permiso.
        // En el piloto con eleventa se ve, pero no se cierra.
        api.MapGet("/cajas/{cajaId:guid}/corte-del-dia", async (Guid cajaId, Guid usuarioId, PosDbContext db, PresenciaCajas presencia,
            OpcionesEleventa opcionesEleventa) =>
        {
            var caja = await db.Cajas.FindAsync(cajaId) ?? throw new InvalidOperationException("La caja no existe.");
            await new ServicioControl(db).ExigirAsync(usuarioId, caja.SucursalId, AccionControlada.CorteDelDia, "El corte del día lo ve quien tiene ese permiso.");
            var registro = new RegistroCorteDelDia(db);
            var dia = await registro.ArmarAsync(caja.SucursalId, registro.Hoy, presencia.Foto(), opcionesEleventa.CorteEnEleventa);
            // El piloto: los turnos que cerró el corte de eleventa dicen lo que se contó allá (TurnosDelPiloto).
            return opcionesEleventa.CorteEnEleventa ? await CorteEnElPiloto.ConLoDeEleventaAsync(db, dia) : dia;
        });

        api.MapPost("/cajas/{cajaId:guid}/corte-del-dia/cierre", async (Guid cajaId, SolicitudCierreDia s, PosDbContext db, PresenciaCajas presencia,
            OpcionesEleventa opcionesEleventa) =>
        {
            // El piloto: el día lo cierra el corte de eleventa. Cerrarlo aquí no tocaría eleventa, pero no diría nada.
            if (opcionesEleventa.CorteEnEleventa)
                throw new InvalidOperationException(CorteEnElPiloto.CorteDelDiaEnEleventa);
            var caja = await db.Cajas.FindAsync(cajaId) ?? throw new InvalidOperationException("La caja no existe.");
            var registro = new RegistroCorteDelDia(db);
            return await registro.CerrarAsync(caja.SucursalId, caja.Id, s.UsuarioId, registro.Hoy, presencia.Foto());
        });

        api.MapGet("/productos/codigo/{codigo}", async (string codigo, Guid sucursalId, PosDbContext db, CatalogoEnMemoria catalogo) =>
            await catalogo.PorCodigoAsync(db, codigo, sucursalId) is { } p ? Results.Ok(p) : Results.NotFound(new ErrorDto($"No existe el código «{codigo}».")));

        api.MapGet("/productos/{varianteId:guid}", async (Guid varianteId, Guid sucursalId, PosDbContext db, CatalogoEnMemoria catalogo) =>
            await catalogo.PorVarianteAsync(db, varianteId, sucursalId) is { } p ? Results.Ok(p) : Results.NotFound(new ErrorDto("El producto ya no está en el catálogo.")));

        // La pantalla de venta pide más (hasta 300) para juntar las familias completas; la lista muestra 30 renglones.
        api.MapGet("/productos/buscar", async (string q, Guid sucursalId, Guid? cajaId, int? maximo, PosDbContext db, CatalogoEnMemoria catalogo) =>
            await catalogo.BuscarAsync(db, q, sucursalId, Math.Clamp(maximo ?? 30, 1, 300), cajaId: cajaId));

        // Lo que se buscó y qué se eligió: lo elegido sale primero la próxima vez en esa caja.
        api.MapPost("/busquedas", async (SolicitudBusqueda s, PosDbContext db, CatalogoEnMemoria catalogo) =>
        {
            // La misma clave que recuerda la copia de la caja (entrega 5, parte 3).
            var texto = IndiceDeBusqueda.ClaveGuardada(s.Texto);
            if (texto.Length == 0)
                return Results.NoContent();
            if (!await db.Cajas.AnyAsync(c => c.Id == s.CajaId) || (s.VarianteId is { } v && !await db.Variantes.AnyAsync(x => x.Id == v)))
                return Results.BadRequest(new ErrorDto("La caja o el producto de la búsqueda no existen."));
            db.Busquedas.Add(new Busqueda { CajaId = s.CajaId, FechaUtc = DateTime.UtcNow, Texto = texto, Resultados = s.Resultados, VarianteId = s.VarianteId });
            await db.SaveChangesAsync();
            if (s.VarianteId is { } elegida)
                catalogo.Aprender(s.CajaId, texto, elegida);
            return Results.NoContent();
        });

        // Los rápidos de la venta (docs/propuesta-interfaz.md, «Rápidos»): lo que no tiene código de barras y se vende todo el día.
        api.MapGet("/sucursales/{sucursalId:guid}/rapidos", async (Guid sucursalId, PosDbContext db) =>
            await db.ConfiguracionesSucursal.AsNoTracking().Where(c => c.SucursalId == sucursalId).Select(c => c.Rapidos).FirstOrDefaultAsync() ?? []);

        api.MapPost("/ventas", async (SolicitudCobro s, PosDbContext db, CatalogoEnMemoria catalogo, IOptions<OpcionesTicket> ticket, EnvioVentasEleventa eleventa,
            OpcionesEleventa opcionesEleventa) =>
        {
            // La caja manda otra vez la misma venta si no le llegó la respuesta: se regresa el ticket que ya se cobró, como se cobró,
            // y la caja dice que ya había quedado.
            if (s.VentaId is { } ventaId && await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).SingleOrDefaultAsync(v => v.Id == ventaId) is { } cobrada)
                return Results.Ok((await TicketAsync(db, cobrada, ticket.Value)) with { YaCobrada = true });
            // El piloto en la caja 2: solo efectivo, aunque la pantalla ya no ofrezca lo demás.
            if (opcionesEleventa.SoloEfectivo)
                SoloEfectivoEnElPiloto.RevisarCobro(s.Cobros);

            var turno = await db.Turnos.FindAsync(s.TurnoId) ?? throw new InvalidOperationException("El turno no existe.");
            var caja = await db.Cajas.FindAsync(s.CajaId) ?? throw new InvalidOperationException("La caja no existe.");
            var venta = await ArmarVentaAsync(db, catalogo, caja, turno.Id, s.CajeroId, s.VentaId ?? Ids.Nuevo(), s.Lineas,
                await ApiClientes.MayoreoDelCobroAsync(db, s.ClienteId, s.MayoreoDeCliente));
            // La caja escaneó contra su copia (entrega 5): si el precio o el mayoreo de algo del ticket cambió después, no se
            // cobra con precios que el cajero no vio; se le contestan los de ahora y la caja pone el ticket al día. Una caja de
            // antes no manda la versión: se cobra como siempre.
            if (s.VersionCatalogo is { } version && await PreciosCambiadosAsync(db, catalogo, caja.SucursalId, s.Lineas, version) is { } cambiados)
                return Results.Json(cambiados, statusCode: StatusCodes.Status409Conflict);
            (venta.ClienteId, venta.AutorizacionDescuentoId, venta.AutorizacionBajoCostoId, venta.AutorizacionCreditoId) =
                (s.ClienteId, s.AutorizacionDescuentoId, s.AutorizacionBajoCostoId, s.AutorizacionCreditoId);
            // Las impresiones van juntas en una línea; su precio y costo salen de cada trabajo, no de la caja.
            var impresiones = s.Impresiones is { Count: > 0 } ids ? await ImpresionesDelPos.EnLaVenta(db).ParaCobrarAsync(caja.SucursalId, ids) : [];
            if (impresiones.Count > 0)
                venta.FijarImpresiones(impresiones.Sum(t => t.PrecioCentavos), impresiones.Sum(t => t.CostoCentavos));
            // El piloto en la caja 2: la venta entra a la bandeja hacia eleventa junto con el cobro, y se intenta
            // mandar ya, para que el ticket salga con el folio de eleventa.
            eleventa.Encolar(db, venta.Id);
            try
            {
                await new RegistroCaja(db).CobrarAsync(venta, s.Cobros, impresiones: impresiones);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                // La misma venta llegó dos veces a la vez (entrega 5, parte 2): la caja dejó de esperar este cobro (pasó su plazo) y
                // la mandó con la bandeja con el mismo Id, y entró primero esa. Queda una; se regresa su ticket.
                if (await YaCobradaAsync(db, venta.Id) is not { } yaCobrada)
                    throw;
                return Results.Ok((await TicketAsync(db, yaCobrada, ticket.Value)) with { YaCobrada = true });
            }
            await eleventa.MandarAlCobrarAsync(venta.Id);
            return Results.Ok(await TicketAsync(db, venta, ticket.Value));
        });

        api.MapGet("/cajas/{cajaId:guid}/ventas-del-dia", async (Guid cajaId, PosDbContext db) =>
        {
            var hoy = HoraMexico.ALocal(db.Reloj.GetUtcNow().UtcDateTime).Date;
            var desde = HoraMexico.AUtc(hoy);
            var ventas = await (from v in db.Ventas.Include(v => v.Lineas)
                                join u in db.Usuarios on v.CajeroId equals u.Id
                                where v.CajaId == cajaId && v.PagadaUtc >= desde
                                select new { Venta = v, Cajero = u.Nombre }).ToListAsync();
            return ventas.OrderByDescending(x => x.Venta.Folio)
                .Select(x => new VentaDelDiaDto(x.Venta.Id, x.Venta.FolioTexto!, HoraMexico.ALocal(x.Venta.PagadaUtc!.Value), x.Venta.TotalCentavos,
                    (int)x.Venta.NumeroDeArticulos, x.Cajero, x.Venta.Estado == EstadoVenta.Cancelada))
                .ToList();
        });

        // Reimprimir: con motivo si la regla lo pide, y queda en la bitácora.
        api.MapPost("/ventas/{ventaId:guid}/reimpresion", async (Guid ventaId, SolicitudReimpresion s, PosDbContext db, IOptions<OpcionesTicket> ticket) =>
        {
            var venta = await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).SingleOrDefaultAsync(v => v.Id == ventaId)
                ?? throw new InvalidOperationException("La venta no existe.");
            await new ServicioControl(db).RequerirAsync(s.UsuarioId, venta.SucursalId, s.CajaId, AccionControlada.Reimprimir, venta.TotalCentavos,
                $"Reimpresión del ticket {venta.FolioTexto}", motivo: s.Motivo, ventaId: venta.Id);
            await db.SaveChangesAsync();
            return await TicketAsync(db, venta, ticket.Value);
        });

        api.MapGet("/ventas/{ventaId:guid}/ticket", async (Guid ventaId, PosDbContext db, IOptions<OpcionesTicket> ticket) =>
            await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).SingleOrDefaultAsync(v => v.Id == ventaId) is { } venta
                ? Results.Ok(await TicketAsync(db, venta, ticket.Value))
                : Results.NotFound(new ErrorDto("La venta no existe.")));

        api.MapGet("/cajas/{cajaId:guid}/ultimo-ticket", async (Guid cajaId, PosDbContext db, IOptions<OpcionesTicket> ticket) =>
            await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).Where(v => v.CajaId == cajaId && v.Folio != null)
                .OrderByDescending(v => v.Folio).FirstOrDefaultAsync() is { } venta
                ? Results.Ok(await TicketAsync(db, venta, ticket.Value))
                : Results.NotFound(new ErrorDto("Esta caja todavía no tiene ventas.")));
    }

    /// <summary>La venta con ese Id, si ya se guardó (con sus líneas y cobros, para su ticket).</summary>
    internal static async Task<Venta?> YaCobradaAsync(PosDbContext db, Guid ventaId)
    {
        db.ChangeTracker.Clear();
        return await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).SingleOrDefaultAsync(v => v.Id == ventaId);
    }

    /// <summary>
    /// Arma la venta con los precios del catálogo: la caja solo dice qué y cuánto. Un precio distinto (cambiado en la caja,
    /// o el que se cobró sin conexión) queda como precio cambiado. Con <paramref name="mayoreoDeCliente"/> (el cliente del
    /// ticket paga mayoreo siempre), los mismos precios que calculó la caja.
    /// </summary>
    internal static async Task<Venta> ArmarVentaAsync(
        PosDbContext db, CatalogoEnMemoria catalogo, Caja caja, Guid turnoId, Guid cajeroId, Guid ventaId, IReadOnlyList<LineaSolicitada> lineas,
        bool mayoreoDeCliente = false)
    {
        var venta = new Venta
        {
            Id = ventaId,
            SucursalId = caja.SucursalId,
            CajaId = caja.Id,
            TurnoId = turnoId,
            CajeroId = cajeroId,
            Serie = caja.Serie,
            CreadaUtc = db.Reloj.GetUtcNow().UtcDateTime,
            MayoreoDeCliente = mayoreoDeCliente,
        };
        foreach (var l in lineas)
        {
            var cantidad = new Cantidad(l.CantidadMilesimas);
            if (l.VarianteId is not { } varianteId)
            {
                var comun = venta.AgregarArticuloComun(l.Descripcion ?? "", l.PrecioCentavos ?? 0, cantidad);
                venta.AplicarDescuentoSolicitado(comun, l.DescuentoCentesimas, l.DescuentoCentavos, l.MotivoDescuento);
                continue;
            }
            // Sin su existencia: la venta no la usa, y leerla por cada línea era ir a la base antes de cobrar.
            var producto = await catalogo.PorVarianteAsync(db, varianteId, caja.SucursalId, conExistencia: false)
                ?? throw new InvalidOperationException($"«{l.Descripcion ?? "Un producto"}» ya no está en el catálogo.");
            var agregada = venta.AgregarProducto(producto.ParaVenta(), producto.PresentacionModelo(l.PresentacionId), cantidad, producto.PreciosModelo());
            if (l.PrecioCentavos is { } precio && precio != agregada.PrecioCentavos)
                venta.CambiarPrecio(agregada, precio, l.AutorizacionId);
            venta.AplicarDescuentoSolicitado(agregada, l.DescuentoCentesimas, l.DescuentoCentavos, l.MotivoDescuento);
        }
        return venta;
    }

    /// <summary>
    /// Los productos del ticket cuyo precio o mayoreo cambió después de la versión con que la caja puso sus precios, como
    /// están ahora; nada si no cambió ninguno. Se revisa después de armar la venta: lo que cambie mientras tanto ya no la toca.
    /// Una versión más nueva que la del servicio quiere decir que su base cambió entera (la prueba empezó de cero): se
    /// revisa todo.
    /// </summary>
    private static async Task<PreciosCambiadosDto?> PreciosCambiadosAsync(
        PosDbContext db, CatalogoEnMemoria catalogo, Guid sucursalId, IReadOnlyList<LineaSolicitada> lineas, long version)
    {
        var actual = await catalogo.VersionAsync(db);
        long Desde(LineaSolicitada l) => (l.VersionCatalogo ?? version) is var v && v <= actual ? v : -1;
        var cambiados = await PreciosDeLaCopia.CambiadosAsync(db, sucursalId, lineas.Select(l => l with { VersionCatalogo = Desde(l) }).ToList(),
            version, actual);
        if (cambiados.Count == 0)
            return null;
        var productos = new List<ProductoVentaDto>();
        foreach (var varianteId in cambiados)
        {
            if (await catalogo.PorVarianteAsync(db, varianteId, sucursalId) is { } producto)
                productos.Add(producto);
        }
        return new PreciosCambiadosDto(PreciosDeLaCopia.Mensaje(productos), productos, actual);
    }

    internal static async Task<TurnoDto> TurnoDtoAsync(PosDbContext db, Turno t)
    {
        var cajero = await db.Usuarios.Where(u => u.Id == t.CajeroId).Select(u => u.Nombre).SingleAsync();
        return new TurnoDto(t.Id, t.CajaId, t.CajeroId, cajero, HoraMexico.ALocal(t.AbiertoUtc), t.FondoCentavos);
    }

    internal static async Task<CorteDto> CorteDtoAsync(PosDbContext db, Corte c)
    {
        var turno = await db.Turnos.FindAsync(c.TurnoId);
        var nombres = await db.Usuarios.Where(u => u.Id == turno!.CajeroId || u.Id == c.RealizadoPorId).ToDictionaryAsync(u => u.Id, u => u.Nombre);
        return new CorteDto(c.Tipo, HoraMexico.ALocal(c.FechaUtc), nombres[turno!.CajeroId], nombres.GetValueOrDefault(c.RealizadoPorId, "—"), c.Ventas,
            c.FondoCentavos, c.EfectivoVentasCentavos, c.EntradasCentavos, c.SalidasCentavos, c.TarjetaCentavos, c.TransferenciaCentavos, c.EsperadoCentavos,
            c.ContadoCentavos, c.DiferenciaCentavos, c.DevolucionesCentavos, c.Cancelaciones, c.EnContingencia, c.RedondeoCentavos, c.CreditoCentavos,
            c.Abonos, c.AbonosEfectivoCentavos, c.AbonosTarjetaCentavos, c.AbonosTransferenciaCentavos, c.SaldoDevueltoCentavos,
            // Al cerrar el turno, la caja dice si la diferencia pasó del umbral y se avisa por Telegram.
            c is { Tipo: TipoCorte.Z, DiferenciaCentavos: { } diferencia } ? AlertasTelegram.UmbralSiAvisa(db, turno.SucursalId, diferencia) : null);
    }

    internal static async Task<TicketDto> TicketAsync(PosDbContext db, Venta v, OpcionesTicket opciones)
    {
        var caja = await db.Cajas.FindAsync(v.CajaId);
        var cajero = await db.Usuarios.Where(u => u.Id == v.CajeroId).Select(u => u.Nombre).SingleAsync();
        var ids = v.Lineas.Where(l => l.PresentacionId != null).Select(l => l.PresentacionId!.Value).ToList();
        var presentaciones = await db.Presentaciones.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => (p.Nombre, p.EsBase));
        var configuracion = await ApiConfiguracion.ConfiguracionAsync(db, v.SucursalId, opciones);
        var cliente = v.ClienteId is { } clienteId && await db.ClientesRegistrados.FindAsync(clienteId) is { } c ? RegistroClientes.Dto(c) : null;
        // El piloto: el folio de eleventa, si llegó; si otra caja de eleventa lo repitió, el de la caja (FoliosRepetidos).
        var folioEleventa = await db.EnviosAEleventa.AsNoTracking().Where(e => e.Tipo == TipoEnvioEleventa.Venta && e.VentaId == v.Id)
            .Select(e => e.FolioRepetido ? null : e.FolioEleventa).FirstOrDefaultAsync();
        // Con «Al cobrar» de la sucursal: la caja decide con el ticket que ya le llega, sin preguntar nada más al cobrar.
        return ArmadoTicket.Desde(v, caja!.Nombre, cajero, id => presentaciones.TryGetValue(id, out var p) ? p : null,
            configuracion.RenglonesEncabezado, configuracion.RenglonesPie, ApiConfiguracion.Logo(configuracion), cliente,
            await new RegistroCredito(db).TicketAsync(v.Id)) with { FolioEleventa = folioEleventa, AlCobrar = configuracion.AlCobrar };
    }
}
