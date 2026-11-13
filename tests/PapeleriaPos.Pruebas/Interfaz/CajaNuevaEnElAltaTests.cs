using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Pruebas.Servicio;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// El alta de una caja con la base mudada de otra PC (la mudanza de la base, 2026-11-11), en la pantalla de verdad y contra el servicio
/// de verdad: la única caja de esa base puede llamarse «Caja 2», y «Caja nueva» proponía «Caja {cuántas hay + 1}», otra «Caja 2». Ahora
/// propone el primer «Caja N» libre desde ahí.
/// </summary>
public sealed class CajaNuevaEnElAltaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    /// <summary>Deja en la base solo esas cajas (las de antes, de baja), cada una dada de alta desde otra PC y con su propia serie.</summary>
    private async Task ConEstasCajasAsync(params (string Nombre, string Serie)[] cajas)
    {
        await using var db = servicio.Base();
        var deAntes = await db.Cajas.ToListAsync();
        var sucursal = deAntes[0].SucursalId;
        foreach (var caja in deAntes)
        {
            caja.Activa = false;
            // Las series son únicas por sucursal, también las de las cajas de baja: se hacen a un lado.
            caja.Serie = $"x{Guid.NewGuid():N}"[..12];
        }
        foreach (var (nombre, serie) in cajas)
        {
            var caja = new Caja { SucursalId = sucursal, Nombre = nombre, Serie = serie };
            caja.DarDeAlta("CAJA-2", DateTime.UtcNow);
            db.Cajas.Add(caja);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>La pantalla del alta, ya con el usuario y el PIN del dueño: lo que propone para «Caja nueva» (su nombre y su serie).</summary>
    private async Task<(PantallaSinNavegador Pantalla, ConexionCaja Conexion, ServiceProvider Servicios)> HastaElegirLaCajaAsync()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:5080/"), PideAlta = true };
        var servicios = new ServiceCollection()
            .AddSingleton(conexion)
            .AddSingleton(new ClienteCaja(servicio.Fabrica.CreateClient(), conexion))
            .BuildServiceProvider();
        var pantalla = new PantallaSinNavegador(servicios);
        await pantalla.AbrirAsync<ConfigurarCaja>([]);
        await pantalla.EsperarAsync(t => t.Contains("Lo autoriza quien tiene"), "el usuario y el PIN");
        await pantalla.EscribirAsync("alta-usuario", "dueno");
        await pantalla.EscribirAsync("alta-pin", "3333");
        await pantalla.TeclaAsync("alta-pin", "Enter");
        await pantalla.EsperarAsync(t => t.Contains("¿Qué caja es esta PC"), "la lista de cajas");
        return (pantalla, conexion, servicios);
    }

    /// <summary>Los dos campos de «Caja nueva» (no tienen id): el nombre y la serie, en ese orden.</summary>
    private static async Task<(string Nombre, string Serie)> LoQueProponeAsync(PantallaSinNavegador pantalla)
    {
        var campos = (await pantalla.LeerAsync()).Elementos
            .Where(e => e.Etiqueta == "input" && e.Atributos.GetValueOrDefault("type")?.ToString() != "radio").ToList();
        Assert.Equal(2, campos.Count);
        return (campos[0].Valor!, campos[1].Valor!);
    }

    [Theory]
    // Como hoy, mientras no choque: con una caja, «Caja 2»; con dos, «Caja 3».
    [InlineData("Caja 3", "Caja 1", "Caja 2")]
    [InlineData("Caja 2", "Caja 1")]
    [InlineData("Caja 2", "Mostrador")]
    // La base mudada de la caja 2: su única caja ya se llama «Caja 2».
    [InlineData("Caja 3", "Caja 2")]
    // El primero libre desde cuántas hay más una, no el primero de todos: «Caja 1» sigue libre, y no se propone.
    [InlineData("Caja 4", "Caja 2", "Caja 3")]
    [InlineData("Caja 5", "Caja 3", "Caja 4")]
    // Sin distinguir mayúsculas ni los espacios de las orillas.
    [InlineData("Caja 4", "Mostrador", " CAJA 3 ")]
    public async Task Caja_nueva_propone_el_primer_nombre_libre_desde_cuantas_hay_mas_una(string esperado, params string[] yaHay)
    {
        await ConEstasCajasAsync(yaHay.Select((nombre, i) => (nombre, ((char)('B' + i)).ToString())).ToArray());
        var (pantalla, conexion, servicios) = await HastaElegirLaCajaAsync();
        await using var alFinal = servicios;
        await using var antes = pantalla;

        // Todas las cajas tienen equipo: viene elegida «Caja nueva», con la primera serie libre. Todavía no se da de alta nada.
        Assert.Equal((esperado, "A"), await LoQueProponeAsync(pantalla));
        Assert.Null(conexion.CajaId);
        Assert.Empty(pantalla.Errores);
    }

    /// <summary>
    /// El sábado en la principal: con la base de la caja 2 recién traída, el dueño da de alta la caja de esa PC como «Caja nueva» con lo
    /// que se le propone. Quedan «Caja 2» (serie B), con su equipo y su llave de siempre, y «Caja 3» (serie A): ninguna repite nombre.
    /// </summary>
    [Fact]
    public async Task Con_la_base_mudada_la_caja_nueva_se_da_de_alta_sin_repetir_el_nombre_de_la_que_ya_existe()
    {
        await ConEstasCajasAsync(("Caja 2", "B"));
        string huellaDeLaCajaDos;
        await using (var db = servicio.Base())
            huellaDeLaCajaDos = (await db.Cajas.SingleAsync(c => c.Activa)).LlaveHuella!;
        var (pantalla, conexion, servicios) = await HastaElegirLaCajaAsync();
        await using var alFinal = servicios;
        await using var antes = pantalla;
        Assert.Contains("Caja 2 (serie B)", PantallaSinNavegador.Limpio(await pantalla.TextoAsync()));
        Assert.Equal(("Caja 3", "A"), await LoQueProponeAsync(pantalla));

        await pantalla.OprimirAsync(await pantalla.BotonAsync("Dar de alta"));
        await VentaConCopia.EsperarAsync(() => conexion.CajaId is not null, "el alta de la caja nueva");

        await using var base_ = servicio.Base();
        var cajas = await base_.Cajas.AsNoTracking().Where(c => c.Activa).OrderBy(c => c.Nombre).ToListAsync();
        Assert.Equal([("Caja 2", "B"), ("Caja 3", "A")], cajas.Select(c => (c.Nombre, c.Serie)));
        Assert.Equal(conexion.CajaId, cajas[1].Id);
        // La caja 2 conserva su alta: nadie eligió su renglón.
        Assert.Equal(("CAJA-2", huellaDeLaCajaDos), (cajas[0].Equipo, cajas[0].LlaveHuella));
        Assert.Empty(pantalla.Errores);
    }
}
