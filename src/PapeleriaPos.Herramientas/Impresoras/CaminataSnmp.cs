using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Herramientas.Impresoras;

/// <summary>Un dato que contestó la impresora por SNMP: su OID, su tipo (el de BER) y sus bytes.</summary>
public sealed record EntradaSnmp(string Oid, byte Tipo, byte[] Datos)
{
    /// <summary>El valor para leerlo: el número si es número, el texto si es texto, el OID si es OID, la IP si es IP; si no, sus bytes.</summary>
    public string Texto => Tipo switch
    {
        0x06 => CaminataSnmp.LeerOid(Datos),
        0x40 when Datos.Length == 4 => string.Join('.', Datos),
        _ => DiagnosticoImpresora.Texto(new SnmpClient.Value(Tipo, Datos)),
    };

    public string NombreDelTipo => Tipo switch
    {
        0x02 => "INTEGER",
        0x04 => "STRING",
        0x05 => "NULL",
        0x06 => "OID",
        0x40 => "IpAddress",
        0x41 => "Counter32",
        0x42 => "Gauge32",
        0x43 => "Timeticks",
        0x46 => "Counter64",
        _ => $"0x{Tipo:X2}",
    };
}

/// <summary>Lo que se caminó de un árbol SNMP.</summary>
/// <param name="Version">«v2c» (GetBulk) o «v1» (GetNext); vacío si no contestó.</param>
/// <param name="Cortada">Se detuvo por el tope de datos o de tiempo: lo que sigue de ese árbol no se leyó.</param>
public sealed record Caminata(string Raiz, IReadOnlyList<EntradaSnmp> Entradas, string? Version, string? Error, bool Cortada);

/// <summary>
/// Cliente SNMP para el modo «antes y después» de diagnosticar-impresoras: <b>solo lee</b> (GetRequest, GetNextRequest y GetBulkRequest;
/// nunca SetRequest). El SnmpClient del módulo solo hace GET de v1 y no sabe caminar; este va aparte para no tocar el módulo. Primero
/// prueba v2c (GetBulk, menos vueltas) y, si la impresora no lo contesta, v1 (GetNext). Si no contesta nada a la primera pregunta, ya no
/// se le pregunta más en esa foto (una impresora apagada no detiene a las demás).
/// </summary>
public sealed class CaminataSnmp(string host, int puerto = 161, string comunidad = "public", int esperaMs = 1500, int reintentos = 1)
{
    public const byte Get = 0xA0, GetNext = 0xA1, GetBulk = 0xA5, Respuesta = 0xA2;
    private const int V1 = 0, V2c = 1;

    /// <summary>
    /// El intérprete de comandos de Epson (<c>…1248.1.2.2.44</c>): una pregunta ahí <i>ejecuta</i> un comando (así se lee el «st», y así
    /// leen y escriben la EEPROM las herramientas de reparación). No se camina; el «st» se pide aparte, como lo pide el módulo.
    /// </summary>
    public const string ComandosEpson = "1.3.6.1.4.1.1248.1.2.2.44";

    private int? _version;
    private bool _muerta;
    private bool _sinBulk;

    public string Host { get; } = host;

    /// <summary>Ya no contestó nada: lo que falta de esta foto se salta.</summary>
    public bool SinRespuesta => _muerta;

    public string? Version => _version switch { V2c => "v2c", V1 => "v1", _ => null };

    /// <summary>Un GET de un solo OID (el «st» de Epson). Vacío si la impresora dice que no lo tiene.</summary>
    public async Task<EntradaSnmp?> PedirAsync(string oid, CancellationToken ct)
    {
        var (error, datos) = await PreguntarAsync(Get, oid, ct);
        if (error != 0 || datos.Count == 0 || datos[0].Tipo is 0x80 or 0x81 or 0x82)
            return null;
        return datos[0];
    }

