using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Contingencia (entrega 3, parte 2): lo que una caja guarda para vender sin la caja principal, y la bandeja con lo que
/// hizo mientras tanto. Todo con la llave de la caja y solo para su caja.
/// </summary>
public static class ApiContingencia
{
    public static void MapearApiContingencia(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();
        var bandeja = app.MapGroup("/api/bandeja").ConErrores().SoloSuCaja().AddEndpointFilter(AvisarSiNoSubeAsync);

        // La copia local: catálogo con existencias, usuarios (PIN como hash), sus permisos, ticket, la configuración de la tienda y
        // el último folio de la caja.
        api.MapGet("/cajas/{cajaId:guid}/paquete", async (Guid cajaId, PosDbContext db, CatalogoEnMemoria catalogo, IOptions<OpcionesTicket> opciones) =>
        {
            var caja = await db.Cajas.FindAsync(cajaId) ?? throw new InvalidOperationException("La caja no existe.");
            var sucursal = await db.Sucursales.FindAsync(caja.SucursalId);
            var (version, productos, codigos, marcas) = await catalogo.TodosAsync(db, caja.SucursalId);
            // Los dados de baja también, sin el hash de su PIN: sus letras no se le dan a otro, y no entran.
            var usuarios = (await db.Usuarios.AsNoTracking()
                    .Where(u => u.SucursalId == null || u.SucursalId == caja.SucursalId)
                    .Select(u => new UsuarioLocalDto(u.Id, u.NombreUsuario, u.Nombre, u.Rol, u.PinHash, u.Activo))
                    .ToListAsync())
                .Select(u => u.Activo ? u : u with { PinHash = "" })
                .ToList();
            // Los permisos de cada activo y los de defecto (entrega 1). Las reglas por rol, de antes, solo para una caja que
            // todavía no se actualiza.
            var control = new ServicioControl(db);
            var permisos = (await control.UsuariosQueEntranAsync(caja.SucursalId))
                .Select(u => new PermisosLocalesDto(u.Id, u.Permisos.Lista()))
                .Append(new PermisosLocalesDto(null, (await control.DefectoAsync()).Lista()))
                .ToList();
            var reglas = PermisosDeRoles.Efectivas(await db.ReglasPermiso.AsNoTracking()
                .Where(r => r.SucursalId == null || r.SucursalId == caja.SucursalId).ToListAsync(), caja.SucursalId);
            var ticket = await ApiConfiguracion.ConfiguracionAsync(db, caja.SucursalId, opciones.Value);
            // La configuración de la tienda (entrega 7): sin conexión, el nombre en la entrada, las fichas de la espera y el
            // fondo sugerido siguen como en línea.
            var configuracion = await new RegistroConfiguracion(db).LeerAsync(caja.SucursalId);
            var clientes = await ApiClientes.ParaLaCajaAsync(db, caja.SucursalId);
            // La búsqueda de la caja va contra esta copia (entrega 5, parte 3) y da lo mismo que la de aquí: lleva las marcas con
            // que se leen los nombres (las familias), los sinónimos de la tienda («boligrafo» encuentra los lapiceros), lo más
            // vendido y lo que esta caja eligió antes con cada texto.
            var sinonimos = await new RegistroSinonimos(db).ListarAsync();
            var enLaCopia = productos.Select(p => p.VarianteId).ToHashSet();
            var vendidos = (await catalogo.VecesVendidoAsync(db)).Where(x => x.Value > 0 && enLaCopia.Contains(x.Key))
                .Select(x => new VecesVendidoDto(x.Key, x.Value)).ToList();
            var elegidos = (await catalogo.ElegidosAsync(db, caja.Id)).Where(e => enLaCopia.Contains(e.VarianteId)).ToList();
            return new PaqueteContingenciaDto(version, new CajaDto(caja.Id, caja.Nombre, caja.Serie, caja.SucursalId, sucursal!.Nombre, caja.Equipo),
                productos, codigos, usuarios, reglas, ticket.RenglonesEncabezado, ticket.RenglonesPie, ApiConfiguracion.Logo(ticket),
                await new RegistroCaja(db).UltimoFolioAsync(caja.SucursalId, caja.Serie), configuracion.LimiteEfectivoCentavos,
                ApiClientes.ParaCajasDeAntes(clientes), clientes, marcas, sinonimos, permisos, configuracion, vendidos, elegidos);
        });

        // Lo que lleva el turno, sin guardar un corte: la base de la cuenta que la caja lleva por su lado.
        api.MapGet("/turnos/{turnoId:guid}/resumen", async (Guid turnoId, PosDbContext db) =>
            await ApiCaja.CorteDtoAsync(db, await new RegistroCaja(db).ResumenAsync(turnoId)));

        api.MapPost("/cajas/{cajaId:guid}/folio-usado", async (Guid cajaId, FolioUsadoDto f, PosDbContext db) =>
        {
            await new RegistroCaja(db).FolioUsadoAsync(cajaId, f.Folio);
            return Results.NoContent();
        });

        // ── La bandeja: sube en el orden en que pasó ─────────────────────────────

        bandeja.MapPost("/turnos", async (TurnoContingenciaDto t, PosDbContext db) =>
            await ApiCaja.TurnoDtoAsync(db, await new RegistroCaja(db).TurnoDeContingenciaAsync(t)));

        bandeja.MapPost("/ventas", async (VentaContingenciaDto v, PosDbContext db, CatalogoEnMemoria catalogo, EnvioVentasEleventa eleventa) =>
        {
            if (await db.Ventas.AnyAsync(x => x.Id == v.Id))
                return Results.NoContent();
            var caja = await db.Cajas.FindAsync(v.CajaId) ?? throw new InvalidOperationException("La caja no existe.");
            var lineas = v.Lineas.Select(l => new LineaSolicitada(l.VarianteId, l.PresentacionId, l.CantidadMilesimas, l.Descripcion, l.PrecioCentavos,
                DescuentoCentesimas: l.DescuentoCentesimas, DescuentoCentavos: l.DescuentoCentavos, MotivoDescuento: l.MotivoDescuento)).ToList();
            // El precio que se cobró manda; el mayoreo del cliente (el que usó la caja) solo sirve para que el ticket diga el ahorro.
            var venta = await ApiCaja.ArmarVentaAsync(db, catalogo, caja, v.TurnoId, v.CajeroId, v.Id, lineas, v.MayoreoDeCliente == true);
            venta.ClienteId = v.ClienteId;
            // También va a eleventa (el piloto); su ticket ya salió con el folio de la caja, así que se manda en la vuelta.
            // Aunque el piloto sea solo en efectivo, no se rechaza: ya se cobró. Si no fue en efectivo, queda por revisar.
            eleventa.Encolar(db, venta.Id);
            // Si fió: con lo que sabía la caja (una caja de antes no fía sin conexión; si llegara, se revisa con el saldo real).
            await new RegistroCaja(db).CobroDeContingenciaAsync(venta, v.Cobros, v.Folio, v.PagadaUtc, redondear: v.RedondeoCentavos is not null,
                fiado: v.SaldoConocidoCentavos is { } conocido ? new FiadoSinConexion(conocido, v.ExcesoAutorizadoCentavos, v.VenceCredito) : null);
            eleventa.Despertar();
            return Results.NoContent();
        });

        // Un abono sin conexión: se guarda siempre (ya se cobró, también en el piloto con eleventa); lo que no cuadre queda
        // por revisar.
        bandeja.MapPost("/abonos", async (AbonoContingenciaDto a, PosDbContext db) =>
        {
            await new RegistroCredito(db).AbonoDeContingenciaAsync(a);
            return Results.NoContent();
        });

        bandeja.MapPost("/turnos/{turnoId:guid}/movimientos", async (Guid turnoId, MovimientoContingenciaDto m, PosDbContext db, EnvioVentasEleventa eleventa) =>
        {
            // También va a eleventa (el piloto), como la que se hace en línea.
            await new RegistroCaja(db).MovimientoDeContingenciaAsync(turnoId, m, aEleventa: eleventa.Activo);
            eleventa.Despertar();
            return Results.NoContent();
        });

        bandeja.MapPost("/bitacora", async (BitacoraContingenciaDto b, PosDbContext db) =>
        {
            await new ServicioControl(db).RegistrarDeContingenciaAsync(b);
            return Results.NoContent();
        });

        bandeja.MapPost("/turnos/{turnoId:guid}/corte-z", async (Guid turnoId, CorteContingenciaDto c, PosDbContext db, ServicioRespaldos respaldos, EnvioVentasEleventa eleventa) =>
        {
            // En el piloto con eleventa la caja ya no hace el corte sin conexión (se hace en eleventa). Si llega uno (una caja
            // de antes, o de antes del piloto), ya pasó: se guarda, porque lo que la caja hizo después va en otro turno y no
            // entraría. El cierre del turno de eleventa que encola, el puente no lo manda: queda por revisar (CorteEnElPiloto).
            var corte = await ApiCaja.CorteDtoAsync(db, await new RegistroCaja(db).CorteZDeContingenciaAsync(turnoId, c));
            eleventa.Despertar();
            _ = Task.Run(() => respaldos.Hacer($"z-{corte.FechaLocal:HHmm}"));
            return corte;
        });
    }

