using Microsoft.Data.Sqlite;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Cuando algo truena con la venta en curso: el servicio contesta 500 al cobrar (sigue vivo pero no puede guardar: disco lleno, su
/// base dañada) y la base de esta caja no deja escribir. La cajera siempre puede cobrar o se entera de por qué no.
/// </summary>
public sealed class ServicioQueNoPuedeGuardarTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-truena-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static async Task<SolicitudCobro> CobroAsync(VentaConCopia v)
    {
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        var (a, _) = v.DosDeLaCopia();
        return new SolicitudCobro(v.Caja.Id, turno.Id, v.Cajero.UsuarioId, [new LineaSolicitada(a.VarianteId, a.Presentacion.Id, 1000)],
            [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)], Guid.NewGuid());
    }

    private void Romper(string sql)
    {
        using var conexion = new SqliteConnection($"Data Source={_baseLocal}");
        conexion.Open();
        using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.ExecuteNonQuery();
    }

    /// <summary>Un 500 al cobrar: se cobra con la copia, con el mismo Id. Al poder guardar otra vez, sube una vez.</summary>
    [Fact]
    public async Task Un_500_al_cobrar_cae_a_la_copia_y_sube_despues()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var solicitud = await CobroAsync(v);
        v.Red.Truena = ruta => ruta == "/api/ventas";

        var ticket = await v.Cliente.CobrarAsync(solicitud);

        Assert.True(ticket.EnContingencia);
        Assert.Equal(EstadoRed.Lenta, v.Contingencia.EstadoDeLaRed);
        Assert.Equal(1, v.Contingencia.Pendientes);
        v.Red.Truena = null;
        for (var i = 0; i < 5 && v.Contingencia.Pendientes > 0; i++)
        {
            await v.Contingencia.LatidoAsync();
            await v.Contingencia.SubirAsync();
        }
        Assert.Equal((0, 0), (v.Contingencia.Pendientes, v.Contingencia.ConError));
        await using var db = servicio.Base();
        Assert.Equal(1, await db.Ventas.CountAsync(x => x.Id == solicitud.VentaId));
    }

    /// <summary>Un 500 que llegó después de guardar: la copia cobra el mismo Id y al subir no se duplica ni queda con error.</summary>
    [Fact]
    public async Task Un_500_despues_de_guardar_no_duplica_la_venta()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var solicitud = await CobroAsync(v);
        v.Red.TruenaDespues = ruta => ruta == "/api/ventas";

        await v.Cliente.CobrarAsync(solicitud);

        v.Red.TruenaDespues = null;
        for (var i = 0; i < 5 && v.Contingencia.Pendientes > 0; i++)
        {
            await v.Contingencia.LatidoAsync();
            await v.Contingencia.SubirAsync();
        }
        Assert.Equal((0, 0), (v.Contingencia.Pendientes, v.Contingencia.ConError));
        await using var db = servicio.Base();
        Assert.Equal(1, await db.Ventas.CountAsync(x => x.Id == solicitud.VentaId));
    }

    /// <summary>Lo que no tiene copia sigue con su error, ya en palabras: nada de códigos HTTP en pantalla.</summary>
    [Fact]
    public async Task Sin_copia_un_500_lo_dice_sin_codigos()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        v.Red.Truena = ruta => ruta.Contains("/turnos", StringComparison.Ordinal);

        var error = await Assert.ThrowsAsync<ErrorCaja>(() => new Transporte(new HttpClient(v.Red), new ConexionCaja { Servicio = new Uri("http://localhost/") })
            .GetAsync<object>("api/turnos/x"));

        Assert.Equal(500, error.Estado);
        Assert.DoesNotContain("500", error.Message);
        Assert.Contains("avísale al dueño", error.Message);
    }

    /// <summary>
    /// La base de esta caja no deja anotar lo que pasó en línea: la venta ya quedó en el servicio. La pantalla cobra como siempre (ticket
    /// y cambio) y la franja lo dice; antes, F12 parecía no hacer nada y la cajera la volvía a pasar en eleventa.
    /// </summary>
    [Fact]
    public async Task Si_la_base_local_no_deja_anotar_el_cobro_en_linea_sigue_y_se_avisa()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (a, _) = v.DosDeLaCopia();
        await v.EscanearAsync(a.Codigo!);
        Romper("DROP TABLE Diario");

        await v.Pantalla.OprimirAsync(await v.Pantalla.BotonAsync("Cobrar exacto"));

        await v.Pantalla.EsperarAsync(t => t.Contains("Cobrado"), "el cobro en la pantalla");
        Assert.Contains("no deja guardar", v.Contingencia.AvisoBaseLocal);
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>Sin conexión y sin poder guardar: no se cobra, y la cajera lee qué hacer.</summary>
    [Fact]
    public async Task Sin_conexion_si_la_base_local_no_deja_guardar_se_dice()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var solicitud = await CobroAsync(v);
        v.Red.Caida = true;
        await v.Contingencia.LatidoAsync();
        Romper("DROP TABLE Bandeja");

        var error = await Assert.ThrowsAsync<ErrorCaja>(() => v.Cliente.CobrarAsync(solicitud));

        Assert.Contains("eleventa", error.Message);
    }

    /// <summary>
    /// Con la red lenta (o el servicio de esta PC ocupado un momento), un ticket con impresiones se intenta con el servicio: con la copia
    /// no se puede, y decir que no sin preguntar detenía a la cajera sin motivo.
    /// </summary>
    [Fact]
    public async Task Con_la_red_lenta_las_impresiones_se_intentan_en_linea()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var solicitud = await CobroAsync(v) with { Impresiones = [Guid.NewGuid()] };
        v.Contingencia.MarcarLenta();
        var antes = v.Red.Cuantas("/api/ventas");

        var error = await Assert.ThrowsAsync<ErrorCaja>(() => v.Cliente.CobrarAsync(solicitud));

        Assert.Equal(antes + 1, v.Red.Cuantas("/api/ventas"));
        Assert.DoesNotContain("se cobran con conexión", error.Message);
    }

    private sealed class ContestaHtml : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html>otro programa</html>") });
    }

    /// <summary>Otra versión, u otro programa en ese puerto: un error que las pantallas saben decir, no «Algo falló» llevándose el ticket.</summary>
    [Fact]
    public async Task Lo_que_no_se_entiende_es_un_error_que_se_dice()
    {
        var transporte = new Transporte(new HttpClient(new ContestaHtml()), new ConexionCaja { Servicio = new Uri("http://localhost/") });

        var error = await Assert.ThrowsAsync<ErrorCaja>(() => transporte.GetAsync<SesionDto>("api/algo"));
        var alMandar = await Assert.ThrowsAsync<ErrorCaja>(() => transporte.PostAsync<SesionDto>("api/algo", new { }));

        Assert.Equal(("otra-version", "otra-version"), (error.Codigo, alMandar.Codigo));
    }

    /// <summary>
    /// El servicio acepta y no contesta: pedir la cuenta del turno al entrar tiene plazo; sin él, la entrada se quedaba esperando sin fin.
    /// </summary>
    [Fact]
    public async Task Lo_de_fondo_tiene_plazo_aunque_el_servicio_no_conteste()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var turno = (await v.Cliente.TurnoAsync(v.Caja.Id))!;
        v.Red.TodoDetenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await v.Contingencia.TurnoEnLineaAsync(turno).WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            v.Red.TodoDetenido.SetResult();
        }
    }

    /// <summary>Un turno guardado que no se entiende no detiene el latido: lo vendido sin conexión sigue subiendo.</summary>
    [Fact]
    public async Task Un_turno_guardado_que_no_se_entiende_no_detiene_la_subida()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        v.Contingencia.Cerrar();
        var almacen = new AlmacenLocal(_baseLocal);
        almacen.Escribir(l => l.Guardar("turno", new { id = 123 }));
        var contingencia = new Contingencia(almacen, new HttpClient(v.Red), new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = v.Caja.Id });

        await contingencia.LatidoAsync();

        Assert.True(contingencia.ServidorResponde);
        contingencia.Cerrar();
    }

    /// <summary>Restaurar espera la revisión completa del respaldo: va por el transporte de las descargas, no con el tope de 15 s.</summary>
    [Fact]
    public async Task Restaurar_va_con_el_tope_de_las_descargas()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var (interactivo, descargas) = (new RedDePrueba(servicio.Fabrica.Server.CreateHandler()), new RedDePrueba(servicio.Fabrica.Server.CreateHandler()));
        var cliente = new ClienteCaja(new HttpClient(interactivo), new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = v.Caja.Id }, null, new HttpClient(descargas));

        await Assert.ThrowsAnyAsync<Exception>(() => cliente.RestaurarAsync(v.Caja.Id, new SolicitudRestaurar(v.Cajero.UsuarioId, "1111", "no-existe.db")));

        Assert.Equal(0, interactivo.Todas);
        Assert.Equal(1, descargas.Todas);
    }

    /// <summary>En el piloto, sin el servicio, la franja dice que lo cobrado no llega a eleventa; y al cerrar, que el corte no lo va a contar.</summary>
    [Fact]
    public void En_el_piloto_se_dice_que_lo_cobrado_no_llega_a_eleventa()
    {
        Assert.Contains("eleventa", AvisosDeRed.Texto(AvisosDeRed.Franja.SinServicio, piloto: true));
        Assert.Equal(AvisosDeRed.SinServicio, AvisosDeRed.Texto(AvisosDeRed.Franja.SinServicio, piloto: false));
        Assert.Equal(AvisosDeRed.Lenta, AvisosDeRed.Texto(AvisosDeRed.Franja.Lenta, piloto: true));
        Assert.Equal(PilotoConEleventa.CorteEnEleventa, PilotoConEleventa.CorteAlCerrar(0));
        Assert.Contains("3 operaciones que todavía no llegan a eleventa", PilotoConEleventa.CorteAlCerrar(3));
    }

    /// <summary>
    /// Lo que no pudo subir se ve en el chip «Eleventa» con qué es, cuánto y por qué; quien tiene «Configuración de la tienda» marca que ya lo
    /// anotó: deja de reintentarse, se quita y queda en la bitácora.
    /// </summary>
    [Fact]
    public async Task Lo_que_no_pudo_subir_se_ve_y_se_marca_como_anotado_en_eleventa()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        var solicitud = await CobroAsync(v);
        var (a, _) = v.DosDeLaCopia();
        v.Red.Caida = true;
        await v.Contingencia.LatidoAsync();
        await v.Cliente.CobrarAsync(solicitud);
        v.Red.Truena = ruta => ruta == "/api/bandeja/ventas";
        v.Red.Caida = false;
        await v.Contingencia.LatidoAsync();
        await v.Contingencia.SubirAsync();
        Assert.Equal(1, v.Contingencia.ConError);
        await v.Contingencia.RecordarPilotoEleventaAsync(true);

        var dueno = v.Cajero with { Permisos = [new PermisoDeSesion(AccionControlada.ConfigurarTienda, NivelPermiso.Permitido, null, false, [])] };
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = v.Caja.Id };
        await using var servicios = new ServiceCollection()
            .AddSingleton(v.Contingencia)
            .AddSingleton(v.Cliente)
            .AddSingleton(new ClientePuente(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion))
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<PuenteEleventa>(new() { [nameof(PuenteEleventa.Caja)] = v.Caja, [nameof(PuenteEleventa.Sesion)] = dueno });
        await pantalla.EsperarAsync(t => t.Contains("1 sin subir"), "el chip");
        await pantalla.OprimirAsync(await pantalla.BotonAsync("Eleventa"));
        var texto = await pantalla.TextoAsync();
        Assert.Contains(a.Descripcion, texto);
        Assert.Contains("avísale al dueño", texto);

        await pantalla.OprimirAsync(await pantalla.BotonAsync("Ya lo anoté en eleventa"));

        Assert.Equal(0, v.Contingencia.ConError);
        v.Red.Truena = null;
        var antes = v.Red.Cuantas("/api/bandeja/ventas");
        await v.Contingencia.SubirAsync();
        Assert.Equal(antes, v.Red.Cuantas("/api/bandeja/ventas"));
        await using var db = servicio.Base();
        Assert.Equal(0, await db.Ventas.CountAsync(x => x.Id == solicitud.VentaId));
        Assert.Equal(1, await db.Bitacora.CountAsync(b => b.Detalle.Contains("ya anotado en eleventa")));
        Assert.Empty(pantalla.Errores);
    }

    private sealed class DiscoCon(long libres) : PapeleriaPos.Servicio.IEspacioEnDisco
    {
        public long? Libre(string carpeta) => libres;
    }

    /// <summary>Con poco disco, la franja avisa antes de que no se pueda cobrar.</summary>
    [Fact]
    public async Task Con_poco_disco_el_estado_lo_dice()
    {
        await using var conPoco = await ServicioDePrueba.CrearAsync(s => s.AddSingleton<PapeleriaPos.Servicio.IEspacioEnDisco>(new DiscoCon(100_000_000)));
        await using var conDeSobra = await ServicioDePrueba.CrearAsync(s => s.AddSingleton<PapeleriaPos.Servicio.IEspacioEnDisco>(new DiscoCon(50_000_000_000)));

        var poco = await conPoco.Fabrica.CreateClient().GetFromJsonAsync<EstadoServicioDto>("api/estado");
        var sobra = await conDeSobra.Fabrica.CreateClient().GetFromJsonAsync<EstadoServicioDto>("api/estado");

        Assert.Contains("poco disco", poco!.AvisoBase);
        Assert.Null(sobra!.AvisoBase);
    }

    /// <summary>Con el servicio en la misma PC, no hay red que dude: el primer latido que contesta la saca de lenta.</summary>
    [Fact]
    public void Con_el_servicio_en_esta_pc_un_latido_basta_para_volver()
    {
        var red = new CalidadDeLaRed(ReglasDeLaRed.MismaPc, TimeProvider.System);

        red.PlazoVencido();
        Assert.Equal(EstadoRed.Lenta, red.Estado);
        red.Contesto(TimeSpan.FromSeconds(1));

        Assert.Equal(EstadoRed.Buena, red.Estado);
    }
}
