using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos;

/// <summary>
/// Revisa los permisos de cada usuario (entrega 1), da y gasta autorizaciones y escribe la bitácora. Todo lo que pasa por
/// aquí lo decide el servicio, no la pantalla: así no se puede saltar el control desde otra caja o llamando a la API directo.
/// Los permisos son del usuario: la sucursal que reciben los métodos ya no cambia lo que puede, solo dice qué usuarios
/// entran en esa caja (quién autoriza) y dónde queda la bitácora.
/// </summary>
public sealed class ServicioControl(PosDbContext db)
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Los permisos de un usuario, uno por acción (el que no esté, «No»).</summary>
    public async Task<PermisosDeUsuario> PermisosAsync(Guid usuarioId, CancellationToken ct = default) =>
        new(await db.PermisosUsuario.AsNoTracking().Where(p => p.UsuarioId == usuarioId)
            .Select(p => new Permiso(p.Accion, p.Nivel, p.UmbralCentavos, p.PideMotivo)).ToListAsync(ct));

    /// <summary>Los permisos por defecto: con ellos empieza cada usuario nuevo.</summary>
    public async Task<PermisosDeUsuario> DefectoAsync(CancellationToken ct = default) =>
        new(await db.PermisosUsuario.AsNoTracking().Where(p => p.UsuarioId == null)
            .Select(p => new Permiso(p.Accion, p.Nivel, p.UmbralCentavos, p.PideMotivo)).ToListAsync(ct));

    /// <summary>
    /// Los usuarios activos que entran en las cajas de la sucursal (los de ella y los que no tienen sucursal), con sus
    /// permisos: de entre ellos sale quién autoriza y quién puede. Sin sucursal, todos los activos.
    /// </summary>
    public async Task<List<UsuarioConPermisos>> UsuariosQueEntranAsync(Guid? sucursalId, CancellationToken ct = default)
    {
        var usuarios = await db.Usuarios.AsNoTracking()
            .Where(u => u.Activo && (sucursalId == null || u.SucursalId == null || u.SucursalId == sucursalId))
            .Select(u => new { u.Id, u.NombreUsuario, u.Nombre }).ToListAsync(ct);
        var ids = usuarios.Select(u => (Guid?)u.Id).ToList();
        var permisos = (await db.PermisosUsuario.AsNoTracking().Where(p => ids.Contains(p.UsuarioId))
                .Select(p => new { p.UsuarioId, Permiso = new Permiso(p.Accion, p.Nivel, p.UmbralCentavos, p.PideMotivo) }).ToListAsync(ct))
            .ToLookup(p => p.UsuarioId!.Value, p => p.Permiso);
        return usuarios.Select(u => new UsuarioConPermisos(u.Id, u.NombreUsuario, u.Nombre, new PermisosDeUsuario(permisos[u.Id]))).ToList();
    }

    public async Task<DecisionPermiso> EvaluarAsync(Guid usuarioId, Guid sucursalId, AccionControlada accion, long? monto = null, CancellationToken ct = default)
    {
        await UsuarioAsync(usuarioId, ct);
        return Permisos.Evaluar(await PermisosAsync(usuarioId, ct), accion, monto);
    }

    /// <summary>Lo puede hacer sin pedir autorización (y está activo): el costo que se le enseña, una pestaña.</summary>
    public async Task<bool> PuedeAsync(Guid usuarioId, AccionControlada accion, CancellationToken ct = default) =>
        await db.Usuarios.AnyAsync(u => u.Id == usuarioId && u.Activo, ct)
        && Permisos.Evaluar(await PermisosAsync(usuarioId, ct), accion).Nivel == NivelPermiso.Permitido;

    /// <summary>Los usuarios activos de la sucursal que pueden hacer la acción, por su nombre (el corte del turno).</summary>
    public async Task<List<QuienAutoriza>> QuienesPuedenAsync(Guid sucursalId, AccionControlada accion, CancellationToken ct = default) =>
        Permisos.QuienesPueden(await UsuariosQueEntranAsync(sucursalId, ct), accion);

    /// <summary>
    /// Quien entró, con todo lo que puede y quién autoriza cada cosa (de entre los que entran en las cajas de la sucursal),
    /// en una sola consulta: la caja pinta con esto. Sin sucursal (entró sin caja: el celular), con todos los activos.
    /// </summary>
    public async Task<SesionDto> SesionAsync(Usuario usuario, Guid? sucursalId, CancellationToken ct = default)
    {
        var usuarios = await UsuariosQueEntranAsync(sucursalId, ct);
        var propios = usuarios.FirstOrDefault(u => u.Id == usuario.Id)?.Permisos ?? await PermisosAsync(usuario.Id, ct);
        return new SesionDto(usuario.Id, usuario.Nombre, usuario.Rol, Permisos.DeLaSesion(propios, usuarios, usuario.Id),
            FichasEntrada.Resumen(propios, await DefectoAsync(ct)));
    }

    /// <summary>
    /// Para lo que no se autoriza en el momento (catálogo, compras, inventario): o el usuario lo puede hacer o no. La
    /// bitácora la escribe quien llama, junto con la operación.
    /// </summary>
    public async Task ExigirAsync(Guid usuarioId, Guid sucursalId, AccionControlada accion, string mensaje, CancellationToken ct = default)
    {
        if ((await EvaluarAsync(usuarioId, sucursalId, accion, ct: ct)).Nivel != NivelPermiso.Permitido)
            throw new PermisoRequerido(accion, NivelPermiso.NoPermitido, mensaje);
    }

    /// <summary>
    /// Quien puede autorizar se identifica con su usuario y PIN (en la caja, con su ficha) para una acción. Si no puede
    /// autorizarla, queda en la bitácora como negada.
    /// </summary>
    public async Task<Autorizacion> AutorizarAsync(
        AccionControlada accion, Guid sucursalId, Guid? cajaId, Guid solicitanteId, string usuario, string pin, long? monto, string detalle,
        CancellationToken ct = default)
    {
        var autorizador = await db.Usuarios.SingleOrDefaultAsync(u => u.NombreUsuario == usuario.Trim().ToLower(), ct);
        var resultado = autorizador?.VerificarPin(pin, Ahora);
        var puede = resultado == ResultadoPin.Correcto && Permisos.PuedeAutorizar(await PermisosAsync(autorizador!.Id, ct), accion, monto);

        if (!puede)
        {
            Registrar(sucursalId, cajaId, solicitanteId, accion, $"Autorización negada: {detalle}", Permisos.MontoParaBitacora(accion, monto), null,
                autorizador?.Id, ResultadoBitacora.Negado);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException(resultado switch
            {
                ResultadoPin.Bloqueado => "Demasiados intentos: espera unos minutos.",
                ResultadoPin.Correcto => $"{autorizador!.Nombre} no puede autorizar esto.",
                _ => "Usuario o PIN incorrecto.",
            });
        }

        var autorizacion = new Autorizacion
        {
            Accion = accion,
            SucursalId = sucursalId,
            CajaId = cajaId,
            SolicitanteId = solicitanteId,
            AutorizadorId = autorizador!.Id,
            MontoCentavos = monto,
            Detalle = detalle,
            CreadaUtc = Ahora,
            VenceUtc = Ahora + Autorizacion.VigenciaDe(accion),
        };
        db.Autorizaciones.Add(autorizacion);
        await db.SaveChangesAsync(ct);
        return autorizacion;
    }

    /// <summary>
    /// Revisa que el usuario pueda hacer la acción y la deja en la bitácora (sin guardar: se guarda junto con la
    /// operación). Si requiere autorización, gasta la que se mande; sin ella, o si no está permitida, falla.
    /// </summary>
    /// <param name="montoBitacora">Lo que va en la bitácora si no es <paramref name="monto"/> (en un descuento, el monto es el porcentaje).</param>
    public async Task RequerirAsync(
        Guid usuarioId, Guid sucursalId, Guid? cajaId, AccionControlada accion, long? monto, string detalle, Guid? autorizacionId = null,
        string? motivo = null, Guid? ventaId = null, CancellationToken ct = default, long? montoBitacora = null)
    {
        var registrado = montoBitacora ?? Permisos.MontoParaBitacora(accion, monto);
        var decision = await EvaluarAsync(usuarioId, sucursalId, accion, monto, ct);
        if (decision.PideMotivo && string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Esta acción lleva motivo.");
        var texto = string.IsNullOrWhiteSpace(motivo) ? detalle : $"{detalle}. Motivo: {motivo.Trim()}";

        switch (decision.Nivel)
        {
            case NivelPermiso.Permitido:
                Registrar(sucursalId, cajaId, usuarioId, accion, texto, registrado, ventaId, null, ResultadoBitacora.Hecho);
                return;
            case NivelPermiso.NoPermitido:
                throw new PermisoRequerido(accion, NivelPermiso.NoPermitido, "No tienes permiso para esto.");
        }

        if (autorizacionId is not { } id || await db.Autorizaciones.FindAsync([id], ct) is not { } autorizacion)
            throw new PermisoRequerido(accion, NivelPermiso.RequiereAutorizacion, await QuienAutorizaAsync(usuarioId, sucursalId, accion, monto, ct));
        autorizacion.Usar(accion, sucursalId, monto, Ahora);
        Registrar(sucursalId, cajaId, usuarioId, accion, autorizacion.PorTelegram ? $"{texto} (autorizado por Telegram)" : texto, registrado, ventaId,
            autorizacion.AutorizadorId, ResultadoBitacora.Autorizado);
    }

    /// <summary>Un registro de la bitácora que hizo una caja sin conexión. Guarda; si ya llegó antes, no lo repite.</summary>
    public async Task RegistrarDeContingenciaAsync(BitacoraContingenciaDto b, CancellationToken ct = default)
    {
        if (await db.Bitacora.AnyAsync(x => x.Id == b.Id, ct))
            return;
        db.Bitacora.Add(new Bitacora
        {
            Id = b.Id, SucursalId = b.SucursalId, CajaId = b.CajaId, UsuarioId = b.UsuarioId, Accion = b.Accion, Detalle = b.Detalle,
            MontoCentavos = b.MontoCentavos, VentaId = b.VentaId, AutorizoId = b.AutorizoId, Resultado = b.Resultado, FechaUtc = b.FechaUtc,
            EnContingencia = true,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Agrega a la bitácora sin guardar.</summary>
    public void Registrar(
        Guid sucursalId, Guid? cajaId, Guid? usuarioId, AccionControlada accion, string detalle, long? monto = null, Guid? ventaId = null,
        Guid? autorizoId = null, ResultadoBitacora resultado = ResultadoBitacora.Hecho) =>
        db.Bitacora.Add(new Bitacora
        {
            SucursalId = sucursalId,
            CajaId = cajaId,
            UsuarioId = usuarioId,
            Accion = accion,
            Detalle = detalle,
            MontoCentavos = monto,
            VentaId = ventaId,
            AutorizoId = autorizoId,
            Resultado = resultado,
            FechaUtc = Ahora,
        });

    /// <summary>
    /// Lo que se cambia con la sesión, sin volver a escribir el PIN (la Configuración nueva, entrega 7, como «Usuarios y
    /// permisos»): quien entró a la caja tiene que tener la acción en «Puede». Si no, el error dice quién la tiene y el
    /// intento queda en la bitácora. Va antes de tocar nada: si no puede, guarda solo el intento.
    /// </summary>
    public async Task ExigirDeLaSesionAsync(Guid usuarioId, Guid sucursalId, Guid? cajaId, AccionControlada accion, CancellationToken ct = default)
    {
        if ((await EvaluarAsync(usuarioId, sucursalId, accion, ct: ct)).Nivel == NivelPermiso.Permitido)
            return;
        Registrar(sucursalId, cajaId, usuarioId, accion, "Intento sin permiso", resultado: ResultadoBitacora.Negado);
        await db.SaveChangesAsync(ct);
        throw await SinPermisoAsync(sucursalId, accion, ct);
    }

    /// <summary>Lo que se consulta con la sesión (la bitácora): quien entró tiene que tener la acción en «Puede». No queda en la bitácora.</summary>
    public async Task ExigirParaVerAsync(Guid usuarioId, Guid sucursalId, AccionControlada accion, CancellationToken ct = default)
    {
        if ((await EvaluarAsync(usuarioId, sucursalId, accion, ct: ct)).Nivel != NivelPermiso.Permitido)
            throw await SinPermisoAsync(sucursalId, accion, ct);
    }

    /// <summary>«Esto lo hace quien tiene el permiso «…»: Carmen o Luis.», con quienes lo tienen en la sucursal.</summary>
    private async Task<PermisoRequerido> SinPermisoAsync(Guid sucursalId, AccionControlada accion, CancellationToken ct) =>
        new(accion, NivelPermiso.NoPermitido, Permisos.QuienLoHace(accion, await QuienesPuedenAsync(sucursalId, accion, ct)));

    /// <summary>
    /// Usuario y PIN de alguien con la acción en «Puede», donde todavía se piden: dar de alta una caja (antes de que haya
    /// sesión), Telegram (en pausa, se queda como estaba) y verificar una transferencia.
    /// </summary>
    public async Task<Usuario> IdentificarAsync(string usuario, string pin, Guid sucursalId, AccionControlada accion, CancellationToken ct = default)
    {
        var encontrado = await db.Usuarios.SingleOrDefaultAsync(u => u.NombreUsuario == usuario.Trim().ToLower(), ct);
        var resultado = encontrado?.VerificarPin(pin, Ahora);
        var puede = resultado == ResultadoPin.Correcto
            && Permisos.Evaluar(await PermisosAsync(encontrado!.Id, ct), accion).Nivel == NivelPermiso.Permitido;
        if (!puede)
            Registrar(sucursalId, null, encontrado?.Id, accion, "Intento sin permiso", resultado: ResultadoBitacora.Negado);
        await db.SaveChangesAsync(ct);
        return resultado switch
        {
            ResultadoPin.Correcto when puede => encontrado!,
            ResultadoPin.Correcto => throw new InvalidOperationException(Permisos.QuienLoHace(accion, await QuienesPuedenAsync(sucursalId, accion, ct))),
            ResultadoPin.Bloqueado => throw new InvalidOperationException("Demasiados intentos: espera unos minutos."),
            _ => throw new InvalidOperationException("Usuario o PIN incorrecto."),
        };
    }

    /// <summary>
    /// A quien le falta la autorización: «Esto lo autoriza Encargado de prueba o Dueño de prueba.», con los que pueden por
    /// ese monto (sin quien la pide).
    /// </summary>
    private async Task<string> QuienAutorizaAsync(Guid usuarioId, Guid sucursalId, AccionControlada accion, long? monto, CancellationToken ct)
    {
        var quienes = Permisos.EnOrden((await UsuariosQueEntranAsync(sucursalId, ct))
                .Where(u => u.Id != usuarioId && Permisos.PuedeAutorizar(u.Permisos, accion, monto)))
            .Select(u => new QuienAutoriza(u.Usuario, u.Nombre)).ToList();
        return quienes.Count == 0 ? "Esto requiere autorización." : $"Esto lo autoriza {Permisos.Nombres(quienes)}.";
    }

    /// <summary>Lo que pasó en la sucursal entre dos momentos, lo más reciente primero.</summary>
    public async Task<List<(Bitacora Registro, string? Usuario, string? Autorizo)>> ConsultarAsync(
        Guid sucursalId, DateTime desdeUtc, DateTime hastaUtc, CancellationToken ct = default)
    {
        var registros = await db.Bitacora.AsNoTracking()
            .Where(b => b.SucursalId == sucursalId && b.FechaUtc >= desdeUtc && b.FechaUtc < hastaUtc)
            .OrderByDescending(b => b.FechaUtc)
            .Take(2000)
            .ToListAsync(ct);
        var nombres = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        return registros.Select(r => (r, r.UsuarioId is { } u ? nombres.GetValueOrDefault(u) : null,
            r.AutorizoId is { } a ? nombres.GetValueOrDefault(a) : null)).ToList();
    }

    private async Task<Usuario> UsuarioAsync(Guid usuarioId, CancellationToken ct) =>
        await db.Usuarios.FindAsync([usuarioId], ct) is { Activo: true } u ? u : throw new InvalidOperationException("El usuario no existe o está inactivo.");
}
