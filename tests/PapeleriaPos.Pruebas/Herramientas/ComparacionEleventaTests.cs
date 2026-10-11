using PapeleriaPos.Eleventa;

namespace PapeleriaPos.Pruebas.Herramientas;

/// <summary>Comparar dos fotos de eleventa: qué apareció, qué cambió y qué se quitó (el piloto, paso 2).</summary>
public class ComparacionEleventaTests
{
    private static TablaEnFoto Tabla(string nombre, bool completa, params (long Id, string Total)[] renglones) =>
        new(nombre, ["ID", "TOTAL"], ["ID"], renglones.Length, completa,
            renglones.ToDictionary(r => r.Id.ToString(), r => new[] { r.Id.ToString(), r.Total }));

    private static FotoEleventa Foto(Dictionary<string, long> contadores, params TablaEnFoto[] tablas) =>
        new(DateTime.UtcNow, contadores, tablas.ToDictionary(t => t.Nombre));

    [Fact]
    public void Dice_los_contadores_que_avanzaron_y_los_renglones_nuevos_cambiados_y_quitados()
    {
        var antes = Foto(new() { ["GEN_TICKETS"] = 10, ["GEN_OTRO"] = 3 }, Tabla("TURNOS", true, (1, "100"), (2, "50"), (3, "7")));
        var despues = Foto(new() { ["GEN_TICKETS"] = 12, ["GEN_OTRO"] = 3 }, Tabla("TURNOS", true, (1, "100"), (2, "55"), (4, "9")));

        var texto = ComparacionEleventa.Comparar(antes, despues);

        Assert.Contains("GEN_TICKETS 10 → 12 (+2)", texto);
        Assert.DoesNotContain("GEN_OTRO", texto);
        Assert.Contains("~ ID=2: TOTAL 50 → 55", texto);
        Assert.Contains("+ ID=4: TOTAL=9", texto);
        Assert.Contains("- ID=3: TOTAL=7", texto);
        Assert.DoesNotContain("ID=1", texto);
    }

    [Fact]
    public void En_una_tabla_grande_lo_que_sale_de_la_ventana_no_cuenta_como_quitado()
    {
        // Solo los últimos renglones: el 100 ya no sale en la segunda foto porque entró el 103.
        var antes = Foto([], Tabla("VENTATICKETS", false, (100, "1"), (101, "2"), (102, "3")));
        var despues = Foto([], Tabla("VENTATICKETS", false, (101, "2"), (102, "4"), (103, "5")));

        var texto = ComparacionEleventa.Comparar(antes, despues);

        Assert.Contains("+ ID=103: TOTAL=5", texto);
        Assert.Contains("~ ID=102: TOTAL 3 → 4", texto);
        Assert.DoesNotContain("ID=100", texto);
    }

    [Fact]
    public void Sin_nada_distinto_lo_dice()
    {
        var foto = Foto(new() { ["GEN_TICKETS"] = 10 }, Tabla("TURNOS", true, (1, "100")));

        Assert.Equal("Sin cambios.", ComparacionEleventa.Comparar(foto, foto));
    }
}
