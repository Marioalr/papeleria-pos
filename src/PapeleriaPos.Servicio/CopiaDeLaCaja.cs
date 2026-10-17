using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Servicio;

/// <summary>
/// La copia que cada caja guarda para escanear y vender sin la red: completa (<see cref="PaqueteAsync"/>, la de siempre) o
/// solo lo que cambió desde su versión (<see cref="CambiosAsync"/>, entrega 5, parte 2). Las dos se arman con las mismas
/// piezas, para que la copia que queda en la caja sea la misma por los dos caminos.
/// </summary>
public static class CopiaDeLaCaja
{
    /// <summary>
    /// Desde cuántos productos cambiados conviene la copia completa: una cuarta parte del catálogo (y nunca menos de 500).
    /// Pasa cuando la caja estuvo apagada mucho tiempo mientras se cambiaban precios en la caja principal.
    /// </summary>
    public static int MaximoDeCambios(int productos) => Math.Max(500, productos / 4);

    /// <summary>
    /// La copia completa: catálogo con existencias, usuarios (el PIN como hash) con sus permisos, ticket, la configuración de
    /// la tienda, los clientes y el último folio de la caja; con la base, el formato y las versiones desde las que la caja
    /// pide después lo que cambió. Y lo que necesita la búsqueda de la caja para dar lo mismo que la de aquí (entrega 5, parte
    /// 3): todos los códigos, las marcas con que se leen los nombres, los sinónimos, lo más vendido y lo que esta caja eligió
    /// antes con cada texto, con sus versiones. Y los exhibidores y las temporadas, con lo acomodado en ellos (entrega 13).
    /// </summary>
    public static async Task<PaqueteContingenciaDto> PaqueteAsync(
        PosDbContext db, CatalogoEnMemoria catalogo, VersionesDeLaCopia versiones, Guid cajaId, OpcionesTicket opciones, CancellationToken ct = default)
    {
        var caja = await db.Cajas.FindAsync([cajaId], ct) ?? throw new InvalidOperationException("La caja no existe.");
        // La versión de las existencias antes que ellas: lo que cambie mientras tanto ya viene, y la caja lo vuelve a pedir.
        var versionExistencias = (await VersionesDeLaCopia.LeerAsync(db, versiones.Base, ct)).Existencias;
        var (version, productos, codigos, marcas) = await catalogo.TodosAsync(db, await ExistenciasAsync(db, caja.SucursalId, null, ct), ct);
        var (usuarios, permisos, reglas) = await UsuariosAsync(db, caja.SucursalId, ct);
        var ticket = await TicketAsync(db, caja.SucursalId, opciones, ct);
        // La configuración de la tienda (entrega 7): sin conexión, el nombre en la entrada, las fichas de la espera y el
        // fondo sugerido siguen como en línea.
        var configuracion = await new RegistroConfiguracion(db).LeerAsync(caja.SucursalId, ct);
        var clientes = await ApiClientes.ParaLaCajaAsync(db, caja.SucursalId);
        var (versionVendidos, vendidos) = await catalogo.VendidosParaLaCopiaAsync(db, ct);
        var (versionElegidos, elegidos) = await catalogo.ElegidosAsync(db, caja.Id, ct: ct);
        return new PaqueteContingenciaDto(version, await CajaAsync(db, caja, ct), productos, codigos, usuarios, reglas, ticket.Encabezado, ticket.Pie,
            ticket.Logo, await new RegistroCaja(db).UltimoFolioAsync(caja.SucursalId, caja.Serie, ct), configuracion.LimiteEfectivoCentavos,
            ApiClientes.ParaCajasDeAntes(clientes), clientes, marcas, await new RegistroSinonimos(db).ListarAsync(ct), permisos,
            configuracion, versiones.Base, VersionesDeLaCopia.Formato, versionExistencias, vendidos, elegidos, versionVendidos, versionElegidos,
            // Los exhibidores y las temporadas (entrega 13): siempre, aunque no haya ninguno (el formato 3 los trae).
            await RegistroMostrador.ListarAsync(db, ct));
    }

