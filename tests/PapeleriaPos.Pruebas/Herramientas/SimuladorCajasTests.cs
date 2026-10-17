using System.Net;
using System.Text;
using PapeleriaPos.Herramientas.Simulador;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>
/// El simulador de cajas (simular-cajas, docs/prueba-sucursales-linux.md): cajas de verdad sin la pantalla contra el servicio de
/// verdad en el mismo proceso. Lo que se revisa es el cuadre, no los tiempos: la caída va por tickets (no por segundos) y lo
/// demás se espera con su límite, así que no depende de lo que tarde la PC.
/// </summary>
public sealed class SimuladorCajasTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    [Fact]
    public async Task Un_dia_corto_de_dos_cajas_con_la_red_de_una_caida_cuadra()
    {
        var opciones = new OpcionesSimulador
        {
            Cajas = 2, Tickets = 10, Minutos = 0, Semilla = 7, Caida = new CaidaDeRed(0.4, Tickets: 3), Espera = TimeSpan.FromSeconds(60),
        };
        var carpeta = Path.Combine(Path.GetTempPath(), $"simulador-prueba-{Guid.NewGuid():N}");
        try
        {
            // El servicio de la prueba, con su base ficticia, es como el que arranca el simulador con --iniciar-servicio.
            var resultado = await new SimuladorCajas(opciones, new Uri("http://localhost/"), _ => servicio.Fabrica.Server.CreateHandler())
            {
                Base = servicio.Base, Carpeta = carpeta, ServicioPropio = true,
            }.CorrerAsync();

            var informe = Informe.Markdown(resultado);
            Assert.True(resultado.Paso, informe);
            Assert.All(resultado.Cuadre, r => Assert.True(r.Bien, $"{r.Que}: {string.Join(" / ", r.Detalles)}"));
            var (caja1, caja2) = (resultado.Cajas[0], resultado.Cajas[1]);
            Assert.Equal([10, 10], resultado.Cajas.Select(c => c.Tickets));
            // La caída: los tickets de la caja 2 en ella se cobraron con su copia y subieron al volver (el cuadre lo revisa).
            Assert.NotNull(caja2.Caida?.Fin);
            Assert.True(caja2.Ventas.Count(v => !v.EnLinea) >= 3, informe);
            // Lo demás del día pasó: una cancelación en la caja 1, una devolución en la 2, el precio que cambió el dueño, el
            // producto que llegó (dado de alta, o como artículo común si la caja 1 no tenía a la caja principal), las entradas y
            // salidas, y el corte de cada caja.
            Assert.Contains(caja1.Devoluciones, d => d.EsCancelacion);
            Assert.Contains(caja2.Devoluciones, d => !d.EsCancelacion);
            Assert.Contains(resultado.Sucesos, s => s.Que.Contains("cambió el precio", StringComparison.Ordinal));
            Assert.True(resultado.Sucesos.Any(s => s.Que.Contains("dio de alta", StringComparison.Ordinal)) || caja1.ArticulosComunes > 0, informe);
            Assert.All(resultado.Cajas, c => Assert.True(c.Movimientos.Count >= 3));
            Assert.All(resultado.Cajas, c => Assert.Equal(0, c.Corte?.DiferenciaCentavos));
            Assert.Contains("## Tiempos contra las metas", informe);
            Assert.Contains("**Cuadra.**", informe);
        }
        finally
        {
            // Las cajas ya se cerraron (soltaron su base): se puede borrar.
            ArchivosDePrueba.BorrarCarpeta(carpeta);
        }
    }

    // ── El cuadre encuentra lo que no cuadra ────────────────────────────────

    private static readonly Guid Pluma = Guid.NewGuid();

    private static VentaSimulada Venta(int numero, long total, bool enLinea = true, long piezas = 1) => new()
    {
        Caja = 1, VentaId = Guid.NewGuid(), Folio = $"SA-{numero:000000}", Numero = numero, TotalCentavos = total, EfectivoCentavos = total,
        TarjetaCentavos = 0, TransferenciaCentavos = 0, EnLinea = enLinea, YaCobrada = false, Cuando = TimeSpan.Zero,
        Renglones = [new RenglonVendido(Pluma, "Pluma azul", piezas * 1000, piezas * 1000, true)],
    };

    private static LibroDeCaja Libro(IReadOnlyList<VentaSimulada> ventas, int sinConfirmar = 0, long fondo = 50_000) =>
        new(1, "Caja simulada 1", Guid.NewGuid(), new DateTime(2026, 10, 17, 9, 0, 0), fondo, ventas, [], [], null, null, 0, 0, null, sinConfirmar);

    private static List<VentaDelDiaDto> Alla(IEnumerable<VentaSimulada> ventas) =>
        ventas.Select(v => new VentaDelDiaDto(v.VentaId, v.Folio, new DateTime(2026, 10, 17, 10, 0, 0), v.TotalCentavos, 1, "Cajero")).ToList();

    [Fact]
    public void Una_venta_perdida_otra_de_mas_y_un_total_distinto_no_cuadran()
    {
        var (a, b, c) = (Venta(1, 1500), Venta(2, 2000), Venta(3, 900));
        var alla = Alla([a, c]);
        alla[1] = alla[1] with { TotalCentavos = 1000 };
        alla.Add(new VentaDelDiaDto(Guid.NewGuid(), "SA-000004", new DateTime(2026, 10, 17, 11, 0, 0), 500, 1, "Cajero"));

        var revision = Cuadre.Ventas(Libro([a, b, c]), alla);

        Assert.False(revision.Bien);
        Assert.Contains(revision.Detalles, d => d.StartsWith("Perdida: la venta SA-000002", StringComparison.Ordinal));
        Assert.Contains(revision.Detalles, d => d.Contains("SA-000003") && d.Contains("$9.00") && d.Contains("$10.00"));
        Assert.Contains(revision.Detalles, d => d.StartsWith("De más: la caja principal tiene la venta SA-000004", StringComparison.Ordinal));
        Assert.True(Cuadre.Ventas(Libro([a, b, c]), Alla([a, b, c])).Bien);
    }

    [Fact]
    public void Un_folio_brincado_solo_cuadra_si_un_cobro_quedo_sin_confirmar()
    {
        var ventas = new[] { Venta(1, 100), Venta(2, 100), Venta(4, 100) };

        Assert.False(Cuadre.Folios(Libro(ventas)).Bien);
        Assert.True(Cuadre.Folios(Libro(ventas, sinConfirmar: 1)).Bien);
        Assert.False(Cuadre.Folios(Libro([Venta(1, 100), Venta(1, 100)])).Bien);
    }

    [Fact]
    public void Lo_cobrado_sin_conexion_tiene_que_haber_subido_una_vez()
    {
        var sinConexion = Venta(1, 100, enLinea: false);
        var libro = Libro([sinConexion, Venta(2, 100)]);

        Assert.True(Cuadre.Bandeja(libro, [new ElementoDeLaBandeja("api/bandeja/ventas", sinConexion.VentaId, true, null)]).Bien);
        Assert.False(Cuadre.Bandeja(libro, []).Bien);
        Assert.False(Cuadre.Bandeja(libro, [new ElementoDeLaBandeja("api/bandeja/ventas", sinConexion.VentaId, false, "El turno no existe.")]).Bien);
    }

    [Fact]
    public void El_turno_cuadra_con_el_libro_de_la_caja_y_su_corte_sin_diferencia()
    {
        var libro = Libro([Venta(1, 1500), Venta(2, 2500)]) with { Movimientos = [new MovimientoSimulado(TipoMovimientoCaja.Salida, 3000, "Garrafón", true)] };
        var esperado = 50_000 + 4_000 - 3_000;
        var resumen = new CorteDto(TipoCorte.X, DateTime.Now, "Cajero", "Cajero", 2, 50_000, 4_000, 0, 3_000, 0, 0, esperado, null, null);
        var corte = resumen with { Tipo = TipoCorte.Z, ContadoCentavos = esperado, DiferenciaCentavos = 0 };

        Assert.True(Cuadre.Turno(libro with { Corte = corte, Contado = esperado }, resumen, sigueAbierto: false).Bien);
        // Un cobro que la caja principal no tiene, el turno abierto, un corte con faltante.
        var sinUnCobro = resumen with { EfectivoVentasCentavos = 2_500, EsperadoCentavos = esperado - 1_500 };
        Assert.False(Cuadre.Turno(libro with { Corte = corte, Contado = esperado }, sinUnCobro, sigueAbierto: false).Bien);
        Assert.False(Cuadre.Turno(libro with { Corte = corte, Contado = esperado }, resumen, sigueAbierto: true).Bien);
        Assert.False(Cuadre.Turno(libro with { Corte = corte with { DiferenciaCentavos = -100 }, Contado = esperado }, resumen, sigueAbierto: false).Bien);
    }

    [Fact]
    public void Las_existencias_bajan_lo_vendido_menos_lo_devuelto()
    {
        var venta = Venta(1, 3000, piezas: 3);
        var libro = Libro([venta]) with
        {
            Devoluciones = [new DevolucionSimulada(1, venta.VentaId, venta.Folio, false, 1000, [(Pluma, 1000, true)])],
        };
        var nombres = new Dictionary<Guid, string> { [Pluma] = "Pluma azul" };
        var antes = new Dictionary<Guid, decimal> { [Pluma] = 10 };

        Assert.True(Cuadre.Existencias([libro], antes, nombres, new Dictionary<Guid, decimal> { [Pluma] = 8 }).Bien);
        Assert.False(Cuadre.Existencias([libro], antes, nombres, new Dictionary<Guid, decimal> { [Pluma] = 7 }).Bien);
        // Lo que llegó dañado entra y sale como merma: no regresa a la existencia.
        var danada = libro with { Devoluciones = [libro.Devoluciones[0] with { Renglones = [(Pluma, 1000, false)] }] };
        Assert.True(Cuadre.Existencias([danada], antes, nombres, new Dictionary<Guid, decimal> { [Pluma] = 7 }).Bien);
        Assert.False(Cuadre.KardexCuadra(nombres, new Dictionary<Guid, decimal> { [Pluma] = 7 }, new Dictionary<Guid, decimal> { [Pluma] = 8 }).Bien);
    }

    // ── Las opciones ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("30s@50%", 0.5, 30, null)]
    [InlineData("2m@40%", 0.4, 120, null)]
    [InlineData("1m30s@10%", 0.1, 90, null)]
    [InlineData("5t@50%", 0.5, null, 5)]
    public void La_caida_se_lee_en_tiempo_o_en_tickets(string texto, double inicio, int? segundos, int? tickets)
    {
        var caida = CaidaDeRed.Leer(texto, out var error);

        Assert.Null(error);
        Assert.Equal(inicio, caida!.Inicio, 3);
        Assert.Equal(segundos is { } s ? TimeSpan.FromSeconds(s) : null, caida.Duracion);
        Assert.Equal(tickets, caida.Tickets);
    }

    [Theory]
    [InlineData("30@50%")]
    [InlineData("30s@100%")]
    [InlineData("0s@50%")]
    [InlineData("30s")]
    public void Una_caida_que_no_se_entiende_lo_dice(string texto)
    {
        Assert.Null(CaidaDeRed.Leer(texto, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Las_opciones_traen_lo_de_siempre_y_dicen_lo_que_no_se_entiende()
    {
        var omision = OpcionesSimulador.Leer(["--servicio", "http://localhost:5180"], out var sinError)!;
        var leidas = OpcionesSimulador.Leer(["--cajas", "3", "--tickets", "40", "--minutos", "0", "--caida", "20s@30%", "--caida-caja", "2",
            "--dueno", "jefa:9876"], out _)!;

        Assert.Null(sinError);
        Assert.Equal((2, 120, 10d, null), (omision.Cajas, omision.Tickets, omision.Minutos, omision.Caida));
        Assert.Equal(("dueno", "cajero", "encargado"), (omision.Dueno.Usuario, omision.Cajero.Usuario, omision.Encargado.Usuario));
        Assert.Equal((3, 40, 0d, 2), (leidas.Cajas, leidas.Tickets, leidas.Minutos, leidas.CajaDeLaCaida));
        Assert.Equal(new Credencial("jefa", "9876"), leidas.Dueno);
        Assert.Null(OpcionesSimulador.Leer(["--cajas", "0"], out var error));
        Assert.Contains("--cajas", error);
        Assert.Null(OpcionesSimulador.Leer(["--caida", "20s@30%", "--caida-caja", "3"], out error));
        Assert.Null(OpcionesSimulador.Leer(["--cajero", "sinpin"], out error));
    }

    // ── La red que se cae ───────────────────────────────────────────────────

    /// <summary>Contesta a todo con un cuerpo que llega de a poco (como el aviso): el primer pedazo y luego nada hasta que se corte.</summary>
    private sealed class ServicioQueSeQueda : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FlujoSinFin()) });

        private sealed class FlujoSinFin : Stream
        {
            private bool _primero = true;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_primero)
                {
                    _primero = false;
                    return Encoding.UTF8.GetBytes(": vivo\n\n", buffer.Span);
                }
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
    }

    [Fact]
    public async Task Con_la_red_caida_no_se_conecta_y_lo_que_estaba_abierto_se_corta()
    {
        var red = new RedSimulada();
        using var http = new HttpClient(red.Envolver(new ServicioQueSeQueda()));
        using var respuesta = await http.GetAsync("http://localhost/api/cajas/1/avisos", HttpCompletionOption.ResponseHeadersRead);
        await using var aviso = await respuesta.Content.ReadAsStreamAsync();
        var bufer = new byte[64];
        Assert.True(await aviso.ReadAsync(bufer) > 0);
        var esperando = aviso.ReadAsync(bufer).AsTask();

        red.Caer();

        await Assert.ThrowsAsync<IOException>(() => esperando.WaitAsync(TimeSpan.FromSeconds(10)));
        var noConecta = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://localhost/api/estado"));
        Assert.Equal(HttpRequestError.ConnectionError, noConecta.HttpRequestError);
        red.Volver();
        using var otraVez = await http.GetAsync("http://localhost/api/estado", HttpCompletionOption.ResponseHeadersRead);
        Assert.True(otraVez.IsSuccessStatusCode);
    }

    [Fact]
    public void Cada_llamada_se_mide_por_su_ruta_sin_lo_que_cambia()
    {
        string Ruta(HttpMethod metodo, string url) => MedidorDeRed.Ruta(new HttpRequestMessage(metodo, url));

        Assert.Equal("GET api/cajas/{id}/cambios", Ruta(HttpMethod.Get, $"http://localhost/api/cajas/{Guid.NewGuid()}/cambios?desde=4&existencias=9"));
        Assert.Equal("GET api/productos/codigo/{codigo}", Ruta(HttpMethod.Get, "http://localhost/api/productos/codigo/7501234567890?sucursalId=x"));
        Assert.Equal("GET api/sucursales/{id}/ventas/folio/{folio}",
            Ruta(HttpMethod.Get, $"http://localhost/api/sucursales/{Guid.NewGuid()}/ventas/folio/SA-000012"));
        Assert.Equal("POST api/ventas", Ruta(HttpMethod.Post, "http://localhost/api/ventas"));
        var medida = Medida.De([5, 1, 3, 2, 4, 100, 6, 7, 8, 9])!;
        Assert.Equal((10, 5d, 100d, 100d, 100d), (medida.Veces, medida.Mitad, medida.P95, medida.P99, medida.Maximo));
    }

    [Fact]
    public void El_informe_dice_cuando_la_peor_fue_la_primera_del_dia()
    {
        var mediciones = new Mediciones();
        foreach (var ms in new[] { 400, 20, 30 })
            mediciones.Anotar(Operacion.CobrarEnLinea, TimeSpan.FromMilliseconds(ms));

        Assert.True(mediciones.LaPeorFueLaPrimera([Operacion.CobrarEnLinea]));
        mediciones.Anotar(Operacion.CobrarEnLinea, TimeSpan.FromMilliseconds(500));
        Assert.False(mediciones.LaPeorFueLaPrimera([Operacion.CobrarEnLinea]));
        // Con una sola vez no hay una peor que explicar.
        var una = new Mediciones();
        una.Anotar(Operacion.CobrarEnLinea, TimeSpan.FromMilliseconds(400));
        Assert.False(una.LaPeorFueLaPrimera([Operacion.CobrarEnLinea]));
    }
}
