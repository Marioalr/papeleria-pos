using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PapeleriaPos.Herramientas.Impresoras;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>
/// diagnosticar-impresoras --antes-y-despues (2026-11-08): las fotos de cada impresora antes y después de una acción, con un agente SNMP
/// falso (UDP de verdad en 127.0.0.1) y una impresora HTTP falsa (IPP, eSCL y Web Config). Nunca contra la tienda. Los dos falsos
/// anotan cualquier cosa que no sea una pregunta (un Set de SNMP, otra operación de IPP, otro método de HTTP) y las pruebas exigen que no
/// haya ninguna.
/// </summary>
public class AntesYDespuesImpresorasTests
{
    private const string Contador = "1.3.6.1.2.1.43.10.2.1.4.1.1";

    // ───────────── El agente SNMP falso ─────────────

    private sealed class AgenteSnmp : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _fin = new();
        private readonly Task _bucle;

        public SortedDictionary<string, EntradaSnmp> Mib { get; } = new(Comparer<string>.Create(CaminataSnmp.Comparar));
        public List<string> NoPermitido { get; } = [];
        public List<(byte Pdu, string Oid)> Preguntas { get; } = [];

        /// <summary>Como una impresora vieja: no contesta v2c.</summary>
        public bool SoloV1 { get; init; }

        /// <summary>Contesta tooBig a cualquier GetBulk (su respuesta no cabe).</summary>
        public bool SinGetBulk { get; init; }

        /// <summary>Como una impresora apagada: no contesta nada.</summary>
        public bool Muerto { get; init; }

        public int Puerto => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        public AgenteSnmp() => _bucle = Task.Run(BucleAsync);

        public void Poner(string oid, byte tipo, byte[] datos)
        {
            lock (Mib)
                Mib[oid] = new EntradaSnmp(oid, tipo, datos);
        }

        public void Texto(string oid, string valor) => Poner(oid, 0x04, Encoding.ASCII.GetBytes(valor));

        public void Numero(string oid, long valor, byte tipo = 0x41)
        {
            var bytes = BitConverter.GetBytes(valor).Reverse().SkipWhile((b, i) => b == 0 && i < 7).ToArray();
            if ((bytes[0] & 0x80) != 0)
                bytes = [0, .. bytes];
            Poner(oid, tipo, bytes);
        }

        private async Task BucleAsync()
        {
            while (!_fin.IsCancellationRequested)
            {
                UdpReceiveResult llegada;
                try
                {
                    llegada = await _udp.ReceiveAsync(_fin.Token);
                }
                catch (Exception) when (_fin.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }
                var p = CaminataSnmp.Leer(llegada.Buffer);
                if (p is null)
                {
                    lock (NoPermitido) NoPermitido.Add("algo que no es SNMP");
                    continue;
                }
                lock (Preguntas)
                    Preguntas.Add((p.Pdu, p.Datos[0].Oid));
                if (p.Pdu is not (0xA0 or 0xA1 or 0xA5))
                {
                    lock (NoPermitido) NoPermitido.Add($"PDU 0x{p.Pdu:X2} en {p.Datos[0].Oid}");
                    continue;
                }
                if (Muerto || (SoloV1 && p.Version != 0))
                    continue;
                if (SinGetBulk && p.Pdu == 0xA5)
                {
                    await _udp.SendAsync(CaminataSnmp.ArmarRespuesta(p.Version, "public", p.Id, 1, [new EntradaSnmp(p.Datos[0].Oid, 0x05, [])]),
                        llegada.RemoteEndPoint);
                    continue;
                }
                var (error, datos) = Contestar(p);
                await _udp.SendAsync(CaminataSnmp.ArmarRespuesta(p.Version, "public", p.Id, error, datos), llegada.RemoteEndPoint);
            }
        }

        private (int, List<EntradaSnmp>) Contestar(CaminataSnmp.Leido p)
        {
            var oid = p.Datos[0].Oid;
            lock (Mib)
            {
                if (p.Pdu == 0xA0)
                    return Mib.TryGetValue(oid, out var exacto) ? (0, [exacto])
                        : p.Version == 0 ? (2, [new EntradaSnmp(oid, 0x05, [])]) : (0, [new EntradaSnmp(oid, 0x80, [])]);
                var siguientes = Mib.Values.Where(e => CaminataSnmp.Comparar(e.Oid, oid) > 0).Take(p.Pdu == 0xA5 ? 7 : 1).ToList();
                if (siguientes.Count == 0)
                    return p.Version == 0 ? (2, [new EntradaSnmp(oid, 0x05, [])]) : (0, [new EntradaSnmp(oid, 0x82, [])]);
                if (p.Pdu == 0xA5 && siguientes.Count < 7)
                    siguientes.Add(new EntradaSnmp(siguientes[^1].Oid, 0x82, []));
                return (0, siguientes);
            }
        }

