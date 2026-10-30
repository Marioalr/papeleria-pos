using System.IO.Ports;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Los puertos COM de verdad (System.IO.Ports): los de la ventana de Windows y los de probar-lector. Se lee con el hilo del lector
/// esperando en el puerto (sin gastar procesador mientras no se escanea): Windows despierta la lectura en cuanto llega algo.
/// </summary>
public sealed class PuertosDelSistema : IPuertosSerie
{
    public IReadOnlyList<string> Nombres() =>
        SerialPort.GetPortNames().Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(n[3..], out var numero) ? numero : int.MaxValue)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IPuertoAbierto Abrir(AjustesLector ajustes)
    {
        var puerto = new SerialPort(ajustes.Puerto, ajustes.Baudios, Paridad(ajustes.Paridad), ajustes.BitsDeDatos, Parada(ajustes.BitsDeParada))
        {
            Handshake = ajustes.ControlDeFlujo switch
            {
                ControlDeFlujoLector.Hardware => Handshake.RequestToSend,
                ControlDeFlujoLector.XonXoff => Handshake.XOnXOff,
                _ => Handshake.None,
            },
            ReadTimeout = SerialPort.InfiniteTimeout,
            ReadBufferSize = 4096,
        };
        try
        {
            // Un byte con error de paridad llega como SUB (1A), que no es de un código: la lectura se descarta, no entra «?» a la venta.
            if (OperatingSystem.IsWindows())
                puerto.ParityReplace = 0x1A;
            puerto.Open();
            // DTR y RTS, si se puede: un puerto sin esas líneas (un adaptador sencillo, una terminal virtual) lee igual. Apagadas ya
            // vienen. Con control de flujo por hardware, RTS lo maneja el puerto.
            if (ajustes.Dtr)
                SiSePuede(() => puerto.DtrEnable = true);
            if (ajustes.Rts && ajustes.ControlDeFlujo != ControlDeFlujoLector.Hardware)
                SiSePuede(() => puerto.RtsEnable = true);
            // Lo que quedó en el puerto de antes (ruido al conectar) no se toma como el principio de un código.
            puerto.DiscardInBuffer();
            return new Abierto(puerto);
        }
        catch
        {
            try
            {
                puerto.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Lo que importa es por qué no abrió.
            }
            throw;
        }
    }

    private static void SiSePuede(Action poner)
    {
        try
        {
            poner();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            // El puerto no tiene esa línea: se lee sin ella.
        }
    }

    private static Parity Paridad(ParidadLector paridad) => paridad switch
    {
        ParidadLector.Impar => Parity.Odd,
        ParidadLector.Par => Parity.Even,
        ParidadLector.Marca => Parity.Mark,
        ParidadLector.Espacio => Parity.Space,
        _ => Parity.None,
    };

    private static StopBits Parada(decimal bits) => bits switch
    {
        1.5m => StopBits.OnePointFive,
        2 => StopBits.Two,
        _ => StopBits.One,
    };

    private sealed class Abierto : IPuertoAbierto
    {
        private readonly SerialPort _puerto;
        private int _espera = SerialPort.InfiniteTimeout;
        private int _errores;

        public Abierto(SerialPort puerto)
        {
            _puerto = puerto;
            _puerto.ErrorReceived += (_, _) => Interlocked.Increment(ref _errores);
        }

        public int ErroresDeLinea => Volatile.Read(ref _errores);

        public int Leer(byte[] destino, TimeSpan espera)
        {
            var ms = espera == Timeout.InfiniteTimeSpan ? SerialPort.InfiniteTimeout : Math.Max(1, (int)Math.Ceiling(espera.TotalMilliseconds));
            // Cambiar el plazo le habla al puerto: solo cuando cambia (de esperar sin fin a esperar la pausa de un código).
            if (ms != _espera)
                _puerto.ReadTimeout = _espera = ms;
            try
            {
                return _puerto.Read(destino, 0, destino.Length);
            }
            catch (TimeoutException)
            {
                return 0;
            }
        }

        public void Dispose() => _puerto.Dispose();
    }
}
