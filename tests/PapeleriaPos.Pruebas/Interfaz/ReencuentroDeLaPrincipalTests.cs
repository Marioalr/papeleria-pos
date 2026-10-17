using System.Net;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La caja vuelve a encontrar sola a la caja principal si le cambió la IP (la red de la tienda en el POS): busca en la red cuando
/// la dirección guardada no contesta un rato, y solo se cambia a un servicio que prueba con la huella de la llave de esta caja que
/// es su caja principal. Sin red de verdad: lo que contesta en la red y cada servicio son de mentira, y el reloj avanza a mano.
/// </summary>
public sealed class ReencuentroDeLaPrincipalTests : IDisposable
{
    private static readonly Guid CajaId = Guid.Parse("7d1f0f6e-58f5-4c4c-9a63-1d5b8a0c2b11");
    private static readonly string Llave = LlaveCaja.Nueva();
    private static readonly Uri Guardada = new("http://192.168.0.10:5080/");
    private static readonly Uri Nueva = new("http://192.168.0.57:5080/");
    private static readonly Uri Impostor = new("http://192.168.0.66:5080/");
    private static readonly Uri QuePasaLaPregunta = new("http://192.168.0.77:5080/");

    private readonly RelojDeLaRed _reloj = RelojDeLaRed.Nuevo();
    private readonly CarpetaDePrueba _carpeta = new();
    private readonly ServiciosEnLaRed _servicios = new();
    private readonly BuscadorFalso _buscador = new();
    private readonly List<Uri> _guardadas = [];

    public void Dispose() => _carpeta.Dispose();

    private ConexionCaja Conexion(Uri? servicio = null, string? llave = null) => new()
    {
        Servicio = servicio ?? Guardada,
        CajaId = CajaId,
        Llave = llave ?? Llave,
        PideAlta = true,
        AlGuardar = c =>
        {
            _guardadas.Add(c.Servicio);
            return Task.CompletedTask;
        },
    };

    private (ReencuentroDeLaPrincipal Reencuentro, BitacoraDeRed Bitacora) Nuevo(ConexionCaja conexion)
    {
        var bitacora = new BitacoraDeRed(_carpeta.Ruta, "red-caja", _reloj);
        bitacora.Empezar("esta caja");
        return (new ReencuentroDeLaPrincipal(conexion, _buscador, new HttpClient(_servicios), _reloj, bitacora, () => RedDeLaCaja2), bitacora);
    }

    /// <summary>La red de la caja 2: 192.168.0.0/24.</summary>
    private static readonly IReadOnlyList<(IPAddress, int)> RedDeLaCaja2 = [(IPAddress.Parse("192.168.0.11"), 24)];

