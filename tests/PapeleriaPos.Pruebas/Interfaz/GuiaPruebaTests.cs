using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>La guía de la prueba con una persona real: la de la pantalla de venta del rediseño, con la entrada y el corte nuevos (2026-10-17).</summary>
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
        // La 1 fue la de la pantalla de antes (2026-10-10): sus respuestas se guardaron sin versión.
        Assert.True(GuiaPrueba.Version > 1);
        Assert.Equal(1, new RespuestasPruebaDto(null, new Dictionary<string, RespuestaTareaDto>(), null).Version);
    }

    [Fact]
    public void Primero_va_la_venta_luego_las_paginas_de_atras_y_al_final_la_configuracion()
    {
        var temas = GuiaPrueba.Tareas.Select(t => t.Tema).ToList();
        var primeraDePaginas = temas.FindIndex(GuiaPrueba.EsDeLasPaginas);
        var primeraDeConfiguracion = temas.FindIndex(t => t.StartsWith(GuiaPrueba.ConfiguracionNueva, StringComparison.Ordinal));

        // La guía agrupa por tema: si uno se repitiera más abajo, sus tareas se juntarían arriba y cambiaría el orden.
        Assert.Equal(temas.Distinct().Count(), temas.Where((t, i) => i == 0 || temas[i - 1] != t).Count());
        Assert.Equal("entrar", GuiaPrueba.Tareas[0].Id);
        // Después de vender, las páginas de atrás (entrega 8): Productos, Inventario y Clientes, seguidas; y al final, solo la
        // Configuración nueva (entrega 7).
        Assert.True(primeraDePaginas > 0);
        Assert.True(primeraDeConfiguracion > primeraDePaginas);
        Assert.All(temas.Skip(primeraDePaginas).Take(primeraDeConfiguracion - primeraDePaginas), t => Assert.True(GuiaPrueba.EsDeLasPaginas(t), t));
        Assert.All(temas.Skip(primeraDeConfiguracion), t => Assert.StartsWith(GuiaPrueba.ConfiguracionNueva, t));
        Assert.All(temas.Take(primeraDePaginas), t => Assert.False(GuiaPrueba.EsDeLasPaginas(t), t));
        // Lo de vender va antes que la tarjeta, la transferencia y el crédito.
        var ids = GuiaPrueba.Tareas.Select(t => t.Id).ToList();
        Assert.True(ids.IndexOf("cobrar") < ids.IndexOf("tarjeta-transferencia"));
        Assert.True(ids.IndexOf("cliente-venta") < ids.IndexOf("credito-fiar"));
        // Luego cambiar de usuario y el corte (entrega 6), antes de las páginas: lo de la venta se prueba primero, y las páginas
        // se hacen con otro turno del cajero, abierto después del corte del día. La primera de ellas, ir y volver de la venta.
        Assert.True(ids.IndexOf("credito-sin-conexion") < ids.IndexOf("otro-turno"));
        Assert.True(ids.IndexOf("otro-turno") < ids.IndexOf("entrar-sin-conexion"));
        Assert.True(ids.IndexOf("entrar-sin-conexion") < ids.IndexOf("corte"));
        Assert.True(ids.IndexOf("corte") < ids.IndexOf("corte-del-dia"));
        Assert.True(ids.IndexOf("corte-del-dia") < primeraDePaginas);
        Assert.Equal("paginas", ids[primeraDePaginas]);
        Assert.True(ids.IndexOf("corte") < ids.IndexOf("credito-cuenta"));
    }

    /// <summary>
    /// Productos, Inventario y Clientes ya son páginas (entrega 8): ya no queda nada que abra sus ventanas de antes, ni el
    /// tema que las marcaba, ni la fila de pestañas de F4.
    /// </summary>
    [Theory]
    [InlineData("Ventana de antes")]
    [InlineData("ventanas de antes")]
    [InlineData("F4 →")]
    [InlineData("Ajustes y mermas")]
    [InlineData("En la pestaña «Por revisar»")]
    public void Las_paginas_de_atras_ya_no_se_describen_como_ventanas(string deAntes) =>
        Assert.DoesNotContain(GuiaPrueba.Tareas, t => t.Hacer.Contains(deAntes) || t.Esperado.Contains(deAntes) || t.Tema.Contains(deAntes));

    /// <summary>
    /// La guía v5 (versión 4, 2026-10-24): lo de igualar a eleventa va en su tema y en el orden en que se hace. Los reportes, con el
    /// turno del dueño, después del corte y antes de las páginas; el turno del cajero de las páginas se abre al terminarlos. Ins es
    /// «Varios» y el artículo común, Ctrl+P; «Agregar inventario», junto a «Recibir mercancía».
    /// </summary>
    [Fact]
    public void Lo_de_eleventa_va_en_su_tema_y_en_su_orden()
    {
        var ids = GuiaPrueba.Tareas.Select(t => t.Id).ToList();
        var tarea = (string id) => GuiaPrueba.Tareas.Single(t => t.Id == id);

        Assert.True(GuiaPrueba.Version >= 4);
        Assert.Equal(["corte", "corte-del-dia", "historial-cortes", "cancelar-salida", "reportes", "excel-y-ventas-por-periodo", "paginas"],
            ids.Skip(ids.IndexOf("corte")).Take(7));
        Assert.All(new[] { "reportes", "excel-y-ventas-por-periodo" }, id => Assert.Equal("Reportes", tarea(id).Tema));
        Assert.DoesNotContain("abrir otro turno del cajero", tarea("historial-cortes").Hacer);
        Assert.Contains("abrir otro turno del cajero", tarea("excel-y-ventas-por-periodo").Hacer);

        Assert.All(new[] { "nota", "coma-en-el-dinero" }, id => Assert.Equal("Cobrar", tarea(id).Tema));
        Assert.Equal(ids.IndexOf("alta-rapida") + 1, ids.IndexOf("varios"));
        Assert.Contains("Ins", tarea("varios").Hacer);
        Assert.DoesNotContain("Ins", tarea("alta-rapida").Hacer + tarea("alta-rapida").Esperado);
        Assert.Contains("Ctrl+P", tarea("alta-rapida").Hacer);
        Assert.Contains("«Imprimir»", tarea("al-cobrar").Hacer);
        Assert.DoesNotContain("si lo piden, «Reimprimir»", tarea("al-cobrar").Esperado);

        // Las promociones antes de «Cambiar precios» (con los precios de la base); los productos en Excel, después.
        string[] deProductos = ["promociones", "promo-ctrl-e", "promo-cliente", "dejar-al-costo", "promocion-debajo-del-costo", "promo-fuera",
            "precios-varios", "productos-excel", "productos-excel-dueno"];
        Assert.Equal(deProductos, ids.Skip(ids.IndexOf("productos") + 1).Take(deProductos.Length));
        Assert.All(deProductos, id => Assert.Equal("Productos (F3)", tarea(id).Tema));
        Assert.Equal(ids.IndexOf("recibir") + 1, ids.IndexOf("agregar-inventario"));
        Assert.Equal(tarea("recibir").Tema, tarea("agregar-inventario").Tema);
        Assert.Contains("Agregar inventario", tarea("recibir").Esperado);
    }

    [Fact]
    public void Las_paginas_se_prueban_como_las_dejo_la_entrega_8()
    {
        var tarea = (string id) => GuiaPrueba.Tareas.Single(t => t.Id == id);

        Assert.Contains("F1", tarea("paginas").Hacer);
        Assert.Contains("como estaba", tarea("paginas").Esperado);
        Assert.Contains("ya no abre vacío", tarea("productos").Esperado);
        Assert.Contains("tú: hasta $200.00", tarea("recibir").Esperado);
        Assert.Contains("← Inventario", tarea("recibir").Esperado);
        Assert.Contains("«Etiqueta»", tarea("etiquetas").Hacer);
        Assert.Contains("puedes cambiarlo", tarea("credito-cuenta").Esperado);
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
    [InlineData("Volver a la pantalla de hoy")]
    [InlineData("Probar la pantalla nueva")]
    [InlineData("Pantalla de hoy")]
    [InlineData("Dar de alta «")]
    public void La_venta_ya_no_se_describe_con_la_pantalla_de_hoy(string deLaPantallaDeHoy) =>
        Assert.DoesNotContain(GuiaPrueba.Tareas, t => t.Hacer.Contains(deLaPantallaDeHoy) || t.Esperado.Contains(deLaPantallaDeHoy)
            || t.Tema.Contains(deLaPantallaDeHoy));

    /// <summary>
    /// La entrada es con fichas y PIN, y el corte del turno solo pide el total a quien ya entró (entrega 6, 2026-10-14): en
    /// la caja ya no se escribe el usuario (solo en el conteo desde el celular), ni hay corte X aparte ni el Z con el usuario
    /// del encargado.
    /// </summary>
    [Theory]
    [InlineData("entra como «")]
    [InlineData("Entra con el usuario")]
    [InlineData("La entrada es la de hoy")]
    [InlineData("escribe su usuario y PIN")]
    [InlineData("Corte de cierre (Z)")]
    [InlineData("corte X (parcial)")]
    public void La_entrada_y_el_corte_ya_no_se_describen_como_antes(string deAntes) =>
        Assert.DoesNotContain(GuiaPrueba.Tareas, t => t.Hacer.Contains(deAntes) || t.Esperado.Contains(deAntes));

    [Fact]
    public void Se_entra_con_la_ficha_y_el_PIN_y_el_corte_no_vuelve_a_pedirlo()
    {
        var entrar = GuiaPrueba.Tareas.Single(t => t.Id == "entrar");
        var corte = GuiaPrueba.Tareas.Single(t => t.Id == "corte");

        Assert.Contains("ficha", entrar.Hacer);
        Assert.Contains("1111", entrar.Hacer);
        Assert.Contains("no vuelve a escribir su PIN", corte.Esperado);
        Assert.Contains(GuiaPrueba.Tareas, t => t.Id == "corte-del-dia" && t.Hacer.Contains("Cerrar el día"));
    }

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
        Assert.Contains("## El corte", actual);
        Assert.Contains("## Clientes", actual);
        Assert.Contains("## Productos (F3)", actual);
    }
}
