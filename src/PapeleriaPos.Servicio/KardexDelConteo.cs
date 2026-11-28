using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Lo que eleventa movió de cada producto de un conteo del celular desde que se contó, leído de su kardex en el momento
/// (<see cref="KardexDeEleventa"/>, 2026-11-27): con eso «lo movido desde que se contó» ya no depende de cuándo lo trajo la lectura de
/// cada 5 minutos. Solo en el piloto con eleventa y solo lee. Se quita al dejar eleventa.
/// </summary>
public sealed class KardexDelConteo(OpcionesEleventa opciones)
{
    private readonly ServidorEleventa? _servidor = opciones.Conexion();

    /// <summary>Si hay de dónde leer (la lectura de eleventa prendida y completa). Sin eso, vale lo que anotó la lectura.</summary>
    public bool Disponible => _servidor is not null;

    /// <summary>
    /// De cada variante que viene de eleventa, lo que eleventa movió desde <paramref name="contadoUtc"/> (su hora), sin lo que escribió
    /// el propio POS. Las dadas de alta aquí no vienen. Falla si eleventa no contesta a tiempo: quien llama decide.
    /// </summary>
    public async Task<Dictionary<Guid, MovidoEnEleventa>> LeerAsync(PosDbContext db, IReadOnlyDictionary<Guid, DateTime> contadoUtc, CancellationToken ct)
    {
        var servidor = _servidor ?? throw new InvalidOperationException("La lectura de eleventa no está configurada.");
        var variantes = contadoUtc.Keys.ToList();
        var productoDe = (await db.MapeosImportacion.AsNoTracking()
                .Where(m => m.Origen == SincronizacionEleventa.Origen && m.Entidad == nameof(Variante) && variantes.Contains(m.IdNuevo))
                .Select(m => new { m.IdNuevo, m.IdOrigen }).ToListAsync(ct))
            .Where(m => int.TryParse(m.IdOrigen, out _)).GroupBy(m => m.IdNuevo).ToDictionary(g => g.Key, g => int.Parse(g.First().IdOrigen));
        var contadoLocal = new Dictionary<int, DateTime>();
        foreach (var (variante, producto) in productoDe)
            contadoLocal[producto] = HoraMexico.ALocal(contadoUtc[variante]);
        // Sin cancelarla (FirebirdSinCancelar): si eleventa se queda callado, se deja de esperar.
        var leido = await KardexDeEleventa.LeerAsync(servidor, contadoLocal, FirebirdSinCancelar.Nunca).WaitAsync(ApiConteosDelCelular.EsperaDeLaLectura, ct);
        var movido = KardexDeEleventa.Calcular(leido, await KardexDeEleventa.DelPosAsync(db, leido, ct), contadoLocal.Keys);
        return productoDe.ToDictionary(p => p.Key, p => movido[p.Value]);
    }

    /// <summary>Lo leído, en un texto: si dos lecturas seguidas dan el mismo, eleventa no movió esos productos entre una y otra.</summary>
    public static string Firma(IReadOnlyDictionary<Guid, MovidoEnEleventa> leido) =>
        string.Join(";", leido.OrderBy(l => l.Key).Select(l => $"{l.Key:N}={l.Value.Firma}"));
}
