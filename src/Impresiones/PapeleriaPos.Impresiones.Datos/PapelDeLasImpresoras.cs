using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Avisos;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// Las hojas en las charolas de cada impresora y su aviso en el «!» de la caja (2026-11-28). Las impresoras de la tienda no dicen cuántas
/// hojas les quedan (docs/impresion.md, «Las hojas en la charola»): se anota cuántas se ponen al llenar y se restan las de lo que sale
/// (<see cref="CalculoDelPapel"/>). Los avisos se ponen y se quitan por el puerto (<see cref="IPosParaImpresiones.PonerAvisoAsync"/>), con la
/// clave <c>papel:{impresora}:{charola}</c>, y se guardan aquí.
/// </summary>
public sealed class PapelDeLasImpresoras(DbContext db, IPosParaImpresiones pos)
{
    /// <summary>La acción de los avisos del papel: la caja abre «Anotar el papel» en esa impresora y charola.</summary>
    public const string AccionAnotar = "impresiones.papel";

    private DbSet<LlenadoPapel> Llenados => db.Set<LlenadoPapel>();
    private DbSet<TrabajoImpresion> Trabajos => db.Set<TrabajoImpresion>();
    private DateTime Ahora => pos.Reloj.GetUtcNow().UtcDateTime;

    public static string Prefijo(Impresora impresora) => $"papel:{impresora.Id:N}:";

    public static string Clave(Impresora impresora, int charola) => $"{Prefijo(impresora)}{charola}";

