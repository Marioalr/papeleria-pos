using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Abrir la caja (entrega 5, parte 3, docs/propuesta-interfaz.md, «Rendimiento», «Arranque»): con la copia de esta caja, la
/// entrada sale sin esperar a la red. La caja, su turno y las fichas de «¿Quién atiende?» salen de la copia (las mismas que da
/// el servicio) y la caja principal los pone al día cuando contesta. Aquí la red no contesta nada hasta el final: no depende
/// de lo que tarda la PC.
/// </summary>
public sealed class EntradaConLaCopiaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-entrada-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    [Fact]
    public async Task La_entrada_sale_con_la_copia_sin_esperar_a_la_red()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var red = new RedDePrueba(servicio.Fabrica.Server.CreateHandler());
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };

        // La caja ya bajó su copia y conoce su turno.
        var antes = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(red), conexion);
        var clienteAntes = new ClienteCaja(new HttpClient(red), conexion, antes);
        await antes.LatidoAsync();
        var cajero = await clienteAntes.EntrarAsync("cajero", "1111");
        var turno = await clienteAntes.TurnoAsync(caja.Id) ?? await clienteAntes.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        var delServicio = await clienteAntes.FichasAsync(caja.Id);
        antes.Cerrar();

        // Se vuelve a abrir, con la red tan lenta que nada contesta.
        var lenta = new RedDePrueba(servicio.Fabrica.Server.CreateHandler())
        {
            TodoDetenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), new HttpClient(lenta), conexion);
        var cliente = new ClienteCaja(new HttpClient(lenta), conexion, contingencia);
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(cliente)
            .AddSingleton(contingencia)
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        try
        {
            var deLaCopia = await cliente.DeLaCopiaAsync(caja.Id).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(deLaCopia);
            Assert.Equal(caja.Id, deLaCopia.Value.Caja.Id);
            Assert.Equal(turno.Id, deLaCopia.Value.Turno?.Id);

            // Sin esperar a que termine de abrir: el lado de la entrada también le pregunta a la caja principal (el nombre de la
            // tienda), y eso no contesta.
            var abrir = pantalla.AbrirAsync<Entrada>(new()
            {
                [nameof(Entrada.Caja)] = deLaCopia.Value.Caja,
                [nameof(Entrada.Turno)] = deLaCopia.Value.Turno,
            });
            await pantalla.EsperarAsync(t => t.Contains("¿Quién atiende?") && t.Contains("Cajero de prueba"), "las fichas de la copia");

            // Las mismas que da el servicio, y sin que la red haya contestado nada.
            Assert.Equal(delServicio, cliente.FichasDeLaCopia(caja.Id));
            Assert.False(lenta.Respondio("/usuarios"));
            Assert.Empty(pantalla.Errores);

            // Cuando contesta, las pone al día (son las mismas: no cambia nada).
            lenta.TodoDetenido.SetResult();
            await abrir.WaitAsync(TimeSpan.FromSeconds(20));
            await VentaConCopia.EsperarAsync(() => lenta.Respondio("/usuarios"), "las fichas del servicio");
            Assert.Contains("Cajero de prueba", await pantalla.TextoAsync());
        }
        finally
        {
            lenta.TodoDetenido.TrySetResult();
            await ((IAsyncDisposable)pantalla).DisposeAsync();
            await servicios.DisposeAsync();
            contingencia.Cerrar();
        }
    }

    /// <summary>Sin copia (la caja recién instalada), no hay de dónde: se le pregunta al servicio, como antes.</summary>
    [Fact]
    public async Task Sin_copia_se_le_pregunta_al_servicio()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var http = new HttpClient(servicio.Fabrica.Server.CreateHandler());
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);

        Assert.Null(await cliente.DeLaCopiaAsync(caja.Id));
        Assert.Null(cliente.FichasDeLaCopia(caja.Id));
        // Ni sin la caja de la ventana de Windows (el navegador de la caja principal).
        Assert.Null(await new ClienteCaja(http, conexion).DeLaCopiaAsync(caja.Id));
        contingencia.Cerrar();
    }
}
