using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// «Respaldar ahora» en segundo plano (revisión del PR #133): contesta enseguida, ya empezado, y la pantalla pregunta cómo va; con
/// una base grande, esperarlo pasaba de los 15 s de la caja, que lo daba por fallido aunque la copia saliera, y al volver a
/// pedirlo hacía otra completa. Mientras uno va en camino no empieza otro. Si la copia sale pero la limpieza de los viejos no, el
/// respaldo está hecho y lo de la limpieza se dice aparte. Lo que se espera tiene su límite: no depende de lo que tarda la PC.
/// </summary>
public sealed class RespaldarAhoraEnSegundoPlanoTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"respaldar-ahora-{Guid.NewGuid():N}");

    public void Dispose() => ArchivosDePrueba.BorrarCarpeta(_carpeta);

    private string Respaldos => Path.Combine(_carpeta, "respaldos");

    /// <summary>Una base chiquita con una tabla y un renglón.</summary>
    private string Base()
    {
        Directory.CreateDirectory(_carpeta);
        var cadena = $"Data Source={Path.Combine(_carpeta, "pos.db")};Pooling=False";
        using var conexion = new SqliteConnection(cadena);
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = "CREATE TABLE Prueba (Texto TEXT); INSERT INTO Prueba VALUES ('hola');";
        comando.ExecuteNonQuery();
        return cadena;
    }

    private static async Task<RespaldoAhoraDto> TerminadoAsync(ServicioRespaldos respaldos)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            if (respaldos.EstadoAhora is { Haciendo: false } terminado)
                return terminado;
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException("No terminó el respaldo.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Mientras_uno_va_en_camino_no_empieza_otro()
    {
        var detenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var respaldos = new ServicioRespaldos(Base(), Respaldos, 30, NullLogger<ServicioRespaldos>.Instance) { AntesDeCopiarAhora = () => detenido.Task };
        var anotados = new List<RespaldoAhoraDto>();

        var primero = respaldos.IniciarAhora(t => { lock (anotados) anotados.Add(t); return Task.CompletedTask; });
        var segundo = respaldos.IniciarAhora(t => { lock (anotados) anotados.Add(t); return Task.CompletedTask; });

        Assert.True(primero.Haciendo);
        Assert.Same(primero, segundo);
        detenido.SetResult();
        var terminado = await TerminadoAsync(respaldos);

        Assert.Null(terminado.Fallo);
        Assert.Equal(primero.DesdeLocal, terminado.DesdeLocal);
        Assert.Single(Directory.GetFiles(Respaldos, "pos-*-ahora-*.db"));
        Assert.Equal(terminado, Assert.Single(anotados));
        // Ya terminado, sí se puede pedir otro.
        Assert.NotSame(primero, respaldos.IniciarAhora());
        await TerminadoAsync(respaldos);
    }

    [Fact]
    public async Task Si_la_limpieza_falla_el_respaldo_esta_hecho_y_se_dice_aparte()
    {
        var respaldos = new ServicioRespaldos(Base(), Respaldos, 30, NullLogger<ServicioRespaldos>.Instance)
        {
            Limpiar = (_, _, _) => throw new IOException("El archivo pos-2026-09-01.db está ocupado."),
        };

        respaldos.IniciarAhora();
        var terminado = await TerminadoAsync(respaldos);

        Assert.Null(terminado.Fallo);
        Assert.True(File.Exists(terminado.Hecho!.Ruta));
        Assert.Equal("No se pudieron borrar los respaldos de hace más de 30 días: El archivo pos-2026-09-01.db está ocupado.", terminado.Hecho.AvisoLimpieza);
    }

    [Fact]
    public async Task Una_copia_que_falla_lo_dice_no_deja_temporal_y_se_puede_pedir_otra()
    {
        Directory.CreateDirectory(_carpeta);
        var origen = Path.Combine(_carpeta, "no-es-base.sqlite");
        File.WriteAllBytes(origen, Enumerable.Repeat((byte)0x42, 8192).ToArray());
        var respaldos = new ServicioRespaldos($"Data Source={origen};Pooling=False", Respaldos, 30, NullLogger<ServicioRespaldos>.Instance);

        respaldos.IniciarAhora();
        var terminado = await TerminadoAsync(respaldos);

        Assert.Null(terminado.Hecho);
        Assert.StartsWith($"No se pudo hacer el respaldo en {Respaldos}: ", terminado.Fallo);
        Assert.Empty(Directory.GetFiles(Respaldos));
        Assert.True(respaldos.IniciarAhora().Haciendo);
        await TerminadoAsync(respaldos);
    }
}