    /// <summary>El último llenado de cada charola de esa impresora.</summary>
    public async Task<Dictionary<int, LlenadoPapel>> UltimosLlenadosAsync(Guid impresoraId, CancellationToken ct = default)
    {
        var todos = await Llenados.AsNoTracking().Where(l => l.ImpresoraId == impresoraId).ToListAsync(ct);
        return todos.GroupBy(l => l.Charola).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.AnotadoUtc).First());
    }

    /// <summary>
    /// Cómo está cada charola de la impresora: lo que se calcula que le queda, lo que se espera imprimir y si toca avisar.
    /// <paramref name="sinPapel"/> y <paramref name="bandejas"/> son lo que dijo la impresora en su última lectura (vacío si no se sabe).
    /// </summary>
    public async Task<IReadOnlyList<EstadoDeLaCharola>> EstadoAsync(Impresora impresora, bool? sinPapel = null, IReadOnlyList<InputTray>? bandejas = null,
        CancellationToken ct = default)
    {
        var ahora = Ahora;
        var llenados = await UltimosLlenadosAsync(impresora.Id, ct);
        var desde = new[] { ahora.AddDays(-CalculoDelPapel.DiasDeHistoria) }.Concat(llenados.Values.Select(l => l.AnotadoUtc)).Min();
        var trabajos = (await Trabajos.AsNoTracking().Where(t => t.ImpresoraId == impresora.Id && t.ImpresoUtc >= desde)
                .Select(t => new { t.ImpresoUtc, t.Tamano, t.Hojas, t.Caras, t.CarasContadas, t.Tipo }).ToListAsync(ct))
            .Select(t => new HojasDeUnTrabajo(t.ImpresoUtc, t.Tamano, CalculoDelPapel.HojasDe(new TrabajoImpresion
            {
                Pc = "", Cola = "", Documento = "", Tamano = t.Tamano, TipoPapel = "", Calidad = "", DetalleJson = "",
                Hojas = t.Hojas, Caras = t.Caras, CarasContadas = t.CarasContadas, Tipo = t.Tipo,
            }))).ToList();
        var charolas = PapelDeImpresoras.CharolasDe(impresora.Perfil);
        var estados = new List<EstadoDeLaCharola>();
        foreach (var charola in charolas)
        {
            var deLaCharola = trabajos.Where(t => CalculoDelPapel.CharolaPara(t.Tamano, llenados) == charola.Numero).ToList();
            var llenado = llenados.GetValueOrDefault(charola.Numero);
            int usadas = llenado is null ? 0 : deLaCharola.Where(t => t.ImpresoUtc > llenado.AnotadoUtc).Sum(t => t.Hojas);
            var uso = CalculoDelPapel.UsoPorHora(deLaCharola, ahora);
            var bandeja = bandejas?.FirstOrDefault(b => b.Index == charola.Numero);
            // «Sin papel» del ST2 no dice cuál charola: es de la principal, salvo que una bandeja del Printer-MIB diga cuál.
            bool sinPapelAqui = bandeja is { CurrentLevel: 0, MaxCapacity: > 0 }
                || (sinPapel == true && charola.Numero == 1 && bandejas?.Any(b => b is { CurrentLevel: 0, MaxCapacity: > 0 }) != true);
            estados.Add(CalculoDelPapel.Evaluar(charola, llenado, usadas, uso, ahora, sinPapelAqui, bandeja is { Reports: true } b2 ? b2.CurrentLevel : null));
        }
        return estados;
    }

    /// <summary>
    /// Pone, cambia o quita los avisos del papel de esa impresora según cómo está cada charola. No guarda (como el puerto): quien llama guarda.
    /// </summary>
    public async Task RevisarAvisosAsync(Impresora impresora, bool? sinPapel = null, IReadOnlyList<InputTray>? bandejas = null, CancellationToken ct = default)
    {
        var vigentes = new List<string>();
        if (impresora.Activa)
        {
            foreach (var estado in await EstadoAsync(impresora, sinPapel, bandejas, ct))
            {
                if (estado.Nivel is not { } nivel)
                    continue;
                var clave = Clave(impresora, estado.Charola.Numero);
                var (titulo, texto) = Textos(impresora, estado);
                await pos.PonerAvisoAsync(impresora.SucursalId, clave, nivel, titulo, texto,
                    new AccionDeAviso(AccionAnotar, "Anotar el papel que pusiste", $"{impresora.Numero}:{estado.Charola.Numero}"), ct: ct);
                vigentes.Add(clave);
            }
        }
        await pos.QuitarAvisosAsync(impresora.SucursalId, Prefijo(impresora), vigentes, ct);
    }

    /// <summary>El título y el texto del aviso de una charola.</summary>
    public static (string Titulo, string Texto) Textos(Impresora impresora, EstadoDeLaCharola e)
    {
        var donde = $"{impresora.Nombre} · {e.Charola.Nombre}";
        if (e.SinPapel)
            return ($"{donde}: sin papel", "La impresora dice que se quedó sin papel. Al llenarla, anota cuántas hojas pusiste.");
        if (e.Quedan is not { } quedan)
            return ($"{donde}: anota el próximo llenado",
                "La impresora no dice cuántas hojas le quedan: si anotas cuántas pones al llenarla, aquí se avisa antes de que se acaben.");
        var cuantas = e.Reportadas ? $"quedan {quedan} hojas" : $"quedan ~{quedan} hojas";
        var porQue = e.Esperadas > 0
            ? $"De aquí a las {e.HastaLocal:HH:mm}{(e.Pico ? ", el final de la hora pico," : "")} se imprimen unas {e.Esperadas}: llénala antes."
            : "Son pocas: llénala cuando puedas.";
        return ($"{donde}: {cuantas}", porQue + (e.Reportadas ? "" : " Se calcula con el último llenado anotado y lo que salió desde entonces."));
    }

    /// <summary>
    /// Anota un llenado: <paramref name="puestas"/> hojas de <paramref name="tamano"/> en esa charola (o «la llené»: lo que le cabe), revisa los
    /// avisos de la impresora y guarda. Regresa el error en español si no se puede (la charola no existe, cero hojas…).
    /// </summary>
    public async Task<(LlenadoPapel? Llenado, string? Error)> AnotarAsync(Impresora impresora, int charola, int puestas, bool llena, string? tamano,
        Guid? usuarioId, CancellationToken ct = default)
    {
        if (PapelDeImpresoras.Charola(impresora.Perfil, charola) is not { } c)
            return (null, $"La {impresora.Nombre} no tiene la charola {charola}.");
        if (!llena && puestas <= 0)
            return (null, "Escribe cuántas hojas pusiste (o marca que la llenaste).");
        if (!llena && puestas > c.Capacidad * 2)
            return (null, $"A la {c.Nombre.ToLowerInvariant()} le caben unas {c.Capacidad} hojas: revisa el número.");
        var tam = string.IsNullOrWhiteSpace(tamano) ? c.TamanoPorOmision.ToString()
            : Enum.TryParse<PaperSize>(tamano, ignoreCase: true, out var t) && t != PaperSize.Otro ? t.ToString() : null;
        if (tam is null)
            return (null, "El tamaño es Carta, Oficio o A4.");
        var antes = (await EstadoAsync(impresora, ct: ct)).First(e => e.Charola.Numero == charola);
        var llenado = new LlenadoPapel
        {
            SucursalId = impresora.SucursalId, ImpresoraId = impresora.Id, Charola = charola, Puestas = llena ? c.Capacidad : puestas,
            Quedaron = CalculoDelPapel.QuedaronAlLlenar(c, puestas, antes.SinPapel ? 0 : antes.Quedan, llena),
            Tamano = tam, AnotadoUtc = Ahora, UsuarioId = usuarioId,
        };
        Llenados.Add(llenado);
        await db.SaveChangesAsync(ct);
        await RevisarAvisosAsync(impresora, ct: ct);
        await db.SaveChangesAsync(ct);
        return (llenado, null);
    }
}
