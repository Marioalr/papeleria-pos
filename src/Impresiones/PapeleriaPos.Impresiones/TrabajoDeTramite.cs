using System.Text.RegularExpressions;

namespace PapeleriaPos.Impresiones;

/// <summary>
/// El trámite de un trabajo de impresión (docs/tramites.md, «Imprimir sin cobrar dos veces»; 2026-10-31): la copia que va incluida en él
/// o sus copias de más. Lo manda ya marcado la caja que lo imprimió desde el panel de sus trámites (<see cref="Contratos.TrabajoMedidoDto.Tramite"/>):
/// el servicio no lo adivina por el nombre del trabajo (revisión de servicios/curp: un archivo de un cliente que se llamara «T-2026.pdf»
/// salía gratis y no aparecía en F2).
/// <list type="bullet">
/// <item>La copia incluida («T-0042 · CURP») se cobra con el trámite (el producto CURP en el ticket): no sale en F2 «por cobrar» y no lleva
/// precio, solo su costo (<see cref="EstadoCobroImpresion.EnTramite"/>);</item>
/// <item>las copias de más («T-0042 · CURP · copias extra») se cobran como cualquier impresión, en F2.</item>
/// </list>
/// De ninguno se guarda la miniatura: sería el documento de un cliente (su CURP) guardado en la base. El agente tampoco la manda. Tampoco
/// de un trabajo que se llame exactamente como uno de un trámite aunque no llegue marcado (<see cref="SeLlamaComoUnTramite"/>; segunda
/// revisión): la marca se pudo perder, la miniatura sería la CURP de alguien.
/// </summary>
/// <param name="Folio">El folio del trámite («T-0042»).</param>
/// <param name="CopiasExtra">Las copias de más: se cobran en F2 al precio de la impresión.</param>
public sealed record TrabajoDeTramite(string Folio, bool CopiasExtra)
{
    /// <summary>Entre el folio, el trámite y la marca de las copias de más.</summary>
    public const string Separador = " · ";

    /// <summary>Lo que lleva al final el nombre de las copias de más.</summary>
    public const string MarcaCopiasExtra = "copias extra";

    /// <summary>«T-0042 · CURP» o «T-0042 · CURP · copias extra»: el folio, el nombre del trámite (letras, números y espacios; sin la extensión de un archivo) y nada más.</summary>
    private static readonly Regex ComoUnTramite = new(@"^T-\d{4,} · [\p{L}\p{N}]+(?: [\p{L}\p{N}]+)*(?: · copias extra)?$", RegexOptions.CultureInvariant);

    /// <summary>El de la copia incluida: «T-0042 · CURP». Es el nombre del trabajo en la cola de Windows, nunca el del cliente.</summary>
    public static string Nombre(string folio, string tramite) => $"{folio}{Separador}{tramite}";

    /// <summary>El de las copias de más: «T-0042 · CURP · copias extra».</summary>
    public static string NombreDeCopiasExtra(string folio, string tramite) => $"{folio}{Separador}{tramite}{Separador}{MarcaCopiasExtra}";

    /// <summary>
    /// Se llama exactamente como un trabajo que manda el panel de los trámites («T-0042 · CURP», con o sin «· copias extra»). No dice que sea
    /// de un trámite (eso lo dice la marca de la caja): solo que su miniatura no se guarda. Su precio y F2, como cualquier impresión.
    /// </summary>
    public static bool SeLlamaComoUnTramite(string? documento) => documento is not null && ComoUnTramite.IsMatch(documento);
}

/// <summary>Un trabajo que mandó a imprimir el panel de los trámites: su nombre exacto, de qué trámite es y cuándo se mandó (sin nada del cliente).</summary>
public sealed record TrabajoDeTramiteEnviado(string Nombre, TrabajoDeTramite Tramite, DateTime EnviadoUtc);