        public void Dispose()
        {
            _fin.Cancel();
            _udp.Dispose();
            try { _bucle.Wait(2000); } catch (AggregateException) { }
        }
    }

    /// <summary>Un «@BDC ST2» con la serie y los contadores de color y B/N (campo 0x36: 8 bytes FF y dos enteros de 32 bits).</summary>
    private static byte[] St2(long color, long bn, string serie = "X3TEST0001")
    {
        var campos = new List<byte> { 0x01, 0x01, 0x04 };
        campos.AddRange([0x36, 16, .. Enumerable.Repeat((byte)0xFF, 8), .. BitConverter.GetBytes((uint)color), .. BitConverter.GetBytes((uint)bn)]);
        campos.AddRange([0x40, (byte)serie.Length, .. Encoding.ASCII.GetBytes(serie)]);
        return [0x00, .. "@BDC ST2\r\n"u8.ToArray(), (byte)campos.Count, (byte)(campos.Count >> 8), .. campos];
    }

    private static AgenteSnmp WF6590(bool soloV1 = false, bool sinGetBulk = false)
    {
        var a = new AgenteSnmp { SoloV1 = soloV1, SinGetBulk = sinGetBulk };
        a.Texto("1.3.6.1.2.1.1.1.0", "EPSON Built-in 11b/g/n Print Server");
        a.Numero("1.3.6.1.2.1.1.3.0", 1000, 0x43);
        a.Texto("1.3.6.1.2.1.25.3.2.1.3.1", "EPSON WF-6590 Series");
        a.Numero("1.3.6.1.2.1.25.3.5.1.1.1", 3, 0x02);
        a.Numero(Contador, 64713);
        a.Texto("1.3.6.1.2.1.43.11.1.1.6.1.1", "Black ink");
        a.Numero("1.3.6.1.2.1.43.11.1.1.9.1.1", 59, 0x02);
        a.Numero("1.3.6.1.4.1.1248.1.1.3.1.1.1", 10);
        // Dentro del intérprete de comandos de Epson: no se debe caminar.
        a.Texto("1.3.6.1.4.1.1248.1.2.2.44.1.1.2.1.101.101.1", "EEPROM");
        a.Numero("1.3.6.1.4.1.1248.1.2.2.50.1", 7);
        a.Poner(LectorImpresora.EpsonSt, 0x04, St2(25059, 39654));
        return a;
    }

    // ───────────── La impresora HTTP falsa (IPP, eSCL, Web Config) ─────────────

    private sealed class ImpresoraHttp(string host) : HttpMessageHandler
    {
        public List<string> NoPermitido { get; } = [];
        public List<string> Pedidos { get; } = [];
        public List<(int Id, string Nombre, int Estado, int Caras)> Trabajos { get; } = [];
        public bool SinEscl { get; init; }
        public bool Apagada { get; init; }
        public int CopiasPanel { get; set; } = 120;
        public int Escaneos { get; set; } = 40;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            lock (Pedidos)
                Pedidos.Add($"{request.Method} {uri}");
            if (uri.Host != host)
                throw new HttpRequestException($"pregunta a otro: {uri}");
            if (Apagada)
                throw new HttpRequestException("no conecta", new SocketException((int)SocketError.TimedOut));
            if (request.Method == HttpMethod.Post)
            {
                var cuerpo = await request.Content!.ReadAsByteArrayAsync(ct);
                var operacion = Ipp.Operacion(cuerpo);
                if (uri.AbsolutePath != "/ipp/print" || uri.Port != 631 || operacion is not (Ipp.GetJobs or Ipp.GetPrinterAttributes))
                {
                    lock (NoPermitido) NoPermitido.Add($"POST {uri} operación 0x{operacion:X4}");
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
                return Bytes(operacion == Ipp.GetPrinterAttributes ? Atributos() : TrabajosIpp(Encoding.UTF8.GetString(cuerpo).Contains("not-completed")));
            }
            if (request.Method != HttpMethod.Get)
            {
                lock (NoPermitido) NoPermitido.Add($"{request.Method} {uri}");
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
            return uri.AbsolutePath switch
            {
                "/eSCL/ScannerCapabilities" when !SinEscl => Texto("<?xml version='1.0'?><scan:ScannerCapabilities xmlns:scan='http://schemas.hp.com/imaging/escl/2011/05/03' xmlns:pwg='http://www.pwg.org/schemas/2010/12/sm'><pwg:Version>2.6</pwg:Version><pwg:MakeAndModel>EPSON L5590 Series</pwg:MakeAndModel></scan:ScannerCapabilities>"),
                "/eSCL/ScannerStatus" when !SinEscl => Texto($"<?xml version='1.0'?><scan:ScannerStatus xmlns:scan='http://schemas.hp.com/imaging/escl/2011/05/03' xmlns:pwg='http://www.pwg.org/schemas/2010/12/sm'><pwg:State>Idle</pwg:State><scan:Jobs>{string.Concat(Enumerable.Range(1, Escaneos - 40).Select(i => $"<scan:JobInfo><pwg:JobUuid>uuid-{i}</pwg:JobUuid><scan:Age>{i * 7}</scan:Age><pwg:ImagesCompleted>3</pwg:ImagesCompleted><pwg:JobState>Completed</pwg:JobState></scan:JobInfo>"))}</scan:Jobs></scan:ScannerStatus>"),
                "/" or "/PRESENTATION/HTML/TOP/INDEX.HTML" => Texto("<html><body><h1>EPSON WF-6590 Series</h1><p>Dirección IP</p><p>192.0.2.21</p></body></html>"),
                "/PRESENTATION/HTML/TOP/PRTINFO.HTML" => Texto($"<html><body><table><tr><td>Copias</td><td>{CopiasPanel:N0}</td></tr><tr><td>Escaneos: {Escaneos}</td></tr></table>"
                    + "<img class='color' src='../../IMAGE/Ink_Waste.PNG' height='4' style=''></body></html>"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found") },
            };
        }

        private static HttpResponseMessage Texto(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s) };

        private static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };

        private byte[] Atributos() => new RespuestaIpp()
            .Grupo(0x04)
            .Texto(0x41, "printer-make-and-model", "EPSON WF-6590 Series")
            .Entero(0x23, "printer-state", 3)
            .Entero(0x21, "printer-up-time", Environment.TickCount)
            .Texto(0x44, "which-jobs-supported", "completed", "not-completed")
            .Coleccion("media-col-default", ("media-size-name", "na_letter_8.5x11in"))
            .Fin();

        private byte[] TrabajosIpp(bool pendientes)
        {
            var r = new RespuestaIpp();
            lock (Trabajos)
            {
                foreach (var t in Trabajos.Where(t => (t.Estado < 7) == pendientes))
                    r.Grupo(0x02).Entero(0x21, "job-id", t.Id).Texto(0x42, "job-name", t.Nombre).Entero(0x23, "job-state", t.Estado)
                        .Texto(0x42, "job-originating-user-name", "Juan Pérez").Entero(0x21, "job-impressions-completed", t.Caras)
                        .Texto(0x44, "print-color-mode", "monochrome");
            }
            return r.Fin();
        }
    }

    /// <summary>Arma respuestas IPP para la impresora falsa (escrito aparte del lector, para no probarlo contra sí mismo).</summary>
    private sealed class RespuestaIpp
    {
        private readonly MemoryStream _m = new();

        public RespuestaIpp()
        {
            _m.Write([0x01, 0x01, 0x00, 0x00, 0, 0, 0, 1, 0x01]);
            Texto(0x47, "attributes-charset", "utf-8");
            Texto(0x48, "attributes-natural-language", "en");
        }

        public RespuestaIpp Grupo(byte etiqueta)
        {
            _m.WriteByte(etiqueta);
            return this;
        }

        public RespuestaIpp Texto(byte etiqueta, string nombre, params string[] valores)
        {
            for (var i = 0; i < valores.Length; i++)
                Par(etiqueta, i == 0 ? nombre : "", Encoding.UTF8.GetBytes(valores[i]));
            return this;
        }

        public RespuestaIpp Entero(byte etiqueta, string nombre, int valor)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, valor);
            Par(etiqueta, nombre, b);
            return this;
        }

        public RespuestaIpp Coleccion(string nombre, params (string Miembro, string Valor)[] miembros)
        {
            Par(0x34, nombre, []);
            foreach (var (miembro, valor) in miembros)
            {
                Par(0x4A, "", Encoding.UTF8.GetBytes(miembro));
                Par(0x44, "", Encoding.UTF8.GetBytes(valor));
            }
            Par(0x37, "", []);
            return this;
        }

        private void Par(byte etiqueta, string nombre, byte[] valor)
        {
            var n = Encoding.UTF8.GetBytes(nombre);
            _m.WriteByte(etiqueta);
            _m.Write([(byte)(n.Length >> 8), (byte)n.Length]);
            _m.Write(n);
            _m.Write([(byte)(valor.Length >> 8), (byte)valor.Length]);
            _m.Write(valor);
        }

        public byte[] Fin()
        {
            _m.WriteByte(0x03);
            return _m.ToArray();
        }
    }

    private static OpcionesFoto Opciones(AgenteSnmp a) => new() { PuertoSnmp = a.Puerto, EsperaSnmpMs = 300 };

    // ───────────── La caminata SNMP ─────────────

    [Fact]
    public async Task La_caminata_v2c_trae_el_arbol_en_orden_y_se_brinca_los_comandos_de_Epson()
    {
        using var agente = WF6590();
        var cliente = new CaminataSnmp("127.0.0.1", agente.Puerto, esperaMs: 300);

        var epson = await cliente.CaminarAsync("1.3.6.1.4.1.1248", 5000, TimeSpan.FromSeconds(10), CancellationToken.None);
        var mib = await cliente.CaminarAsync("1.3.6.1.2.1.43", 5000, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("v2c", epson.Version);
        Assert.Equal(["1.3.6.1.4.1.1248.1.1.3.1.1.1", "1.3.6.1.4.1.1248.1.2.2.50.1"], epson.Entradas.Select(e => e.Oid));
        Assert.DoesNotContain(agente.Preguntas, p => p.Pdu != 0xA0 && p.Oid.StartsWith(CaminataSnmp.ComandosEpson + ".", StringComparison.Ordinal)
            && !p.Oid.EndsWith(".4294967295", StringComparison.Ordinal));
        Assert.Equal([Contador, "1.3.6.1.2.1.43.11.1.1.6.1.1", "1.3.6.1.2.1.43.11.1.1.9.1.1"], mib.Entradas.Select(e => e.Oid));
        Assert.Equal("64713", mib.Entradas[0].Texto);
        Assert.Equal("Black ink", mib.Entradas[1].Texto);
        Assert.False(mib.Cortada);
        Assert.Empty(agente.NoPermitido);
        Assert.All(agente.Preguntas, p => Assert.Contains(p.Pdu, new byte[] { 0xA0, 0xA1, 0xA5 }));
    }

    [Fact]
    public async Task Sin_v2c_camina_con_GetNext_de_v1_y_el_tope_la_corta()
    {
        using var agente = WF6590(soloV1: true);
        var cliente = new CaminataSnmp("127.0.0.1", agente.Puerto, esperaMs: 200, reintentos: 0);

        var todo = await cliente.CaminarAsync("1.3.6.1.2.1", 5000, TimeSpan.FromSeconds(10), CancellationToken.None);
        var cortada = await cliente.CaminarAsync("1.3.6.1.2.1", 2, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("v1", todo.Version);
        Assert.Equal(7, todo.Entradas.Count);
        Assert.True(cortada.Cortada);
        Assert.Equal(2, cortada.Entradas.Count);
        Assert.Contains(agente.Preguntas, p => p.Pdu == 0xA1);
    }

    [Fact]
    public async Task Si_el_GetBulk_no_le_cabe_sigue_de_uno_en_uno_con_v2c()
    {
        using var agente = WF6590(sinGetBulk: true);
        var cliente = new CaminataSnmp("127.0.0.1", agente.Puerto, esperaMs: 300);

        var mib = await cliente.CaminarAsync("1.3.6.1.2.1.43", 5000, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("v2c", mib.Version);
        Assert.Equal(3, mib.Entradas.Count);
        Assert.Null(mib.Error);
        Assert.Single(agente.Preguntas, p => p.Pdu == 0xA5);
    }

    [Fact]
    public async Task Una_impresora_que_no_contesta_no_se_vuelve_a_preguntar_en_esa_foto()
    {
        using var agente = new AgenteSnmp { Muerto = true };
        var cliente = new CaminataSnmp("127.0.0.1", agente.Puerto, esperaMs: 150, reintentos: 0);
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        var primera = await cliente.CaminarAsync("1.3.6.1.2.1.43", 5000, TimeSpan.FromSeconds(10), CancellationToken.None);
        var segunda = await cliente.CaminarAsync("1.3.6.1.2.1.25.3", 5000, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.NotNull(primera.Error);
        Assert.Contains("se saltó", segunda.Error);
        Assert.True(cliente.SinRespuesta);
        Assert.Equal(2, agente.Preguntas.Count); // v2c y v1, una vez
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Una_pregunta_SNMP_nunca_es_un_Set()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CaminataSnmp.Armar(1, "public", 0xA3, 1, Contador, 0));
        var get = CaminataSnmp.Leer(CaminataSnmp.Armar(1, "public", 0xA5, 77, "1.3.6.1.4.1.2699", 20))!;
        Assert.Equal((1, (byte)0xA5, 77, "1.3.6.1.4.1.2699"), (get.Version, get.Pdu, get.Id, get.Datos[0].Oid));
    }

    // ───────────── IPP ─────────────

    [Fact]
    public void IPP_lee_trabajos_con_sus_estados_y_enmascara_documentos_y_personas()
    {
        var datos = new RespuestaIpp().Grupo(0x02).Entero(0x21, "job-id", 12).Texto(0x42, "job-name", "Factura Juan.pdf").Entero(0x23, "job-state", 9)
            .Texto(0x42, "job-originating-user-name", "Juan Pérez").Entero(0x21, "job-impressions-completed", 2)
            .Grupo(0x02).Entero(0x21, "job-id", 13).Texto(0x42, "job-name", "IMG").Entero(0x23, "job-state", 5).Fin();

        var r = Ipp.Leer(datos);

        Assert.True(r.Bien);
        var trabajos = r.Trabajos.ToList();
        Assert.Equal(2, trabajos.Count);
        Assert.Equal("Fac….pdf (16)", trabajos[0]["job-name"]);
        Assert.Equal("Jua… (10)", trabajos[0]["job-originating-user-name"]);
        Assert.Equal("9 (completed)", trabajos[0]["job-state"]);
        Assert.Equal("2", trabajos[0]["job-impressions-completed"]);
        Assert.Equal("IMG… (3)", trabajos[1]["job-name"]);
        Assert.Equal("5 (processing)", trabajos[1]["job-state"]);
    }

    [Fact]
    public void IPP_pide_solo_Get_Jobs_de_todos_y_Get_Printer_Attributes()
    {
        var pregunta = Ipp.PedirTrabajos("ipps://192.0.2.21:631/ipp/print", 3, "completed");
        Assert.Equal(Ipp.GetJobs, Ipp.Operacion(pregunta));
        var texto = Encoding.UTF8.GetString(pregunta);
        Assert.Contains("which-jobs", texto);
        Assert.Contains("completed", texto);
        Assert.Contains("my-jobs", texto);
        Assert.Contains("job-impressions-completed", texto);
        Assert.Equal(Ipp.GetPrinterAttributes, Ipp.Operacion(Ipp.PedirAtributos("ipps://192.0.2.21:631/ipp/print", 1)));
        var atributos = Ipp.Leer(new RespuestaIpp().Grupo(0x04).Coleccion("media-col-default", ("media-size-name", "na_letter")).Fin());
        Assert.Equal("{media-size-name=na_letter}", atributos.Impresora!["media-col-default"]);
    }

    [Theory]
    [InlineData("Tarea de historia final.docx", "Tar….docx (28)")]
    [InlineData("foto", "fot… (4)")]
    [InlineData("ab", "ab… (2)")]
    [InlineData("(no-value)", "(no-value)")]
    public void El_nombre_de_un_documento_se_queda_con_tres_letras_y_su_extension(string nombre, string esperado) =>
        Assert.Equal(esperado, Ipp.Enmascarar(nombre));

    // ───────────── Web Config ─────────────

    [Fact]
    public void De_Web_Config_salen_los_numeros_que_parecen_contadores_y_las_barras()
    {
        var numeros = FotoImpresora.NumerosDeLaPagina("""
            <html><script>var x = 5;</script><table>
            <tr><td>Número total de páginas</td><td>64,713</td></tr>
            <tr><td>Copias: 120 hojas</td></tr>
            <tr><td>Dirección IP</td><td>192.168.1.20</td></tr>
            </table><img src='../../IMAGE/Ink_K.PNG' height='30'><img src='../../IMAGE/Ink_Waste.PNG' height='4'></html>
            """);

        Assert.Equal("64713", numeros["Número total de páginas"]);
        Assert.Equal("120", numeros["Copias"]);
        Assert.Equal("30", numeros["barra Ink_K"]);
        Assert.Equal("4", numeros["barra Ink_Waste"]);
        Assert.DoesNotContain(numeros, n => n.Value.Contains("192"));
    }

    // ───────────── La foto y lo que cambió ─────────────

    [Fact]
    public async Task Entre_dos_fotos_sale_solo_lo_que_cambio_y_cuanto()
    {
        using var agente = WF6590();
        var http = new ImpresoraHttp("127.0.0.1");
        using var cliente = new HttpClient(http);
        var lector = new LectorImpresora("127.0.0.1", cliente, TimeProvider.System, Opciones(agente));

        var antes = await lector.FotoAsync(CancellationToken.None);
        agente.Numero(Contador, 64715);
        agente.Numero("1.3.6.1.2.1.1.3.0", 5000, 0x43);
        agente.Numero("1.3.6.1.4.1.1248.1.2.2.50.1", 9);
        agente.Poner(LectorImpresora.EpsonSt, 0x04, St2(25061, 39654));
        http.CopiasPanel = 122;
        http.Escaneos = 41;
        lock (http.Trabajos)
            http.Trabajos.Add((12, "Factura Juan.pdf", 9, 2));
        var despues = await lector.FotoAsync(CancellationToken.None);
        var c = CambiosEntreFotos.Comparar(antes, despues);

        Assert.Equal("EPSON WF-6590 Series", antes.Modelo);
        Assert.Equal("X3TEST0001", antes.Serie);
        Assert.Equal("https://127.0.0.1:631/ipp/print", antes.Ipp.Url);
        Assert.Equal((2L, 2L, 0L), (c.Caras, c.Color, c.BlancoYNegro));
        Assert.Contains(c.Cambios, x => x.Clave == "1.3.6.1.4.1.1248.1.2.2.50.1" && x.Diferencia == 2);
        Assert.Contains(c.Cambios, x => x is { Parte: "web PRTINFO", Clave: "Copias", Diferencia: 2 });
        Assert.Contains(c.Cambios, x => x is { Parte: "web PRTINFO", Clave: "Escaneos", Diferencia: 1 });
        Assert.Contains(c.Cambios, x => x.Parte == "escl ScannerStatus" && x.Clave.Contains("ImagesCompleted") && x.Despues == "3");
        Assert.Equal(["12"], c.TrabajosIpp);
        Assert.Contains(c.Cambios, x => x.Clave == "trabajo 12 job-name" && x.Despues == "Fac….pdf (16)");
        Assert.Single(c.Cambios, x => x.EsReloj && x.Clave == "1.3.6.1.2.1.1.3.0");
        Assert.DoesNotContain(c.Cambios, x => x.Parte == "escl ScannerCapabilities");
        Assert.Empty(agente.NoPermitido);
        Assert.Empty(http.NoPermitido);
    }

    [Fact]
    public void Una_parte_que_no_contesto_en_una_foto_no_se_lista_como_cambio()
    {
        var hora = DateTimeOffset.Now;
        LecturaHttp Escl(int? codigo, string? error) => new("ScannerStatus", "https://h/eSCL/ScannerStatus", codigo,
            codigo is null ? null : "<a><b>1</b></a>", error);
        FotoImpresora Foto(Caminata c, LecturaHttp escl) => new("h", hora, hora, [c], null, new LecturaIpp(null, "no", null, null, null), [escl], []);
        var antes = Foto(new Caminata("1.3", [new("1.3.1", 0x02, [1]), new("1.3.2", 0x02, [1]), new("1.3.3", 0x02, [1])], "v2c", null, false),
            Escl(200, null));
        var despues = Foto(new Caminata("1.3", [new("1.3.1", 0x02, [2]), new("1.3.2", 0x02, [1])], "v2c", null, true),
            Escl(null, "no conecta"));

        var c = CambiosEntreFotos.Comparar(antes, despues);

        // La de después se cortó en 1.3.2: lo que sigue (1.3.3) no se da por desaparecido.
        Assert.Equal(["1.3.1"], c.Cambios.Select(x => x.Clave));
        Assert.Contains(c.SinComparar, s => s.Contains("escl ScannerStatus") && s.Contains("después no contestó"));
        Assert.Contains(c.SinComparar, s => s.Contains("se cortó"));
    }

    // ───────────── La sesión completa ─────────────

    [Fact]
    public async Task Una_sesion_guarda_las_fotos_los_cambios_y_un_resumen_sin_nombres_de_clientes()
    {
        using var agente = WF6590();
        var http = new ImpresoraHttp("127.0.0.1");
        using var cliente = new HttpClient(http);
        using var apagadaSnmp = new AgenteSnmp { Muerto = true };
        var apagadaHttp = new ImpresoraHttp("localhost") { Apagada = true };
        using var clienteApagada = new HttpClient(apagadaHttp);
        var carpeta = Path.Combine(Path.GetTempPath(), "antes-y-despues-" + Guid.NewGuid().ToString("N"));
        var consola = new StringWriter();
        var lectores = new[]
        {
            new LectorImpresora("127.0.0.1", cliente, TimeProvider.System, Opciones(agente)),
            new LectorImpresora("localhost", clienteApagada, TimeProvider.System, Opciones(apagadaSnmp) with { EsperaSnmpMs = 100 }),
        };
        var sesion = new AntesYDespues(lectores, carpeta, TextWriter.Synchronized(consola), TimeProvider.System,
            new OpcionesAntesYDespues { VigilarIpp = true, PausaVigilancia = TimeSpan.FromMilliseconds(50), Espera = TimeSpan.Zero });
        var renglones = new Queue<Func<Task<string?>>>(
        [
            () => Task.FromResult<string?>("Copia color panel"),
            async () =>
            {
                // Mientras «se hace»: sale un trabajo por IPP, sube el contador y el color.
                await Task.Delay(200);
                lock (http.Trabajos)
                    http.Trabajos.Add((12, "Factura Juan.pdf", 5, 0));
                await Task.Delay(300);
                lock (http.Trabajos)
                    http.Trabajos[0] = (12, "Factura Juan.pdf", 9, 2);
                agente.Numero(Contador, 64715);
                agente.Poner(LectorImpresora.EpsonSt, 0x04, St2(25061, 39654));
                await Task.Delay(300);
                return "";
            },
            () => Task.FromResult<string?>("  "),
            () => Task.FromResult<string?>("escaneo ADF 3 hojas"),
            () => { http.Escaneos = 41; return Task.FromResult<string?>(""); },
            () => Task.FromResult<string?>("salir"),
        ]);

        try
        {
            var codigo = await sesion.EjecutarAsync(_ => renglones.Dequeue()(), CancellationToken.None);

            Assert.Equal(0, codigo);
            Assert.Equal(2, sesion.Acciones.Count);
            var primera = sesion.Acciones[0];
            Assert.Equal("01-copia-color-panel", primera.Carpeta);
            var buena = primera.Comparaciones.Single(c => c.Host == "127.0.0.1");
            Assert.Equal((2L, 2L, 0L), (buena.Caras, buena.Color, buena.BlancoYNegro));
            Assert.Contains(primera.Vigilados, v => v.Id == "12" && v.Texto.Contains("job-state=5 (processing)"));
            Assert.Contains(primera.Vigilados, v => v.Id == "12" && v.Texto.Contains("job-state=9 (completed)"));
            Assert.Contains(sesion.Acciones[1].Comparaciones.Single(c => c.Host == "127.0.0.1").Cambios,
                x => x.Parte == "escl ScannerStatus" && x.Clave.Contains("ImagesCompleted"));

            var resumen = await File.ReadAllTextAsync(Path.Combine(carpeta, "resumen.md"));
            Assert.Contains("## 1. «Copia color panel»", resumen);
            Assert.Contains("**Caras:** +2 (64713 → 64715); **ST2:** color +2, B/N 0.", resumen);
            Assert.Contains("| 1 | Copia color panel |", resumen);
            Assert.Contains("caras +2 · color +2, B/N 0 · IPP 1", resumen);
            Assert.Contains("trabajo 12 (nuevo)", resumen);
            Assert.Contains("Fac….pdf (16)", resumen);
            Assert.Contains("## 2. «escaneo ADF 3 hojas»", resumen);
            Assert.Contains("| localhost | — | — | no contestó |", resumen);
            Assert.Contains("| 1 | Copia color panel | ", resumen);
            Assert.Contains("· IPP 1 | sin comparar |", resumen);
            Assert.Contains("Sin comparar: no contestó nada en ninguna de las dos fotos", resumen);
            Assert.Contains("ScannerStatus/Jobs: (vacío) → (ya no está)", resumen);
            Assert.Contains("Escaneos: 40 → 41 (**+1**)", resumen);

            Assert.True(File.Exists(Path.Combine(carpeta, "00-inicio", "127.0.0.1-inicio", "snmp-printer-mib.txt")));
            Assert.True(File.Exists(Path.Combine(carpeta, "01-copia-color-panel", "127.0.0.1-antes", "st2.txt")));
            Assert.True(File.Exists(Path.Combine(carpeta, "01-copia-color-panel", "127.0.0.1-despues", "web-PRTINFO.html")));
            Assert.True(File.Exists(Path.Combine(carpeta, "01-copia-color-panel", "127.0.0.1-cambios.txt")));
            Assert.True(File.Exists(Path.Combine(carpeta, "01-copia-color-panel", "vigilancia-ipp.txt")));
            Assert.Contains(".1.3.6.1.2.1.43.10.2.1.4.1.1 = Counter32: 64715",
                await File.ReadAllTextAsync(Path.Combine(carpeta, "01-copia-color-panel", "127.0.0.1-despues", "snmp-printer-mib.txt")));

            // Ningún archivo trae el nombre del documento ni el de la persona.
            foreach (var archivo in Directory.EnumerateFiles(carpeta, "*", SearchOption.AllDirectories))
            {
                var texto = await File.ReadAllTextAsync(archivo);
                Assert.DoesNotContain("Factura Juan", texto);
                Assert.DoesNotContain("Juan Pérez", texto);
            }
            Assert.Contains("Escribe qué vas a hacer", consola.ToString());
            Assert.Empty(agente.NoPermitido);
            Assert.Empty(apagadaSnmp.NoPermitido);
            Assert.Empty(http.NoPermitido);
        }
        finally
        {
            Directory.Delete(carpeta, recursive: true);
        }
    }

    [Fact]
    public async Task Ctrl_C_a_media_accion_guarda_lo_que_hay_y_dice_que_quedo_a_medias()
    {
        using var agente = WF6590();
        using var cliente = new HttpClient(new ImpresoraHttp("127.0.0.1") { SinEscl = true });
        var carpeta = Path.Combine(Path.GetTempPath(), "antes-y-despues-" + Guid.NewGuid().ToString("N"));
        using var ctrlC = new CancellationTokenSource();
        var sesion = new AntesYDespues([new LectorImpresora("127.0.0.1", cliente, TimeProvider.System, Opciones(agente))], carpeta,
            new StringWriter(), TimeProvider.System, new OpcionesAntesYDespues { Espera = TimeSpan.Zero });
        var vuelta = 0;

        try
        {
            await sesion.EjecutarAsync(async ct =>
            {
                if (++vuelta == 1)
                    return "copia B/N panel";
                await ctrlC.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return "";
            }, ctrlC.Token);

            var resumen = await File.ReadAllTextAsync(Path.Combine(carpeta, "resumen.md"));
            Assert.Contains("La acción 1. «copia B/N panel» quedó a medias", resumen);
            Assert.Contains("ScannerCapabilities: HTTP 404", resumen);
            Assert.Empty(sesion.Acciones);
        }
        finally
        {
            Directory.Delete(carpeta, recursive: true);
        }
    }

    [Theory]
    [InlineData("Copia color panel", "copia-color-panel")]
    [InlineData("Impresión «Smart Panel» B/N, 2 págs.", "impresion-smart-panel-b-n-2-pags")]
    [InlineData("¿?", "accion")]
    public void La_carpeta_de_cada_accion_no_lleva_acentos_ni_signos(string etiqueta, string esperada) =>
        Assert.Equal(esperada, AntesYDespues.Carpeta(etiqueta));
}
