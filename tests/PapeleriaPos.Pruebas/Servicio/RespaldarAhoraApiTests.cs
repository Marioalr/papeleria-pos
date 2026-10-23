using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// «Respaldar ahora» (Configuración › Respaldos, 2026-10-22): quien tiene «Configuración de la tienda» hace una copia de la base
/// en ese momento, con el mismo ServicioRespaldos de la diaria y la del corte Z, y sabe dónde quedó y cuánto mide. La copia es
/// la base completa, también lo que sigue en el -wal. Sin el permiso, o con los respaldos apagados, no. Cada prueba lleva su
/// servicio, con su carpeta de respaldos y sin la copia diaria de fondo: lo que se revisa no depende de cuándo corre esa.
/// </summary>
public sealed class RespaldarAhoraApiTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"respaldos-prueba-{Guid.NewGuid():N}");

    public void Dispose() => ArchivosDePrueba.BorrarCarpeta(_carpeta);

    private static void SinLaDiaria(IServiceCollection s) =>
        s.Remove(s.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RespaldoDiario)));

    private Task<ServicioDePrueba> ConRespaldosAsync() =>
        ServicioDePrueba.CrearAsync(SinLaDiaria, ("Respaldos:Activo", "true"), ("Respaldos:Carpeta", _carpeta));

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Pregunta cómo va «Respaldar ahora» hasta que termina (con su límite: no depende de lo que tarda la PC).</summary>
    private static async Task<RespaldosDto> TerminadoAsync(HttpClient http, string url)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var estado = await Leer<RespaldosDto>(await http.GetAsync(url));
            if (estado.Ahora is { Haciendo: false })
                return estado;
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException("No terminó el respaldo.");
            await Task.Delay(20);
        }
    }

    private static async Task<(HttpClient Http, CajaDto Caja, Func<string, string, Task<SesionDto>> Entrar)> AbrirAsync(ServicioDePrueba s)
    {
        var http = s.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        return (http, caja, async (usuario, pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin))));
    }

    [Fact]
    public async Task El_dueno_respalda_ahora_la_base_completa_y_sabe_donde_quedo()
    {
        await using var servicio = await ConRespaldosAsync();
        var (http, caja, entrar) = await AbrirAsync(servicio);
        var (dueno, cajero) = (await entrar("dueno", "3333"), await entrar("cajero", "1111"));
        var url = $"api/cajas/{caja.Id}/configuracion/respaldos";

        var antes = await Leer<RespaldosDto>(await http.GetAsync($"{url}?usuarioId={dueno.UsuarioId}"));
        Assert.Equal((_carpeta, 30), (antes.Carpeta, antes.Dias));
        Assert.Null(antes.Ultimo);
        Assert.Null(antes.NoSeHacenPorque);

        // Lo último que se hizo (una entrada de efectivo) todavía está en el -wal: la copia también la lleva.
        var abierto = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = abierto.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(abierto);
        var entrada = Guid.NewGuid();
        await Leer<ComprobanteMovimientoDto>(await http.PostAsJsonAsync($"api/turnos/{turno.Id}/movimientos",
            new SolicitudMovimiento(TipoMovimientoCaja.Entrada, 12300, "Antes del respaldo", cajero.UsuarioId, entrada)));

        // Contesta enseguida, ya empezado; cómo va se pregunta hasta que termina.
        var pedido = await Leer<RespaldosDto>(await http.PostAsJsonAsync(url, new SolicitudRespaldo(dueno.UsuarioId)));
        Assert.NotNull(pedido.Ahora);
        var terminado = await TerminadoAsync(http, $"{url}?usuarioId={dueno.UsuarioId}");
        Assert.Null(terminado.Ahora!.Fallo);
        var hecho = terminado.Ahora.Hecho!;

        Assert.Equal(_carpeta, Path.GetDirectoryName(hecho.Ruta));
        Assert.Null(hecho.AvisoLimpieza);
        Assert.Matches(@"^pos-\d{4}-\d{2}-\d{2}-ahora-\d{4}\.db$", Path.GetFileName(hecho.Ruta));
        Assert.Equal(new FileInfo(hecho.Ruta).Length, hecho.Bytes);
        await using (var copia = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = hecho.Ruta, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await copia.OpenAsync();
            await using var comando = copia.CreateCommand();
            comando.CommandText = "SELECT \"Motivo\" FROM \"MovimientosCaja\" WHERE \"Id\" = $id";
            comando.Parameters.AddWithValue("$id", entrada.ToString().ToUpperInvariant());
            Assert.Equal("Antes del respaldo", await comando.ExecuteScalarAsync());
        }

        // «Último respaldo» ya es ese, y queda en la bitácora (antes de que el estado diga que terminó).
        Assert.Equal((hecho.Ruta, hecho.Bytes), (terminado.Ultimo!.Ruta, terminado.Ultimo.Bytes));
        Assert.Empty(Directory.GetFiles(_carpeta, "*.tmp*"));
        await using var db = servicio.Base();
        var bitacora = await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.ConfigurarTienda && b.Detalle.StartsWith("Respaldó la base"))
            .Select(b => new { b.UsuarioId, b.Detalle }).SingleAsync();
        Assert.Equal(((Guid?)dueno.UsuarioId, $"Respaldó la base: {Path.GetFileName(hecho.Ruta)} ({hecho.Tamano})"), (bitacora.UsuarioId, bitacora.Detalle));
    }

    /// <summary>Sin «Configuración de la tienda» no se ve ni se respalda (el encargado y el cajero de prueba no la tienen).</summary>
    [Fact]
    public async Task Sin_el_permiso_no_se_respalda()
    {
        await using var servicio = await ConRespaldosAsync();
        var (http, caja, entrar) = await AbrirAsync(servicio);
        var url = $"api/cajas/{caja.Id}/configuracion/respaldos";

        foreach (var (usuario, pin) in new[] { ("encargado", "2222"), ("cajero", "1111") })
        {
            var sesion = await entrar(usuario, pin);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync(url, new SolicitudRespaldo(sesion.UsuarioId))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"{url}?usuarioId={sesion.UsuarioId}")).StatusCode);
        }
        Assert.False(Directory.Exists(_carpeta) && Directory.EnumerateFiles(_carpeta, "*ahora*").Any());
    }

    /// <summary>Con los respaldos apagados (como en el servicio de las pruebas), lo dice y no hace nada.</summary>
    [Fact]
    public async Task Con_los_respaldos_apagados_lo_dice()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(("Respaldos:Carpeta", _carpeta));
        var (http, caja, entrar) = await AbrirAsync(servicio);
        var dueno = await entrar("dueno", "3333");
        var url = $"api/cajas/{caja.Id}/configuracion/respaldos";

        var respuesta = await http.PostAsJsonAsync(url, new SolicitudRespaldo(dueno.UsuarioId));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        const string Apagados = "En esta caja principal los respaldos están apagados (Respaldos:Activo).";
        Assert.Equal($"No se hizo el respaldo. {Apagados}", (await respuesta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal(Apagados, (await Leer<RespaldosDto>(await http.GetAsync($"{url}?usuarioId={dueno.UsuarioId}"))).NoSeHacenPorque);
        Assert.False(Directory.Exists(_carpeta) && Directory.EnumerateFiles(_carpeta, "*ahora*").Any());
    }
}
