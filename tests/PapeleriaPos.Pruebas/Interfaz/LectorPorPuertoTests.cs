using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Un puerto COM de mentira (aquí no hay lector): lo que «manda el lector» llega en pedazos, uno por cada lectura, como por USB; se
/// puede desconectar a media lectura, y la PC puede no tenerlo (sin conectar) o tenerlo otro programa (ocupado, como eleventa).
/// </summary>
internal sealed class PuertosDeMentira : IPuertosSerie
{
    private readonly Lock _candado = new();
    private readonly List<string> _lista = ["COM1", "COM3"];
    private PuertoDeMentira? _abierto;
    private int _aperturas;

    /// <summary>Lo que pasa al abrir: vacío, abre; si no, esa excepción (la de Windows con eleventa abierto, o sin el puerto).</summary>
    public Func<Exception?> AlAbrir { get; set; } = () => null;

    public PuertoDeMentira? Abierto
    {
        get
        {
            lock (_candado)
                return _abierto;
        }
    }

    public int Aperturas => Volatile.Read(ref _aperturas);

    public void Quitar(string puerto)
    {
        lock (_candado)
            _lista.Remove(puerto);
    }

    public void Poner(string puerto)
    {
        lock (_candado)
            _lista.Add(puerto);
    }

    public IReadOnlyList<string> Nombres()
    {
        lock (_candado)
            return [.. _lista];
    }

    public IPuertoAbierto Abrir(AjustesLector ajustes)
    {
        Interlocked.Increment(ref _aperturas);
        if (AlAbrir() is { } error)
            throw error;
        lock (_candado)
            return _abierto = new PuertoDeMentira();
    }

    /// <summary>Lo que dice Windows con el puerto abierto por otro programa.</summary>
    public static Exception Ocupado() => new UnauthorizedAccessException("Access to the port 'COM3' is denied.");

    /// <summary>Lo que dice Windows sin el puerto (el lector USB desconectado): System.IO.Ports da el error de Windows tal cual.</summary>
    public static Exception NoExiste() => new FileNotFoundException("Could not find file 'COM3'.", "COM3");
}

internal sealed class PuertoDeMentira : IPuertoAbierto
{
    private readonly BlockingCollection<object> _llega = new();
    private readonly CancellationTokenSource _cierre = new();

    public bool Cerrado => _cierre.IsCancellationRequested;

    /// <summary>Cerrado del otro lado: cada lectura regresa vacía sin esperar.</summary>
    public bool RegresaVacio { get; set; }

    public int ErroresDeLinea { get; set; }

    /// <summary>Un pedazo, como llega en una lectura del puerto.</summary>
    public void Mandar(params byte[] bytes) => _llega.Add(bytes);

    public void Mandar(string texto) => Mandar(Encoding.Latin1.GetBytes(texto));

    /// <summary>Se desconecta el lector USB a media lectura: Windows da un error al leer.</summary>
    public void Desconectar() => _llega.Add(new IOException("The device does not recognize the command."));

    /// <summary>Lo que no cupo en la lectura anterior: como un puerto de verdad, cada lectura da a lo más lo que cabe.</summary>
    private byte[] _resto = [];

    public int Leer(byte[] destino, TimeSpan espera)
    {
        if (_resto.Length > 0)
            return Copiar(_resto, destino);
        if (RegresaVacio)
            return 0;
        object algo;
        try
        {
            if (!_llega.TryTake(out algo!, espera == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)Math.Ceiling(espera.TotalMilliseconds), _cierre.Token))
                return 0;
        }
        catch (OperationCanceledException)
        {
            throw new IOException("The I/O operation has been aborted because of either a thread exit or an application request.");
        }
        if (algo is Exception error)
            throw error;
        return Copiar((byte[])algo, destino);
    }

    private int Copiar(byte[] bytes, byte[] destino)
    {
        var cuantos = Math.Min(bytes.Length, destino.Length);
        bytes.AsSpan(0, cuantos).CopyTo(destino);
        _resto = bytes[cuantos..];
        return cuantos;
    }

    public void Dispose() => _cierre.Cancel();
}

