using PapeleriaPos.Datos;
using PapeleriaPos.Herramientas.Prueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La base de la prueba con una persona real (<c>preparar-prueba</c>) trae lo que se señala con productos (entrega 13): el
/// mostrador en sus dos repisas y la mesa de cada temporada, para que Ctrl+M y el chip tengan qué tocar. Lo que se acomode en
/// la prueba no se pisa.
/// </summary>
public sealed class MostradorDePruebaTests(ServicioEnPrueba prueba) : IClassFixture<ServicioEnPrueba>
{
    [Fact]
    public async Task La_base_de_la_prueba_trae_el_mostrador_y_las_temporadas_con_productos()
    {
        Assert.Contains(prueba.Hecho, h => h.StartsWith("Mostrador y temporadas con ", StringComparison.Ordinal));
        await using var db = new PosDbContext(BaseDeDatos.Opciones($"Data Source={prueba.BaseInicial};Pooling=False"));
        var mostrador = await RegistroMostrador.ListarAsync(db);

        var principal = mostrador.Single(e => e.Nombre == "Mostrador");
        Assert.All(principal.Lugares, l => Assert.True(l.Productos.Count >= 2, $"«{l.Nombre}» tiene {l.Productos.Count} productos."));
        foreach (var temporada in new[] { "15 de septiembre", "Todos Santos", "Navidad" })
            Assert.NotEmpty(mostrador.Single(e => e.Nombre == temporada).Lugares[0].Productos);
        // Otra vez no acomoda nada: lo de la prueba se queda.
        Assert.Empty(await MostradorDePrueba.AcomodarAsync(db));
    }
}
