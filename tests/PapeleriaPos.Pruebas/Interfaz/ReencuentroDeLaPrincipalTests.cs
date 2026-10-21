using System.Net;
using System.Security.Cryptography;
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
        // Lo decide el dueño: la franja ofrece darla de alta otra vez (con «Buscar en la red» elige la de verdad).
        Assert.Equal(ReencuentroDeLaPrincipal.DosPrincipalesAlDarDeAlta, reencuentro.AltaPorque);
        Assert.Single(_carpeta.Lineas(), l => l.StartsWith("Contestaron 192.168.0.99:5080 y 192.168.0.57:5080, y prueban ser cajas principales distintas",
            StringComparison.Ordinal));
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

    // ── La llave propia del servicio ────────────────────────────────────────

    /// <summary>La llave del servicio de la caja principal de esta caja, y la de otro servicio (una copia de su base, en otra PC).</summary>
    private static readonly ECDsa LlaveDeLaPrincipal = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static readonly ECDsa LlaveDeOtra = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static string Publica(ECDsa llave) => Convert.ToBase64String(llave.ExportSubjectPublicKeyInfo());

    /// <summary>
    /// Esta caja ya conoce la llave de su caja principal: una copia de su base en otra PC (sabe la huella de la llave de la caja,
    /// pero no firma con la llave del servicio) no gana, aunque sea la única que contesta. Puede ser también la caja principal
    /// reinstalada o en otra PC, con otra IP: la franja lo dice y ofrece darla de alta otra vez (lo decide el dueño).
    /// </summary>
    [Fact]
    public async Task Con_la_llave_del_servicio_una_copia_de_la_base_no_gana_aunque_sea_la_unica()
    {
        var copia = new Uri("http://192.168.0.99:5080/");
        _servicios.Copias.Add(copia);
        _servicios.LlavesDe[copia] = LlaveDeOtra;
        _buscador.Contestan = [copia];
        var conexion = Conexion();
        conexion.LlaveDelServicio = Publica(LlaveDeLaPrincipal);
        var (reencuentro, _) = Nuevo(conexion);

        Assert.False(await reencuentro.BuscarAhoraAsync());

        Assert.Equal(Guardada, conexion.Servicio);
        Assert.Empty(_guardadas);
        Assert.Equal(ReencuentroDeLaPrincipal.OtraCajaPrincipal, reencuentro.Resultado);
        Assert.Equal(ReencuentroDeLaPrincipal.OtraCajaPrincipal, reencuentro.AltaPorque);
        Assert.Contains(_carpeta.Lineas(), l => l.StartsWith("Contestó 192.168.0.99:5080, que sabe de esta caja pero no firma con la llave de su caja principal",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// Con la llave del servicio, contestan dos que saben de esta caja y firman con llaves distintas (la PC de antes y una laptop
    /// con un respaldo): son dos, no se usa ninguna. Si firman con la misma llave, es una sola PC que contesta por dos tarjetas.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Con_la_llave_del_servicio_dos_que_no_firman_como_la_de_siempre(bool conLaMismaLlave)
    {
        var copia = new Uri("http://192.168.0.99:5080/");
        var otraCopia = new Uri("http://192.168.0.98:5080/");
        using var deLaOtra = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _servicios.Copias.Add(copia);
        _servicios.Copias.Add(otraCopia);
        _servicios.LlavesDe[copia] = LlaveDeOtra;
        _servicios.LlavesDe[otraCopia] = conLaMismaLlave ? LlaveDeOtra : deLaOtra;
        _buscador.Contestan = [copia, otraCopia];
        var conexion = Conexion();
        conexion.LlaveDelServicio = Publica(LlaveDeLaPrincipal);
        var (reencuentro, _) = Nuevo(conexion);

        Assert.False(await reencuentro.BuscarAhoraAsync());

        Assert.Equal(Guardada, conexion.Servicio);
        Assert.Equal(conLaMismaLlave ? ReencuentroDeLaPrincipal.OtraCajaPrincipal : ReencuentroDeLaPrincipal.DosPrincipales, reencuentro.Resultado);
        Assert.Equal(conLaMismaLlave ? ReencuentroDeLaPrincipal.OtraCajaPrincipal : ReencuentroDeLaPrincipal.DosPrincipalesAlDarDeAlta, reencuentro.AltaPorque);
        if (!conLaMismaLlave)
            Assert.Contains(_carpeta.Lineas(), l => l.StartsWith("Contestaron 192.168.0.99:5080 y 192.168.0.98:5080, que saben de esta caja pero no firman",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// Lo que dijo la búsqueda (contestan dos) se queda mientras la dirección guardada no conteste: la comprobación de cada latido no
    /// le quita a la franja «Dar de alta otra vez». Cuando la guardada vuelve y prueba quién es, ya no hace falta.
    /// </summary>
    [Fact]
    public async Task Dar_de_alta_otra_vez_no_parpadea_mientras_la_guardada_no_contesta()
    {
        var copia = new Uri("http://192.168.0.99:5080/");
        _servicios.Principal = Nueva;
        _servicios.Copias.Add(copia);
        _buscador.Contestan = [copia, Nueva];
        var conexion = Conexion();
        var (reencuentro, _) = Nuevo(conexion);
        Assert.False(await reencuentro.BuscarAhoraAsync());

        Assert.Equal(Comprobacion.NoContesta, await reencuentro.ComprobarGuardadaAsync());
        Assert.Equal(ReencuentroDeLaPrincipal.DosPrincipalesAlDarDeAlta, reencuentro.AltaPorque);

        _servicios.Principal = Guardada;
        Assert.Equal(Comprobacion.Es, await reencuentro.ComprobarGuardadaAsync());
        Assert.Null(reencuentro.AltaPorque);
    }

    /// <summary>Con la llave del servicio, la caja principal en su IP nueva sí gana, aunque también conteste una copia de su base.</summary>
    [Fact]
    public async Task Con_la_llave_del_servicio_la_principal_gana_aunque_conteste_una_copia()
    {
        var copia = new Uri("http://192.168.0.99:5080/");
        _servicios.Principal = Nueva;
        _servicios.LlavesDe[Nueva] = LlaveDeLaPrincipal;
        _servicios.Copias.Add(copia);
        _servicios.LlavesDe[copia] = LlaveDeOtra;
        _buscador.Contestan = [copia, Nueva];
        var conexion = Conexion();
        conexion.LlaveDelServicio = Publica(LlaveDeLaPrincipal);
        var (reencuentro, _) = Nuevo(conexion);

        Assert.True(await reencuentro.BuscarAhoraAsync());

        Assert.Equal(Nueva, conexion.Servicio);
        Assert.Contains(_carpeta.Lineas(), l => l.StartsWith("También contestó 192.168.0.99:5080, que sabe de esta caja pero no firma", StringComparison.Ordinal));
    }

    /// <summary>Una firma que no cuadra con la llave que dice (alguien que copió la llave pública, no la privada) no cuenta.</summary>
    [Fact]
    public async Task Una_firma_que_no_cuadra_no_cuenta()
    {
        _servicios.Principal = Nueva;
        _servicios.LlavesDe[Nueva] = LlaveDeLaPrincipal;
        _servicios.FirmanConOtra.Add(Nueva);
        _buscador.Contestan = [Nueva];
        var conexion = Conexion();
        conexion.LlaveDelServicio = Publica(LlaveDeLaPrincipal);
        var (reencuentro, _) = Nuevo(conexion);

        Assert.False(await reencuentro.BuscarAhoraAsync());
        Assert.Equal(Guardada, conexion.Servicio);
    }

    /// <summary>
    /// La misma caja principal que contesta por dos tarjetas (cable y Wi-Fi) firma con la misma llave: es una sola, y la caja sí se
    /// cambia (antes de conocer su llave también: la llave dice que es el mismo servicio).
    /// </summary>
    [Fact]
    public async Task La_misma_caja_principal_por_dos_tarjetas_es_una_sola()
    {
        var porWifi = new Uri("http://192.168.0.58:5080/");
        _servicios.Principal = Nueva;
        _servicios.Copias.Add(porWifi);
        _servicios.LlavesDe[Nueva] = LlaveDeLaPrincipal;
        _servicios.LlavesDe[porWifi] = LlaveDeLaPrincipal;
        _buscador.Contestan = [Nueva, porWifi];
        var conexion = Conexion();
        var (reencuentro, _) = Nuevo(conexion);

        Assert.True(await reencuentro.BuscarAhoraAsync());
        Assert.Equal(Nueva, conexion.Servicio);
    }

    /// <summary>
    /// La dirección guardada prueba quién es: la primera vez, la caja aprende la llave de su caja principal y la guarda (en la
    /// dirección que puso el dueño). Desde ahí, una copia de la base ya no gana.
    /// </summary>
    [Fact]
    public async Task La_primera_vez_que_la_guardada_prueba_quien_es_la_caja_aprende_su_llave()
    {
        _servicios.Principal = Guardada;
        _servicios.LlavesDe[Guardada] = LlaveDeLaPrincipal;
        var conexion = Conexion();
        var (reencuentro, _) = Nuevo(conexion);

        Assert.Equal(Comprobacion.Es, await reencuentro.ComprobarGuardadaAsync());

        Assert.Equal(Publica(LlaveDeLaPrincipal), conexion.LlaveDelServicio);
        Assert.Equal([Guardada], _guardadas);
        Assert.Contains(_carpeta.Lineas(), l => l.StartsWith("Esta caja recuerda la llave de su caja principal", StringComparison.Ordinal));
    }

    /// <summary>En una dirección que la caja encontró sola (no la que puso el dueño), la llave no se aprende: se sigue como antes.</summary>
    [Fact]
    public async Task En_una_direccion_que_no_puso_el_dueno_la_llave_no_se_aprende()
    {
        _servicios.Principal = Guardada;
        _servicios.LlavesDe[Guardada] = LlaveDeLaPrincipal;
        var conexion = Conexion();
        conexion.DireccionDeConfianza = new Uri("http://192.168.0.200:5080/");
        var (reencuentro, _) = Nuevo(conexion);

        Assert.Equal(Comprobacion.Es, await reencuentro.ComprobarGuardadaAsync());
        Assert.Null(conexion.LlaveDelServicio);
        Assert.Empty(_guardadas);
    }

    /// <summary>
    /// La dirección guardada sabe de esta caja, pero firma con otra llave (se reinstaló, o es una copia de su base): no es. La caja no
    /// le manda su llave y la franja ofrece darla de alta otra vez.
    /// </summary>
    [Fact]
    public async Task Si_la_guardada_firma_con_otra_llave_no_es()
    {
        _servicios.Principal = Guardada;
        _servicios.LlavesDe[Guardada] = LlaveDeOtra;
        var conexion = Conexion();
        conexion.LlaveDelServicio = Publica(LlaveDeLaPrincipal);
        var (reencuentro, _) = Nuevo(conexion);

        Assert.Equal(Comprobacion.NoEs, await reencuentro.ComprobarGuardadaAsync());
        Assert.Equal(ReencuentroDeLaPrincipal.OtraCajaPrincipal, reencuentro.AltaPorque);
        Assert.Equal(ReencuentroDeLaPrincipal.OtraCajaPrincipal, reencuentro.Resultado);
        Assert.Equal(Publica(LlaveDeLaPrincipal), conexion.LlaveDelServicio);
    }

    /// <summary>
    /// Un servicio de antes (sin la prueba): si la caja todavía no conoce la llave de su caja principal, se sigue como antes; si ya la
    /// conoce, un «no tengo la prueba» no le basta (no se deja engañar con eso).
    /// </summary>
    [Theory]
    [InlineData(false, Comprobacion.SinLaPrueba)]
    [InlineData(true, Comprobacion.NoEs)]
    public async Task Un_servicio_de_antes_sigue_como_antes_solo_si_no_se_conoce_su_llave(bool conoceLaLlave, Comprobacion queda)
    {
        _servicios.SinLaRuta.Add(Guardada);
        var conexion = Conexion();
        if (conoceLaLlave)
            conexion.LlaveDelServicio = Publica(LlaveDeLaPrincipal);
        var (reencuentro, _) = Nuevo(conexion);

        Assert.Equal(queda, await reencuentro.ComprobarGuardadaAsync());
    }

    [Fact]
    public async Task Si_la_guardada_ya_no_conoce_a_esta_caja_lo_dice()
    {
        _servicios.NoLaConocen.Add(Guardada);
        var (reencuentro, _) = Nuevo(Conexion());

        Assert.Equal(Comprobacion.NoLaConoce, await reencuentro.ComprobarGuardadaAsync());
    }

    [Fact]
    public async Task Si_la_guardada_no_contesta_lo_dice()
    {
        var (reencuentro, _) = Nuevo(Conexion());

        Assert.Equal(Comprobacion.NoContesta, await reencuentro.ComprobarGuardadaAsync());
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

        /// <summary>La llave propia del servicio en cada dirección (la caja principal, una copia de su base): firma con ella.</summary>
        public Dictionary<Uri, ECDsa> LlavesDe { get; } = [];

        /// <summary>Estos dicen la llave de su dirección, pero firman con otra (una firma que no cuadra).</summary>
        public HashSet<Uri> FirmanConOtra { get; } = [];

        /// <summary>Servicios de antes: no tienen la ruta de la prueba.</summary>
        public HashSet<Uri> SinLaRuta { get; } = [];

        /// <summary>Servicios que ya no conocen a esta caja (la quitaron).</summary>
        public HashSet<Uri> NoLaConocen { get; } = [];

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
            if (SinLaRuta.Contains(donde))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (NoLaConocen.Contains(donde))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"mensaje":"Esa caja no está dada de alta en este servicio.","codigo":"caja-sin-alta"}""",
                        System.Text.Encoding.UTF8, "application/json"),
                });
            if (Impostores.Contains(donde))
                return Responder(new IdentidadServicioDto(Descubrimiento.Prueba(LlaveCaja.Huella("otra"), caja, reto, Descubrimiento.Direccion(donde)!)));
            if (QuePasanLaPregunta.TryGetValue(donde, out var aQuien))
                return Responder(new IdentidadServicioDto(Descubrimiento.Prueba(LlaveCaja.Huella(Llave), caja, reto, Descubrimiento.Direccion(aQuien)!)));
            if ((donde == Principal || Copias.Contains(donde)) && caja == CajaId)
            {
                var direccion = Descubrimiento.Direccion(donde)!;
                var prueba = Descubrimiento.Prueba(LlaveCaja.Huella(Llave), caja, reto, direccion);
                if (!LlavesDe.TryGetValue(donde, out var suya))
                    return Responder(new IdentidadServicioDto(prueba), Relleno);
                using var otra = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var firma = (FirmanConOtra.Contains(donde) ? otra : suya)
                    .SignData(Descubrimiento.MensajeFirmado(caja, reto, direccion, prueba), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                return Responder(new IdentidadServicioDto(prueba, Publica(suya), Convert.ToBase64String(firma)), Relleno);
            }
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
