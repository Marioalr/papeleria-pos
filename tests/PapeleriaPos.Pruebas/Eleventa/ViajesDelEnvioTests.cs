using System.Net;
using System.Net.Sockets;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// Auditoría del 2026-11-04: cada instrucción del puente preparaba su propio comando, y con Firebird 2.0 (protocolo 10) eso son de 6 a 7
/// viajes por el Wi-Fi: una venta de 3 renglones, 241. Lo que se repite por renglón usa un solo comando preparado. Se cuentan los viajes con
/// un intermediario contra el Firebird de prueba (el 2.5, que junta algunos: en la tienda se ahorra más), y lo que queda escrito es lo mismo
/// (lo revisan las demás pruebas del envío).
/// </summary>
public sealed class ViajesDelEnvioTests
{
    /// <summary>Entre el puente y Firebird: un viaje es cada vez que el puente vuelve a hablar después de que Firebird contestó.</summary>
    private sealed class Intermediario : IDisposable
    {
        private readonly TcpListener _oyente = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _abiertos = [];
        private readonly (string Servidor, int Puerto) _destino;
        private int _contesto = 1;
        private int _viajes;

        public Intermediario(string servidor, int puerto)
        {
            _destino = (servidor, puerto);
            _oyente.Start();
            _ = AceptarAsync();
        }

        public int Puerto => ((IPEndPoint)_oyente.LocalEndpoint).Port;
        public int Viajes => Volatile.Read(ref _viajes);
        public void Reiniciar() => Volatile.Write(ref _viajes, 0);

        private async Task AceptarAsync()
        {
            try
            {
                while (true)
                {
                    var puente = await _oyente.AcceptTcpClientAsync();
                    var firebird = new TcpClient { NoDelay = true };
                    await firebird.ConnectAsync(_destino.Servidor, _destino.Puerto);
                    puente.NoDelay = true;
                    lock (_abiertos)
                        _abiertos.AddRange([puente, firebird]);
                    _ = PasarAsync(puente.GetStream(), firebird.GetStream(), esIda: true);
                    _ = PasarAsync(firebird.GetStream(), puente.GetStream(), esIda: false);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // Se cerró.
            }
        }

        private async Task PasarAsync(NetworkStream de, NetworkStream a, bool esIda)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (await de.ReadAsync(buffer) is var leidos and > 0)
                {
                    if (!esIda)
                        Volatile.Write(ref _contesto, 1);
                    else if (Interlocked.Exchange(ref _contesto, 0) == 1)
                        Interlocked.Increment(ref _viajes);
                    await a.WriteAsync(buffer.AsMemory(0, leidos));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Se cerró.
            }
        }

        public void Dispose()
        {
            _oyente.Stop();
            lock (_abiertos)
                _abiertos.ForEach(x => x.Dispose());
        }
    }

    [FactConFirebird]
    public async Task Cada_renglon_de_mas_no_vuelve_a_preparar_lo_mismo()
    {
        await using var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO) VALUES (7, 'Ana Ruiz', 'cajera2')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        for (var id = 10; id < 16; id++)
            await e.ProductoAsync(id, $"PROD-{id}", $"Producto {id}", depto: 5, costo: 4, precio: 9, existencia: 50);
        var abierto = new DateTime(2026, 3, 9, 8, 0, 0);
        await e.AbrirCajaAsync(4, cajero: 7, abierto, fondo: 300);
        var caja = new CajaEnEleventa(4, 7, "10.0.0.20", AbreTurno: false);
        var turno = new TurnoParaEleventa(abierto, 300);
        using var intermediario = new Intermediario(e.Servidor.Servidor, e.Servidor.Puerto);
        var servidor = e.Servidor with { Puerto = intermediario.Puerto };

        async Task<int> ViajesAsync(int minutos, params int[] productos)
        {
            var cobrada = abierto.AddMinutes(minutos);
            ArticuloParaEleventa[] articulos = [.. productos.Select(p => new ArticuloParaEleventa(p, $"Producto {p}", 1, 9))];
            intermediario.Reiniciar();
            await EnvioEleventa.MandarAsync(servidor, caja, new VentaParaEleventa(cobrada.AddMinutes(-1), cobrada, 100, turno, articulos));
            return intermediario.Viajes;
        }

        // La primera prepara lo del departamento y el día por primera vez: no se mide.
        await ViajesAsync(1, 10);
        var uno = await ViajesAsync(2, 11);
        var cinco = await ViajesAsync(3, 11, 12, 13, 14, 15);

        var porRenglon = (cinco - uno) / 4.0;
        Assert.True(porRenglon <= 15, $"Un renglón: {uno} viajes; cinco: {cinco}; {porRenglon} por renglón de más.");
    }
}
