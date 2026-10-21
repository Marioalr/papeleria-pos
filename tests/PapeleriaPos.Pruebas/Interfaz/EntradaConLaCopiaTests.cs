using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
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
        ArchivosDePrueba.BorrarBase(_baseLocal);
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

    /// <summary>
    /// La copia tenía un solo usuario (sale elegido: solo falta su PIN) y el servicio trae más, como alguien dado de alta hace
    /// un momento, que puede ser quien está tecleando. La ficha elegida sola se quita, con lo que se tecleó: antes se quedaba,
    /// y el PIN del nuevo gastaba los intentos del otro. Si se tocó la ficha, se queda (eso no cambia).
    /// </summary>
    [Fact]
    public async Task Si_la_copia_tenia_uno_solo_y_el_servicio_trae_mas_ya_no_queda_elegido()
    {
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };

        // Cuando la caja bajó su copia, solo el cajero estaba activo.
        await ActivosAsync(false, "dueno", "encargado");
        try
        {
            var http = new HttpClient(servicio.Fabrica.Server.CreateHandler());
            var antes = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
            await antes.LatidoAsync();
            Assert.Equal(["Cajero de prueba"], new ClienteCaja(http, conexion, antes).FichasDeLaCopia(caja.Id)!.Select(f => f.Nombre));
            antes.Cerrar();
        }
        finally
        {
            await ActivosAsync(true, "dueno", "encargado");
        }

        // Se vuelve a abrir con la red tan lenta que nada contesta: sale con la copia, con su única ficha elegida.
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
            var abrir = pantalla.AbrirAsync<Entrada>(new()
            {
                [nameof(Entrada.Caja)] = deLaCopia!.Value.Caja,
                [nameof(Entrada.Turno)] = deLaCopia.Value.Turno,
            });
            await pantalla.EsperarAsync(t => t.Contains("PIN de Cajero de prueba"), "la única ficha de la copia, elegida");

            // Quien teclea su PIN es el nuevo: dos cifras.
            foreach (var cifra in "47")
                await pantalla.OprimirAsync(await pantalla.ElementoAsync(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == cifra.ToString(), $"la cifra {cifra}"));
            Assert.Equal(2, (await pantalla.LeerAsync()).Elementos.Count(e => e.Etiqueta == "i" && e.Clase == "on"));

            // Contesta el servicio, con los tres: ya no hay ficha elegida ni cifras; cada quien toca la suya.
            lenta.TodoDetenido.SetResult();
            await abrir.WaitAsync(TimeSpan.FromSeconds(20));
            await pantalla.EsperarAsync(t => t.Contains("Encargado") && !t.Contains("PIN de"), "las tres fichas, sin elegida");
            Assert.DoesNotContain((await pantalla.LeerAsync()).Elementos, e => e.Etiqueta == "button" && e.Clase == "ficha-entrada on");
            Assert.Empty(pantalla.Errores);
        }
        finally
        {
            lenta.TodoDetenido.TrySetResult();
            await ((IAsyncDisposable)pantalla).DisposeAsync();
            await servicios.DisposeAsync();
            contingencia.Cerrar();
        }
    }

    private async Task ActivosAsync(bool activos, params string[] usuarios)
    {
        await using var db = servicio.Base();
        foreach (var usuario in await db.Usuarios.Where(u => usuarios.Contains(u.NombreUsuario)).ToListAsync())
            usuario.Activo = activos;
        await db.SaveChangesAsync();
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