    /// <summary>
    /// Lo que cambió desde la versión de la caja (<paramref name="desde"/>) y desde su versión de existencias, todo leído de un
    /// mismo momento de la base. Si no se puede dar desde ahí (otra base, otro formato, una versión más nueva que la de la
    /// base o demasiados cambios), lo dice: la caja baja la copia completa. Con lo que necesita su búsqueda (entrega 5, parte
    /// 3): si cambió algún producto, las marcas; lo más vendido si se calculó otra vez desde <paramref name="vendidos"/>; y
    /// lo que esta caja eligió después de <paramref name="elegidos"/>.
    /// </summary>
    /// <param name="vendidos">La versión de lo más vendido que trae la copia de la caja.</param>
    /// <param name="elegidos">Hasta dónde trae lo elegido la copia de la caja.</param>
    public static async Task<CambiosCopiaDto> CambiosAsync(
        PosDbContext db, CatalogoEnMemoria catalogo, VersionesDeLaCopia versiones, Guid cajaId, long desde, long existencias, Guid base_, int formato,
        OpcionesTicket opciones, CancellationToken ct = default, long vendidos = 0, long elegidos = 0)
    {
        if (base_ != versiones.Base)
            return CambiosCopiaDto.Completo("La copia es de otra base (la caja principal se reinició o empezó de cero).");
        if (formato != VersionesDeLaCopia.Formato)
            return CambiosCopiaDto.Completo("La copia tiene otro formato (la caja o la caja principal se actualizó).");
        return await EnUnMismoMomentoAsync(db, async () =>
        {
            var actuales = await VersionesDeLaCopia.LeerAsync(db, base_, ct);
            var caja = await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cajaId, ct) ?? throw new InvalidOperationException("La caja no existe.");
            if (desde > actuales.Catalogo || existencias > actuales.Existencias)
                return CambiosCopiaDto.Completo("La copia es más nueva que la base de la caja principal.");
            var quitados = await db.Borrados.AsNoTracking().Where(b => b.Version > desde).ToListAsync(ct);
            if (quitados.Any(b => b.Entidad == nameof(Producto) || b.Entidad == nameof(Variante) && b.VarianteId is null))
                return CambiosCopiaDto.Completo("Se quitó un producto completo.");

            var variantes = desde == actuales.Catalogo ? [] : await VariantesCambiadasAsync(db, desde, quitados, ct);
            if (variantes.Count > MaximoDeCambios(catalogo.Productos))
                return CambiosCopiaDto.Completo($"Son {variantes.Count:N0} productos con cambios: conviene la copia completa.");
            var (version, productos, codigos, sinCatalogo, marcas) = await catalogo.ParaLaCopiaAsync(actuales.Catalogo, variantes,
                await ExistenciasAsync(db, caja.SucursalId, variantes, ct), ct);
            // Con la versión que se leyó: si el índice ya va en una más nueva, lo que cambió después se pide la próxima vez.
            version = Math.Min(version, actuales.Catalogo);
            var cambiadas = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == caja.SucursalId && e.Version > existencias)
                .Select(e => new { e.VarianteId, e.Cantidad }).ToListAsync(ct);

            bool Quitado(params string[] entidades) => quitados.Any(b => entidades.Contains(b.Entidad));
            // Lo más vendido (se calcula una vez al día) y lo elegido en la búsqueda: no cambian la versión del catálogo, cada uno
            // dice la suya (entrega 5, parte 3).
            var (versionVendidos, losVendidos) = await catalogo.VendidosParaLaCopiaAsync(db, ct);
            var (versionElegidos, losElegidos) = await catalogo.ElegidosAsync(db, cajaId, elegidos, ct);
            var cambios = new CambiosCopiaDto(null, version, actuales.Existencias, await new RegistroCaja(db).UltimoFolioAsync(caja.SucursalId, caja.Serie, ct),
                productos, sinCatalogo, codigos, cambiadas.Select(e => new ExistenciaLocalDto(e.VarianteId, e.Cantidad.EnUnidades)).ToList(),
                Vendidos: versionVendidos != vendidos ? losVendidos : null, Elegidos: losElegidos.Count > 0 ? losElegidos : null,
                VersionVendidos: versionVendidos, VersionElegidos: versionElegidos);
            if (desde == actuales.Catalogo)
                return cambios;
            // Las marcas con que se leen los nombres salen de los productos: si cambió alguno (también si se renombró su marca, o
            // se quitó el último de una), pueden ser otras.
            if (variantes.Count > 0)
                cambios = cambios with { Marcas = marcas };

            if (await db.Usuarios.AnyAsync(u => u.Version > desde, ct) || await db.PermisosUsuario.AnyAsync(p => p.Version > desde, ct)
                || await db.ReglasPermiso.AnyAsync(r => r.Version > desde, ct) || Quitado(nameof(Usuario), nameof(PermisoUsuario), nameof(ReglaPermiso)))
            {
                var (usuarios, permisos, reglas) = await UsuariosAsync(db, caja.SucursalId, ct);
                cambios = cambios with { Usuarios = usuarios, Permisos = permisos, Reglas = reglas };
            }
            if (caja.Version > desde || await db.Sucursales.AnyAsync(s => s.Id == caja.SucursalId && s.Version > desde, ct))
                cambios = cambios with { Caja = await CajaAsync(db, caja, ct) };
            if (await db.ConfiguracionesTicket.AnyAsync(c => c.SucursalId == caja.SucursalId && c.Version > desde, ct) || Quitado(nameof(ConfiguracionTicket)))
                cambios = cambios with { Ticket = await TicketAsync(db, caja.SucursalId, opciones, ct) };
            if (await db.ConfiguracionesSucursal.AnyAsync(c => c.SucursalId == caja.SucursalId && c.Version > desde, ct) || Quitado(nameof(ConfiguracionSucursal)))
                cambios = cambios with { Configuracion = await new RegistroConfiguracion(db).LeerAsync(caja.SucursalId, ct) };
            if (await db.ClientesRegistrados.AnyAsync(c => c.Version > desde, ct) || Quitado(nameof(ClienteRegistrado)))
            {
                var clientes = await ApiClientes.ParaLaCajaAsync(db, caja.SucursalId);
                cambios = cambios with { Clientes = ApiClientes.ParaCajasDeAntes(clientes), Registrados = clientes };
            }
            if (await db.Sinonimos.AnyAsync(s => s.Version > desde, ct) || Quitado(nameof(Sinonimo)))
                cambios = cambios with { Sinonimos = await new RegistroSinonimos(db).ListarAsync(ct) };
            // Lo que se señala (entrega 13): si cambió un exhibidor o una temporada, un lugar o lo acomodado (también lo quitado),
            // todos, completos: son pocos.
            if (await db.Exhibidores.AnyAsync(e => e.Version > desde, ct) || await db.LugaresExhibidor.AnyAsync(l => l.Version > desde, ct)
                || await db.ProductosExhibidos.AnyAsync(p => p.Version > desde, ct)
                || Quitado(nameof(Exhibidor), nameof(LugarExhibidor), nameof(ProductoExhibido)))
                cambios = cambios with { Exhibidores = await RegistroMostrador.ListarAsync(db, ct) };
            return cambios;
        }, ct);
    }

    /// <summary>
    /// Los productos (variantes) con algo que cambió desde esa versión: ellas, su producto y su marca (el nombre), sus
    /// presentaciones, sus códigos y sus precios, y lo que se les quitó o pasó a otro producto.
    /// </summary>
    private static async Task<HashSet<Guid>> VariantesCambiadasAsync(PosDbContext db, long desde, List<Borrado> quitados, CancellationToken ct)
    {
        var ids = new HashSet<Guid>(quitados.Where(b => b.VarianteId is not null).Select(b => b.VarianteId!.Value));
        ids.UnionWith(await db.Variantes.AsNoTracking()
            .Where(v => v.Version > desde || v.Producto!.Version > desde || v.Producto.Marca != null && v.Producto.Marca.Version > desde)
            .Select(v => v.Id).ToListAsync(ct));
        ids.UnionWith(await db.Presentaciones.AsNoTracking().Where(p => p.Version > desde).Select(p => p.VarianteId).ToListAsync(ct));
        ids.UnionWith(await db.CodigosBarras.AsNoTracking().Where(c => c.Version > desde).Select(c => c.Presentacion!.VarianteId).ToListAsync(ct));
        ids.UnionWith(await db.Precios.AsNoTracking().Where(p => p.Version > desde).Select(p => p.VarianteId).ToListAsync(ct));
        return ids;
    }

    /// <summary>
    /// Todo lo de dentro se lee de un mismo momento de la base: una lectura diferida (sin el candado de escritura) en la que lo
    /// primero que se lee son las versiones. Lo que se escriba mientras tanto llega con la siguiente versión.
    /// </summary>
    private static async Task<T> EnUnMismoMomentoAsync<T>(PosDbContext db, Func<Task<T>> leer, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            using var lectura = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await db.Database.UseTransactionAsync(lectura, ct);
            try
            {
                return await leer();
            }
            finally
            {
                await db.Database.UseTransactionAsync(null, ct);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Las existencias de la sucursal (de esas variantes, o de todas), en unidades.</summary>
    private static async Task<Dictionary<Guid, decimal>> ExistenciasAsync(PosDbContext db, Guid sucursalId, IReadOnlyCollection<Guid>? variantes, CancellationToken ct)
    {
        var consulta = db.Existencias.AsNoTracking().Where(e => e.SucursalId == sucursalId);
        if (variantes is not null)
        {
            if (variantes.Count == 0)
                return [];
            consulta = consulta.Where(e => variantes.Contains(e.VarianteId));
        }
        return await consulta.ToDictionaryAsync(e => e.VarianteId, e => e.Cantidad.EnUnidades, ct);
    }

    private static async Task<CajaDto> CajaAsync(PosDbContext db, Caja caja, CancellationToken ct)
    {
        var sucursal = await db.Sucursales.AsNoTracking().Where(s => s.Id == caja.SucursalId).Select(s => s.Nombre).SingleAsync(ct);
        return new CajaDto(caja.Id, caja.Nombre, caja.Serie, caja.SucursalId, sucursal, caja.Equipo);
    }

    /// <summary>
    /// Los usuarios de la sucursal: los dados de baja también, sin el hash de su PIN (sus letras no se le dan a otro, y no
    /// entran). Los permisos de cada activo y los de defecto (entrega 1); las reglas por rol, de antes, solo para una caja
    /// que todavía no se actualiza.
    /// </summary>
    private static async Task<(List<UsuarioLocalDto>, List<PermisosLocalesDto>, IReadOnlyList<ReglaDto>)> UsuariosAsync(PosDbContext db, Guid sucursalId, CancellationToken ct)
    {
        var usuarios = (await db.Usuarios.AsNoTracking()
                .Where(u => u.SucursalId == null || u.SucursalId == sucursalId)
                .Select(u => new UsuarioLocalDto(u.Id, u.NombreUsuario, u.Nombre, u.Rol, u.PinHash, u.Activo))
                .ToListAsync(ct))
            .Select(u => u.Activo ? u : u with { PinHash = "" })
            .ToList();
        var control = new ServicioControl(db);
        var permisos = (await control.UsuariosQueEntranAsync(sucursalId, ct))
            .Select(u => new PermisosLocalesDto(u.Id, u.Permisos.Lista()))
            .Append(new PermisosLocalesDto(null, (await control.DefectoAsync(ct)).Lista()))
            .ToList();
        var reglas = PermisosDeRoles.Efectivas(await db.ReglasPermiso.AsNoTracking()
            .Where(r => r.SucursalId == null || r.SucursalId == sucursalId).ToListAsync(ct), sucursalId);
        return (usuarios, permisos, reglas);
    }

    private static async Task<TicketLocalDto> TicketAsync(PosDbContext db, Guid sucursalId, OpcionesTicket opciones, CancellationToken ct)
    {
        var ticket = await ApiConfiguracion.ConfiguracionAsync(db, sucursalId, opciones);
        return new TicketLocalDto(ticket.RenglonesEncabezado, ticket.RenglonesPie, ApiConfiguracion.Logo(ticket));
    }
}
