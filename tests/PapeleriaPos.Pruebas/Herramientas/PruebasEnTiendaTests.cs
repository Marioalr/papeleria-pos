using System.Text.RegularExpressions;
using PapeleriaPos.Herramientas.Tienda;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>Las herramientas del fin de semana en la tienda: la impresora de tickets compartida y el lector.</summary>
public class PruebasEnTiendaTests
{
    private static readonly PruebaImpresoraTickets.Vuelta ALaVez = PruebaImpresoraTickets.Vueltas[0];
    private static readonly PruebaImpresoraTickets.Vuelta CasiJuntos = PruebaImpresoraTickets.Vueltas[1];

    /// <summary>Hace de la impresora: cuenta cuántos tickets le llegan a la vez y, si se le pide, los detiene hasta soltarlos.</summary>
    private sealed class ImpresoraDePrueba(bool detener = false, Func<int, bool>? falla = null) : IImpresora
    {
        private readonly TaskCompletionSource _soltar = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _enCurso;
        private int _llegados;

        public int MasALaVez { get; private set; }
        public int EnCurso => Volatile.Read(ref _enCurso);
        public List<byte[]> Recibidos { get; } = [];

        public bool MuestraVistaPrevia => false;

        public void Soltar() => _soltar.TrySetResult();

        public async Task EnviarAsync(byte[] escPos, CancellationToken ct = default)
        {
            var numero = Interlocked.Increment(ref _llegados);
            var ahora = Interlocked.Increment(ref _enCurso);
            lock (Recibidos)
            {
                MasALaVez = Math.Max(MasALaVez, ahora);
                Recibidos.Add(escPos);
            }
            try
            {
                if (falla?.Invoke(numero) == true)
                    throw new IOException("No responde la impresora de tickets en 192.168.1.50:9100.");
                if (detener)
                    await _soltar.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            finally
            {
                Interlocked.Decrement(ref _enCurso);
            }
        }
    }

    [Fact]
    public async Task A_la_vez_los_dos_tickets_van_juntos_cada_uno_por_su_conexion()
    {
        var impresora = new ImpresoraDePrueba(detener: true);
        var conexiones = 0;

        var prueba = PruebaImpresoraTickets.ProbarAsync(() => { Interlocked.Increment(ref conexiones); return impresora; }, "CAJA2", ALaVez, 2, TimeProvider.System);
        // Los dos llegan a la impresora antes de que termine el primero: si fueran uno tras otro, nunca serían dos.
        for (var espera = 0; impresora.EnCurso < 2 && espera < 200; espera++)
            await Task.Delay(25);
        Assert.Equal(2, impresora.EnCurso);
        impresora.Soltar();
        var resultados = await prueba;

        Assert.Equal(2, conexiones);
        Assert.Equal(2, impresora.MasALaVez);
        Assert.All(resultados, r => Assert.Null(r.Error));
        Assert.All(resultados, r => Assert.NotNull(r.Listo));
    }

    [Fact]
    public async Task Casi_juntos_el_segundo_sale_despues_del_primero()
    {
        var resultados = await PruebaImpresoraTickets.ProbarAsync(() => new ImpresoraDePrueba(), "CAJA2", CasiJuntos, 2, TimeProvider.System);

        Assert.Equal([1, 2], resultados.Select(r => r.Numero));
        Assert.True(resultados[1].Mandado - resultados[0].Mandado >= TimeSpan.FromMilliseconds(250),
            $"El segundo se mandó a los {resultados[1].Mandado.TotalMilliseconds} ms.");
    }

    [Fact]
    public async Task Si_un_ticket_falla_dice_cual_y_por_que_y_los_demas_siguen()
    {
        var impresora = new ImpresoraDePrueba(falla: numero => numero == 2);

        var resultados = await PruebaImpresoraTickets.ProbarAsync(() => impresora, "CAJA2", ALaVez, 2, TimeProvider.System);
        var texto = string.Join('\n', PruebaImpresoraTickets.Describir(resultados));

        Assert.Single(resultados, r => r.Error is null);
        var fallido = Assert.Single(resultados, r => r.Error is not null);
        Assert.Null(fallido.Listo);
        Assert.Contains($"✗ Ticket {fallido.Numero}: se mandó a los", texto);
        Assert.Contains("No responde la impresora de tickets en 192.168.1.50:9100.", texto);
        Assert.Contains("Revisa el papel", texto);
    }

