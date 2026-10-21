using PapeleriaPos.Impresion;

namespace PapeleriaPos.Herramientas.Tienda;

/// <summary>
/// Para el fin de semana en la tienda (docs/pruebas-en-tienda.md, el paso 7 del piloto): qué hace la impresora de tickets
/// que comparten las dos cajas cuando le llegan dos tickets a la vez. Se mandan como los manda la caja
/// (<see cref="ImpresoraRed"/>, al puerto 9100), cada uno por su conexión. Cada ticket dice que es de prueba, de qué PC
/// salió y de qué vuelta: en el papel se ve cuál salió, en qué orden y si alguno se cortó.
/// </summary>
public static class PruebaImpresoraTickets
{
    public sealed record Vuelta(string Nombre, TimeSpan Separacion);

    /// <summary>Primero a la vez; después uno tras otro, casi juntos, como dos cajas que cobran casi al mismo tiempo.</summary>
    public static readonly IReadOnlyList<Vuelta> Vueltas =
    [
        new("a la vez", TimeSpan.Zero),
        new("casi juntos", TimeSpan.FromMilliseconds(300)),
    ];

    /// <param name="Mandado">Cuándo empezó a mandarse, desde el inicio de la vuelta.</param>
    /// <param name="Listo">Cuándo confirmó la impresora que terminó de imprimirlo; null si falló.</param>
    public sealed record Resultado(int Numero, TimeSpan Mandado, TimeSpan? Listo, string? Error);

    /// <summary>Manda <paramref name="cuantos"/> tickets, cada uno por su conexión, y dice qué pasó con cada uno.</summary>
    public static async Task<IReadOnlyList<Resultado>> ProbarAsync(Func<IImpresora> impresora, string pc, Vuelta vuelta, int cuantos,
        TimeProvider reloj, CancellationToken ct = default)
    {
        // Se arman antes de empezar: así solo se mide mandarlos, y «a la vez» salen juntos de verdad.
        var hora = reloj.GetLocalNow();
        var tickets = Enumerable.Range(1, cuantos)
            .Select(numero => TicketEscPos.Generar(Ticket(pc, vuelta, numero, cuantos, hora), abrirCajon: false))
            .ToList();
        var inicio = reloj.GetTimestamp();
        return await Task.WhenAll(tickets.Select((ticket, i) => Task.Run(async () =>
        {
            if (i > 0 && vuelta.Separacion > TimeSpan.Zero)
                await Task.Delay(vuelta.Separacion * i, reloj, ct);
            var mandado = reloj.GetElapsedTime(inicio);
            try
            {
                await impresora().EnviarAsync(ticket, ct);
                return new Resultado(i + 1, mandado, reloj.GetElapsedTime(inicio), null);
            }
            catch (IOException ex)
            {
                return new Resultado(i + 1, mandado, null, ex.Message);
            }
        }, ct)));
    }

    public static IReadOnlyList<Elemento> Ticket(string pc, Vuelta vuelta, int numero, int cuantos, DateTimeOffset hora) =>
    [
        new Renglon("PRUEBA DE LA IMPRESORA", Alineacion.Centro, Negrita: true),
        new Renglon("No es una venta", Alineacion.Centro),
        new Renglon(""),
        new Renglon($"Ticket {numero} de {cuantos}, {vuelta.Nombre}"),
        new Renglon($"Desde {pc}"),
        new Renglon($"Vuelta de las {hora:HH:mm:ss.fff}"),
        new Renglon(""),
        // Del largo de un ticket chico: si la impresora corta o revuelve dos tickets, se ve en los renglones numerados.
        .. Enumerable.Range(1, 10).Select(i => new Renglon($"Renglón {i} de 10 del ticket {numero}")),
        new Renglon(""),
        new Renglon($"Fin del ticket {numero}", Alineacion.Centro, Negrita: true),
    ];

    public static IEnumerable<string> Describir(IReadOnlyList<Resultado> resultados)
    {
        foreach (var r in resultados)
            yield return r.Error is { } error
                ? $"  ✗ Ticket {r.Numero}: se mandó a los {Ms(r.Mandado)} y falló: {error}"
                : $"  ✓ Ticket {r.Numero}: se mandó a los {Ms(r.Mandado)} y la impresora dijo que terminó a los {Ms(r.Listo!.Value)}.";
        yield return "  Revisa el papel: ¿salieron todos completos, hasta «Fin del ticket», sin renglones de otro en medio?";
    }

    private static string Ms(TimeSpan tiempo) => $"{tiempo.TotalMilliseconds:N0} ms";
}
