using Microsoft.EntityFrameworkCore;
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
            Registrar(sucursalId, cajaId, solicitanteId, accion, $"Autorización negada: {detalle}", monto, null, autorizador?.Id, ResultadoBitacora.Negado);
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
            VenceUtc = Ahora + Autorizacion.Vigencia,
        };
        db.Autorizaciones.Add(autorizacion);
        await db.SaveChangesAsync(ct);
        return autorizacion;
    }

    /// <summary>
    /// Revisa que el usuario pueda hacer la acción y la deja en la bitácora (sin guardar: se guarda junto con la
    /// operación). Si requiere autorización, gasta la que se mande; sin ella, o si no está permitida, falla.
    /// </summary>
    public async Task RequerirAsync(
        Guid usuarioId, Guid sucursalId, Guid? cajaId, AccionControlada accion, long? monto, string detalle, Guid? autorizacionId = null,
        string? motivo = null, Guid? ventaId = null, CancellationToken ct = default)
    {
        var decision = await EvaluarAsync(usuarioId, sucursalId, accion, monto, ct);
        if (decision.PideMotivo && string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Esta acción lleva motivo.");
        var texto = string.IsNullOrWhiteSpace(motivo) ? detalle : $"{detalle}. Motivo: {motivo.Trim()}";

        switch (decision.Nivel)
        {
            case NivelPermiso.Permitido:
                Registrar(sucursalId, cajaId, usuarioId, accion, texto, monto, ventaId, null, ResultadoBitacora.Hecho);
                return;
            case NivelPermiso.NoPermitido:
                throw new PermisoRequerido(accion, NivelPermiso.NoPermitido, "No tienes permiso para esto.");
        }

        if (autorizacionId is not { } id || await db.Autorizaciones.FindAsync([id], ct) is not { } autorizacion)
            throw new PermisoRequerido(accion, NivelPermiso.RequiereAutorizacion, "Esto requiere la autorización del encargado.");
        autorizacion.Usar(accion, sucursalId, monto, Ahora);
        Registrar(sucursalId, cajaId, usuarioId, accion, texto, monto, ventaId, autorizacion.AutorizadorId, ResultadoBitacora.Autorizado);
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
            ResultadoPin.Correcto => throw new InvalidOperationException(accion == AccionControlada.EditarReglas
                ? "Esto lo autoriza el dueño."
                : "Esto lo autoriza el encargado o el dueño."),
            ResultadoPin.Bloqueado => throw new InvalidOperationException("Demasiados intentos: espera unos minutos."),
            _ => throw new InvalidOperationException("Usuario o PIN incorrecto."),
        };
    }

    private async Task<Usuario> UsuarioAsync(Guid usuarioId, CancellationToken ct) =>
        await db.Usuarios.FindAsync([usuarioId], ct) is { Activo: true } u ? u : throw new InvalidOperationException("El usuario no existe o está inactivo.");
}