    [Fact]
    public void El_ticket_dice_que_es_de_prueba_y_de_donde_salio()
    {
        var hora = new DateTimeOffset(2026, 10, 17, 11, 5, 3, 250, TimeSpan.FromHours(-6));
        var documento = PruebaImpresoraTickets.Ticket("CAJA2", ALaVez, 1, 2, hora);
        var texto = TicketTexto.Generar(documento);

        Assert.Contains("PRUEBA DE LA IMPRESORA", texto);
        Assert.Contains("No es una venta", texto);
        Assert.Contains("Ticket 1 de 2, a la vez", texto);
        Assert.Contains("Desde CAJA2", texto);
        Assert.Contains("Vuelta de las 11:05:03.250", texto);
        Assert.Contains("Renglón 10 de 10 del ticket 1", texto);
        Assert.Contains("Fin del ticket 1", texto);
        // Sin QR, como el ticket de la caja por ahora (2026-10-16).
        Assert.Empty(documento.OfType<Qr>());
        // Nada se corta: cada renglón cabe en los 48 caracteres del ticket.
        Assert.All(documento.OfType<Renglon>(), r => Assert.True(r.Texto.Length <= DisenoTicket.Columnas, r.Texto));
        // Y sale con su corte, como un ticket de la caja.
        var bytes = TicketEscPos.Generar(documento, abrirCajon: false);
        Assert.Equal(new byte[] { 0x1D, (byte)'V', 66, 0 }, bytes[^4..]);
    }

    private static PruebaLector.Codigo Escaneo(string texto, double cadaMs, double enterMs = -1, double pausaEn = -1, double pausaMs = 0)
    {
        var teclas = new List<PruebaLector.Tecla>();
        var hora = 0.0;
        for (var i = 0; i < texto.Length; i++)
        {
            if (i > 0)
                hora += i == pausaEn ? pausaMs : cadaMs;
            teclas.Add(new PruebaLector.Tecla(texto[i], TimeSpan.FromMilliseconds(hora)));
        }
        return PruebaLector.Medir(teclas, TimeSpan.FromMilliseconds(hora + (enterMs < 0 ? cadaMs : enterMs)));
    }

    [Fact]
    public void Mide_el_codigo_de_la_primera_tecla_al_Enter()
    {
        var c = Escaneo("7501234567890", cadaMs: 2, enterMs: 6);

        Assert.Equal("7501234567890", c.Texto);
        Assert.Equal(TimeSpan.FromMilliseconds(12 * 2 + 6), c.Duracion);
        Assert.Equal(TimeSpan.FromMilliseconds(6), c.EntreTeclas);
        Assert.Equal(30.0 / 13, c.PromedioEntreTeclas.TotalMilliseconds, 3);
    }

    [Fact]
    public void Un_lector_rapido_lo_reconoce_la_caja()
    {
        var c = Escaneo("7501234567890", cadaMs: 3);
        var texto = string.Join('\n', PruebaLector.Describir(c));

        Assert.True(PruebaLector.LoReconoceLaCaja(c));
        Assert.Contains("«7501234567890» (13 caracteres)", texto);
        Assert.Contains("✓ En el cobro, la caja lo reconoce como el lector.", texto);
        Assert.Contains("Son 13 cifras", texto);
    }

    [Fact]
    public void Una_pausa_de_mas_entre_dos_teclas_o_antes_del_Enter_no_es_el_lector_para_la_caja()
    {
        var enMedio = Escaneo("75012345678", cadaMs: 3, pausaEn: 5, pausaMs: 60);
        var antesDelEnter = Escaneo("75012345678", cadaMs: 3, enterMs: 70);

        Assert.False(PruebaLector.LoReconoceLaCaja(enMedio));
        Assert.False(PruebaLector.LoReconoceLaCaja(antesDelEnter));
        var texto = string.Join('\n', PruebaLector.Describir(enMedio));
        Assert.Contains("✗ En el cobro se tomaría como tecleado: pasaron 60 ms entre dos teclas y la caja espera hasta 50 ms.", texto);
        Assert.Contains("Son 11 cifras: compáralas con el código de ese producto en eleventa.", texto);
    }

