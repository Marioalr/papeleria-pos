using Microsoft.Data.Sqlite;
using PapeleriaPos.Pruebas.Servicio;
using Xunit.Abstractions;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Pintar menos en Blazor (entrega 5, parte 3, docs/propuesta-interfaz.md, «Rendimiento»): con un ticket de 20 renglones, lo
/// que se repinta al escanear uno más y al escanear otra vez uno que ya está, contado en lo que la pantalla le manda a la
/// página. No depende de lo que tarda la PC.
/// <para>
/// Antes (2026-10-16, con 20 renglones), cada escaneo pintaba dos veces toda la pantalla de venta (con el estado de arriba y
/// lo de las ventanas), con 449 cambios para la página y 134 manejadores de eventos cambiados por otros (los − y + de cada
/// renglón, en cada pintado). Ahora: una vez la pantalla, el renglón nuevo y el que estaba elegido, con unos 40 cambios y 5
/// manejadores (los botones del renglón que deja de estar elegido).
/// </para>
/// </summary>
public sealed class PintarMenosTests(ServicioDePrueba servicio, ITestOutputHelper salida) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-pintar-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    [Fact]
    public async Task Escanear_uno_mas_no_repinta_los_demas_renglones()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var productos = v.DeLaCopia();
        Assert.True(productos.Count >= 22, $"La copia solo trae {productos.Count} productos por pieza.");
        foreach (var p in productos.Take(20))
            await v.EscanearAsync(p.Codigo!);
        Assert.Equal(20, (await VentaConCopia.RenglonesAsync(v.Pantalla)).Count);

        v.Pantalla.ContarDesdeAhora();
        await v.EscanearAsync(productos[20].Codigo!);
        var uno = v.Pantalla.Contados();

        v.Pantalla.ContarDesdeAhora();
        await v.EscanearAsync(productos[5].Codigo!);
        var otraVez = v.Pantalla.Contados();

        salida.WriteLine($"Uno más, con 20 renglones: {uno}.");
        salida.WriteLine($"Otra vez uno que ya está: {otraVez}.");
        Assert.Equal(21, (await VentaConCopia.RenglonesAsync(v.Pantalla)).Count);
        Assert.StartsWith($"2 {productos[5].Descripcion}", (await VentaConCopia.RenglonesAsync(v.Pantalla))[5]);
        foreach (var dibujos in new[] { uno, otraVez })
        {
            // Una sola vez la pantalla, y de los renglones solo el que cambió y el que dejó de estar elegido.
            Assert.Equal(1, dibujos.De("PantallaVenta"));
            Assert.Equal(2, dibujos.De("FilaTicket"));
            // Lo de arriba y lo de las ventanas no cambió: no se vuelve a pintar.
            Assert.Equal(0, dibujos.De("EstadoCaja"));
            Assert.Equal(0, dibujos.De("CascadingValue`1"));
            // Los botones de los renglones no cambian de manejador con cada pintado: solo se van los del que deja de estar elegido.
            Assert.True(dibujos.ManejadoresCambiados <= 6, $"Se cambiaron {dibujos.ManejadoresCambiados} manejadores.");
        }
        Assert.Empty(v.Pantalla.Errores);
    }
}
