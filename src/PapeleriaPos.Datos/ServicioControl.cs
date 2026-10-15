using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos;

/// <summary>
/// Revisa las reglas, da y gasta autorizaciones y escribe la bitácora. Todo lo que pasa por aquí lo decide el servicio,
/// no la pantalla: así no se puede saltar el control desde otra caja o llamando a la API directo.
/// </summary>
public sealed class ServicioControl(PosDbContext db)
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    public async Task<List<ReglaPermiso>> ReglasAsync(Guid sucursalId, CancellationToken ct = default) =>
        await db.ReglasPermiso.AsNoTracking().Where(r => r.SucursalId == null || r.SucursalId == sucursalId).ToListAsync(ct);

    public async Task<DecisionPermiso> EvaluarAsync(Guid usuarioId, Guid sucursalId, AccionControlada accion, long? monto = null, CancellationToken ct = default)
    {
        var usuario = await UsuarioAsync(usuarioId, ct);
        return Permisos.Evaluar(await ReglasAsync(sucursalId, ct), usuario.Rol, sucursalId, accion, monto);
    }

    /// <summary>Los usuarios activos de la sucursal que pueden hacer la acción, por su nombre (el corte del turno).</summary>
    public async Task<List<QuienAutoriza>> QuienesPuedenAsync(Guid sucursalId, AccionControlada accion, CancellationToken ct = default)
    {
        var usuarios = await db.Usuarios.AsNoTracking().Where(u => u.Activo && (u.SucursalId == null || u.SucursalId == sucursalId))
            .Select(u => new { u.Id, u.NombreUsuario, u.Nombre, u.Rol }).ToListAsync(ct);
        return Permisos.QuienesPueden(await ReglasAsync(sucursalId, ct), usuarios.Select(u => (u.Id, u.NombreUsuario, u.Nombre, u.Rol)), sucursalId, accion);
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
    /// El encargado (o el dueño) se identifica con usuario y PIN para una acción. Si no puede autorizarla, queda en
    /// la bitácora como negada.
    /// </summary>
    public async Task<Autorizacion> AutorizarAsync(
        AccionControlada accion, Guid sucursalId, Guid? cajaId, Guid solicitanteId, string usuario, string pin, long? monto, string detalle,
        CancellationToken ct = default)
    {
        var autorizador = await db.Usuarios.SingleOrDefaultAsync(u => u.NombreUsuario == usuario.Trim().ToLower(), ct);
        var resultado = autorizador?.VerificarPin(pin, Ahora);
        var puede = resultado == ResultadoPin.Correcto
            && Permisos.PuedeAutorizar(await ReglasAsync(sucursalId, ct), autorizador!.Rol, sucursalId, accion, monto);

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
            throw new PermisoRequerido(accion, NivelPermiso.RequiereAutorizacion, "Esto requiere la autorización del encargado.");
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

    /// <summary>Usuario y PIN de alguien con la acción permitida (corte Z, configurar el ticket, editar reglas).</summary>
    public async Task<Usuario> IdentificarAsync(string usuario, string pin, Guid sucursalId, AccionControlada accion, CancellationToken ct = default)
    {
        var encontrado = await db.Usuarios.SingleOrDefaultAsync(u => u.NombreUsuario == usuario.Trim().ToLower(), ct);
        var resultado = encontrado?.VerificarPin(pin, Ahora);
        var puede = resultado == ResultadoPin.Correcto
            && Permisos.Evaluar(await ReglasAsync(sucursalId, ct), encontrado!.Rol, sucursalId, accion).Nivel == NivelPermiso.Permitido;
        if (!puede)
            Registrar(sucursalId, null, encontrado?.Id, accion, "Intento sin permiso", resultado: ResultadoBitacora.Negado);
        await db.SaveChangesAsync(ct);
        return resultado switch
        {
            ResultadoPin.Correcto when puede => encontrado!,
            ResultadoPin.Correcto => throw new InvalidOperationException(Permisos.SoloDelDueno(accion)
                ? "Esto lo autoriza el dueño."
                : "Esto lo autoriza el encargado o el dueño."),
            ResultadoPin.Bloqueado => throw new InvalidOperationException("Demasiados intentos: espera unos minutos."),
            _ => throw new InvalidOperationException("Usuario o PIN incorrecto."),
        };
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
