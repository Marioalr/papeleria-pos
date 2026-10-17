using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Los plazos de la venta (entrega 5, parte 2, revisión) son solo para lo que el cajero espera: lo que se hace después en línea
/// (la cuenta del turno) lleva los suyos, y el corte Z no lleva el plazo corto. Si la respuesta del corte Z se pierde después de
/// que la caja principal lo guardó, el que hace la caja sin conexión es el mismo: al subir no choca con el turno ya cerrado.
/// </summary>
public sealed class PlazosDeLaVentaTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-plazos-{Guid.NewGuid():N}.db");

    public void Dispose() => BasesDePrueba.Borrar(_baseLocal);

    /// <summary>
    /// El turno se lee con el plazo de la lectura; la cuenta del turno que se pide después (la base de lo que la caja suma por su
    /// lado) ya no lo lleva: sale sin los plazos de la venta.
    /// </summary>
    [Fact]
    public async Task La_cuenta_del_turno_que_se_pide_despues_del_turno_no_lleva_el_plazo_de_la_lectura()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        v.Cliente.PlazoLectura = TimeSpan.FromSeconds(30);

        var turno = await v.Cliente.TurnoAsync(v.Caja.Id);

        Assert.NotNull(turno);
        Assert.Equal([true], v.Red.ConPlazos($"/api/cajas/{v.Caja.Id}/turno").TakeLast(1));
        Assert.Equal([false], v.Red.ConPlazos("/resumen"));
        Assert.True(v.Red.Respondio("/resumen"));
    }

    /// <summary>
    /// El corte Z tarda en la caja principal más que el plazo de un cobro: la caja lo espera (es el cierre del turno, no un
    /// cliente en la fila) y no lo hace por su lado.
    /// </summary>
    [Fact]
    public async Task El_corte_Z_no_lleva_el_plazo_corto_de_la_venta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        var encargado = await v.Cliente.EntrarAsync("encargado", "2222");
        v.Cliente.PlazoEscritura = TimeSpan.FromMilliseconds(100);
        v.Red.RetrasoDe = ruta => ruta.EndsWith("/corte-z", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(600) : TimeSpan.Zero;

        var corte = await v.Cliente.CorteZAsync(turno.Id, new SolicitudCorteZ(encargado.UsuarioId, 50000));

        Assert.Equal([false], v.Red.ConPlazos("/corte-z"));
        Assert.False(corte.EnContingencia);
        Assert.Equal((0, 0), (v.Contingencia.Pendientes, v.Contingencia.ConError));
        Assert.Equal(EstadoRed.Buena, v.Contingencia.EstadoDeLaRed);
    }

    /// <summary>
    /// La caja principal guarda el corte Z, pero la respuesta se pierde: la caja lo hace con su cuenta (con el mismo Id) y, al
    /// volver, sube sin quedarse con error; queda un corte del turno.
    /// </summary>
    [Fact]
    public async Task El_corte_Z_cuya_respuesta_se_pierde_sube_sin_quedarse_con_error()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        var encargado = await v.Cliente.EntrarAsync("encargado", "2222");
        v.Red.PierdeRespuesta = ruta => ruta.EndsWith("/corte-z", StringComparison.Ordinal);

        var corte = await v.Cliente.CorteZAsync(turno.Id, new SolicitudCorteZ(encargado.UsuarioId, 50000));
        Assert.True(corte.EnContingencia);
        Assert.Equal(1, v.Contingencia.Pendientes);
        v.Red.PierdeRespuesta = null;
        await v.Contingencia.LatidoAsync();

        Assert.Equal((0, 0), (v.Contingencia.Pendientes, v.Contingencia.ConError));
        await using var db = servicio.Base();
        Assert.Equal(1, await db.Cortes.CountAsync(c => c.TurnoId == turno.Id && c.Tipo == TipoCorte.Z));
    }
}
