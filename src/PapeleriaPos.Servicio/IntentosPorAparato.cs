using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Los PIN malos de cada aparato de la red, sin llave: el alta de una caja y la entrada de /conteo (auditoría, 2026-11-04). El bloqueo de
/// la cuenta es de la cuenta: cinco PIN malos desde cualquier celular dejaban fuera al dueño, sin rastro. Aquí el aparato espera antes de
/// llegar a eso (tres, y la cuenta aguanta cinco): el PIN ya ni se revisa y la ficha de la caja sigue entrando. Se cuenta al entrar, no al
/// fallar, para que muchos a la vez no se cuelen. De memoria: al reiniciar el servicio vuelve a cero, y la cuenta sigue con los suyos.
/// Lo que queda: quien insiste, esperando, sí llega a bloquear la cuenta, cada unos diez minutos; cada vez queda en la bitácora.
/// </summary>
public sealed class IntentosPorAparato(IServiceScopeFactory alcances, ILogger<IntentosPorAparato> log, TimeProvider reloj)
{
    public const int Permitidos = 3;
    public static readonly TimeSpan Espera = TimeSpan.FromMinutes(5);

    /// <summary>Más que los aparatos de la tienda: de ahí en adelante se limpian los que ya no esperan.</summary>
    private const int AparatosSinLimpiar = 256;

    private sealed class Cuenta
    {
        public int Intentos;
        public DateTime Ultimo;
        public bool Anotado;
    }

    private readonly Dictionary<string, Cuenta> _cuentas = [];

    private DateTime Ahora => reloj.GetUtcNow().UtcDateTime;

    /// <summary>El aparato de esta conexión. Vacío en esta misma PC: su caja no se detiene por esto (solo la frena el bloqueo de la cuenta).</summary>
    public static string? DeLaConexion(HttpContext? http)
    {
        var ip = http?.Connection.RemoteIpAddress;
        return ip is null || SeguridadCajas.EsLaMismaPc(ip) ? null : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    /// <summary>Si este aparato puede probar un PIN: si sí, ya le cuenta. Después, <see cref="FueBueno"/> o <see cref="FueMaloAsync"/>.</summary>
    public bool Intentar(string? aparato)
    {
        if (aparato is null)
            return true;
        lock (_cuentas)
        {
            if (_cuentas.Count > AparatosSinLimpiar)
                foreach (var vencido in _cuentas.Where(c => Ahora - c.Value.Ultimo >= Espera).Select(c => c.Key).ToList())
                    _cuentas.Remove(vencido);
            if (!_cuentas.TryGetValue(aparato, out var cuenta) || Ahora - cuenta.Ultimo >= Espera)
                _cuentas[aparato] = cuenta = new Cuenta();
            if (cuenta.Intentos >= Permitidos)
                return false;
            cuenta.Intentos++;
            cuenta.Ultimo = Ahora;
            return true;
        }
    }

    /// <summary>Era el bueno, o no se llegó a revisar: ese intento no cuenta.</summary>
    public void FueBueno(string? aparato)
    {
        if (aparato is null)
            return;
        lock (_cuentas)
        {
            if (_cuentas.TryGetValue(aparato, out var cuenta) && cuenta.Intentos > 0)
                cuenta.Intentos--;
        }
    }

    /// <summary>Era malo: al llegar al tope, una línea en el registro y un renglón en la bitácora, una vez por espera.</summary>
    public async Task FueMaloAsync(string? aparato, string usuario)
    {
        if (aparato is null)
            return;
        lock (_cuentas)
        {
            if (!_cuentas.TryGetValue(aparato, out var cuenta) || cuenta.Intentos < Permitidos || cuenta.Anotado)
                return;
            cuenta.Anotado = true;
        }
        var escrito = usuario.Trim();
        escrito = escrito.Length > 40 ? escrito[..40] : escrito;
        log.LogWarning("Demasiados PIN malos desde {Aparato} (el último, con el usuario «{Usuario}»): ese aparato espera {Minutos} minutos.",
            aparato, escrito, Espera.TotalMinutes);
        try
        {
            using var alcance = alcances.CreateScope();
            if (alcance.ServiceProvider.GetService<PosDbContext>() is not { } db
                || await db.Sucursales.Select(s => (Guid?)s.Id).FirstOrDefaultAsync() is not { } sucursal)
                return;
            new ServicioControl(db).Registrar(sucursal, null, null, AccionControlada.AdministrarCajas,
                $"Demasiados PIN malos desde el aparato {aparato} (el último, con el usuario «{escrito}»): espera {Espera.TotalMinutes:0} minutos",
                resultado: ResultadoBitacora.Negado);
            await db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Ya está en el registro; la respuesta al aparato no depende de esto.
            log.LogWarning(ex, "No se pudo anotar en la bitácora el tope de PIN malos de {Aparato}.", aparato);
        }
    }
}
