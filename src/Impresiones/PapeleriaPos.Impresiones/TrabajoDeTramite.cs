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
/// De ninguno se guarda la miniatura: sería el documento de un cliente (su CURP) guardado en la base. El agente tampoco la manda.
/// </summary>
/// <param name="Folio">El folio del trámite («T-0042»).</param>
/// <param name="CopiasExtra">Las copias de más: se cobran en F2 al precio de la impresión.</param>
public sealed record TrabajoDeTramite(string Folio, bool CopiasExtra)
{
    /// <summary>Entre el folio, el trámite y la marca de las copias de más.</summary>
    public const string Separador = " · ";

    /// <summary>Lo que lleva al final el nombre de las copias de más.</summary>
    public const string MarcaCopiasExtra = "copias extra";

    /// <summary>El de la copia incluida: «T-0042 · CURP». Es el nombre del trabajo en la cola de Windows, nunca el del cliente.</summary>
    public static string Nombre(string folio, string tramite) => $"{folio}{Separador}{tramite}";

    /// <summary>El de las copias de más: «T-0042 · CURP · copias extra».</summary>
    public static string NombreDeCopiasExtra(string folio, string tramite) => $"{folio}{Separador}{tramite}{Separador}{MarcaCopiasExtra}";
}

/// <summary>
/// Lo que esta caja mandó a imprimir desde el panel de sus trámites, en memoria (revisión de servicios/curp, 2026-10-31). El agente de
/// impresión de la misma caja (corre en el mismo proceso) reconoce con esto sus trabajos: el nombre exacto que puso el panel y que el
/// trabajo entró a la cola de Windows poco después de mandarlo. Ese trabajo va al servicio ya marcado como del trámite; todo lo demás es
/// una impresión como cualquiera, con su precio y su miniatura, aunque se llame igual. Si la caja se cierra entre que lo manda y el agente
/// lo mide (raro: una hoja tarda segundos), también: se cobra en F2.
/// </summary>
public sealed class TrabajosDeTramites(TimeProvider? reloj = null)
{
    /// <summary>Lo más que puede tardar el trabajo en entrar a la cola de Windows después de mandarlo (dibujar una hoja tarda segundos).</summary>
    public static readonly TimeSpan Plazo = TimeSpan.FromMinutes(5);

    /// <summary>Lo que el reloj de la PC y el de la cola pueden no coincidir (la cola anota la hora en milisegundos de su propio llamado).</summary>
    private static readonly TimeSpan Holgura = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;
    private readonly Lock _candado = new();
    private readonly List<(string Nombre, TrabajoDeTramite Tramite, DateTime EnviadoUtc)> _enviados = [];

    /// <summary>El panel va a mandar este trabajo a la impresora, con este nombre.</summary>
    public void Enviado(string nombreDelTrabajo, TrabajoDeTramite tramite)
    {
        var ahora = _reloj.GetUtcNow().UtcDateTime;
        lock (_candado)
        {
            // Lo de hace más de una hora ya no puede llegar: no se acumula en un día de pico.
            _enviados.RemoveAll(e => ahora - e.EnviadoUtc > TimeSpan.FromHours(1));
            _enviados.Add((nombreDelTrabajo, tramite, ahora));
        }
    }

    /// <summary>
    /// El trámite de un trabajo de la cola: si se llama exactamente como uno que mandó esta caja y entró a la cola (<paramref name="enviadoUtc"/>)
    /// poco después de mandarlo. Si no, vacío: es una impresión como cualquiera.
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
