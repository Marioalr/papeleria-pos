using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Impresiones.Servicio;

/// <summary>
/// Impresiones (entrega 5): lo que mandan los agentes, lo que falta cobrar, las no vendidas, cotizar un PDF, las copias
/// del panel y la impresora de la sucursal (niveles, contador y calibración).
/// </summary>
public static class ApiImpresiones
{
    /// <summary>
    /// Las rutas del módulo, sobre el grupo que le da el servicio:
    /// <c>app.MapGroup("/api").ConErrores().SoloSuCaja().MapearApiImpresiones()</c>. Lo que vale para toda la API (los
    /// errores para el cajero, que una caja solo opere la suya) lo pone el servicio, no el módulo. Lo que es del POS (los
    /// permisos de cada usuario, los nombres, el PIN) sale de su base.
    /// </summary>
    public static RouteGroupBuilder MapearApiImpresiones(this RouteGroupBuilder api)
    {
        // El agente de la caja manda cada trabajo impreso; el servicio le pone costo y precio y lo suma al lote que se
        // verifica con el contador de la impresora.
        api.MapPost("/impresiones", async (TrabajoMedidoDto t, PosDbContext db, VigilanciaImpresora vigilancia) =>
        {
            var nuevo = await db.Set<TrabajoImpresion>().FindAsync(t.Id) is null;
            var trabajo = await ImpresionesDelPos.Registro(db).RegistrarAsync(t);
            if (nuevo)
                await vigilancia.TrabajoRegistradoAsync(trabajo);
            return new { trabajo.Id, trabajo.PrecioCentavos };
        });

        // Lo que falta cobrar en la sucursal, de todas las PC. El costo solo va a quien tiene «Ver costos, utilidad y margen».
        api.MapGet("/sucursales/{sucursalId:guid}/impresiones/por-cobrar", async (Guid sucursalId, Guid usuarioId, PosDbContext db) =>
        {
            var registro = ImpresionesDelPos.Registro(db);
            var trabajos = await registro.PorCobrarAsync(sucursalId);
            var costos = await registro.CostosAsync(sucursalId);
            var veCosto = await VeCostoAsync(db, usuarioId);
            var nombres = await NombresAsync(db, trabajos);
            return trabajos.Select(t => PorCobrar(t, nombres, veCosto, costos)).ToList();
        });

        api.MapPost("/impresiones/{id:guid}/no-vendida", async (Guid id, SolicitudNoVendida s, PosDbContext db) =>
        {
            await ImpresionesDelPos.Registro(db).NoVendidaAsync(id, s);
            return Results.NoContent();
        });

        // Con un atasco o una cancelación que contó la impresora: se cobra solo lo que salió.
        api.MapPost("/impresiones/{id:guid}/solo-lo-que-salio", async (Guid id, SolicitudSoloLoQueSalio s, PosDbContext db) =>
        {
            var registro = ImpresionesDelPos.Registro(db);
            var trabajo = await registro.SoloLoQueSalioAsync(id);
            return PorCobrar(trabajo, await NombresAsync(db, [trabajo]), await VeCostoAsync(db, s.UsuarioId), await registro.CostosAsync(trabajo.SucursalId));
        });

        // El desglose de la línea "Impresiones": todo lo que se imprimió en un día y cómo se cobró, para quien tiene «Consultar
        // la bitácora» (con los usuarios de antes, el dueño y el encargado, como con los roles); el costo, con «Ver costos,
        // utilidad y margen».
        api.MapGet("/sucursales/{sucursalId:guid}/impresiones", async (Guid sucursalId, DateOnly fecha, Guid usuarioId, PosDbContext db) =>
        {
            var control = new ServicioControl(db);
            if (!await control.PuedeAsync(usuarioId, AccionControlada.ConsultarBitacora))
                throw new PermisoRequerido(AccionControlada.ConsultarBitacora, NivelPermiso.NoPermitido,
                    $"El desglose de las impresiones lo ve quien tiene el permiso «{Permisos.Nombre(AccionControlada.ConsultarBitacora)}»: "
                    + $"{Permisos.Nombres(await control.QuienesPuedenAsync(sucursalId, AccionControlada.ConsultarBitacora))}.");
            var veCosto = await VeCostoAsync(db, usuarioId);
            var trabajos = await ImpresionesDelPos.Registro(db).DelDiaAsync(sucursalId, fecha);
            var nombres = await NombresAsync(db, trabajos);
            return trabajos.Select(t => new ImpresionDelDiaDto(t.Id, t.Tipo, t.Documento, t.Pc, HoraMexico.ALocal(t.ImpresoUtc), t.Caras,
                t.CarasContadas, t.Color, t.PrecioCentavos, t.Cobro, t.MotivoNoVendida, t.AtendioId is { } a ? nombres.GetValueOrDefault(a) : null,
                veCosto ? RegistroImpresiones.Costo(t) : null)).ToList();
        });

        // ── Cotizar y copias ─────────────────────────────────────────────────

        api.MapGet("/sucursales/{sucursalId:guid}/impresiones/opciones", (Guid sucursalId, PosDbContext db) =>
            ImpresionesDelPos.Registro(db).OpcionesAsync(sucursalId));

        // La caja midió el PDF; aquí se le pone precio con la configuración de la sucursal (con el costo, para quien lo ve).
        api.MapPost("/impresiones/cotizar", async (SolicitudCotizacion s, PosDbContext db) =>
            await ImpresionesDelPos.Registro(db).CotizarAsync(s, s.UsuarioId is { } u && await VeCostoAsync(db, u)));

        api.MapPost("/impresiones/copias/precio", async (SolicitudPrecioCopias s, PosDbContext db) =>
        {
            var (costo, descripcion) = await ImpresionesDelPos.Registro(db).CalcularCopiasAsync(s.SucursalId, s.Cantidad, s.Color, s.Tamano, s.Duplex);
            return new PrecioCopiasDto((long)Math.Round(costo.Price * 100, MidpointRounding.AwayFromZero), descripcion);
        });

        api.MapPost("/impresiones/copias", async (SolicitudCopias s, PosDbContext db) =>
        {
            var registro = ImpresionesDelPos.Registro(db);
            var trabajo = await registro.RegistrarCopiasAsync(s);
            return PorCobrar(trabajo, await NombresAsync(db, [trabajo]), false, await registro.CostosAsync(trabajo.SucursalId));
        });

        // ── La impresora ─────────────────────────────────────────────────────

        api.MapGet("/sucursales/{sucursalId:guid}/impresora", (Guid sucursalId, PosDbContext db, VigilanciaImpresora vigilancia) =>
            ImpresionesDelPos.Impresora(db).EstadoAsync(sucursalId, vigilancia.Error(sucursalId)));

        // Cambia cómo se lee la impresora quien tiene «Configurar la impresora», con su usuario y su PIN.
        api.MapPut("/sucursales/{sucursalId:guid}/impresora", async (Guid sucursalId, SolicitudConfiguracionImpresora s, PosDbContext db,
            VigilanciaImpresora vigilancia) =>
        {
            await new ServicioControl(db).IdentificarAsync(s.Usuario, s.Pin, sucursalId, AccionControlada.ConfigurarImpresora);
            var configuracion = await ImpresionesDelPos.Impresora(db).GuardarConfiguracionAsync(sucursalId, s.Configuracion);
            var error = configuracion.ImpresoraHost is null ? null : await vigilancia.LeerAhoraAsync(sucursalId);
            return await ImpresionesDelPos.Impresora(db).EstadoAsync(sucursalId, error);
        });

        api.MapPost("/sucursales/{sucursalId:guid}/impresora/leer", async (Guid sucursalId, PosDbContext db, VigilanciaImpresora vigilancia) =>
        {
            var error = await vigilancia.LeerAhoraAsync(sucursalId);
            return await ImpresionesDelPos.Impresora(db).EstadoAsync(sucursalId, error);
        });

        api.MapGet("/sucursales/{sucursalId:guid}/impresora/calibracion", (Guid sucursalId, PosDbContext db) =>
            ImpresionesDelPos.Impresora(db).CalibracionAsync(sucursalId));

        // Aplica la calibración quien tiene «Configurar la impresora», con su usuario y su PIN.
        api.MapPost("/sucursales/{sucursalId:guid}/impresora/calibracion", async (Guid sucursalId, SolicitudCredenciales s, PosDbContext db) =>
        {
            await new ServicioControl(db).IdentificarAsync(s.Usuario, s.Pin, sucursalId, AccionControlada.ConfigurarImpresora);
            return await ImpresionesDelPos.Impresora(db).AplicarCalibracionAsync(sucursalId);
        });

        return api;
    }