    [Fact]
    public void Lo_corto_no_se_distingue_de_alguien_que_teclea_rapido()
    {
        var c = Escaneo("200", cadaMs: 2);

        Assert.False(PruebaLector.LoReconoceLaCaja(c));
        Assert.Contains("✗ Tiene menos de 4 caracteres", string.Join('\n', PruebaLector.Describir(c)));
    }

    [Fact]
    public void El_resumen_dice_si_hay_que_ajustar_el_ritmo_de_la_caja()
    {
        var rapido = Escaneo("7501234567890", cadaMs: 2);
        var lento = Escaneo("7501234567890", cadaMs: 2, pausaEn: 3, pausaMs: 62);
        var deUnaPersona = Escaneo("7501234567890", cadaMs: 2, pausaEn: 3, pausaMs: 120);

        Assert.Equal(["Escaneos que la caja reconoce como el lector en el cobro: 1 de 1."], PruebaLector.Resumen([rapido]));
        var conUnoLento = string.Join('\n', PruebaLector.Resumen([rapido, lento, deUnaPersona]));
        Assert.Contains("reconoce como el lector en el cobro: 1 de 3.", conUnoLento);
        // Lo tecleado a mano no cuenta para el ritmo que debería esperar la caja.
        Assert.Contains("RAFAGA_MS en caja.js tendría que ser de unos 72 ms", conUnoLento);
        Assert.Contains("1 con pausas de 80 ms o más, como las de una persona", conUnoLento);
        Assert.Equal(["No se escaneó ningún código."], PruebaLector.Resumen([Escaneo("12", cadaMs: 2)]));
    }

    [Fact]
    public void El_ritmo_que_espera_la_herramienta_es_el_de_caja_js()
    {
        var cajaJs = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "PapeleriaPos.Interfaz", "wwwroot", "caja.js"));

