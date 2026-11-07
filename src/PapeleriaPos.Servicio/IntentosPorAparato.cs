using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Los PIN malos de cada aparato de la red, sin llave: el alta de una caja y la entrada de /conteo (auditoría, 2026-11-04). El bloqueo de
/// la cuenta es de la cuenta: cinco PIN malos desde cualquier celular dejaban fuera al dueño, sin rastro. Aquí el aparato espera antes de
/// llegar a eso (tres, y la cuenta aguanta cinco): el PIN ya ni se revisa y la ficha de la caja sigue entrando. Se cuenta al entrar, no al
/// fallar, para que muchos a la vez no se cuelen. Quien sigue insistiendo espera el doble cada vez, hasta <see cref="EsperaMaxima"/>: con una
/// espera fija, un programa probaba unos 720 PIN al día y sacaba uno de 4 dígitos en unos días; así, unos 18 al día. Vuelve a cero con un PIN
/// bueno o a <see cref="Olvido"/> sin intentos. De memoria: al reiniciar el servicio vuelve a cero, y la cuenta sigue con los suyos.
/// Lo que queda: cambiar de dirección IP empieza de cero, y quien tiene un PIN bueno propio puede volver a cero el aparato con él.
/// </summary>
public sealed class IntentosPorAparato(IServiceScopeFactory alcances, ILogger<IntentosPorAparato> log, TimeProvider reloj)
{
    public const int Permitidos = 3;
    public static readonly TimeSpan Espera = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan EsperaMaxima = TimeSpan.FromHours(4);

    /// <summary>Sin intentos este tiempo, el aparato empieza de cero. Antes de esto, los malos se siguen sumando: dos cada rato no se libran del tope.</summary>
    public static readonly TimeSpan Olvido = TimeSpan.FromDays(1);

    /// <summary>Más que los aparatos de la tienda: de ahí en adelante se limpian los que ya no esperan.</summary>
    private const int AparatosSinLimpiar = 256;

    private sealed class Cuenta
    {
        public int Intentos;
        public DateTime Ultimo;
        public bool Anotado;

        /// <summary>Las veces que ya esperó, seguidas.</summary>
        public int Esperas;

        public TimeSpan EsperaQueToca => Esperas >= 6 ? EsperaMaxima : TimeSpan.FromTicks(Math.Min(Espera.Ticks << Esperas, EsperaMaxima.Ticks));
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
                foreach (var vencido in _cuentas.Where(c => Ahora - c.Value.Ultimo >= Olvido).Select(c => c.Key).ToList())
                    _cuentas.Remove(vencido);
            if (!_cuentas.TryGetValue(aparato, out var cuenta) || Ahora - cuenta.Ultimo >= Olvido)
                _cuentas[aparato] = cuenta = new Cuenta();
            if (cuenta.Intentos >= Permitidos)
            {
                if (Ahora - cuenta.Ultimo < cuenta.EsperaQueToca)
                    return false;
                cuenta.Intentos = 0;
                cuenta.Anotado = false;
                cuenta.Esperas++;
            }
            cuenta.Intentos++;
            cuenta.Ultimo = Ahora;
            return true;
        }
    }

    /// <summary>Era el bueno: este aparato empieza de cero.</summary>
    public void FueBueno(string? aparato)
    {
        if (aparato is null)
            return;
        lock (_cuentas)
            _cuentas.Remove(aparato);
    }

    /// <summary>No se llegó a revisar: ese intento no cuenta, y lo demás sigue como estaba.</summary>
    public void NoSeReviso(string? aparato)
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
        TimeSpan espera;
        lock (_cuentas)
        {
            if (!_cuentas.TryGetValue(aparato, out var cuenta) || cuenta.Intentos < Permitidos || cuenta.Anotado)
                return;
            cuenta.Anotado = true;
            espera = cuenta.EsperaQueToca;
        }
        var escrito = usuario.Trim();
        escrito = escrito.Length > 40 ? escrito[..40] : escrito;
        log.LogWarning("Demasiados PIN malos desde {Aparato} (el último, con el usuario «{Usuario}»): ese aparato espera {Minutos} minutos.",
            aparato, escrito, espera.TotalMinutes);
        try
        {
            using var alcance = alcances.CreateScope();
            if (alcance.ServiceProvider.GetService<PosDbContext>() is not { } db
                || await db.Sucursales.Select(s => (Guid?)s.Id).FirstOrDefaultAsync() is not { } sucursal)
                return;
            new ServicioControl(db).Registrar(sucursal, null, null, AccionControlada.AdministrarCajas,
                $"Demasiados PIN malos desde el aparato {aparato} (el último, con el usuario «{escrito}»): espera {espera.TotalMinutes:0} minutos",
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
