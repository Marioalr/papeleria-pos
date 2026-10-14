using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>La guía de la prueba con una persona real: la de la pantalla nueva (2026-10-17).</summary>
public class GuiaPruebaTests
{
    [Fact]
    public void Cada_tarea_tiene_su_Id_y_dice_que_hacer_y_que_debe_pasar()
    {
        var ids = GuiaPrueba.Tareas.Select(t => t.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(GuiaPrueba.Tareas, t =>
        {
            Assert.Matches("^[a-z]+(-[a-z]+)*$", t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Tema));
            Assert.False(string.IsNullOrWhiteSpace(t.Hacer));
            Assert.False(string.IsNullOrWhiteSpace(t.Esperado));
        });
    }

    [Fact]
    public void Las_respuestas_de_la_primera_prueba_son_de_otra_version()
    {
        // La 1 fue la de la pantalla de hoy (2026-10-10): sus respuestas se guardaron sin versión.
        Assert.True(GuiaPrueba.Version > 1);
        Assert.Equal(1, new RespuestasPruebaDto(null, new Dictionary<string, RespuestaTareaDto>(), null).Version);
    }

    [Fact]
    public void Primero_va_la_venta_y_al_final_lo_que_abre_las_pantallas_de_hoy()
    {
        var temas = GuiaPrueba.Tareas.Select(t => t.Tema).ToList();
        var primeraDeHoy = temas.FindIndex(t => t.StartsWith(GuiaPrueba.PantallaDeHoy, StringComparison.Ordinal));

        // La guía agrupa por tema: si uno se repitiera más abajo, sus tareas se juntarían arriba y cambiaría el orden.
        Assert.Equal(temas.Distinct().Count(), temas.Where((t, i) => i == 0 || temas[i - 1] != t).Count());
        Assert.Equal("entrar", GuiaPrueba.Tareas[0].Id);
        Assert.True(primeraDeHoy > 0);
        Assert.All(temas.Skip(primeraDeHoy), t => Assert.StartsWith(GuiaPrueba.PantallaDeHoy, t));
        // Lo de vender en la pantalla nueva va antes que la tarjeta, la transferencia y el crédito.
        var ids = GuiaPrueba.Tareas.Select(t => t.Id).ToList();
        Assert.True(ids.IndexOf("cobrar") < ids.IndexOf("tarjeta-transferencia"));
        Assert.True(ids.IndexOf("cliente-venta") < ids.IndexOf("credito-fiar"));
        Assert.True(ids.IndexOf("credito-sin-conexion") < primeraDeHoy);
        Assert.True(ids.IndexOf("corte") < ids.IndexOf("credito-cuenta"));
    }

    [Theory]
    [InlineData("Le atiende")]
    [InlineData("F6 - Pendiente")]
    [InlineData("Todo a crédito")]
    [InlineData("El resto a crédito")]
    [InlineData("La ventana de cobro")]
    [InlineData("En «Devoluciones»")]
    [InlineData("Contar ahora")]
    [InlineData("intenta autorizar con «encargado»")]
    public void La_venta_ya_no_se_describe_con_la_pantalla_de_hoy(string deLaPantallaDeHoy) =>
        Assert.DoesNotContain(GuiaPrueba.Tareas, t => t.Hacer.Contains(deLaPantallaDeHoy) || t.Esperado.Contains(deLaPantallaDeHoy));

    /// <summary>
    /// El cambio ya no pide otro Enter desde $500 ni tiene tope de $2,000 (decisión del dueño, 2026-10-14): quien prueba
    /// marcaría «Algo falló» en algo que se quitó a propósito.
    /// </summary>
    [Theory]
    [InlineData("Enter otra vez")]
    [InlineData("Cambio grande:")]
    [InlineData("No es dinero")]
    [InlineData("el cambio pasaría de")]
    public void La_guia_no_espera_la_confirmacion_ni_el_tope_del_cambio(string loQueSeQuito) =>
        Assert.DoesNotContain(GuiaPrueba.Tareas, t => t.Hacer.Contains(loQueSeQuito) || t.Esperado.Contains(loQueSeQuito));

    [Fact]
    public void La_franja_ensena_la_primera_sin_contestar_y_siguiente_no_se_atora()
    {
        var tareas = GuiaPrueba.Tareas;
        var respuestas = new Dictionary<string, RespuestaTareaDto>
        {
            [tareas[0].Id] = new(true, null),
            [tareas[1].Id] = new(false, "Se trabó"),
            [tareas[3].Id] = new(null, "Solo una nota: sigue sin contestar"),
        };

        Assert.Equal(2, GuiaPrueba.SinContestarDespuesDe(respuestas, -1));
        Assert.Equal(3, GuiaPrueba.SinContestarDespuesDe(respuestas, 2));
        // Al llegar al final da la vuelta: la que se saltó vuelve.
        Assert.Equal(2, GuiaPrueba.SinContestarDespuesDe(respuestas, tareas.Count - 1));
        Assert.Equal(3, GuiaPrueba.Siguiente(respuestas, 2));

        var todas = tareas.ToDictionary(t => t.Id, _ => new RespuestaTareaDto(true, null));
        Assert.Equal(-1, GuiaPrueba.SinContestarDespuesDe(todas, -1));
        // Si solo falta la de ahora, «Siguiente» pasa a la que sigue en orden y luego regresa a ella.
        todas[tareas[5].Id] = new RespuestaTareaDto(null, null);
        Assert.Equal(6, GuiaPrueba.Siguiente(todas, 5));
        Assert.Equal(5, GuiaPrueba.Siguiente(todas, 6));
    }

    [Fact]
    public void El_documento_dice_la_version_de_la_guia_y_avisa_si_es_otra()
    {
        var actual = GuiaPrueba.Documento(new RespuestasPruebaDto("Ana", new Dictionary<string, RespuestaTareaDto>(), null, GuiaPrueba.Version),
            new DateTime(2026, 10, 17, 12, 0, 0));
        var deOtra = GuiaPrueba.Documento(new RespuestasPruebaDto("Ana", new Dictionary<string, RespuestaTareaDto>(), null), new DateTime(2026, 10, 17, 12, 0, 0));

        Assert.Contains($"- Guía: versión {GuiaPrueba.Version}{Environment.NewLine}", actual);
        Assert.Contains($"- Guía: versión 1 (la de esta caja es la {GuiaPrueba.Version})", deOtra);
        Assert.Contains($"## {GuiaPrueba.PantallaDeHoy} · Corte", actual);
    }
}