    /// <summary>Camina el árbol <paramref name="raiz"/>: hasta <paramref name="maximo"/> datos o <paramref name="limite"/> de tiempo.</summary>
    public async Task<Caminata> CaminarAsync(string raiz, int maximo, TimeSpan limite, CancellationToken ct)
    {
        var entradas = new List<EntradaSnmp>();
        if (_muerta)
            return new Caminata(raiz, entradas, null, "no contestó (se saltó)", false);
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var actual = raiz;
        try
        {
            while (true)
            {
                if (entradas.Count >= maximo || reloj.Elapsed > limite)
                    return new Caminata(raiz, entradas, Version, null, true);
                var (error, datos) = await PreguntarAsync(_version == V1 || _sinBulk ? GetNext : GetBulk, actual, ct);
                if (error == 1 && !_sinBulk && _version == V2c)
                {
                    // tooBig: la respuesta de GetBulk no cabe en lo que manda la impresora; se sigue de uno en uno.
                    _sinBulk = true;
                    continue;
                }
                if (error != 0 || datos.Count == 0)
                    break; // v1: noSuchName al final de todo
                var termino = false;
                foreach (var dato in datos)
                {
                    if (dato.Tipo is 0x80 or 0x81 or 0x82 || !Dentro(dato.Oid, raiz) || Comparar(dato.Oid, actual) <= 0)
                    {
                        termino = true;
                        break;
                    }
                    if (Dentro(dato.Oid, ComandosEpson))
                    {
                        // Se brinca todo el intérprete: la siguiente pregunta empieza después de él.
                        actual = ComandosEpson + ".4294967295";
                        break;
                    }
                    actual = dato.Oid;
                    entradas.Add(dato);
                    if (entradas.Count >= maximo)
                        break;
                }
                if (termino)
                    break;
            }
            return new Caminata(raiz, entradas, Version, null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new Caminata(raiz, entradas, Version, ex.Message, entradas.Count > 0);
        }
    }

    /// <summary>Una pregunta, con la versión que contestó la impresora (la primera vez prueba v2c y luego v1).</summary>
    private async Task<(int Error, List<EntradaSnmp> Datos)> PreguntarAsync(byte pdu, string oid, CancellationToken ct)
    {
        if (_muerta)
            throw new TimeoutException($"SNMP sin respuesta de {Host}");
        if (_version is { } version)
            return await UnaAsync(version, version == V1 && pdu == GetBulk ? GetNext : pdu, oid, ct);
        try
        {
            var r = await UnaAsync(V2c, pdu, oid, ct);
            if (r.Error is 0 or 1 || pdu == Get) // 1 (tooBig): sí habla v2c, solo que no le cabe el GetBulk
            {
                _version = V2c;
                return r;
            }
        }
        catch (TimeoutException)
        {
        }
        try
        {
            var r = await UnaAsync(V1, pdu == GetBulk ? GetNext : pdu, oid, ct);
            _version = V1;
            return r;
        }
        catch (TimeoutException)
        {
            _muerta = true;
            throw;
        }
    }

    private async Task<(int Error, List<EntradaSnmp> Datos)> UnaAsync(int version, byte pdu, string oid, CancellationToken ct)
    {
        var id = Random.Shared.Next(1, int.MaxValue);
        var paquete = Armar(version, comunidad, pdu, id, oid, maxRepeticiones: 10);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var direccion = IPAddress.TryParse(Host, out var ip) ? ip
            : (await Dns.GetHostAddressesAsync(Host, AddressFamily.InterNetwork, ct))[0];
        var destino = new IPEndPoint(direccion, puerto);
        for (var intento = 0; intento <= reintentos; intento++)
        {
            await udp.SendAsync(paquete, destino, ct);
            using var espera = CancellationTokenSource.CreateLinkedTokenSource(ct);
            espera.CancelAfter(esperaMs);
            try
            {
                while (true)
                {
                    var llegada = await udp.ReceiveAsync(espera.Token);
                    if (Leer(llegada.Buffer) is { } r && r.Id == id)
                        return (r.Error, r.Datos);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
                // Windows avisa así que el puerto está cerrado (SNMP apagado): es lo mismo que no contestar.
                throw new TimeoutException($"SNMP: {Host} rechazó la pregunta (¿tiene SNMP apagado?)");
            }
        }
        throw new TimeoutException($"SNMP sin respuesta de {Host}");
    }

    /// <summary>Arma la pregunta: Get o GetNext (v1 o v2c) o GetBulk (v2c). Nunca un Set.</summary>
    public static byte[] Armar(int version, string comunidad, byte pdu, int id, string oid, int maxRepeticiones)
    {
        if (pdu is not (Get or GetNext or GetBulk))
            throw new ArgumentOutOfRangeException(nameof(pdu), "Solo se pregunta: Get, GetNext o GetBulk.");
        var dato = Tlv(0x30, [.. Tlv(0x06, EscribirOid(oid)), .. Tlv(0x05, [])]);
        // En GetBulk, los dos enteros de en medio son non-repeaters (0) y max-repetitions.
        var cuerpo = Tlv(pdu, [.. Entero(id), .. Entero(0), .. Entero(pdu == GetBulk ? maxRepeticiones : 0), .. Tlv(0x30, dato)]);
        return Tlv(0x30, [.. Entero(version), .. Tlv(0x04, Encoding.ASCII.GetBytes(comunidad)), .. cuerpo]);
    }

    public sealed record Leido(int Version, byte Pdu, int Id, int Error, List<EntradaSnmp> Datos);

    /// <summary>Lee un mensaje SNMP (pregunta o respuesta); vacío si no se entiende.</summary>
    public static Leido? Leer(byte[] r)
    {
        try
        {
            var p = 0;
            Entrar(r, ref p, 0x30);
            var version = (int)Numero(Tomar(r, ref p, 0x02));
            Tomar(r, ref p, 0x04);
            var pdu = r[p];
            Entrar(r, ref p, pdu);
            var id = (int)Numero(Tomar(r, ref p, 0x02));
            var error = (int)Numero(Tomar(r, ref p, 0x02));
            Tomar(r, ref p, 0x02);
            var fin = Entrar(r, ref p, 0x30);
            var datos = new List<EntradaSnmp>();
            while (p < fin)
            {
                Entrar(r, ref p, 0x30);
                var oid = LeerOid(Tomar(r, ref p, 0x06));
                var tipo = r[p];
                var valor = Tomar(r, ref p, tipo);
                datos.Add(new EntradaSnmp(oid, tipo, valor));
            }
            return new Leido(version, pdu, id, error, datos);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Arma una respuesta (para el agente falso de las pruebas).</summary>
    public static byte[] ArmarRespuesta(int version, string comunidad, int id, int error, IEnumerable<EntradaSnmp> datos)
    {
        var lista = datos.SelectMany(d => Tlv(0x30, [.. Tlv(0x06, EscribirOid(d.Oid)), .. Tlv(d.Tipo, d.Datos)])).ToArray();
        var cuerpo = Tlv(Respuesta, [.. Entero(id), .. Entero(error), .. Entero(0), .. Tlv(0x30, lista)]);
        return Tlv(0x30, [.. Entero(version), .. Tlv(0x04, Encoding.ASCII.GetBytes(comunidad)), .. cuerpo]);
    }

    /// <summary>El primero va antes que el segundo (por número en cada parte), igual o después.</summary>
    public static int Comparar(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var c = ulong.Parse(x[i], CultureInfo.InvariantCulture).CompareTo(ulong.Parse(y[i], CultureInfo.InvariantCulture));
            if (c != 0)
                return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    public static bool Dentro(string oid, string raiz) => oid == raiz || oid.StartsWith(raiz + ".", StringComparison.Ordinal);

    public static byte[] EscribirOid(string oid)
    {
        var partes = oid.Trim('.').Split('.').Select(s => uint.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var bytes = new List<byte> { (byte)(partes[0] * 40 + partes[1]) };
        foreach (var arco in partes.Skip(2))
        {
            var pila = new Stack<byte>();
            var v = arco;
            pila.Push((byte)(v & 0x7F));
            while ((v >>= 7) > 0)
                pila.Push((byte)(0x80 | (v & 0x7F)));
            bytes.AddRange(pila);
        }
        return [.. bytes];
    }

    public static string LeerOid(byte[] datos)
    {
        if (datos.Length == 0)
            return "";
        var partes = new List<ulong>();
        var primero = (ulong)datos[0];
        partes.AddRange(primero < 80 ? [primero / 40, primero % 40] : [2, primero - 80]);
        ulong v = 0;
        for (var i = 1; i < datos.Length; i++)
        {
            v = (v << 7) | (uint)(datos[i] & 0x7F);
            if ((datos[i] & 0x80) == 0)
            {
                partes.Add(v);
                v = 0;
            }
        }
        return string.Join('.', partes);
    }

    private static byte[] Entero(int v)
    {
        var bytes = BitConverter.GetBytes(v).Reverse().ToArray();
        var i = 0;
        while (i < 3 && ((bytes[i] == 0 && (bytes[i + 1] & 0x80) == 0) || (bytes[i] == 0xFF && (bytes[i + 1] & 0x80) != 0)))
            i++;
        return Tlv(0x02, bytes[i..]);
    }

    private static long Numero(byte[] datos)
    {
        long v = datos.Length > 0 && (datos[0] & 0x80) != 0 ? -1 : 0;
        foreach (var b in datos)
            v = (v << 8) | b;
        return v;
    }

    private static byte[] Tlv(byte etiqueta, byte[] contenido)
    {
        byte[] largo = contenido.Length switch
        {
            < 128 => [(byte)contenido.Length],
            < 256 => [0x81, (byte)contenido.Length],
            < 65536 => [0x82, (byte)(contenido.Length >> 8), (byte)contenido.Length],
            _ => [0x83, (byte)(contenido.Length >> 16), (byte)(contenido.Length >> 8), (byte)contenido.Length],
        };
        return [etiqueta, .. largo, .. contenido];
    }

    private static int Largo(byte[] r, ref int p)
    {
        int b = r[p++];
        if (b < 0x80)
            return b;
        int n = b & 0x7F, largo = 0;
        if (n is 0 or > 3)
            throw new InvalidDataException("SNMP: largo que no se entiende");
        for (var i = 0; i < n; i++)
            largo = (largo << 8) | r[p++];
        return largo;
    }

    /// <summary>Entra a un contenedor; regresa dónde termina.</summary>
    private static int Entrar(byte[] r, ref int p, byte etiqueta)
    {
        if (r[p] != etiqueta)
            throw new InvalidDataException($"SNMP: se esperaba 0x{etiqueta:X2}, llegó 0x{r[p]:X2}");
        p++;
        var largo = Largo(r, ref p);
        return p + largo;
    }

    private static byte[] Tomar(byte[] r, ref int p, byte etiqueta)
    {
        var fin = Entrar(r, ref p, etiqueta);
        if (fin > r.Length)
            throw new InvalidDataException("SNMP: mensaje cortado");
        var datos = r[p..fin];
        p = fin;
        return datos;
    }
}
