using System.Collections.Frozen;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>
/// De qué producto es un código escaneado, igual en el servicio y en la copia de cada caja: sin importar mayúsculas. Si hay
/// códigos que solo cambian en mayúsculas («ZZ-CASO-01» y «zz-caso-01», distintos en la base), se lleva el escaneo el que es
/// igual tal cual; si ninguno lo es, el del producto más antiguo (su Id va por fecha). No depende del orden en que vengan los
/// códigos: la copia de una caja puesta al día (lo que cambió va al final de su lista) escanea igual que una recién bajada y
/// que el servicio (entrega 5, partes 2 y 3). Antes ganaba el primero de la lista, y el orden de la copia puesta al día no es
/// el de la base.
/// </summary>
public sealed class CodigosDeEscaneo
{
    /// <summary>Cada código sin importar mayúsculas, con el que se lleva el escaneo.</summary>
    private readonly FrozenDictionary<string, CodigoLocalDto> _sinMayusculas;

    /// <summary>Solo los que tienen otro que cambia en mayúsculas: cada uno tal cual.</summary>
    private readonly FrozenDictionary<string, CodigoLocalDto> _talCual;

    public CodigosDeEscaneo(IEnumerable<CodigoLocalDto> codigos)
    {
        var sinMayusculas = new Dictionary<string, CodigoLocalDto>(StringComparer.OrdinalIgnoreCase);
        var talCual = new Dictionary<string, CodigoLocalDto>(StringComparer.Ordinal);
        foreach (var grupo in codigos.GroupBy(c => c.Codigo, StringComparer.OrdinalIgnoreCase))
        {
            var ordenados = grupo.OrderBy(c => c.VarianteId).ThenBy(c => c.Codigo, StringComparer.Ordinal).ThenBy(c => c.PresentacionId).ToList();
            sinMayusculas[grupo.Key] = ordenados[0];
            if (ordenados.Select(c => c.Codigo).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                foreach (var c in ordenados)
                    talCual.TryAdd(c.Codigo, c);
            }
        }
        _sinMayusculas = sinMayusculas.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _talCual = talCual.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static CodigosDeEscaneo Ninguno { get; } = new([]);

    /// <summary>Cuántos códigos distintos (sin importar mayúsculas).</summary>
    public int Count => _sinMayusculas.Count;

    /// <summary>Si alguno se escribe así, sin importar mayúsculas.</summary>
    public bool Contiene(string codigo) => _sinMayusculas.ContainsKey(codigo);

    /// <summary>El código que se lleva el escaneo de <paramref name="codigo"/> (ya sin espacios en las orillas); vacío si no hay.</summary>
    public CodigoLocalDto? Buscar(string codigo) =>
        _talCual.TryGetValue(codigo, out var igual) ? igual : _sinMayusculas.GetValueOrDefault(codigo);
}