    /// <summary>
    /// Si algo de la bandeja no se puede guardar, el dueño se entera por Telegram (una vez por cada cosa, aunque la caja
    /// lo reintente). Se avisa con otra conexión a la base: la de la operación quedó a medias.
    /// </summary>
    private static async ValueTask<object?> AvisarSiNoSubeAsync(EndpointFilterInvocationContext contexto, EndpointFilterDelegate siguiente)
    {
        try
        {
            return await siguiente(contexto);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            var id = contexto.Arguments.Select(a => a?.GetType().GetProperty("Id")?.GetValue(a)).OfType<Guid>().FirstOrDefault();
            using var alcance = contexto.HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            if (contexto.HttpContext.Items.TryGetValue(SeguridadCajas.CajaDeLaLlave, out var valor) && valor is Guid cajaId && await db.Cajas.FindAsync(cajaId) is { } caja)
            {
                AlertasTelegram.Avisar(db, caja.SucursalId, TipoAlerta.CajaSinConexion,
                    $"No se pudo subir · {caja.Nombre}\nLo que hizo sin conexión no entró: {ex.Message}\nQueda en la caja para revisarlo y se reintenta solo.",
                    $"bandeja:{id}");
                await db.SaveChangesAsync();
            }
            throw;
        }
    }
}
