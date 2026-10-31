using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos;

/// <summary>
/// La tienda que empieza de cero (la instalación desde cero, 2026-10-31): en una base sin sucursal ni usuarios, la sucursal con su
/// ticket, la primera cuenta (el dueño, con todos los permisos y su PIN) y la caja de la PC del servicio, dada de alta, todo en una
/// transacción. Antes, toda base salía de <c>importar-eleventa</c> o de <c>preparar-prueba</c>. Quién puede pedirlo (solo la misma
/// PC) lo revisa la API; aquí, que la base siga nueva al guardar.
/// </summary>
public sealed class RegistroTiendaNueva(PosDbContext db)
{
    /// <summary>La tienda, la cuenta y la caja, como quedaron, con la llave de la caja (solo la ve la caja que la pidió).</summary>
    public sealed record Creada(Sucursal Sucursal, Usuario Duenio, Caja Caja, string Llave);

    /// <summary>Si la base es nueva: sin sucursal y sin usuarios. Solo entonces se crea la tienda.</summary>
    public static async Task<bool> EsNuevaAsync(PosDbContext db, CancellationToken ct = default) =>
        !await db.Sucursales.AnyAsync(ct) && !await db.Usuarios.AnyAsync(ct);

    /// <summary>
    /// Crea la tienda: lo que no sirve de la solicitud se dice antes de tocar nada. La bitácora dice qué se creó y quién quedó como
    /// primera cuenta, nunca el PIN. Si mientras tanto otra caja ya la creó (dos al mismo tiempo), no se crea otra.
    /// </summary>
    public async Task<Creada> CrearAsync(SolicitudTiendaNueva s, CancellationToken ct = default)
    {
        var nombreSucursal = TiendaNueva.Limpio(s.Sucursal);
        var nombre = TiendaNueva.Limpio(s.Nombre);
        var nombreCaja = TiendaNueva.Limpio(s.Caja);
        var encabezado = (s.Encabezado ?? []).Select(r => (r ?? "").Trim()).Where(r => r.Length > 0).ToList();
        var pie = (s.Pie ?? []).Select(r => (r ?? "").Trim()).Where(r => r.Length > 0).ToList();
        var mal = TiendaNueva.RevisarSucursal(nombreSucursal)
            ?? TiendaNueva.RevisarRenglones(encabezado, "encabezado")
            ?? TiendaNueva.RevisarRenglones(pie, "pie")
            ?? TiendaNueva.RevisarNombre(nombre)
            ?? TiendaNueva.RevisarUsuario(s.Usuario)
            ?? FichasEntrada.RevisarPinNuevo(s.Pin, s.Pin)
            ?? TiendaNueva.RevisarCaja(nombreCaja, s.Serie);
        if (mal is not null)
            throw new ArgumentException(mal);

        // SQLite: con el candado de escritura desde aquí (BEGIN IMMEDIATE), otra que pida lo mismo espera y ya ve esta.
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        if (!await EsNuevaAsync(db, ct))
            throw new TiendaYaCreada();
        var ahora = db.Reloj.GetUtcNow().UtcDateTime;

        var sucursal = new Sucursal { Clave = TiendaNueva.Clave, Nombre = nombreSucursal };
        db.Sucursales.Add(sucursal);
        var ticket = new ConfiguracionTicket { SucursalId = sucursal.Id };
        ticket.CambiarTextos(encabezado, pie);
        db.ConfiguracionesTicket.Add(ticket);

        // La primera cuenta (docs/propuesta-interfaz.md, «Primera instalación»): con todos los permisos, en todas las sucursales de la
        // base, como el dueño de antes. Con ella se dan de alta los demás usuarios y las otras cajas.
        var duenio = new Usuario
        {
            Nombre = nombre, NombreUsuario = TiendaNueva.Usuario(s.Usuario)!, Rol = Rol.Dueno, PrimeraCuenta = true, SucursalId = null,
        };
        duenio.EstablecerPin(s.Pin);
        db.Usuarios.Add(duenio);
        db.PermisosUsuario.AddRange(Permisos.Acciones.Select(a => new PermisoUsuario { UsuarioId = duenio.Id, Accion = a, Nivel = NivelPermiso.Permitido }));

        var caja = new Caja { SucursalId = sucursal.Id, Nombre = nombreCaja, Serie = TiendaNueva.Serie(s.Serie)!, EsPrincipal = true };
        var llave = caja.DarDeAlta(s.Equipo, ahora);
        db.Cajas.Add(caja);

        var control = new ServicioControl(db);
        control.Registrar(sucursal.Id, caja.Id, duenio.Id, AccionControlada.ConfigurarTienda,
            $"Tienda nueva: la sucursal «{sucursal.Nombre}», con su ticket");
        control.Registrar(sucursal.Id, caja.Id, duenio.Id, AccionControlada.EditarReglas,
            $"Primera cuenta: {duenio.Nombre} (usuario «{duenio.NombreUsuario}»), con todos los permisos");
        control.Registrar(sucursal.Id, caja.Id, duenio.Id, AccionControlada.AdministrarCajas,
            $"Dio de alta el equipo «{caja.Equipo}» como {caja.Nombre} (serie {caja.Serie}), en la PC del servicio");
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return new Creada(sucursal, duenio, caja, llave);
    }
}

/// <summary>
/// La tienda ya existía al tomar el candado de escritura: otra caja la creó mientras esta esperaba (revisión del 2026-10-31). La API le
/// contesta lo mismo que a una que llega después de creada (409 «tienda-creada»).
/// </summary>
public sealed class TiendaYaCreada() : InvalidOperationException(TiendaNueva.YaExiste);