    /// <summary>
    /// Contestan tres en la red: uno que no sabe la prueba, uno que le pasa la pregunta a la caja principal de verdad (su prueba es
    /// buena, pero para la dirección de la caja principal, no para la suya) y la caja principal en su IP nueva. Solo se cambia a esa,
    /// y la guarda.
    /// </summary>
    [Fact]
    public async Task Solo_se_cambia_a_la_que_prueba_con_la_llave_de_esta_caja_que_es_su_caja_principal()
    {
        _servicios.Impostores.Add(Impostor);
        _servicios.QuePasanLaPregunta[QuePasaLaPregunta] = Nueva;
        _servicios.Principal = Nueva;
        _buscador.Contestan = [Impostor, QuePasaLaPregunta, Nueva];
        var conexion = Conexion();
        var (reencuentro, _) = Nuevo(conexion);

        var cambio = await reencuentro.BuscarAhoraAsync();

        Assert.True(cambio);
        Assert.Equal(Nueva, conexion.Servicio);
        Assert.Equal([Nueva], _guardadas);
        Assert.Equal("Se encontró la caja principal en 192.168.0.57:5080.", reencuentro.Resultado);
        Assert.Contains(_carpeta.Lineas(), l => l.StartsWith("La caja principal cambió de dirección: 192.168.0.10:5080 → 192.168.0.57:5080.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Si_ninguna_prueba_ser_su_caja_principal_no_se_cambia_ni_se_guarda_nada()
    {
        _servicios.Impostores.Add(Impostor);
        _servicios.QuePasanLaPregunta[QuePasaLaPregunta] = Nueva;
        _servicios.Principal = Nueva;
        _buscador.Contestan = [Impostor, QuePasaLaPregunta];
        var conexion = Conexion();
        var (reencuentro, _) = Nuevo(conexion);

        var cambio = await reencuentro.BuscarAhoraAsync();

        Assert.False(cambio);
        Assert.Equal(Guardada, conexion.Servicio);
        Assert.Empty(_guardadas);
        Assert.Equal("Contestó 192.168.0.66:5080 y 192.168.0.77:5080, pero no probó ser la caja principal de esta caja: no se usa.", reencuentro.Resultado);
        Assert.Contains(_carpeta.Lineas(),
            l => l == "Contestó 192.168.0.66:5080 y 192.168.0.77:5080, pero no probó con la llave de esta caja que es su caja principal: no se usa.");
    }

    /// <summary>A quien contesta en la red no se le manda la llave: ni en el encabezado ni en la dirección; solo el número de la caja y el reto.</summary>
    [Fact]
    public async Task La_llave_de_la_caja_no_viaja_a_lo_que_contesta_en_la_red()
    {
        _servicios.Impostores.Add(Impostor);
        _servicios.Principal = Nueva;
        _buscador.Contestan = [Impostor, Nueva];
        var (reencuentro, _) = Nuevo(Conexion());

        await reencuentro.BuscarAhoraAsync();

        Assert.Equal(2, _servicios.Preguntas.Count);
        Assert.All(_servicios.Preguntas, p =>
        {
            Assert.False(p.Headers.Contains(ClienteCaja.EncabezadoLlave));
            Assert.DoesNotContain(Llave, p.RequestUri!.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(LlaveCaja.Huella(Llave), p.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
        });
        // Un reto distinto cada vez: una respuesta vieja no sirve.
        Assert.Equal(2, _servicios.Preguntas.Select(p => p.RequestUri!.Query).Distinct().Count());
    }

    /// <summary>Con la llave de otra caja (o la de antes de volver a darla de alta), la caja principal de verdad no la prueba: no se cambia.</summary>
    [Fact]
    public async Task Con_otra_llave_la_prueba_no_coincide()
    {
        _servicios.Principal = Nueva;
        _buscador.Contestan = [Nueva];
        var conexion = Conexion(llave: LlaveCaja.Nueva());
        var (reencuentro, _) = Nuevo(conexion);

        Assert.False(await reencuentro.BuscarAhoraAsync());
        Assert.Equal(Guardada, conexion.Servicio);
    }

    /// <summary>
    /// Mientras la dirección guardada contesta, no busca nada. Si deja de contestar, busca a los 30 s y, si no la encuentra, cada
    /// vez más espaciado (30 s, 1 min, 2 min y luego cada 5 min): durante una caída de horas no llena la red ni la bitácora.
    /// </summary>
    [Fact]
    public async Task Busca_solo_cuando_no_contesta_un_rato_y_cada_vez_mas_espaciado()
    {
        _buscador.Contestan = [];
        var (reencuentro, _) = Nuevo(Conexion());

        for (var i = 0; i < 100; i++)
        {
            reencuentro.Latido(contesto: true);
            _reloj.Avanzar(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, _buscador.Veces);

        // Cada latido es de 5 s: busca a los 30 s sin contestar, luego a 30 s, 1 min, 2 min, 5 min y 5 min de la anterior.
        var cuando = new List<TimeSpan>();
        var desde = _reloj.Ahora;
        for (var i = 0; i <= 12 * 15; i++)
        {
            reencuentro.Latido(contesto: false);
            await reencuentro.UltimaBusqueda;
            if (_buscador.Veces > cuando.Count)
                cuando.Add(_reloj.Ahora - desde);
            _reloj.Avanzar(TimeSpan.FromSeconds(5));
        }

        Assert.Equal([30, 60, 120, 240, 540, 840], cuando.Select(t => (int)t.TotalSeconds).ToList());
    }

    /// <summary>En cuanto contesta otra vez, deja de buscar y lo anota; la siguiente vez que no conteste, empieza de nuevo.</summary>
    [Fact]
    public async Task Cuando_contesta_otra_vez_deja_de_buscar()
    {
        _buscador.Contestan = [];
        var (reencuentro, _) = Nuevo(Conexion());
        for (var i = 0; i <= 6; i++)
        {
            reencuentro.Latido(contesto: false);
            await reencuentro.UltimaBusqueda;
            _reloj.Avanzar(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, _buscador.Veces);

        reencuentro.Latido(contesto: true);
        reencuentro.Latido(contesto: false);
        await reencuentro.UltimaBusqueda;

        Assert.Equal(1, _buscador.Veces);
        Assert.Null(reencuentro.Resultado);
        Assert.Contains("La caja principal contestó otra vez: se deja de buscarla (se buscó una vez).", _carpeta.Lineas());
    }

    /// <summary>
    /// Si la caja principal contesta otra vez mientras se está buscando, la búsqueda termina sin error y sin decir nada: no queda
    /// un «no contestó» viejo para la siguiente caída, y la siguiente vuelve a esperar sus 30 s.
    /// </summary>
    [Fact]
    public async Task Si_contesta_mientras_se_busca_la_busqueda_termina_sin_decir_nada()
    {
        var detenido = new TaskCompletionSource();
        _buscador.Antes = detenido.Task;
        _buscador.Contestan = [];
        var (reencuentro, _) = Nuevo(Conexion());
        for (var i = 0; i <= 6; i++)
        {
            reencuentro.Latido(contesto: false);
            _reloj.Avanzar(TimeSpan.FromSeconds(5));
        }
        Assert.True(reencuentro.Buscando);

        reencuentro.Latido(contesto: true);
        detenido.SetResult();
        await reencuentro.UltimaBusqueda;

        Assert.Null(reencuentro.Resultado);
        Assert.DoesNotContain(_carpeta.Lineas(), l => l.StartsWith("No contestó", StringComparison.Ordinal));
        reencuentro.Latido(contesto: false);
        await reencuentro.UltimaBusqueda;
        Assert.Equal(1, _buscador.Veces);
    }

    /// <summary>Uno que se corta a media respuesta no detiene la búsqueda: el siguiente, la caja principal, se revisa y se usa.</summary>
    [Fact]
    public async Task Uno_que_se_corta_a_media_respuesta_no_detiene_la_busqueda()
    {
        _servicios.Cortados.Add(Impostor);
        _servicios.Principal = Nueva;
        _buscador.Contestan = [Impostor, Nueva];
        var conexion = Conexion();
        var (reencuentro, _) = Nuevo(conexion);

        Assert.True(await reencuentro.BuscarAhoraAsync());
        Assert.Equal(Nueva, conexion.Servicio);
    }

    /// <summary>Un servicio que contesta de más (más que una prueba) no se lee completo ni se usa.</summary>
    [Fact]
    public async Task Una_respuesta_de_mas_no_se_usa()
    {
        _servicios.Principal = Nueva;
        _servicios.Relleno = new string(' ', ReencuentroDeLaPrincipal.TopeDeLaRespuesta);
        _buscador.Contestan = [Nueva];
        var conexion = Conexion();
        var (reencuentro, _) = Nuevo(conexion);

        Assert.False(await reencuentro.BuscarAhoraAsync());
        Assert.Equal(Guardada, conexion.Servicio);
    }

    /// <summary>
    /// Durante horas sin encontrarla, la bitácora dice una vez que se buscó y lo que contestó (no en cada búsqueda), y lo vuelve a
    /// decir solo si contesta algo distinto.
    /// </summary>
    [Fact]
    public async Task Buscar_muchas_veces_no_llena_la_bitacora()
    {
        _buscador.Contestan = [];
        var (reencuentro, _) = Nuevo(Conexion());

        for (var i = 0; i < 20; i++)
            await reencuentro.BuscarAhoraAsync();
        _servicios.Impostores.Add(Impostor);
        _buscador.Contestan = [Impostor];
        for (var i = 0; i < 20; i++)
            await reencuentro.BuscarAhoraAsync();

        var lineas = _carpeta.Lineas();
        Assert.Single(lineas, l => l.StartsWith("Se busca la caja principal en la red", StringComparison.Ordinal));
        Assert.Single(lineas, l => l.StartsWith("No contestó ninguna caja principal", StringComparison.Ordinal));
        Assert.Single(lineas, l => l.StartsWith("Contestó 192.168.0.66:5080", StringComparison.Ordinal));
    }

    /// <summary>
    /// Sin llave (la caja del navegador o la de la prueba), con la caja principal en la misma PC o simulando que no hay conexión,
    /// no hay a quién buscar.
    /// </summary>
    [Theory]
    [InlineData("sin llave")]
    [InlineData("en esta PC")]
    [InlineData("simulando")]
    public async Task Sin_llave_o_con_la_principal_en_esta_pc_no_busca(string como)
    {
        _buscador.Contestan = [Nueva];
        _servicios.Principal = Nueva;
        var conexion = como switch
        {
            "sin llave" => new ConexionCaja { Servicio = Guardada },
            "en esta PC" => Conexion(new Uri("http://localhost:5080/")),
            _ => Conexion(),
        };
        conexion.SimularSinConexion = como == "simulando";
        var (reencuentro, _) = Nuevo(conexion);

        for (var i = 0; i <= 20; i++)
        {
            reencuentro.Latido(contesto: false);
            _reloj.Avanzar(TimeSpan.FromSeconds(5));
        }

        Assert.False(reencuentro.Aplica);
        Assert.False(await reencuentro.BuscarAhoraAsync());
        Assert.Equal(0, _buscador.Veces);
    }

    /// <summary>Si contesta en la dirección guardada y prueba quién es, no hay nada que cambiar: el latido vuelve solo; lo dice.</summary>
    [Fact]
    public async Task Si_la_guardada_contesta_y_prueba_quien_es_no_se_cambia()
    {
        _buscador.Contestan = [Guardada];
        _servicios.Principal = Guardada;
        var conexion = Conexion();
        var (reencuentro, _) = Nuevo(conexion);

        Assert.False(await reencuentro.BuscarAhoraAsync());
        Assert.Equal(Guardada, conexion.Servicio);
        Assert.Empty(_guardadas);
        Assert.Equal("La caja principal contesta en 192.168.0.10:5080 y probó quién es: la caja sigue intentando.", reencuentro.Resultado);
    }

    /// <summary>
    /// Un equipo de la red contesta por UDP haciéndose pasar por la dirección guardada (que ya no es de la caja principal): eso solo
    /// no detiene la búsqueda. La guardada cuenta como «la misma» solo si prueba quién es; la de verdad, en su IP nueva, sí se usa.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_falsa_desde_la_ip_guardada_no_detiene_la_busqueda()
    {
        _buscador.Contestan = [Guardada, Nueva];
        _servicios.Principal = Nueva;
        var conexion = Conexion();
        var (reencuentro, _) = Nuevo(conexion);

        Assert.True(await reencuentro.BuscarAhoraAsync());
        Assert.Equal(Nueva, conexion.Servicio);
    }

    /// <summary>Muchas respuestas falsas desde un mismo equipo (con otros puertos) cuentan como una: la caja principal se revisa igual.</summary>
    [Fact]
    public async Task Muchas_respuestas_falsas_desde_un_equipo_no_dejan_fuera_a_la_principal()
    {
        _buscador.Contestan = [.. Enumerable.Range(1, 20).Select(puerto => new Uri($"http://192.168.0.66:{puerto}/")), Nueva];
        _servicios.Principal = Nueva;
        var conexion = Conexion();
        var (reencuentro, _) = Nuevo(conexion);

        Assert.True(await reencuentro.BuscarAhoraAsync());
        Assert.Equal(Nueva, conexion.Servicio);
        Assert.Equal(2, _servicios.Preguntas.Count);
    }

    /// <summary>
    /// Si dos prueban ser la caja principal (una puede ser una copia de su base: la PC de antes, un respaldo en otra PC), no se
    /// cambia sola a ninguna: sigue con su copia, la franja dice que se avise al dueño y la bitácora lo anota una vez.
    /// </summary>
    [Fact]
    public async Task Si_dos_prueban_ser_la_caja_principal_no_se_cambia_sola_a_ninguna()
    {
        var copia = new Uri("http://192.168.0.99:5080/");
        _servicios.Principal = Nueva;
        _servicios.Copias.Add(copia);
        _buscador.Contestan = [copia, Nueva];
        var conexion = Conexion();
        var (reencuentro, _) = Nuevo(conexion);

        Assert.False(await reencuentro.BuscarAhoraAsync());
        Assert.False(await reencuentro.BuscarAhoraAsync());

        Assert.Equal(Guardada, conexion.Servicio);
        Assert.Empty(_guardadas);
        Assert.Equal(ReencuentroDeLaPrincipal.DosPrincipales, reencuentro.Resultado);
        Assert.Single(_carpeta.Lineas(), l => l.StartsWith("Contestaron 192.168.0.99:5080 y 192.168.0.57:5080, y las dos probaron", StringComparison.Ordinal));
    }

    /// <summary>Lo que contesta desde otra red no es de la tienda: ni se le pregunta.</summary>
    [Fact]
    public async Task Lo_que_contesta_desde_otra_red_no_se_revisa()
    {
        _buscador.Contestan = [new Uri("http://10.8.0.5:5080/")];
        var (reencuentro, _) = Nuevo(Conexion());

        Assert.False(await reencuentro.BuscarAhoraAsync());
        Assert.Empty(_servicios.Preguntas);
    }

    /// <summary>
    /// Los tiempos de la búsqueda van con la marca de tiempo de la PC, no con la hora: si Windows corrige su hora en plena caída
    /// (una hora para atrás o para adelante), no se deja de buscar ni se busca antes de tiempo.
    /// </summary>
    [Fact]
    public async Task Si_windows_corrige_su_hora_la_busqueda_no_se_atrasa_ni_se_adelanta()
    {
        _buscador.Contestan = [];
        var (reencuentro, _) = Nuevo(Conexion());

        reencuentro.Latido(contesto: false);
        _reloj.Avanzar(TimeSpan.FromSeconds(5));
        _reloj.CambiarLaHora(TimeSpan.FromHours(1));
        reencuentro.Latido(contesto: false);
        await reencuentro.UltimaBusqueda;
        Assert.Equal(0, _buscador.Veces);

        _reloj.CambiarLaHora(TimeSpan.FromHours(-2));
        for (var i = 0; i < 5; i++)
        {
            _reloj.Avanzar(TimeSpan.FromSeconds(5));
            reencuentro.Latido(contesto: false);
            await reencuentro.UltimaBusqueda;
        }
        Assert.Equal(1, _buscador.Veces);
    }

    /// <summary>Lo que contesta en la red, como se le pida.</summary>
    private sealed class BuscadorFalso : IBuscadorDeLaPrincipal
    {
        private int _veces;

        public IReadOnlyList<Uri> Contestan { get; set; } = [];

        /// <summary>Si se pone, la búsqueda espera a que termine (para ver qué pasa mientras se busca).</summary>
        public Task? Antes { get; set; }

        public int Veces => Volatile.Read(ref _veces);

        public async Task<IReadOnlyList<Uri>> BuscarAsync(TimeSpan espera, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _veces);
            if (Antes is { } antes)
                await antes;
            return Contestan;
        }
    }

    /// <summary>Una respuesta que se corta: al leerla, la conexión ya no está.</summary>
    private sealed class SeCorta : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Se cortó la conexión.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Se cortó la conexión."));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Los servicios de la red, de mentira: la caja principal (que sabe la huella de la llave de esta caja y firma con su propia
    /// dirección), los que contestan cualquier cosa y los que le pasan la pregunta a la caja principal y devuelven su respuesta.
    /// </summary>
    private sealed class ServiciosEnLaRed : HttpMessageHandler
    {
        public Uri? Principal { get; set; }

        /// <summary>Otros servicios con una copia de la base: saben la huella de la llave de esta caja y firman con su dirección.</summary>
        public List<Uri> Copias { get; } = [];

        public List<Uri> Impostores { get; } = [];

        /// <summary>Este le pasa la pregunta a aquel y contesta lo que aquel conteste.</summary>
        public Dictionary<Uri, Uri> QuePasanLaPregunta { get; } = [];

        public List<HttpRequestMessage> Preguntas { get; } = [];

        /// <summary>Lo que la caja principal manda de más después de su prueba (para una respuesta demasiado larga).</summary>
        public string Relleno { get; set; } = "";

        /// <summary>Estos contestan, pero la conexión se corta a media respuesta.</summary>
        public List<Uri> Cortados { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Preguntas)
                Preguntas.Add(request);
            var uri = request.RequestUri!;
            if (uri.AbsolutePath != "/api/identidad")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var donde = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
            var consulta = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var caja = Guid.Parse(consulta["caja"]!);
            var reto = consulta["reto"]!;
            if (Cortados.Contains(donde))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SeCorta()) });
            if (Impostores.Contains(donde))
                return Responder(new IdentidadServicioDto(Descubrimiento.Prueba(LlaveCaja.Huella("otra"), caja, reto, Descubrimiento.Direccion(donde)!)));
            if (QuePasanLaPregunta.TryGetValue(donde, out var aQuien))
                return Responder(new IdentidadServicioDto(Descubrimiento.Prueba(LlaveCaja.Huella(Llave), caja, reto, Descubrimiento.Direccion(aQuien)!)));
            if ((donde == Principal || Copias.Contains(donde)) && caja == CajaId)
                return Responder(new IdentidadServicioDto(Descubrimiento.Prueba(LlaveCaja.Huella(Llave), caja, reto, Descubrimiento.Direccion(donde)!)), Relleno);
            throw new HttpRequestException(HttpRequestError.ConnectionError, "No contesta.");
        }

        private static Task<HttpResponseMessage> Responder(IdentidadServicioDto identidad, string relleno = "") =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(identidad, System.Text.Json.JsonSerializerOptions.Web) + relleno,
                    System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