    /// <summary>El costo, la ganancia y el margen de las impresiones: solo con «Ver costos, utilidad y margen» (al migrar, el dueño).</summary>
    private static Task<bool> VeCostoAsync(PosDbContext db, Guid usuarioId) =>
        new ServicioControl(db).PuedeAsync(usuarioId, AccionControlada.VerUtilidad);

    private static async Task<Dictionary<Guid, string>> NombresAsync(PosDbContext db, IEnumerable<TrabajoImpresion> trabajos)
    {
        var ids = trabajos.Where(t => t.AtendioId != null).Select(t => t.AtendioId!.Value).Distinct().ToList();
        return await db.Usuarios.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nombre);
    }

    private static ImpresionPorCobrarDto PorCobrar(TrabajoImpresion t, Dictionary<Guid, string> nombres, bool veCosto, CostSettings costos) =>
        new(t.Id, t.Documento, t.Pc, HoraMexico.ALocal(t.ImpresoUtc), t.Paginas, t.Caras, t.Color, t.Tamano, t.PrecioCentavos,
            t.AtendioId is { } a ? nombres.GetValueOrDefault(a) : null, t.Miniatura is { } m ? Convert.ToBase64String(m) : null, t.Nota,
            veCosto ? RegistroImpresiones.Costo(t) : null, t.Tipo, t.CarasContadas, RegistroImpresiones.PrecioSoloLoQueSalio(t, costos),
            t.PrecioCompletoCentavos);
}