        Assert.Equal(PruebaLector.RafagaMs, int.Parse(Regex.Match(cajaJs, @"const RAFAGA_MS = (\d+);").Groups[1].Value));
        Assert.Equal(PruebaLector.RafagaMinimo, int.Parse(Regex.Match(cajaJs, @"const RAFAGA_MINIMO = (\d+);").Groups[1].Value));
    }

    // ── probar-lector --puerto: el lector por puerto COM (piloto alfa) ──

    private static List<Lectura> Leidas(string texto, FinDelCodigo fin = FinDelCodigo.Auto)
    {
        var decodificador = new DecodificadorLector(fin, TimeSpan.FromMilliseconds(100));
        var lecturas = decodificador.Agregar(System.Text.Encoding.Latin1.GetBytes(texto), TimeSpan.Zero);
        if (decodificador.Pausa(TimeSpan.FromSeconds(1)) is { } porPausa)
            lecturas.Add(porPausa);
        return lecturas;
    }

    [Fact]
    public void Los_ajustes_de_la_linea_de_comandos_son_los_de_caja_json()
    {
        var (ajustes, problema) = PruebaLectorPuerto.Ajustes(["--puerto", "com3", "--baudios", "19200", "--paridad", "Ninguna", "--fin", "CR", "--dtr", "false"]);

        Assert.Null(problema);
        Assert.Equal(new AjustesLector("COM3") { Baudios = 19200, Fin = FinDelCodigo.CR, Dtr = false }, ajustes);
        Assert.Equal("Indica el puerto con --puerto (por ejemplo, --puerto COM3: el que tiene eleventa en la caja 2).", PruebaLectorPuerto.Ajustes(["--baudios", "9600"]).Problema);
        Assert.Equal("En lo que escribiste: «Baudios» va de 50 a 921,600 (está «rápido»).", PruebaLectorPuerto.Ajustes(["--puerto", "COM3", "--baudios", "rápido"]).Problema);
    }

    [Fact]
    public void Dice_los_puertos_si_esta_ocupado_y_cada_codigo_con_lo_que_tardo_y_su_fin()
    {
        var ajustes = new AjustesLector("COM3");
        Assert.Equal("Puertos COM en esta PC: COM1, COM3.", PruebaLectorPuerto.Puertos(["COM1", "COM3"]));
        Assert.StartsWith("Esta PC no tiene puertos COM ahora", PruebaLectorPuerto.Puertos([]));
        var ocupado = PruebaLectorPuerto.Estado(new EstadoLector(SituacionLector.Ocupado, "COM3", "lo tiene abierto otro programa", DateTimeOffset.Now), ajustes, TimeSpan.FromSeconds(3));
        Assert.Equal("COM3 está OCUPADO: lo tiene abierto otro programa (eleventa, o la caja del POS). Ciérralo: aquí se vuelve a intentar cada 3 s. Esc para terminar.", ocupado);
        Assert.StartsWith("No se encuentra COM3: no existe en esta PC",
            PruebaLectorPuerto.Estado(new EstadoLector(SituacionLector.SinConectar, "COM3", "no existe en esta PC", DateTimeOffset.Now), ajustes, TimeSpan.FromSeconds(3)));
        Assert.StartsWith("COM3 abierto (COM3 a 9,600 baudios (8N1)",
            PruebaLectorPuerto.Estado(new EstadoLector(SituacionLector.Listo, "COM3", null, DateTimeOffset.Now), ajustes, TimeSpan.FromSeconds(3)));

        Assert.Equal("  +    4 ms  37 35 30 31 0D  «7501<CR>»", PruebaLectorPuerto.Pedazo("7501\r"u8.ToArray(), TimeSpan.FromMilliseconds(4)));
        var codigo = Assert.Single(Leidas("7501234567890\r\n"));
        Assert.Equal(["«7501234567890» (13 caracteres), en 0 ms; terminó con CRLF.", "  Son 13 cifras: compáralas con el código de ese producto en eleventa."],
            PruebaLectorPuerto.Describir(codigo));
        var basura = Assert.Single(Leidas("\u00f8\u0000\r"));
        Assert.StartsWith("Se descartó: trae caracteres que no son de un código", Assert.Single(PruebaLectorPuerto.Describir(basura)));
    }

    [Fact]
    public void Al_terminar_dice_como_queda_Lector_en_caja_json_o_que_revisar()
    {
        var ajustes = new AjustesLector("COM3");
        var conCr = Leidas("7501\r7502\r");
        var resumen = PruebaLectorPuerto.Resumen(conCr, ajustes, erroresDeLinea: 0).ToList();
        Assert.Equal("Llegaron 2 códigos.", resumen[0]);
        // «Fin» no se sugiere: la caja toma CR, LF, CRLF o la pausa sola, y la guía dice no ponerlo (revisión del PR #142).
        Assert.Equal("  \"Lector\": { \"Puerto\": \"COM3\", \"Baudios\": 9600 },", resumen[^1]);
        Assert.Contains("  Terminaron con CR: la caja lo toma sola, sin poner «Fin».", string.Join('\n', resumen));

        // Sin carácter de fin, con la pausa: tampoco.
        var sinFin = PruebaLectorPuerto.Resumen(Leidas("7501"), ajustes, 0).ToList();
        Assert.DoesNotContain("\"Fin\"", sinFin[^1]);
        Assert.Contains("  Terminaron con una pausa: la caja lo toma sola, sin poner «Fin».", string.Join('\n', sinFin));

        // Basura y errores de la línea: la velocidad o la paridad no son las del lector.
        var conBasura = string.Join('\n', PruebaLectorPuerto.Resumen([.. Leidas("\u00f8\r"), .. Leidas("7501\r")], ajustes, erroresDeLinea: 3));
        Assert.Contains("Llegó 1 código y se descartó 1 lectura que no era un código.", conBasura);
        Assert.Contains("El puerto dio 3 errores de la línea. Con basura, la velocidad (--baudios) o la paridad no son las del lector: cópialas de eleventa.", conBasura);

        Assert.StartsWith("No llegó nada por COM3.", Assert.Single(PruebaLectorPuerto.Resumen([], ajustes, 0)));
    }

    private static string RaizDelRepositorio()
    {
        for (var carpeta = new DirectoryInfo(AppContext.BaseDirectory); carpeta is not null; carpeta = carpeta.Parent)
            if (File.Exists(Path.Combine(carpeta.FullName, "src", "PapeleriaPos.Interfaz", "wwwroot", "caja.js")))
                return carpeta.FullName;
        throw new DirectoryNotFoundException("No encontré la raíz del repositorio.");
    }
}
