using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El cobro con plazo (entrega 5, parte 2): si la respuesta no llega a tiempo, el cobro se hace con la copia y va a la bandeja
/// con el mismo Id. Puede que la caja principal sí lo haya guardado (se perdió solo la respuesta): al subir, queda una venta,
/// con el folio que se imprimió, una sola vez en el turno y una sola vez hacia eleventa (el piloto). La pantalla enseña un
/// solo ticket.
/// </summary>
public sealed class CobroConPlazoTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-plazo-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    /// <summary>El piloto, con un eleventa que no contesta (nadie escucha en ese puerto): lo que va allá espera en su bandeja.</summary>
    private static readonly (string, string)[] Piloto =
    [
        ("Eleventa:Escribir", "true"), ("Eleventa:Servidor", "127.0.0.1"), ("Eleventa:Puerto", "1"), ("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"),
        ("Eleventa:Clave", "masterkey"), ("Eleventa:Caja", "4"), ("Eleventa:Cajero", "7"), ("Eleventa:SegundosEntreEnvios", "3600"),
    ];

    [Fact]
    public async Task El_cobro_que_no_llega_a_tiempo_va_a_la_bandeja_con_el_mismo_Id_y_queda_una_venta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto);
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        var antes = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<CorteDto>($"api/turnos/{turno.Id}/resumen"))!;
        var (producto, _) = v.DosDeLaCopia();

        // El cobro llega a la caja principal y se guarda, pero su respuesta no llega antes del plazo (aquí, 300 ms; en la caja
        // de verdad, 2.5 s). Solo para el cobro: lo de antes de la prueba no depende de lo que tarda la PC.
        v.Cliente.PlazoEscritura = TimeSpan.FromMilliseconds(300);
        v.Red.CobroRetenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await v.EscanearAsync(producto.Codigo!);
        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));
        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro con la copia");
        var folio = Regex.Match(await v.Pantalla.TextoAsync(), @"Ticket\s+([A-Z]+-\d+)").Groups[1].Value;

        // Se hizo con la copia: a la bandeja, y la red quedó lenta (el cajero ya esperó; lo siguiente no lo espera).
        Assert.Equal(1, v.Red.Cuantas("/api/ventas"));
        Assert.Equal(EstadoRed.Lenta, v.Contingencia.EstadoDeLaRed);
        Assert.Equal(1, v.Contingencia.Pendientes);
        var corteEnLaCaja = await v.Cliente.CorteXAsync(turno.Id, v.Cajero.UsuarioId);

        // La respuesta llega tarde: ya nadie la espera. El latido sube la bandeja con el mismo Id.
        v.Red.CobroRetenido.SetResult();
        await v.Contingencia.LatidoAsync();

        Assert.Equal((0, 0), (v.Contingencia.Pendientes, v.Contingencia.ConError));
        await using var db = servicio.Base();
        var delTurno = await db.Ventas.AsNoTracking().Where(x => x.TurnoId == turno.Id).ToListAsync();
        Assert.True(delTurno.Count(x => x.FolioTexto == folio) == 1,
            $"En pantalla «{folio}»; en el turno: {string.Join(", ", delTurno.Select(x => x.FolioTexto))}. Pantalla: {await v.Pantalla.TextoAsync()}");
        var venta = delTurno.Single(x => x.FolioTexto == folio);
        Assert.Equal(1, await db.Ventas.CountAsync(x => x.Id == venta.Id));
        Assert.Equal(1, await db.EnviosAEleventa.CountAsync(e => e.VentaId == venta.Id));
        var despues = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<CorteDto>($"api/turnos/{turno.Id}/resumen"))!;
        Assert.Equal(antes.Ventas + 1, despues.Ventas);
        Assert.Equal(corteEnLaCaja.Ventas, despues.Ventas);
        Assert.Equal(corteEnLaCaja.EsperadoCentavos, despues.EsperadoCentavos);
        // La pantalla sigue con un solo ticket, el de ese folio.
        var texto = await v.Pantalla.TextoAsync();
        Assert.Single(Regex.Matches(texto, @"Ticket\s+[A-Z]+-\d+"));
        Assert.Equal(folio, Regex.Match(texto, @"Ticket\s+([A-Z]+-\d+)").Groups[1].Value);
        Assert.Empty(v.Pantalla.Errores);
    }
}
