using System.Text.RegularExpressions;

namespace PapeleriaPos.Impresiones;

/// <summary>
/// Un trabajo de impresión de un trámite (docs/tramites.md, «Imprimir sin cobrar dos veces»; 2026-10-31): la caja lo manda a la
/// cola con el folio del trámite en el nombre y nunca con el del cliente. Lo reconoce el servicio al registrarlo:
/// <list type="bullet">
/// <item>«T-0042 · CURP» es la copia que va incluida en el trámite: se cobra con él (el producto CURP en el ticket), no sale en F2
/// «por cobrar» y no lleva precio, solo su costo (<see cref="EstadoCobroImpresion.EnTramite"/>);</item>
/// <item>«T-0042 · CURP · copias extra» son las copias de más, que se cobran como cualquier impresión, en F2;</item>
/// <item>«T-0042.pdf» (el archivo, si salió desde otro programa) también es la del trámite.</item>
/// </list>
/// De ninguno se guarda la miniatura: sería el documento de un cliente (su CURP) guardado en la base. El agente tampoco la manda.
/// </summary>
/// <param name="Folio">El folio del trámite («T-0042»).</param>
/// <param name="CopiasExtra">Las copias de más: se cobran en F2 al precio de la impresión.</param>
public sealed partial record TrabajoDeTramite(string Folio, bool CopiasExtra)
{
    /// <summary>Entre el folio, el trámite y la marca de las copias de más.</summary>
    public const string Separador = " · ";

    /// <summary>Lo que lleva al final el nombre de las copias de más.</summary>
    public const string MarcaCopiasExtra = "copias extra";

    /// <summary>El de la copia incluida: «T-0042 · CURP».</summary>
    public static string Nombre(string folio, string tramite) => $"{folio}{Separador}{tramite}";

    /// <summary>El de las copias de más: «T-0042 · CURP · copias extra».</summary>
    public static string NombreDeCopiasExtra(string folio, string tramite) => $"{folio}{Separador}{tramite}{Separador}{MarcaCopiasExtra}";

    /// <summary>
    /// El trámite de un trabajo por su nombre, o vacío si no es de uno: empieza con el folio («T-» y al menos cuatro números) y sigue
    /// con « · » y el resto, o es el archivo del trámite («T-0042.pdf»). Un documento de un cliente que se llame parecido («T-2026 -
    /// examen.pdf») no lo es: el separador es el punto medio, que nadie teclea.
    /// </summary>
    public static TrabajoDeTramite? De(string? documento)
    {
        if (string.IsNullOrWhiteSpace(documento) || ConFolio().Match(documento.Trim()) is not { Success: true } encontrado)
            return null;
        var resto = encontrado.Groups["resto"].Value.Trim();
        return new TrabajoDeTramite(encontrado.Groups["folio"].Value.ToUpperInvariant(),
            resto.EndsWith(MarcaCopiasExtra, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"^(?<folio>[Tt]-\d{4,})(?:\.[Pp][Dd][Ff]|\s*·\s*(?<resto>.+))?$")]
    private static partial Regex ConFolio();
}