/// <summary>
/// Lo que esta caja mandó a imprimir desde el panel de sus trámites (revisión de servicios/curp, 2026-10-31). El agente de impresión de la
/// misma caja (corre en el mismo proceso) reconoce con esto sus trabajos: el nombre exacto que puso el panel y que el trabajo entró a la
/// cola de Windows poco después de mandarlo. Ese trabajo va al servicio ya marcado como del trámite; todo lo demás es una impresión como
/// cualquiera, con su precio, aunque se llame igual.
/// <para>
/// Se recuerda un día (segunda revisión): el agente mide un trabajo cuando ya salió de la impresora, y una impresora apagada, sin papel o
/// atascada lo deja horas en la cola. Con <see cref="Recordar"/> (la caja de Windows) también se guarda en la base local de la caja: se
/// reconoce aunque la caja se haya cerrado o reiniciado antes de que saliera. Solo el nombre del trabajo, su folio y la hora.
/// </para>
/// </summary>
public sealed class TrabajosDeTramites(TimeProvider? reloj = null)
{
    /// <summary>Lo más que puede tardar el trabajo en entrar a la cola de Windows después de mandarlo (dibujar una hoja tarda segundos).</summary>
    public static readonly TimeSpan Plazo = TimeSpan.FromMinutes(5);

    /// <summary>Cuánto se recuerda lo que se mandó: lo que sale de la impresora después de esto se cobra en F2, como cualquiera.</summary>
    public static readonly TimeSpan Recuerdo = TimeSpan.FromHours(24);

    /// <summary>Lo que el reloj de la PC y el de la cola pueden no coincidir (la cola anota la hora en milisegundos de su propio llamado).</summary>
    private static readonly TimeSpan Holgura = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;
    private readonly Lock _candado = new();
    private readonly List<TrabajoDeTramiteEnviado> _enviados = [];
    private Action<IReadOnlyList<TrabajoDeTramiteEnviado>>? _guardar;

    /// <summary>Lo que se recuerda ahora (lo que se guarda).</summary>
    public IReadOnlyList<TrabajoDeTramiteEnviado> Enviados
    {
        get
        {
            lock (_candado)
                return [.. _enviados];
        }
    }

    /// <summary>
    /// Lo que se mandó antes de abrir la caja (su base local) y dónde guardar cada cambio: la caja de Windows, al abrir, antes de que empiece
    /// el agente. Lo de hace más de un día ya no se carga.
    /// </summary>
    public void Recordar(IEnumerable<TrabajoDeTramiteEnviado> antes, Action<IReadOnlyList<TrabajoDeTramiteEnviado>> guardar)
    {
        var ahora = _reloj.GetUtcNow().UtcDateTime;
        lock (_candado)
        {
            _enviados.InsertRange(0, antes.Where(e => ahora - e.EnviadoUtc <= Recuerdo));
            _guardar = guardar;
        }
    }

    /// <summary>El panel va a mandar este trabajo a la impresora, con este nombre.</summary>
    public void Enviado(string nombreDelTrabajo, TrabajoDeTramite tramite)
    {
        var ahora = _reloj.GetUtcNow().UtcDateTime;
        IReadOnlyList<TrabajoDeTramiteEnviado> paraGuardar;
        Action<IReadOnlyList<TrabajoDeTramiteEnviado>>? guardar;
        lock (_candado)
        {
            // Lo de hace más de un día ya no se espera: no se acumula.
            _enviados.RemoveAll(e => ahora - e.EnviadoUtc > Recuerdo);
            _enviados.Add(new TrabajoDeTramiteEnviado(nombreDelTrabajo, tramite, ahora));
            (paraGuardar, guardar) = ([.. _enviados], _guardar);
        }
        guardar?.Invoke(paraGuardar);
    }

    /// <summary>
    /// El trámite de un trabajo de la cola: si se llama exactamente como uno que mandó esta caja y entró a la cola (<paramref name="enviadoUtc"/>)
    /// poco después de mandarlo, aunque salga de la impresora horas después. Si no, vacío: es una impresión como cualquiera. Lo reconocido se
    /// sigue recordando (un día): si el agente lo vuelve a medir (no lo pudo guardar a la primera), va igual.
    /// </summary>
    public TrabajoDeTramite? De(string? documento, DateTime enviadoUtc)
    {
        if (string.IsNullOrEmpty(documento))
            return null;
        lock (_candado)
        {
            for (var i = _enviados.Count - 1; i >= 0; i--)
            {
                var (nombre, tramite, enviado) = _enviados[i];
                if (string.Equals(nombre, documento, StringComparison.Ordinal) && enviadoUtc >= enviado - Holgura && enviadoUtc <= enviado + Plazo)
                    return tramite;
            }
        }
        return null;
    }
}
