using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Cuando algo truena con la venta en curso: el servicio contesta 500 al cobrar (sigue vivo pero no puede guardar: disco lleno, su
/// base dañada) y la base de esta caja no deja escribir. La cajera siempre puede cobrar o se entera de por qué no.
/// </summary>
public sealed class ServicioQueNoPuedeGuardarTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-truena-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static async Task<SolicitudCobro> CobroAsync(VentaConCopia v)
    {
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        var (a, _) = v.DosDeLaCopia();
        return new SolicitudCobro(v.Caja.Id, turno.Id, v.Cajero.UsuarioId, [new LineaSolicitada(a.VarianteId, a.Presentacion.Id, 1000)],
            [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)], Guid.NewGuid());
    }

    private void Romper(string sql)
    {
        using var conexion = new SqliteConnection($"Data Source={_baseLocal}");
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.ExecuteNonQuery();
    }

    /// <summary>Un 500 al cobrar: se cobra con la copia, con el mismo Id. Al poder guardar otra vez, sube una vez.</summary>
    [Fact]
    public async Task Un_500_al_cobrar_cae_a_la_copia_y_sube_despues()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var solicitud = await CobroAsync(v);
        v.Red.Truena = ruta => ruta == "/api/ventas";

        var ticket = await v.Cliente.CobrarAsync(solicitud);

        Assert.True(ticket.EnContingencia);
        Assert.Equal(EstadoRed.Lenta, v.Contingencia.EstadoDeLaRed);
        Assert.Equal(1, v.Contingencia.Pendientes);
        v.Red.Truena = null;
        for (var i = 0; i < 5 && v.Contingencia.Pendientes > 0; i++)
        {
            await v.Contingencia.LatidoAsync();
            await v.Contingencia.SubirAsync();
        }
        Assert.Equal((0, 0), (v.Contingencia.Pendientes, v.Contingencia.ConError));
        await using var db = servicio.Base();
        Assert.Equal(1, await db.Ventas.CountAsync(x => x.Id == solicitud.VentaId));
    }

    /// <summary>Un 500 que llegó después de guardar: la copia cobra el mismo Id y al subir no se duplica ni queda con error.</summary>
    [Fact]
    public async Task Un_500_despues_de_guardar_no_duplica_la_venta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var solicitud = await CobroAsync(v);
        v.Red.TruenaDespues = ruta => ruta == "/api/ventas";

        await v.Cliente.CobrarAsync(solicitud);

        v.Red.TruenaDespues = null;
        for (var i = 0; i < 5 && v.Contingencia.Pendientes > 0; i++)
        {
            await v.Contingencia.LatidoAsync();
            await v.Contingencia.SubirAsync();
        }
        Assert.Equal((0, 0), (v.Contingencia.Pendientes, v.Contingencia.ConError));
        await using var db = servicio.Base();
        Assert.Equal(1, await db.Ventas.CountAsync(x => x.Id == solicitud.VentaId));
    }

    /// <summary>Lo que no tiene copia sigue con su error, ya en palabras: nada de códigos HTTP en pantalla.</summary>
    [Fact]
    public async Task Sin_copia_un_500_lo_dice_sin_codigos()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        v.Red.Truena = ruta => ruta.Contains("/turnos", StringComparison.Ordinal);

        var error = await Assert.ThrowsAsync<ErrorCaja>(() => new Transporte(new HttpClient(v.Red), new ConexionCaja { Servicio = new Uri("http://localhost/") })
            .GetAsync<object>("api/turnos/x"));

        Assert.Equal(500, error.Estado);
        Assert.DoesNotContain("500", error.Message);
        Assert.Contains("avísale al dueño", error.Message);
    }

    /// <summary>
    /// La base de esta caja no deja anotar lo que pasó en línea: la venta ya quedó en el servicio. La pantalla cobra como siempre (ticket
    /// y cambio) y la franja lo dice; antes, F12 parecía no hacer nada y la cajera la volvía a pasar en eleventa.
    /// </summary>
    [Fact]
    public async Task Si_la_base_local_no_deja_anotar_el_cobro_en_linea_sigue_y_se_avisa()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        Romper("DROP TABLE Diario");

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro en la pantalla");
        Assert.Contains("no deja guardar", v.Contingencia.AvisoBaseLocal);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Sin conexión y sin poder guardar: no se cobra, y la cajera lee qué hacer.</summary>
    [Fact]
    public async Task Sin_conexion_si_la_base_local_no_deja_guardar_se_dice()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var solicitud = await CobroAsync(v);
        v.Red.Caida = true;
        await v.Contingencia.LatidoAsync();
        Romper("DROP TABLE Bandeja");

        var error = await Assert.ThrowsAsync<ErrorCaja>(() => v.Cliente.CobrarAsync(solicitud));

        Assert.Contains("eleventa", error.Message);
    }

    /// <summary>
    /// Con la red lenta (o el servicio de esta PC ocupado un momento), un ticket con impresiones se intenta con el servicio: con la copia
    /// no se puede, y decir que no sin preguntar detenía a la cajera sin motivo.
    /// </summary>
    [Fact]
    public async Task Con_la_red_lenta_las_impresiones_se_intentan_en_linea()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var solicitud = await CobroAsync(v) with { Impresiones = [Guid.NewGuid()] };
        v.Contingencia.MarcarLenta();
        var antes = v.Red.Cuantas("/api/ventas");

        var error = await Assert.ThrowsAsync<ErrorCaja>(() => v.Cliente.CobrarAsync(solicitud));

        Assert.Equal(antes + 1, v.Red.Cuantas("/api/ventas"));
        Assert.DoesNotContain("se cobran con conexión", error.Message);
    }

    /// <summary>Con el servicio en la misma PC, no hay red que dude: el primer latido que contesta la saca de lenta.</summary>
    [Fact]
    public void Con_el_servicio_en_esta_pc_un_latido_basta_para_volver()
    {
        var red = new CalidadDeLaRed(ReglasDeLaRed.MismaPc, TimeProvider.System);

        red.PlazoVencido();
        Assert.Equal(EstadoRed.Lenta, red.Estado);
        red.Contesto(TimeSpan.FromSeconds(1));

        Assert.Equal(EstadoRed.Buena, red.Estado);
    }
}
