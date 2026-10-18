using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Lo que la caja recuerda de lo que contestó la caja principal del mostrador y las temporadas (entrega 13). Con copia, solo
/// mientras la copia no llega a eso, y solo sobre una copia de su misma base: después de «Empezar de cero» (o de restaurar un
/// respaldo) las versiones vuelven a empezar más abajo, y Ctrl+M y el chip se quedaban con lo de antes hasta cerrar la caja.
/// Sin copia (el navegador), Ctrl+M le pregunta cada vez y el chip a lo más cada minuto: se quedaban con lo de la primera vez.
/// </summary>
public sealed class MostradorRecordadoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-recordado-{Guid.NewGuid():N}.db");
    private readonly List<Contingencia> _abiertas = [];

    public void Dispose()
    {
        foreach (var contingencia in _abiertas)
            contingencia.Cerrar(TimeSpan.FromSeconds(10));
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static List<string> Nombres(IEnumerable<ExhibidorDto>? exhibidores) => exhibidores?.Select(e => e.Nombre).ToList() ?? [];

    /// <summary>Mediodía de ese día en la tienda.</summary>
    private static DateTimeOffset EnLaTienda(int anio, int mes, int dia) => new(anio, mes, dia, 12, 0, 0, TimeSpan.FromHours(-6));

    [Fact]
    public async Task Con_copia_lo_que_contesto_solo_vale_sobre_una_copia_de_su_misma_base_y_se_suelta_al_llegarle()
    {
        // Una caja principal propia: aquí «empieza de cero».
        await using var principal = await ServicioDePrueba.CrearAsync();
        var caja = (await principal.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var red = new RedDePrueba(principal.Fabrica.Server.CreateHandler());
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(red), conexion);
        _abiertas.Add(contingencia);
        var cliente = new ClienteCaja(new HttpClient(red), conexion, contingencia);
        await contingencia.LatidoAsync();
        await contingencia.CopiaLeida;
        var dueno = await cliente.EntrarAsync("dueno", "3333");
        SolicitudNuevoExhibidor Nuevo(string nombre) => new(dueno.UsuarioId, caja.SucursalId, nombre, TipoExhibidor.Exhibidor);

        // Se da de alta una vitrina: hasta que la copia la traiga, Ctrl+M y el chip la ven con lo que contestó la caja principal.
        var antes = await cliente.NuevoExhibidorAsync(Nuevo("Vitrina de antes"));
        Assert.Equal(contingencia.Paquete!.Base, antes.Base);
        Assert.DoesNotContain("Vitrina de antes", Nombres(contingencia.Paquete.Exhibidores));
        Assert.Contains("Vitrina de antes", Nombres(cliente.ExhibidoresDeLaCopia()));

        // «Empezar de cero»: la base vuelve a como se instaló (sin la vitrina, con las versiones más abajo) y la caja baja la
        // copia completa de la base nueva. Manda la copia: lo que se recordaba de antes ya no.
        await using (var db = principal.Base())
        {
            (await db.Exhibidores.SingleAsync(e => e.Nombre == "Vitrina de antes")).Activo = false;
            await db.SaveChangesAsync();
            await db.Secuencias.Where(s => s.Nombre == Secuencia.Catalogo).ExecuteUpdateAsync(s => s.SetProperty(x => x.Valor, 1L));
        }
        principal.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.CatalogoEnMemoria>().Invalidar();
        principal.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.VersionesDeLaCopia>().BaseNueva();
        await contingencia.LatidoAsync();
        var deCero = contingencia.Paquete!;
        Assert.NotEqual(antes.Base, deCero.Base);
        Assert.True(deCero.Version < antes.Version, $"La copia de cero ({deCero.Version}) debía quedar abajo de lo de antes ({antes.Version}).");
        Assert.DoesNotContain("Vitrina de antes", Nombres(cliente.ExhibidoresDeLaCopia()));

        // Lo que se cambia después, con una versión más baja que lo de antes, sí se ve en lo que falta para que llegue la copia.
        var despues = await cliente.NuevoExhibidorAsync(Nuevo("Vitrina de después"));
        Assert.True(despues.Version < antes.Version, $"Lo de después ({despues.Version}) debía quedar abajo de lo de antes ({antes.Version}).");
        Assert.Equal(["Vitrina de después"], Nombres(cliente.ExhibidoresDeLaCopia()).Where(n => n.StartsWith("Vitrina", StringComparison.Ordinal)));

        // La copia llega a eso: manda la copia.
        await principal.CatalogoAlDiaAsync();
        await contingencia.LatidoAsync();
        Assert.Contains("Vitrina de después", Nombres(contingencia.Paquete!.Exhibidores));
        Assert.Same(contingencia.Paquete.Exhibidores, cliente.ExhibidoresDeLaCopia());
    }

    /// <summary>Sin copia (el navegador), cada vez que se abre Ctrl+M se le pregunta a la caja principal: lo de otra caja se ve.</summary>
    [Fact]
    public async Task Sin_copia_Ctrl_M_se_abre_con_lo_de_ahora()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var navegador = new ClienteCaja(servicio.Fabrica.CreateClient(), conexion);
        await using var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(navegador)
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        var parametros = new Dictionary<string, object?> { [nameof(Mostrador.Caja)] = caja, [nameof(Mostrador.Sesion)] = dueno };

        await using (var primera = new PantallaSinNavegador(servicios))
        {
            await primera.AbrirAsync<Mostrador>(parametros);
            await primera.EsperarAsync(t => t.Contains("Mostrador", StringComparison.Ordinal), "el mostrador");
            Assert.DoesNotContain("Vitrina de otra caja", await primera.TextoAsync());
        }
        // Otra caja da de alta una vitrina.
        await Leer<MostradorDto>(await http.PostAsJsonAsync("api/mostrador",
            new SolicitudNuevoExhibidor(dueno.UsuarioId, caja.SucursalId, "Vitrina de otra caja", TipoExhibidor.Exhibidor)));

        Assert.Null(navegador.ExhibidoresDeLaCopia());
        await using var segunda = new PantallaSinNavegador(servicios);
        await segunda.AbrirAsync<Mostrador>(parametros);
        await segunda.EsperarAsync(t => t.Contains("Vitrina de otra caja", StringComparison.Ordinal), "la vitrina de la otra caja");
        Assert.Empty(segunda.Errores);
    }

    /// <summary>
    /// Sin copia (el navegador), el chip le vuelve a preguntar a la caja principal, a lo más cada minuto (no con cada escaneo):
    /// la temporada que se dio de alta en otra caja sale sola. Con el reloj de prueba: no depende de lo que tarda la PC.
    /// </summary>
    [Fact]
    public async Task Sin_copia_el_chip_vuelve_a_preguntar_cada_minuto()
    {
        var reloj = new RelojFijo(EnLaTienda(2026, 11, 5));
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var red = new RedDePrueba(servicio.Fabrica.Server.CreateHandler());
        await using var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(new HttpClient(red), conexion))
            .AddSingleton<TimeProvider>(reloj)
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        await using var pantalla = new PantallaSinNavegador(servicios);
        var chips = await pantalla.AbrirAsync<ChipsDeTemporada>(new());
        Task Pintar() => pantalla.Dispatcher.InvokeAsync(() => chips.SetParametersAsync(ParameterView.Empty));
        async Task<List<string>> ChipsAsync() => (await pantalla.LeerAsync()).Elementos
            .Where(e => e.Clase?.Contains("vn-chip-temporada", StringComparison.Ordinal) == true).Select(e => PantallaSinNavegador.Limpio(e.Texto)).ToList();
        await pantalla.EsperarAsync(t => t.Contains("Todos Santos", StringComparison.Ordinal), "el chip");

        // Otra caja da de alta una temporada que corre ese día.
        var nueva = await Leer<MostradorDto>(await http.PostAsJsonAsync("api/mostrador",
            new SolicitudNuevoExhibidor(dueno.UsuarioId, caja.SucursalId, "Feria del libro", TipoExhibidor.Temporada)));
        await Leer<MostradorDto>(await http.PutAsJsonAsync($"api/mostrador/{nueva.Exhibidores.Single(e => e.Nombre == "Feria del libro").Id}",
            new SolicitudCambiarExhibidor(dueno.UsuarioId, caja.SucursalId, Fechas: new FechasTemporadaDto(new DiaDelAnio(11, 1), new DiaDelAnio(11, 30)))));

        // Con cada escaneo (la venta se vuelve a pintar) no se le pregunta otra vez...
        var preguntas = red.Cuantas("/api/mostrador");
        reloj.Ahora += TimeSpan.FromSeconds(30);
        await Pintar();
        Assert.Equal(["Todos Santos Ctrl+M"], await ChipsAsync());
        Assert.Equal(preguntas, red.Cuantas("/api/mostrador"));

        // ...y al minuto, sí: sale la temporada nueva.
        reloj.Ahora += TimeSpan.FromSeconds(31);
        await Pintar();
        Assert.Equal(["Todos Santos Ctrl+M", "Feria del libro Ctrl+M"], await ChipsAsync());
        Assert.Empty(pantalla.Errores);
    }
}
