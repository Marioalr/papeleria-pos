using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// F2 después de la auditoría y la revisión de #155 (2026-11-08): se usa con el teclado (el cursor abre en la primera tarjeta, ↑ ↓ pasan,
/// Espacio elige, Enter agrega), la lista ya no trae las miniaturas (se piden al mostrarse), una impresión que terminó con error lo dice con
/// su nota, y la tarjeta dice B/N por la tinta, sus copias, su papel y si es dúplex. Con su propio servicio: solo estas impresiones.
/// </summary>
public sealed class F2ConTecladoTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    [Fact]
    public async Task Con_el_teclado_se_elige_y_se_agrega_y_cada_tarjeta_dice_lo_suyo()
    {
        var (http, caja, _, _) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ahora = DateTime.UtcNow;
        var miniatura = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var vieja = ApiDeImpresoras.Trabajo(caja, "La de antes.pdf", 1, ahora.AddMinutes(-30), null) with { MiniaturaBase64 = miniatura };
        var atorada = ApiDeImpresoras.Trabajo(caja, "Se atoró.pdf", 3, ahora, null) with
        {
            Estado = EstadoTrabajo.TerminoConError, Nota = "Terminó con error en la impresora (atasco o cancelación).",
            Ajustes = new AjustesTrabajoDto("Standard", "Photo", "NorthAmericaLetter", 215.9, 279.4, Color: true, Duplex: true, Copias: 2, PaginasPorHoja: 1,
                DelPrintTicket: true),
        };
        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones", vieja));
        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones", atorada));

        // La lista ya no trae las miniaturas: solo si tiene; se pide aparte.
        var lista = (await http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>($"api/sucursales/{caja.SucursalId}/impresiones/por-cobrar?usuarioId={Guid.Empty}"))!;
        Assert.All(lista, t => Assert.Null(t.MiniaturaBase64));
        Assert.True(lista.Single(t => t.Id == vieja.Id).ConMiniatura);
        Assert.Equal(Convert.FromBase64String(miniatura), await http.GetByteArrayAsync($"api/impresiones/{vieja.Id}/miniatura"));

        var pagina = new PaginaQueContesta();
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        await using var servicios = new ServiceCollection().AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion)).AddSingleton<IJSRuntime>(pagina).BuildServiceProvider();
        var cliente = servicios.GetRequiredService<ClienteCaja>();
        var sesion = await cliente.EntrarAsync("cajero", "1111");
        ImpresionesPorCobrar.Elegidas? agregadas = null;
        await using var p = new PantallaSinNavegador(servicios);
        await p.AbrirAsync<ImpresionesPorCobrar>(new()
        {
            [nameof(ImpresionesPorCobrar.Caja)] = caja, [nameof(ImpresionesPorCobrar.Sesion)] = sesion,
            [nameof(ImpresionesPorCobrar.AlAgregar)] = EventCallback.Factory.Create<ImpresionesPorCobrar.Elegidas>(this, e => agregadas = e),
        });
        await p.EsperarAsync(t => t.Contains("Se atoró.pdf"), "las tarjetas");

        // Lo que dice cada una.
        var texto = PantallaSinNavegador.Limpio(await p.TextoAsync());
        Assert.Contains("Terminó con error", texto);
        Assert.Contains("Terminó con error en la impresora (atasco o cancelación).", texto);
        Assert.Contains("3 págs × 2 copias", texto);
        // Con el driver en color pero sin tinta de color: B/N.
        Assert.DoesNotContain("· Color", texto);
        Assert.Contains("Dúplex", texto);
        // La miniatura de la de antes llega sola, pedida aparte.
        await CajaConVariasImpresoras.EsperarAsync(async () => (await p.LeerAsync()).Elementos.Any(e => e.Etiqueta == "img"
            && e.Atributos.GetValueOrDefault("src") is string src && src.EndsWith(miniatura, StringComparison.Ordinal)), "la miniatura");

        // El cursor abre en la primera (la más nueva).
        var primera = $"imp-m{atorada.Id:N}";
        var segunda = $"imp-m{vieja.Id:N}";
        Assert.Contains(pagina.Llamadas, l => l.Funcion == "enfocar" && Equals(l.Argumentos?[0], $"#{primera}"));
        // ↓ pasa a la otra; Espacio la elige; Enter agrega lo elegido.
        await p.TeclaAsync(primera, "ArrowDown");
        Assert.Contains(pagina.Llamadas, l => l.Funcion == "enfocar" && Equals(l.Argumentos?[0], $"#{segunda}"));
        await p.TeclaAsync(segunda, " ");
        Assert.Equal("true", (await p.PorIdAsync(segunda)).Atributos["aria-checked"]);
        await p.TeclaAsync(segunda, "Enter");
        Assert.Equal([vieja.Id], agregadas!.DelModulo.Select(t => t.Id));

        // Sin nada elegido, Enter agrega la que tiene el cursor.
        await p.TeclaAsync(segunda, " ");
        await p.TeclaAsync(primera, "Enter");
        Assert.Equal([atorada.Id], agregadas!.DelModulo.Select(t => t.Id));
        Assert.Empty(p.Errores);
    }
}
