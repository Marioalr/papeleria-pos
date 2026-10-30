using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Servicio;
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

        // Entrar escribiendo el usuario (el celular, desde la red de la tienda). A quien está dado de baja o todavía no tiene PIN se le
        // dice lo mismo que a un usuario que no existe (revisión del PR #144): escribiendo usuarios no se sabe quién es de la tienda.
        // En la caja, su ficha sí dice «sin PIN».
        api.MapPost("/sesion", async (SolicitudEntrada solicitud, PosDbContext db) =>
        {
            var usuario = await db.Usuarios.SingleOrDefaultAsync(u => u.NombreUsuario == solicitud.Usuario.Trim().ToLower() && u.Activo);
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
                // Vino de eleventa y todavía no tiene PIN: lo mismo que dice la entrada al tocar su ficha, sin sumarle intentos.
                ResultadoPin.SinPin => Results.BadRequest(new ErrorDto(FichasEntrada.SinPin)),
                _ => Results.BadRequest(new ErrorDto(FichasEntrada.PinIncorrecto(Usuario.IntentosPermitidos - usuario.IntentosFallidos))),
            };
        });

        api.MapGet("/cajas/{cajaId:guid}/turno", async (Guid cajaId, PosDbContext db) =>
            await new RegistroCaja(db).TurnoAbiertoAsync(cajaId) is { } turno ? Results.Ok(await TurnoDtoAsync(db, turno)) : Results.NoContent());

        api.MapPost("/turnos", async (SolicitudTurno s, PosDbContext db, ILoggerFactory registros) =>
        {
            var turno = await TurnoDtoAsync(db, await new RegistroCaja(db).AbrirTurnoAsync(s.CajaId, s.CajeroId, s.FondoCentavos, s.Id));
            // En el registro del servicio (el piloto alfa: juntar datos), para saber de qué turno es cada cosa.
            registros.CreateLogger("PapeleriaPos.Servicio.Turnos").LogInformation("Turno · abierto el turno de {Cajero} a las {Abierto}, con {Fondo} de fondo · turno {Turno}",
                turno.Cajero, turno.AbiertoLocal.ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture),
                (turno.FondoCentavos / 100m).ToString("$#,##0.00", CultureInfo.InvariantCulture), turno.Id);
            return turno;
        });

        api.MapPost("/turnos/{turnoId:guid}/movimientos", async (Guid turnoId, SolicitudMovimiento s, PosDbContext db, IOptions<OpcionesTicket> ticket, EnvioVentasEleventa eleventa) =>
            await ApiControl.MovimientoAsync(turnoId, s, db, ticket.Value, eleventa));

        // Las entradas y salidas del turno (F7/F8), para cancelar una equivocada (como en eleventa, 2026-10-22). En el piloto se ven,
        // pero no se cancelan: van a eleventa.
        api.MapGet("/turnos/{turnoId:guid}/movimientos", async (Guid turnoId, PosDbContext db, OpcionesEleventa opcionesEleventa) =>
            await new RegistroCaja(db).MovimientosDelTurnoAsync(turnoId, opcionesEleventa.Escribir));

        // Cancelar una entrada o salida equivocada: en su caja y en su mismo turno, antes de su corte, con motivo y su permiso.
        // Regresa cómo quedaron las del turno. La misma solicitud otra vez (se perdió la respuesta) contesta lo mismo.
        api.MapPost("/turnos/{turnoId:guid}/movimientos/{movimientoId:guid}/cancelacion", async (Guid turnoId, Guid movimientoId, SolicitudCancelarMovimiento s,
            PosDbContext db, OpcionesEleventa opcionesEleventa) =>
        {
            if (!await db.MovimientosCaja.AnyAsync(m => m.Id == movimientoId && m.TurnoId == turnoId))
                return Results.NotFound(new ErrorDto("Esa entrada o salida no es de este turno."));
            var registro = new RegistroCaja(db);
            await registro.CancelarMovimientoAsync(movimientoId, s, opcionesEleventa.Escribir);
            return Results.Ok(await registro.MovimientosDelTurnoAsync(turnoId, opcionesEleventa.Escribir));
        });

        // La cuenta del turno, guardada como corte parcial. La pantalla ya no la enseña antes de cerrar (corte-informativo).
        api.MapPost("/turnos/{turnoId:guid}/corte-x", async (Guid turnoId, Guid usuarioId, PosDbContext db) =>
            await CorteDtoAsync(db, await new RegistroCaja(db).CorteXAsync(turnoId, usuarioId)));

        // El corte X de la pantalla (entrega 6): a ciegas, sin el efectivo; y si quien entró puede hacer el corte.
        api.MapGet("/turnos/{turnoId:guid}/corte-informativo", async (Guid turnoId, Guid usuarioId, PosDbContext db) =>
            await new RegistroCaja(db).InformativoAsync(turnoId, usuarioId));

        // El corte del turno (entrega 6, 2026-10-14): el usuario de la sesión, con el permiso, manda el total que contó.
        api.MapPost("/turnos/{turnoId:guid}/corte-z", async (Guid turnoId, SolicitudCorteZ s, PosDbContext db, ServicioRespaldos respaldos, EnvioVentasEleventa eleventa,
            OpcionesEleventa opcionesEleventa, ILoggerFactory registros) =>
        {
            // El piloto en la caja 2: el corte se hace en eleventa, aunque la pantalla ya no lo ofrezca (una caja de antes).
            if (opcionesEleventa.CorteEnEleventa)
                throw new InvalidOperationException(CorteEnElPiloto.CorteEnEleventa);
            var corte = await CorteDtoAsync(db, await new RegistroCaja(db).CorteZAsync(turnoId, s.UsuarioId, s.ContadoCentavos, id: s.Id));
            registros.CreateLogger("PapeleriaPos.Servicio.Turnos").LogInformation("Turno · corte Z del turno de {Cajero} a las {Cuando}: {Ventas} ventas · turno {Turno}",
                corte.Cajero, corte.FechaLocal.ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture), corte.Ventas, turnoId);
            // Si el turno pasó a eleventa, su cierre ya está en la bandeja: que vaya ya.
            eleventa.Despertar();
            // Con cada cierre, una copia de la base (además de la diaria); si falla, el corte ya quedó guardado.
            _ = Task.Run(() => respaldos.Hacer($"z-{corte.FechaLocal:HHmm}"));
            return corte;
        });

        // El corte del día (entrega 6): todas las cajas y turnos de la sucursal de esta caja, para quien tiene el permiso.
        // En el piloto con eleventa se ve, pero no se cierra. Como el de eleventa (2026-10-17): el de otro día con «fecha» (hoy
        // si no se da; uno que ya pasó solo se ve) y lo vendido por departamento; la utilidad (y el costo de cada departamento),
        // solo para quien ve la utilidad.
        api.MapGet("/cajas/{cajaId:guid}/corte-del-dia", async (Guid cajaId, Guid usuarioId, DateOnly? fecha, PosDbContext db, PresenciaCajas presencia,
            OpcionesEleventa opcionesEleventa) =>
        {
            var caja = await db.Cajas.FindAsync(cajaId) ?? throw new InvalidOperationException("La caja no existe.");
            var control = new ServicioControl(db);
            await control.ExigirAsync(usuarioId, caja.SucursalId, AccionControlada.CorteDelDia, "El corte del día lo ve quien tiene ese permiso.");
            var registro = new RegistroCorteDelDia(db);
            var hoy = registro.Hoy;
            if (fecha > hoy)
                throw new InvalidOperationException(RegistroCorteDelDia.TodaviaNoLlega);
            var dia = await registro.ArmarAsync(caja.SucursalId, fecha ?? hoy, presencia.Foto(), opcionesEleventa.CorteEnEleventa, porDepartamento: true,
                conUtilidad: await control.PuedeAsync(usuarioId, AccionControlada.VerUtilidad));
            // El piloto: los turnos que cerró el corte de eleventa dicen lo que se contó allá (TurnosDelPiloto), también los de un
            // día del piloto que se ve después de dejarlo. Un día que ya pasó es del piloto si lo fue, no según el de hoy.
            dia = await CorteEnElPiloto.ConLoDeEleventaAsync(db, dia);
            return dia.DeOtroDia ? dia with { Piloto = await CorteEnElPiloto.FueDelPilotoAsync(db, dia) } : dia;
        });

        api.MapPost("/cajas/{cajaId:guid}/corte-del-dia/cierre", async (Guid cajaId, SolicitudCierreDia s, PosDbContext db, PresenciaCajas presencia,
            OpcionesEleventa opcionesEleventa) =>
        {
            // El piloto: el día lo cierra el corte de eleventa. Cerrarlo aquí no tocaría eleventa, pero no diría nada.
            if (opcionesEleventa.CorteEnEleventa)
                throw new InvalidOperationException(CorteEnElPiloto.CorteDelDiaEnEleventa);
            var caja = await db.Cajas.FindAsync(cajaId) ?? throw new InvalidOperationException("La caja no existe.");
            var registro = new RegistroCorteDelDia(db);
            // El día que se ve en la caja: si pasó la medianoche con el corte abierto, ya no es hoy y no se cierra (tampoco el
            // nuevo, que nadie ha visto). Una caja de antes no lo manda: hoy.
            return await registro.CerrarAsync(caja.SucursalId, caja.Id, s.UsuarioId, s.Fecha ?? registro.Hoy, presencia.Foto());
        });

        // El recordatorio de cerrar el día (2026-10-17): a la hora de cierre de la tienda, mientras el día no se cierre, a quien
        // lo cierra. La venta lo pregunta cada minuto; sin nada que recordar, 204. La hora es la de los trabajos de fondo (las
        // pruebas la cambian).
        api.MapGet("/cajas/{cajaId:guid}/corte-del-dia/recordatorio", async (Guid cajaId, Guid usuarioId, PosDbContext db, TimeProvider reloj,
            OpcionesEleventa opcionesEleventa) =>
        {
            var caja = await db.Cajas.FindAsync(cajaId) ?? throw new InvalidOperationException("La caja no existe.");
            return await new RegistroCorteDelDia(db).RecordatorioAsync(caja.SucursalId, usuarioId, reloj.GetUtcNow().UtcDateTime, opcionesEleventa.CorteEnEleventa)
                is { } recordatorio ? Results.Ok(recordatorio) : Results.NoContent();
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
            OpcionesEleventa opcionesEleventa, TimeProvider reloj, ControlPrintEnElServicio controlPrint) =>
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
            // Las promociones (fase 3), con el día con que las puso la caja: una caja de antes (o sin copia) no las conoce, y se
            // cobra sin ellas, como las calculó.
            var delCobro = await PromocionesDelCobroAsync(db, catalogo, caja.SucursalId, s.DiaPromociones, reloj);
            // Un descuento que no entra (en monto, más que el renglón) se dice después de lo que cambió: con otra promoción, el
            // renglón vale otra cosa y primero la caja pone el ticket al día (409).
            var descuentosQueNoEntran = new List<ArgumentException>();
            var venta = await ArmarVentaAsync(db, catalogo, caja, turno.Id, s.CajeroId, s.VentaId ?? Ids.Nuevo(), s.Lineas,
                await ApiClientes.MayoreoDelCobroAsync(db, s.ClienteId, s.MayoreoDeCliente), delCobro.Venta, descuentosQueNoEntran: descuentosQueNoEntran);
            // La nota del ticket (F4 al cobrar): sale en el ticket y en sus reimpresiones. Una más larga de lo que cabe no se cobra.
            venta.PonerNota(s.Nota);
            // Si su ticket no sale al cobrar («Ofrecerlo» o «No imprimirlo»; lo fiado sale siempre), su primera impresión no
            // pedirá motivo (2026-10-22).
            venta.TicketSinImprimir = !ConfiguracionTicket.SaleAlCobrar((await ApiConfiguracion.ConfiguracionAsync(db, caja.SucursalId, ticket.Value)).AlCobrar,
                s.Cobros.Any(c => c.Forma == FormaPago.Credito && c.MontoCentavos > 0));
            // La caja escaneó contra su copia (entrega 5): si el precio o el mayoreo de algo del ticket cambió después, no se
            // cobra con precios que el cajero no vio; se le contestan los de ahora y la caja pone el ticket al día. Una caja de
            // antes no manda la versión: se cobra como siempre. Lo mismo si la promoción de una línea no es la que aplica aquí
            // (empezó, terminó o cambió después de su copia): con las promociones de aquí y su día, para que la caja ponga su
            // ticket igual.
            var deLaPromocion = delCobro.Venta is null ? [] : PromocionesDelCobro.Distintas(venta, s.Lineas);
            if ((s.VersionCatalogo is not null || deLaPromocion.Count > 0)
                && await PreciosCambiadosAsync(db, catalogo, caja.SucursalId, s.Lineas, s.VersionCatalogo, deLaPromocion, delCobro) is { } cambiados)
                return Results.Json(cambiados, statusCode: StatusCodes.Status409Conflict);
            if (descuentosQueNoEntran.Count > 0)
                throw descuentosQueNoEntran[0];
            (venta.ClienteId, venta.AutorizacionDescuentoId, venta.AutorizacionBajoCostoId, venta.AutorizacionCreditoId) =
                (s.ClienteId, s.AutorizacionDescuentoId, s.AutorizacionBajoCostoId, s.AutorizacionCreditoId);
            // Las impresiones van juntas en una línea y las copias del panel en otra (2026-10-29); su precio y costo salen de cada trabajo,
            // no de la caja.
            var impresiones = s.Impresiones is { Count: > 0 } ids ? await ImpresionesDelPos.EnLaVenta(db).ParaCobrarAsync(caja.SucursalId, ids) : [];
            // Las de la cola de Control Print (2026-10-25), en las mismas líneas: justo antes de cobrar se vuelven a leer allá (solo leer).
            // Si alguna ya no está por cobrar (se cobró en la caja principal), ya se cobró aquí o cambió su precio, no se cobra nada:
            // la caja la quita (o pone el precio de ahora) y el cajero cobra otra vez. Nunca se cobra dos veces. La revisión tiene un tope
            // (ControlPrintEnElServicio.PresupuestoRevision) y lo que tarde se le quita a la espera del folio de eleventa: por Control
            // Print, el cobro nunca pasa del plazo de la caja (2026-10-29).
            var antesDeControlPrint = System.Diagnostics.Stopwatch.GetTimestamp();
            List<TrabajoControlPrintParaCobrar> deControlPrint;
            try
            {
                deControlPrint = await controlPrint.ParaCobrarAsync(db, caja.SucursalId, s.ImpresionesControlPrint ?? []);
            }
            catch (ControlPrintCambio cambio)
            {
                return Results.Json(cambio.Cambio, statusCode: StatusCodes.Status409Conflict);
            }
            // Solo si el ticket traía tarjetas de Control Print: sin ellas no hubo revisión y eleventa tiene todo su tiempo.
            var enControlPrint = s.ImpresionesControlPrint is { Count: > 0 } ? System.Diagnostics.Stopwatch.GetElapsedTime(antesDeControlPrint) : TimeSpan.Zero;
            await FijarImpresionesAsync(db, catalogo, opcionesEleventa, venta, caja.SucursalId, impresiones, deControlPrint);
            // El piloto en la caja 2: la venta entra a la bandeja hacia eleventa junto con el cobro, y se intenta
            // mandar ya, para que el ticket salga con el folio de eleventa.
            await eleventa.EncolarAsync(db, venta);
            try
            {
                await new RegistroCaja(db).CobrarAsync(venta, s.Cobros, impresiones: impresiones, deControlPrint: deControlPrint);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                // La misma venta llegó dos veces a la vez (entrega 5, parte 2): la caja dejó de esperar este cobro (pasó su plazo) y
                // la mandó con la bandeja con el mismo Id, y entró primero esa. Queda una; se regresa su ticket.
                if (await YaCobradaAsync(db, venta.Id) is not { } yaCobrada)
                {
                    // Otro ticket de esta caja cobró al mismo tiempo el mismo trabajo de Control Print: este no se cobra.
                    if (ex is DbUpdateException { InnerException.Message: var detalle } && detalle.Contains("TrabajosControlPrintCobrados", StringComparison.Ordinal))
                        throw new InvalidOperationException("Una de las impresiones de Control Print se acaba de cobrar en otro ticket de esta caja: "
                            + "quítala del ticket (F2) y cobra otra vez.", ex);
                    throw;
                }
                return Results.Ok((await TicketAsync(db, yaCobrada, ticket.Value)) with { YaCobrada = true });
            }
            // Lo que esperó a eleventa va con el ticket, para el registro de la caja (el piloto alfa: juntar datos). Lo que tardó antes la
            // revisión en Control Print se le quita a esa espera y queda en el mismo renglón del registro del servicio.
            var conEleventa = await eleventa.MandarAlCobrarAsync(venta.Id, venta.FolioTexto, enControlPrint);
            var cobrado = await TicketAsync(db, venta, ticket.Value);
            return Results.Ok(conEleventa is null ? cobrado : cobrado with { EsperaEleventaMs = (int)Math.Round(conEleventa.Espero.TotalMilliseconds) });
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
            // Ya salió: lo que se pida después también es una reimpresión, y un «No imprimirlo» de antes ya no lo marca.
            venta.TicketSinImprimir = false;
            venta.TicketImpresiones++;
            await db.SaveChangesAsync();
            return await TicketAsync(db, venta, ticket.Value);
        });

        // La primera impresión del ticket de una venta que no salió al cobrar (2026-10-22): sin motivo, y a la bitácora como
        // primera impresión. Si ya salió (o se canceló o se devolvió algo), se dice con su código: la caja pide el motivo.
        api.MapPost("/ventas/{ventaId:guid}/primera-impresion", async (Guid ventaId, SolicitudPrimeraImpresion s, PosDbContext db, IOptions<OpcionesTicket> ticket) =>
        {
            try
            {
                var venta = await new RegistroCaja(db).PrimeraImpresionAsync(ventaId, s.UsuarioId, s.CajaId);
                return Results.Ok(await TicketAsync(db, venta, ticket.Value));
            }
            catch (TicketYaSalio ex)
            {
                return Results.BadRequest(new ErrorDto(ex.Message, SolicitudPrimeraImpresion.CodigoConMotivo));
            }
        });

        // El ticket no salió de la impresora de la caja y se dejó así («No imprimirlo»): su primera impresión no pedirá motivo,
        // si después de ese trabajo no salió otro (RegistroCaja.TicketSinImprimirAsync).
        api.MapPost("/ventas/{ventaId:guid}/sin-imprimir", async (Guid ventaId, SolicitudTicketSinImprimir s, PosDbContext db) =>
        {
            await new RegistroCaja(db).TicketSinImprimirAsync(ventaId, s);
            return Results.NoContent();
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

    /// <summary>
    /// Las impresiones del ticket en sus dos líneas (2026-10-29): las impresiones y las copias del panel de la impresora (las registradas
    /// en F2 y las de Control Print de su tipo «copias en el panel»), como las separa la tienda. En el piloto, como las cobra en eleventa:
    /// IMP y COP, con la cantidad = el monto entre su precio.
    /// </summary>
    private static async Task FijarImpresionesAsync(PosDbContext db, CatalogoEnMemoria catalogo, OpcionesEleventa opciones, Venta venta, Guid sucursalId,
        IReadOnlyList<TrabajoImpresion> impresiones, IReadOnlyList<TrabajoControlPrintParaCobrar> deControlPrint)
    {
        foreach (var copias in new[] { false, true })
        {
            var delModulo = impresiones.Where(t => (t.Tipo == TipoTrabajoImpresion.Copia) == copias).ToList();
            var deCp = deControlPrint.Where(t => t.EsCopia == copias).ToList();
            if (delModulo.Count + deCp.Count == 0)
                continue;
            var comoProducto = opciones.Escribir
                ? await ImpresionesComoProductoAsync(db, catalogo, sucursalId, copias ? opciones.ProductoCopias : opciones.ProductoImpresiones)
                : null;
            venta.FijarImpresiones(delModulo.Sum(t => t.PrecioCentavos) + deCp.Sum(t => t.PrecioCentavos),
                delModulo.Sum(t => t.CostoCentavos) + deCp.Sum(t => t.CostoCentavos),
                LineaDeImpresiones.Descripcion(deCp.Select(t => t.Id), delModulo.Select(t => t.Pc), conReferencias: opciones.Escribir, copias), comoProducto,
                copias);
        }
    }

    /// <summary>
    /// En el piloto (2026-10-25): el producto con que la tienda cobra las impresiones en eleventa (IMP; las copias, COP), como está en el
    /// catálogo, con su precio de menudeo. Vacío si no está (la línea va como siempre, una pieza por el monto, y a eleventa como artículo
    /// común).
    /// </summary>
    private static async Task<ImpresionesComoProducto?> ImpresionesComoProductoAsync(PosDbContext db, CatalogoEnMemoria catalogo, Guid sucursalId, string codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo) || await catalogo.PorCodigoAsync(db, codigo.Trim(), sucursalId) is not { } producto)
            return null;
        var presentacion = producto.PresentacionModelo();
        return CalculoPrecio.Calcular(producto.PreciosModelo(), sucursalId, presentacion, Cantidad.Piezas(1)) is { } precio
            ? new ImpresionesComoProducto(producto.VarianteId, presentacion.Id, producto.Unidad, precio.PrecioPorPresentacion(presentacion), producto.Impuesto)
            : null;
    }

    /// <summary>La venta con ese Id, si ya se guardó (con sus líneas y cobros, para su ticket).</summary>
    internal static async Task<Venta?> YaCobradaAsync(PosDbContext db, Guid ventaId)
    {
        db.ChangeTracker.Clear();
        return await db.Ventas.Include(v => v.Lineas).Include(v => v.Cobros).SingleOrDefaultAsync(v => v.Id == ventaId);
    }

    /// <summary>
    /// Cuánto puede ir el día de la caja distinto del de aquí, cerca de la medianoche de aquí (fase 3): un reloj unos minutos
    /// atrás o adelante. Fuera de eso, cuenta el día de aquí.
    /// </summary>
    public static readonly TimeSpan CercaDeMedianoche = TimeSpan.FromMinutes(60);

    /// <summary>Las promociones con que se cobra y el día con que se aplican (vacías sin el día de la caja).</summary>
    internal sealed record PromocionesDeUnCobro(PromocionesDeLaVenta? Venta, List<PromocionDto>? Lista, DateOnly? Dia);

    /// <summary>
    /// Las promociones con que se cobra en la sucursal de la caja (fase 3), con el día con que las aplicó la caja: el de hoy
    /// aquí o, cerca de la medianoche de aquí (<see cref="CercaDeMedianoche"/>), el de ayer (un reloj unos minutos atrás) o el
    /// de mañana (uno adelantado). Si no, con el de hoy: a mediodía, una caja con el reloj un día atrás no cobra la promoción que
    /// terminó ayer. Sin el día (una caja de antes, o sin copia todavía), ninguna: se cobra como lo calculó la caja.
    /// </summary>
    /// <param name="reloj">El de los trabajos de fondo del servicio (las pruebas lo cambian), como el de la copia.</param>
    internal static async Task<PromocionesDeUnCobro> PromocionesDelCobroAsync(
        PosDbContext db, CatalogoEnMemoria catalogo, Guid sucursalId, DateOnly? diaDeLaCaja, TimeProvider reloj, CancellationToken ct = default)
    {
        if (diaDeLaCaja is not { } suyo)
            return new PromocionesDeUnCobro(null, null, null);
        var dia = DiaDelCobro(suyo, reloj.GetUtcNow().UtcDateTime);
        var hoy = CopiaDeLaCaja.Hoy(reloj);
        var lista = await catalogo.PromocionesAsync(db, sucursalId, dia < hoy ? dia : hoy, ct);
        var porProducto = new PromocionesPorProducto(lista);
        return new PromocionesDeUnCobro(new PromocionesDeLaVenta(porProducto.De, () => dia), lista, dia);
    }

    /// <summary>El día con que se cobran las promociones: el de la caja si es el de aquí o, cerca de la medianoche de aquí, el de al lado.</summary>
    public static DateOnly DiaDelCobro(DateOnly diaDeLaCaja, DateTime ahoraUtc)
    {
        var aqui = HoraMexico.ALocal(ahoraUtc);
        var hoy = DateOnly.FromDateTime(aqui);
        var desdeMedianoche = aqui - aqui.Date;
        return diaDeLaCaja == hoy
               || diaDeLaCaja == hoy.AddDays(-1) && desdeMedianoche <= CercaDeMedianoche
               || diaDeLaCaja == hoy.AddDays(1) && TimeSpan.FromDays(1) - desdeMedianoche <= CercaDeMedianoche
            ? diaDeLaCaja
            : hoy;
    }

    /// <summary>
    /// Arma la venta con los precios del catálogo: la caja solo dice qué y cuánto. El precio que manda la caja es el que se
    /// cambió con Ctrl+E (solo de esas líneas lo manda): queda como precio cambiado aunque sea el de la lista, como en su
    /// pantalla (sin mayoreo ni promoción). Con <paramref name="comoSeCobro"/> (lo que sube sin conexión, con el precio de cada
    /// línea), solo uno distinto, o el de una línea que aquí llevaría promoción, es precio cambiado; y la promoción que dice cada
    /// línea, con lo que se ahorró: ya se cobró así. Con <paramref name="mayoreoDeCliente"/> (el cliente del ticket paga mayoreo
    /// siempre), los mismos precios que calculó la caja. Con <paramref name="promociones"/> (fase 3), las que aplican aquí.
    /// </summary>
    /// <param name="descuentosQueNoEntran">
    /// Si viene, el descuento que no entra en su línea (en monto, más que ella) se anota aquí en lugar de fallar: el cobro primero
    /// revisa si cambió el precio o la promoción de algo (con otra promoción, la línea vale otra cosa).
    /// </param>
    internal static async Task<Venta> ArmarVentaAsync(
        PosDbContext db, CatalogoEnMemoria catalogo, Caja caja, Guid turnoId, Guid cajeroId, Guid ventaId, IReadOnlyList<LineaSolicitada> lineas,
        bool mayoreoDeCliente = false, PromocionesDeLaVenta? promociones = null, bool comoSeCobro = false,
        List<ArgumentException>? descuentosQueNoEntran = null)
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
            Promociones = promociones,
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
            if (comoSeCobro && l.PromocionId is { } promocion && l.AhorroPromocionCentavos is > 0 and var ahorro)
                venta.FijarPromocionCobrada(agregada, l.PrecioCentavos ?? agregada.PrecioCentavos, promocion, l.Promocion ?? "Promoción", ahorro);
            // Un precio cambiado en la caja no lleva promoción, aunque sea el de la lista: si no, la caja lo cobraría sin ella y
            // aquí con ella, y el cobro diría que cambió la promoción una y otra vez.
            else if (l.PrecioCentavos is { } precio && (!comoSeCobro || precio != agregada.PrecioCentavos || agregada.TienePromocion))
                venta.CambiarPrecio(agregada, precio, l.AutorizacionId);
            try
            {
                venta.AplicarDescuentoSolicitado(agregada, l.DescuentoCentesimas, l.DescuentoCentavos, l.MotivoDescuento);
            }
            catch (ArgumentException ex) when (descuentosQueNoEntran is not null)
            {
                descuentosQueNoEntran.Add(ex);
            }
        }
        return venta;
    }

    /// <summary>
    /// Los productos del ticket cuyo precio o mayoreo cambió después de la versión con que la caja puso sus precios, como
    /// están ahora; nada si no cambió ninguno. Se revisa después de armar la venta: lo que cambie mientras tanto ya no la toca.
    /// Una versión más nueva que la del servicio quiere decir que su base cambió entera (la prueba empezó de cero): se
    /// revisa todo.
    /// </summary>
    /// <param name="deLaPromocion">Los de una promoción que no es la que aplicó la caja (fase 3): también cambiaron.</param>
    /// <param name="promociones">
    /// Con las que se cobra aquí y su día: van con la respuesta, para que la caja ponga su ticket igual (con ese día, si su reloj
    /// trae otro).
    /// </param>
    private static async Task<PreciosCambiadosDto?> PreciosCambiadosAsync(
        PosDbContext db, CatalogoEnMemoria catalogo, Guid sucursalId, IReadOnlyList<LineaSolicitada> lineas, long? version,
        IReadOnlyCollection<Guid> deLaPromocion, PromocionesDeUnCobro promociones)
    {
        var actual = await catalogo.VersionAsync(db);
        var cambiados = new HashSet<Guid>(deLaPromocion);
        if (version is { } delTicket)
        {
            long Desde(LineaSolicitada l) => (l.VersionCatalogo ?? delTicket) is var v && v <= actual ? v : -1;
            cambiados.UnionWith(await PreciosDeLaCopia.CambiadosAsync(db, sucursalId, lineas.Select(l => l with { VersionCatalogo = Desde(l) }).ToList(),
                delTicket, actual));
        }
        if (cambiados.Count == 0)
            return null;
        var productos = new List<ProductoVentaDto>();
        foreach (var varianteId in cambiados)
        {
            if (await catalogo.PorVarianteAsync(db, varianteId, sucursalId) is { } producto)
                productos.Add(producto);
        }
        return new PreciosCambiadosDto(PreciosDeLaCopia.Mensaje(productos), productos, actual, Promociones: promociones.Lista,
            DiaPromociones: promociones.Dia);
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
        // Las entradas y salidas que se cancelaron (2026-10-22): ya no cuentan, y el corte lo dice.
        var (entradasCanceladas, salidasCanceladas) = (await RegistroCaja.CanceladasAsync(db, [c.TurnoId])).GetValueOrDefault(c.TurnoId);
        return new CorteDto(c.Tipo, HoraMexico.ALocal(c.FechaUtc), nombres[turno!.CajeroId], nombres.GetValueOrDefault(c.RealizadoPorId, "—"), c.Ventas,
            c.FondoCentavos, c.EfectivoVentasCentavos, c.EntradasCentavos, c.SalidasCentavos, c.TarjetaCentavos, c.TransferenciaCentavos, c.EsperadoCentavos,
            c.ContadoCentavos, c.DiferenciaCentavos, c.DevolucionesCentavos, c.Cancelaciones, c.EnContingencia, c.RedondeoCentavos, c.CreditoCentavos,
            c.Abonos, c.AbonosEfectivoCentavos, c.AbonosTarjetaCentavos, c.AbonosTransferenciaCentavos, c.SaldoDevueltoCentavos,
            // Al cerrar el turno, la caja dice si la diferencia pasó del umbral y se avisa por Telegram.
            c is { Tipo: TipoCorte.Z, DiferenciaCentavos: { } diferencia } ? AlertasTelegram.UmbralSiAvisa(db, turno.SucursalId, diferencia) : null,
            entradasCanceladas, salidasCanceladas);
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
            await new RegistroCredito(db).TicketAsync(v.Id)) with
        {
            FolioEleventa = folioEleventa, AlCobrar = configuracion.AlCobrar, Impresiones = v.TicketImpresiones,
        };
    }
}