public sealed class LectorPorPuertoTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"lector-{Guid.NewGuid():N}");
    private readonly RegistroDiario _registro;

    public LectorPorPuertoTests() => _registro = new RegistroDiario(_carpeta, "caja");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_carpeta))
                Directory.Delete(_carpeta, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Lo quita la siguiente limpieza de la carpeta temporal.
        }
    }

    private static readonly TimeSpan Pausa = TimeSpan.FromMilliseconds(100);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static byte[] Bytes(string texto) => Encoding.Latin1.GetBytes(texto);

    private string Registro() =>
        Directory.Exists(_carpeta) ? string.Concat(Directory.EnumerateFiles(_carpeta).Select(a =>
        {
            using var flujo = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(flujo).ReadToEnd();
        })) : "";

    private static async Task EsperarAsync(Func<bool> condicion, string que)
    {
        var reloj = Stopwatch.StartNew();
        while (!condicion())
        {
            if (reloj.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException($"No pasó: {que}.");
            await Task.Delay(10);
        }
    }

    private static AjustesLector Ajustes(FinDelCodigo fin = FinDelCodigo.Auto, bool soloAlFrente = false) =>
        new("COM3") { Fin = fin, Silencio = 60, SoloAlFrente = soloAlFrente };

    // ── Los bytes, en códigos ──

    [Fact]
    public void El_codigo_termina_con_CR_LF_o_CRLF_y_el_LF_del_CRLF_es_del_mismo()
    {
        var decodificador = new DecodificadorLector(FinDelCodigo.Auto, Pausa);

        var lecturas = decodificador.Agregar(Bytes("7501\r7502\n7503\r\n7504\t"), Ms(0));

        Assert.Equal(["7501", "7502", "7503", "7504"], lecturas.Select(l => l.Codigo));
        Assert.Equal([FinLeido.CR, FinLeido.LF, FinLeido.CRLF, FinLeido.Tab], lecturas.Select(l => l.Fin));
        Assert.Equal("37 35 30 33 0D 0A", lecturas[2].Hex);
        Assert.Equal("7503<CR><LF>", lecturas[2].Visible);
        Assert.False(decodificador.HayPendiente);
    }

    [Fact]
    public void Un_codigo_partido_en_varias_lecturas_llega_entero_con_lo_que_tardo()
    {
        var decodificador = new DecodificadorLector(FinDelCodigo.Auto, Pausa);

        Assert.Empty(decodificador.Agregar(Bytes("750"), Ms(1000)));
        Assert.Empty(decodificador.Agregar(Bytes("1234"), Ms(1004)));
        Assert.Equal(Ms(96), decodificador.EsperaHastaLaPausa(Ms(1008)));
        var lectura = Assert.Single(decodificador.Agregar(Bytes("567890\r"), Ms(1012)));

        Assert.Equal("7501234567890", lectura.Codigo);
        Assert.Equal(Ms(12), lectura.Tardo);
        // El LF que llega en la siguiente lectura, enseguida, es del mismo (CRLF), no un código vacío.
        Assert.Empty(decodificador.Agregar([0x0A], Ms(1013)));
        Assert.Equal(FinLeido.CRLF, lectura.Fin);
        Assert.Null(decodificador.EsperaHastaLaPausa(Ms(1014)));
    }

    [Fact]
    public void Sin_caracter_de_fin_el_codigo_termina_con_la_pausa()
    {
        var decodificador = new DecodificadorLector(FinDelCodigo.Auto, Pausa);
        decodificador.Agregar(Bytes("7501234"), Ms(0));
        decodificador.Agregar(Bytes("567890"), Ms(5));

        Assert.Null(decodificador.Pausa(Ms(80)));
        var lectura = decodificador.Pausa(Ms(105));

        Assert.Equal("7501234567890", lectura?.Codigo);
        Assert.Equal(FinLeido.Pausa, lectura?.Fin);
        Assert.Equal(Ms(5), lectura?.Tardo);
        // Lo que llega después de la pausa en la misma lectura: primero termina lo de antes.
        decodificador.Agregar(Bytes("111"), Ms(200));
        var dos = decodificador.Agregar(Bytes("222\r"), Ms(400));
        Assert.Equal(["111", "222"], dos.Select(l => l.Codigo));
    }

    [Fact]
    public void Con_un_fin_fijo_lo_que_llega_sin_el_se_descarta_y_el_LF_del_CRLF_no_estorba()
    {
        var decodificador = new DecodificadorLector(FinDelCodigo.CR, Pausa);

        var conCrLf = decodificador.Agregar(Bytes("7501\r\n7502\r\n"), Ms(0));
        Assert.Equal(["7501", "7502"], conCrLf.Select(l => l.Codigo));
        Assert.All(conCrLf, l => Assert.Equal(FinLeido.CRLF, l.Fin));

        // Con «Fin»: «CR», un pedazo que se quedó sin su CR (un código a medias) nunca entra a la venta.
        decodificador.Agregar(Bytes("75012"), Ms(1000));
        var medias = decodificador.Pausa(Ms(1200));
        Assert.Null(medias?.Codigo);
        Assert.Equal("llegó sin su fin (CR)", medias?.Descartada);
    }

    [Fact]
    public void La_basura_se_descarta_entera_y_lo_que_sigue_se_lee_bien()
    {
        var decodificador = new DecodificadorLector(FinDelCodigo.Auto, Pausa);

        // El puerto a otra velocidad: bytes que no son de un código, con su fin.
        var basura = Assert.Single(decodificador.Agregar([0x00, 0xF8, 0x37, 0x80, 0x0D], Ms(0)));
        Assert.Null(basura.Codigo);
        Assert.Contains("caracteres que no son de un código", basura.Descartada);
        Assert.Equal("<00><F8>7<80><CR>", basura.Visible);

        // Un STX empieza un código: lo de antes, sin su fin, se descarta; el código del STX sí entra.
        var conStx = decodificador.Agregar([0x31, 0x32, 0x02, .. Bytes("7501\r")], Ms(500));
        Assert.Equal([null, "7501"], conStx.Select(l => l.Codigo));

        // Algo larguísimo sin fin (ruido): se corta y se descarta, sin crecer sin límite.
        var largo = decodificador.Agregar(Enumerable.Repeat((byte)'9', DecodificadorLector.CrudoMaximo).ToArray(), Ms(900));
        Assert.Equal(FinLeido.MuyLarga, Assert.Single(largo).Fin);
        Assert.False(decodificador.HayPendiente);

        // Un código más largo que cualquiera, con su fin, tampoco entra; los espacios de los lados se quitan; un CR suelto no es nada.
        Assert.Null(Assert.Single(decodificador.Agregar(Bytes(new string('1', DecodificadorLector.LargoMaximo + 1) + "\r"), Ms(1000))).Codigo);
        Assert.Equal("A-000123", Assert.Single(decodificador.Agregar(Bytes("  A-000123 \r"), Ms(1100))).Codigo);
        Assert.Empty(decodificador.Agregar(Bytes("\r\r\n"), Ms(1500)));
    }

    // ── «Lector» en caja.json ──

    [Fact]
    public void Lector_en_caja_json_con_lo_de_eleventa_y_lo_que_falta_por_omision()
    {
        var (ajustes, problema) = AjustesLector.Leer(Json("""{ "Puerto": "COM3", "Baudios": 9600, "Paridad": "None", "BitsDeDatos": 8, "BitsDeParada": 1, "Fin": "CR" }"""));
        Assert.Null(problema);
        Assert.Equal(new AjustesLector("COM3") { Fin = FinDelCodigo.CR }, ajustes);
        Assert.Equal("COM3 a 9,600 baudios (8N1), el código termina con CR", ajustes!.Resumen);

        // Como lo dice eleventa (en español), con otro puerto escrito a medias y en minúsculas.
        var (deEleventa, _) = AjustesLector.Leer(Json("""{ "puerto": "com4", "baudios": "19200", "paridad": "Par", "bitsDeParada": "1,5", "fin": "CR+LF", "dtr": false }"""));
        Assert.Equal(new AjustesLector("COM4") { Baudios = 19200, Paridad = ParidadLector.Par, BitsDeParada = 1.5m, Fin = FinDelCodigo.CRLF, Dtr = false }, deEleventa);
        Assert.Equal("COM5", AjustesLector.Leer(Json("""{ "Puerto": "5" }""")).Ajustes?.Puerto);

        // 9600 8N1, y el código termina con CR, LF o CRLF, o con una pausa.
        Assert.Equal(new AjustesLector("COM3"), AjustesLector.Leer(Json("""{ "Puerto": "COM3" }""")).Ajustes);
        Assert.Equal("COM3 a 9,600 baudios (8N1), el código termina con CR, LF o CRLF, o con una pausa", new AjustesLector("COM3").Resumen);

        // Sin la sección: sin lector por puerto, sin problema.
        Assert.Equal((null, null), AjustesLector.Leer(null));
        Assert.Equal((null, null), AjustesLector.Leer(Json("null")));
    }

    [Theory]
    [InlineData("""{ "Baudios": 9600 }""", "falta «Puerto»")]
    [InlineData("""{ "Puerto": "COM3", "Baudios": "rápido" }""", "«Baudios» va de 50 a 921,600")]
    [InlineData("""{ "Puerto": "COM3", "Paridad": "Rara" }""", "«Paridad» no puede ser «Rara»")]
    [InlineData("""{ "Puerto": "COM3", "BitsDeParada": 3 }""", "«BitsDeParada» es 1, 1.5 o 2")]
    [InlineData("""{ "Puerto": "COM3", "Fin": "Punto" }""", "«Fin» no puede ser «Punto»")]
    [InlineData("""{ "Puerto": "COM3", "SoloAlFrente": "quizá" }""", "«SoloAlFrente» es true o false")]
    [InlineData("""["COM3"]""", "va entre llaves")]
    public void Un_error_de_dedo_en_Lector_no_impide_abrir_la_caja_y_dice_que(string json, string dice)
    {
        var (ajustes, problema) = AjustesLector.Leer(Json(json));

        Assert.Null(ajustes);
        Assert.Contains(dice, problema);
        Assert.StartsWith("«Lector» en caja.json", problema);
    }

    private static JsonElement Json(string texto)
    {
        using var documento = JsonDocument.Parse(texto);
        return documento.RootElement.Clone();
    }

    // ── El puerto: abrir, leer, ocupado, sin conectar, reconectar ──

    [Fact]
    public async Task Lee_los_codigos_en_su_orden_y_lo_anota_en_el_registro()
    {
        var puertos = new PuertosDeMentira();
        await using var lector = new LectorPorPuerto(Ajustes(), null, puertos, _registro, reintento: Ms(50));
        var codigos = new ConcurrentQueue<string>();
        lector.Codigo += l => codigos.Enqueue(l.Codigo!);
        lector.Iniciar();
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Listo, "que abra el puerto");

        var puerto = puertos.Abierto!;
        puerto.Mandar("7501\r");
        puerto.Mandar("75");
        puerto.Mandar("02\n7503\r\n");
        puerto.Mandar("7504");
        await EsperarAsync(() => codigos.Count == 4, "los cuatro códigos");

        Assert.Equal(["7501", "7502", "7503", "7504"], codigos);
        Assert.Equal(4, lector.Codigos);
        Assert.Equal(["7504", "7503", "7502", "7501"], lector.Ultimas.Select(l => l.Codigo));
        Assert.Equal(FinLeido.Pausa, lector.Ultimas[0].Fin);
        Assert.Null(lector.Estado.Chip);
        var registro = Registro();
        Assert.Contains("Lector por puerto: COM3 a 9,600 baudios (8N1), el código termina con CR, LF o CRLF, o con una pausa.", registro);
        Assert.Contains("Lector: se abrió COM3.", registro);
        // Cada código no se anota: un día de venta llenaría el registro.
        Assert.DoesNotContain("7501", registro);

        await lector.DisposeAsync();
        Assert.True(puerto.Cerrado);
        Assert.Contains("Lector: se cerró COM3 (se cerró la caja): llegaron 4 códigos.", Registro());
        Assert.Equal("llegó 1 código, se descartaron 2 lecturas que no eran un código y 3 códigos no se usaron", LectorPorPuerto.Cuenta(1, 2, 3));
        Assert.Equal("llegaron 1,500 códigos y 1 código no se usó", LectorPorPuerto.Cuenta(1500, 0, 1));
    }

    [Fact]
    public async Task La_basura_no_llega_a_la_venta_se_cuenta_y_se_anota()
    {
        var puertos = new PuertosDeMentira();
        await using var lector = new LectorPorPuerto(Ajustes(), null, puertos, _registro, reintento: Ms(50));
        var codigos = new ConcurrentQueue<string>();
        lector.Codigo += l => codigos.Enqueue(l.Codigo!);
        lector.Iniciar();
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Listo, "que abra el puerto");

        puertos.Abierto!.Mandar(0xFF, 0x00, 0xE3, 0x0D);
        puertos.Abierto.Mandar("7501234567890\r");
        await EsperarAsync(() => codigos.Count == 1, "el código bueno");

        Assert.Equal(["7501234567890"], codigos);
        Assert.Equal(1, lector.Descartadas);
        Assert.Contains("Lector: se descartó lo que llegó porque trae caracteres que no son de un código", Registro());
        Assert.Contains("<FF><00><E3><CR>", Registro());
    }

    [Fact]
    public async Task Ocupado_por_eleventa_lo_dice_arriba_reintenta_sin_llenar_el_registro_y_lo_toma_al_soltarse()
    {
        var puertos = new PuertosDeMentira { AlAbrir = PuertosDeMentira.Ocupado };
        await using var lector = new LectorPorPuerto(Ajustes(), null, puertos, _registro, reintento: Ms(30));
        var cambios = 0;
        lector.CambioDeEstado += () => Interlocked.Increment(ref cambios);
        lector.Iniciar();

        await EsperarAsync(() => puertos.Aperturas >= 5, "varios intentos");
        Assert.Equal(SituacionLector.Ocupado, lector.Estado.Situacion);
        Assert.Equal("Lector ocupado: ¿eleventa abierto?", lector.Estado.Chip);
        Assert.StartsWith("El lector de códigos (COM3) lo tiene abierto otro programa, quizá eleventa. Mientras, escribe los códigos a mano.",
            lector.Estado.Explicacion(lector.Reintento));
        // Una sola línea, aunque se intente cada pocos segundos todo el día.
        Assert.Single(Lineas(Registro(), "está ocupado"));
        Assert.Contains("Lector: COM3 está ocupado (lo tiene abierto otro programa; ¿eleventa abierto?).", Registro());
        Assert.Equal(1, Volatile.Read(ref cambios));

        // Se cierra eleventa: la caja toma el puerto sola.
        puertos.AlAbrir = () => null;
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Listo, "que tome el puerto");
        Assert.Null(lector.Estado.Chip);
        Assert.Contains("Lector: se abrió COM3.", Registro());
    }

    [Fact]
    public async Task Sin_conectar_lo_busca_y_lo_toma_al_conectarlo_y_si_se_desconecta_leyendo_lo_vuelve_a_buscar()
    {
        var puertos = new PuertosDeMentira { AlAbrir = PuertosDeMentira.NoExiste };
        puertos.Quitar("COM3");
        await using var lector = new LectorPorPuerto(Ajustes(), null, puertos, _registro, reintento: Ms(30));
        var codigos = new ConcurrentQueue<string>();
        lector.Codigo += l => codigos.Enqueue(l.Codigo!);
        lector.Iniciar();

        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.SinConectar, "sin conectar");
        Assert.Equal("Lector sin conectar", lector.Estado.Chip);
        Assert.Equal("no existe en esta PC", lector.Estado.Detalle);
        Assert.StartsWith("No se encuentra el lector de códigos en COM3: está desconectado o en otro puerto.", lector.Estado.Explicacion(lector.Reintento));

        // Se conecta el lector USB: aparece el puerto y la caja lo toma sola.
        puertos.Poner("COM3");
        puertos.AlAbrir = () => null;
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Listo, "que lo tome al conectarlo");
        var primero = puertos.Abierto!;
        primero.Mandar("7501\r");
        await EsperarAsync(() => codigos.Count == 1, "un código");

        // Se desconecta a media venta: lo vuelve a buscar (sin conectar mientras no regresa) y lo toma al regresar.
        puertos.Quitar("COM3");
        puertos.AlAbrir = PuertosDeMentira.NoExiste;
        primero.Mandar("75");
        primero.Desconectar();
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.SinConectar, "sin conectar otra vez");
        Assert.True(primero.Cerrado);
        // Por el error al leer o porque el puerto salió de la lista: lo que se note primero.
        Assert.Matches(@"Lector: se desconectó COM3 \(IOException: (The device does not recognize the command|Ya no está en la lista de puertos de la PC)\)\. Se vuelve a buscar\.", Registro());
        puertos.Poner("COM3");
        puertos.AlAbrir = () => null;
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Listo && !ReferenceEquals(puertos.Abierto, primero), "que lo tome al regresar");
        puertos.Abierto!.Mandar("7502\r");
        await EsperarAsync(() => codigos.Count == 2, "el código de después");

        // El «75» que quedó a medias al desconectarse no se pegó al siguiente.
        Assert.Equal(["7501", "7502"], codigos);
        Assert.Equal(2, Lineas(Registro(), "Lector: COM3 no existe en esta PC").Count);
    }

    [Fact]
    public async Task Un_lector_USB_que_se_desconecta_sin_error_al_leer_se_nota_porque_su_puerto_sale_de_la_PC()
    {
        var puertos = new PuertosDeMentira();
        await using var lector = new LectorPorPuerto(Ajustes(), null, puertos, _registro, reintento: Ms(30));
        lector.Iniciar();
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Listo, "que abra el puerto");
        var primero = puertos.Abierto!;

        // Algunos controladores no dan error al leer: el puerto solo desaparece de la lista de la PC.
        puertos.AlAbrir = PuertosDeMentira.NoExiste;
        puertos.Quitar("COM3");
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.SinConectar, "sin conectar");
        Assert.True(primero.Cerrado);
        Assert.Contains("Lector: se desconectó COM3 (IOException: Ya no está en la lista de puertos de la PC). Se vuelve a buscar.", Registro());

        // Un puerto que abre sin estar en la lista (uno virtual) no se da por desconectado: se queda leyendo.
        var virtuales = new PuertosDeMentira();
        virtuales.Quitar("COM3");
        await using var otro = new LectorPorPuerto(Ajustes(), null, virtuales, _registro, reintento: Ms(30));
        otro.Iniciar();
        await EsperarAsync(() => otro.Estado.Situacion == SituacionLector.Listo, "que abra el virtual");
        await Task.Delay(200);
        Assert.Equal(SituacionLector.Listo, otro.Estado.Situacion);
        Assert.Equal(1, virtuales.Aperturas);
    }

    [Fact]
    public async Task Un_puerto_que_regresa_vacio_sin_esperar_no_hace_girar_al_lector_se_da_por_desconectado()
    {
        var puertos = new PuertosDeMentira();
        await using var lector = new LectorPorPuerto(Ajustes(), null, puertos, _registro, reintento: Ms(200));
        lector.Iniciar();
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Listo, "que abra el puerto");

        puertos.AlAbrir = PuertosDeMentira.Ocupado;
        puertos.Abierto!.RegresaVacio = true;
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Ocupado, "que lo dé por desconectado y lo busque otra vez");
        Assert.Contains("Lector: se desconectó COM3 (IOException: Regresa sin datos y sin esperar). Se vuelve a buscar.", Registro());
    }

    [Theory]
    [InlineData(true, SituacionLector.SinConectar)]
    [InlineData(false, SituacionLector.SinConectar)]
    public void Por_que_no_abre_dicho_para_la_tienda(bool enLaLista, SituacionLector esperada)
    {
        Assert.Equal(SituacionLector.Ocupado, LectorPorPuerto.Clasificar(new UnauthorizedAccessException("Access to the port 'COM3' is denied."), true).Situacion);
        Assert.Equal(SituacionLector.Ocupado, LectorPorPuerto.Clasificar(new IOException("Sharing violation", unchecked((int)0x80070020)), true).Situacion);
        Assert.Equal(SituacionLector.SinConectar, LectorPorPuerto.Clasificar(new FileNotFoundException(), enLaLista).Situacion);
        Assert.Equal(esperada, LectorPorPuerto.Clasificar(new IOException("The port 'COM3' does not exist."), enLaLista).Situacion);
        // Un USB que no contesta (ERROR_GEN_FAILURE) estando en la lista: sin conectar, «no contesta».
        Assert.Equal((SituacionLector.SinConectar, "no contesta"), LectorPorPuerto.Clasificar(new IOException("A device attached to the system is not functioning.", unchecked((int)0x8007001F)), true));
        Assert.Equal(SituacionLector.ConError, LectorPorPuerto.Clasificar(new IOException("The parameter is incorrect.", unchecked((int)0x80070057)), true).Situacion);
        Assert.Equal(SituacionLector.ConError, LectorPorPuerto.Clasificar(new ArgumentException("The given port name does not start with COM/com."), null).Situacion);
        // Fuera de Windows, System.IO.Ports dice «acceso denegado» con todo; el porqué viene adentro (ENOENT, EBUSY).
        var noExiste = new UnauthorizedAccessException("Access to the port '/dev/ttyUSB0' is denied.", new IOException("No such file or directory", 2));
        var ocupado = new UnauthorizedAccessException("Access to the port '/dev/ttyUSB0' is denied.", new IOException("Device or resource busy", 16));
        Assert.Equal(OperatingSystem.IsWindows() ? SituacionLector.Ocupado : SituacionLector.SinConectar, LectorPorPuerto.Clasificar(noExiste, false).Situacion);
        Assert.Equal(SituacionLector.Ocupado, LectorPorPuerto.Clasificar(ocupado, true).Situacion);
    }

    [Fact]
    public async Task Sin_Lector_en_caja_json_no_abre_nada_y_con_un_error_de_dedo_lo_dice_sin_intentar()
    {
        var puertos = new PuertosDeMentira();
        await using (var sin = new LectorPorPuerto(null, null, puertos, _registro))
        {
            sin.Iniciar();
            Assert.Equal(SituacionLector.SinLector, sin.Estado.Situacion);
            Assert.Null(sin.Estado.Chip);
        }

        var (ajustes, problema) = AjustesLector.Leer(Json("""{ "Puerto": "COM3", "Baudios": "rápido" }"""));
        await using var malo = new LectorPorPuerto(ajustes, problema, puertos, _registro);
        malo.Iniciar();
        Assert.Equal(SituacionLector.ConError, malo.Estado.Situacion);
        Assert.Equal("Lector con error", malo.Estado.Chip);
        Assert.StartsWith("«Lector» en caja.json: «Baudios» va de 50 a 921,600", malo.Estado.Explicacion(malo.Reintento));
        await Task.Delay(100);
        Assert.Equal(0, puertos.Aperturas);
    }

    [Fact]
    public async Task Con_SoloAlFrente_suelta_el_puerto_mientras_la_caja_no_esta_al_frente_y_lo_vuelve_a_tomar()
    {
        var puertos = new PuertosDeMentira();
        await using var lector = new LectorPorPuerto(Ajustes(soloAlFrente: true), null, puertos, _registro, reintento: Ms(30));
        lector.Iniciar();
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Listo, "que abra el puerto");
        var primero = puertos.Abierto!;

        lector.AlFrente(false);
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Suelto, "que lo suelte");
        Assert.True(primero.Cerrado);
        Assert.Null(lector.Estado.Chip);
        await Task.Delay(100);
        Assert.Equal(1, puertos.Aperturas);

        lector.AlFrente(true);
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Listo, "que lo vuelva a tomar");
        Assert.Equal(2, puertos.Aperturas);
        // Cambiar de ventana no llena el registro.
        Assert.Single(Lineas(Registro(), "Lector: se abrió COM3."));

        // Sin «SoloAlFrente», cambiar de ventana no cambia nada.
        await using var siempre = new LectorPorPuerto(Ajustes(), null, new PuertosDeMentira(), _registro, reintento: Ms(30));
        siempre.Iniciar();
        await EsperarAsync(() => siempre.Estado.Situacion == SituacionLector.Listo, "que abra el otro");
        siempre.AlFrente(false);
        await Task.Delay(100);
        Assert.Equal(SituacionLector.Listo, siempre.Estado.Situacion);
    }

    // ── La entrega en la pantalla ──

    [Fact]
    public async Task Los_codigos_llegan_a_la_pagina_uno_tras_otro_y_en_su_orden()
    {
        var puertos = new PuertosDeMentira();
        await using var lector = new LectorPorPuerto(Ajustes(), null, puertos, _registro, reintento: Ms(30));
        var enLaPagina = new ConcurrentQueue<string>();
        var aLaVez = 0;
        var maximo = 0;
        await using var entrega = new EntregaDelLector(lector, () => true, async codigo =>
        {
            var ahora = Interlocked.Increment(ref aLaVez);
            maximo = Math.Max(maximo, ahora);
            await Task.Delay(Random.Shared.Next(0, 4));
            enLaPagina.Enqueue(codigo);
            Interlocked.Decrement(ref aLaVez);
            return "captura";
        }, _registro);
        entrega.Iniciar();
        lector.Iniciar();
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Listo, "que abra el puerto");

        // 20 códigos seguidos, en una sola lectura del puerto.
        var codigos = Enumerable.Range(1, 20).Select(i => $"75000000000{i:00}").ToList();
        puertos.Abierto!.Mandar(string.Concat(codigos.Select(c => c + "\r\n")));
        await EsperarAsync(() => enLaPagina.Count == 20, "los 20 en la página");

        Assert.Equal(codigos, enLaPagina);
        Assert.Equal(1, maximo);
        await EsperarAsync(() => lector.Ultimas.All(l => l.Entrega is not null), "a dónde fue cada uno");
        Assert.All(lector.Ultimas, l => Assert.Equal("a la venta (el cuadro de captura)", l.Entrega));
        Assert.Equal(0, lector.SinUsar);
    }

    [Fact]
    public async Task Con_la_caja_detras_de_otra_ventana_el_codigo_no_se_usa_y_se_anota()
    {
        await using var lector = new LectorPorPuerto(Ajustes(), null, new PuertosDeMentira(), _registro);
        var pedidos = 0;
        await using var entrega = new EntregaDelLector(lector, () => false, _ => { pedidos++; return Task.FromResult<string?>("captura"); }, _registro);
        var lectura = new Lectura { Codigo = "7501234567890" };

        await entrega.EntregarAsync(lectura);

        Assert.Equal(0, pedidos);
        Assert.Equal("no se usó: la ventana de la caja no estaba al frente", lectura.Entrega);
        Assert.False(lectura.Usada);
        Assert.Equal(1, lector.SinUsar);
        Assert.Contains("un código no se usó: la ventana de la caja no estaba al frente", Registro());
    }

    [Fact]
    public async Task Lo_que_la_pagina_hizo_con_cada_codigo_dicho_para_la_tienda()
    {
        Assert.Equal(("a la venta (el cuadro de captura)", true), EntregaDelLector.Describir("captura"));
        Assert.Equal(("al ticket, desde el cobro (nunca como dinero)", true), EntregaDelLector.Describir("cobro"));
        Assert.Equal(("a «Varios» (Ins)", true), EntregaDelLector.Describir("varios"));
        Assert.Equal(("al campo «Código del producto»", true), EntregaDelLector.Describir("campo:Código del producto"));
        Assert.Equal(("no se usó: el cursor estaba en «Motivo», que no es para códigos", false), EntregaDelLector.Describir("sin-lugar:Motivo"));
        Assert.False(EntregaDelLector.Describir("protegido:Efectivo contado").Usada);
        Assert.False(EntregaDelLector.Describir("sin-caja").Usada);
        Assert.Equal(("no se usó: la página no contestó", false), EntregaDelLector.Describir(null));

        // La página se está volviendo a cargar: el código se pierde, y lo dice el registro (no se cae nada).
        await using var lector = new LectorPorPuerto(Ajustes(), null, new PuertosDeMentira(), _registro);
        await using var entrega = new EntregaDelLector(lector, () => true, _ => throw new InvalidOperationException("La página no está lista."), _registro);
        var lectura = new Lectura { Codigo = "7501" };
        await entrega.EntregarAsync(lectura);
        Assert.Equal("no se usó: la página no contestó", lectura.Entrega);
        Assert.Contains("Lector: la página no recibió un código", Registro());
    }

    private static List<string> Lineas(string registro, string contiene) =>
        registro.Split(Environment.NewLine).Where(l => l.Contains(contiene, StringComparison.Ordinal)).ToList();
}
