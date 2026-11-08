using System.Globalization;
using System.Text.Json;

namespace PapeleriaPos.Impresiones;

/// <summary>Una cola de Windows que vigila el agente de una caja, y de qué impresora de la sucursal es (2026-11-03).</summary>
/// <param name="Cola">El nombre de la cola como lo dice Windows (<c>Get-Printer</c>).</param>
/// <param name="Impresora">El número de su impresora en la sucursal; vacío: la primera (la de antes).</param>
public sealed record ColaVigilada(string Cola, int? Impresora = null);

/// <summary>
/// Las colas que vigila el agente de una caja, de su <c>caja.json</c> (2026-11-03): <c>ColaImpresion</c>, la de siempre (una cola, de la
/// primera impresora), y <c>ColasImpresion</c>, varias, cada una con el número de su impresora:
/// <code>
/// "ColasImpresion": [ { "Cola": "EPSON WF-6590 Series", "Impresora": 1 }, { "Cola": "EPSON L5590 Series", "Impresora": 3 } ]
/// </code>
/// Se lee aparte y sin tronar, como el lector (<c>AjustesLector</c>): un error de dedo no impide abrir la caja; lo que no se entiende se
/// ignora y se dice en el registro de la caja. La primera de la lista es <c>ColaImpresion</c> si está (la que usan los trámites).
/// </summary>
public static class ColasDeLaCaja
{
    public static (IReadOnlyList<ColaVigilada> Colas, IReadOnlyList<string> Avisos) Leer(string? colaImpresion, JsonElement? colasImpresion)
    {
        var avisos = new List<string>();
        var deLaLista = new List<ColaVigilada>();
        if (colasImpresion is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } lista)
        {
            if (lista.ValueKind != JsonValueKind.Array)
            {
                avisos.Add("«ColasImpresion» en caja.json no es una lista ([ { \"Cola\": …, \"Impresora\": … } ]): se ignora.");
            }
            else
            {
                var i = 0;
                foreach (var elemento in lista.EnumerateArray())
                {
                    i++;
                    if (Cola(elemento, i, avisos) is { } cola)
                        deLaLista.Add(cola);
                }
            }
        }

        var colas = new List<ColaVigilada>();
        var principal = string.IsNullOrWhiteSpace(colaImpresion) ? null : colaImpresion.Trim();
        if (principal is not null)
            // Como la escribe ColaImpresion; si también está en la lista, con su número (la lista lo dice); si no, de la primera, como siempre.
            colas.Add(new ColaVigilada(principal, deLaLista.FirstOrDefault(c => Igual(c.Cola, principal))?.Impresora));
        foreach (var cola in deLaLista)
        {
            if (colas.Any(c => Igual(c.Cola, cola.Cola)))
            {
                if (!Igual(cola.Cola, principal))
                    avisos.Add($"La cola «{cola.Cola}» está dos veces en «ColasImpresion»: se vigila una vez, con la primera.");
                continue;
            }
            colas.Add(cola);
        }
        return (colas, avisos);
    }

    private static ColaVigilada? Cola(JsonElement elemento, int i, List<string> avisos)
    {
        if (elemento.ValueKind != JsonValueKind.Object)
        {
            avisos.Add($"«ColasImpresion», renglón {i}: va {{ \"Cola\": \"…\", \"Impresora\": 2 }}; se ignora.");
            return null;
        }
        string? nombre = null;
        JsonElement? numero = null;
        foreach (var propiedad in elemento.EnumerateObject())
        {
            if (string.Equals(propiedad.Name, "Cola", StringComparison.OrdinalIgnoreCase) && propiedad.Value.ValueKind == JsonValueKind.String)
                nombre = propiedad.Value.GetString();
            else if (string.Equals(propiedad.Name, "Impresora", StringComparison.OrdinalIgnoreCase))
                numero = propiedad.Value;
        }
        if (string.IsNullOrWhiteSpace(nombre))
        {
            avisos.Add($"«ColasImpresion», renglón {i}: falta el nombre de la cola («Cola»); se ignora.");
            return null;
        }
        nombre = nombre.Trim();
        int? impresora = null;
        if (numero is { ValueKind: not JsonValueKind.Null } n)
        {
            if (n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var entero) && entero > 0)
                impresora = entero;
            else if (n.ValueKind == JsonValueKind.String && int.TryParse(n.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var escrito)
                     && escrito > 0)
                impresora = escrito;
            else
                avisos.Add($"«ColasImpresion», «{nombre}»: «Impresora» es el número de la impresora en la sucursal (1, 2, 3…); se anota en la primera.");
        }
        return new ColaVigilada(nombre, impresora);
    }

    private static bool Igual(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
