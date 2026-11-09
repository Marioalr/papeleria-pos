using static PapeleriaPos.Impresiones.ConciliacionDelContador;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Las caras que nadie registró contra lo registrado y las tarjetas (2026-11-08): las rachas, lo que llega tarde, las copias que se toman de
/// una tarjeta, otra serie, el contador que vuelve a empezar y el color.
/// </summary>
public sealed class ConciliacionDelContadorTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 17, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Pausa = TimeSpan.FromSeconds(60);

    private static DateTime S(int segundos) => T0.AddSeconds(segundos);

    private static Lectura L(int segundos, long contador, long? color = null, string? serie = "X1") => new(S(segundos), contador, color, serie);

    private static Resultado Revisar(IEnumerable<Lectura> lecturas, int ahora, IEnumerable<Registrado>? registrados = null,
        IEnumerable<Tarjeta>? tarjetas = null, Consumir? consumir = null, int desde = 0) =>
        ConciliacionDelContador.Revisar(lecturas, registrados ?? [], tarjetas ?? [], S(desde), S(ahora), Pausa, consumir);

    [Fact]
    public void Una_racha_sale_en_una_sola_tarjeta_cuando_pasa_la_pausa_y_lo_que_se_espera_a_las_PC()
    {
        Lectura[] lecturas = [L(0, 1000), L(10, 1003), L(20, 1005), L(30, 1005), L(85, 1005)];

        // Todavía no pasa la pausa más lo que se espera: nada, pero hay que leerla de cerca.
        var antes = Revisar(lecturas, 85);
        Assert.Null(antes.Nueva);
        Assert.True(antes.Activa);
        Assert.Equal(5, antes.Saldo);

        var despues = Revisar([.. lecturas, L(111, 1005)], 111);
        var nueva = Assert.IsType<Nueva>(despues.Nueva);
        Assert.Equal((5, (int?)null, S(0), S(20)), (nueva.Caras, nueva.Color, nueva.DesdeUtc, nueva.HastaUtc));
        Assert.False(despues.Activa);
    }

    [Fact]
    public void Sin_una_lectura_despues_de_la_pausa_no_se_cierra()
    {
        // La última lectura es la de la última cara: no se sabe si sigue imprimiendo.
        var r = Revisar([L(0, 1000), L(10, 1004)], 300);
        Assert.Null(r.Nueva);
        Assert.True(r.Activa || r.Saldo == 4);
    }

    [Fact]
    public void Dos_rachas_con_una_pausa_entre_ellas_son_dos_tarjetas()
    {
        Lectura[] lecturas = [L(0, 1000), L(10, 1002), L(100, 1002), L(200, 1002), L(210, 1005), L(300, 1005)];
        var primera = new Tarjeta(Guid.NewGuid(), S(0), S(10), 2, null, PorCobrar: true);

        var r = Revisar(lecturas, 300, tarjetas: [primera]);

        var nueva = Assert.IsType<Nueva>(r.Nueva);
        Assert.Equal((3, S(200), S(210)), (nueva.Caras, nueva.DesdeUtc, nueva.HastaUtc));
    }

    [Fact]
    public void Lo_que_mando_una_PC_no_sale_como_tarjeta()
    {
        Lectura[] lecturas = [L(0, 1000), L(10, 1004), L(120, 1004)];
        var r = Revisar(lecturas, 125, registrados: [new Registrado(S(8), 4, 0)]);
        Assert.Null(r.Nueva);
        Assert.Empty(r.Encoger);
        Assert.Equal(0, r.Saldo);
    }

    [Fact]
    public void Lo_de_una_PC_recien_registrado_espera_a_que_la_impresora_lo_cuente()
    {
        // Se registró hace 20 s y la impresora todavía no lo cuenta: no se encoge la tarjeta del celular que sigue por cobrar.
        var celular = new Tarjeta(Guid.NewGuid(), S(0), S(10), 3, null, PorCobrar: true);
        var r = Revisar([L(0, 1000), L(10, 1003), L(200, 1003)], 220, registrados: [new Registrado(S(200), 6, 0)], tarjetas: [celular]);
        Assert.Empty(r.Encoger);
        Assert.True(r.Activa);
    }

    [Fact]
    public void Lo_de_una_PC_que_llega_tarde_encoge_la_tarjeta_y_si_era_todo_suyo_la_quita()
    {
        Lectura[] lecturas = [L(0, 1000), L(10, 1005), L(120, 1005)];
        var tarjeta = new Tarjeta(Guid.NewGuid(), S(0), S(10), 5, null, PorCobrar: true);

        var parte = Revisar(lecturas, 400, registrados: [new Registrado(S(9), 3, 0)], tarjetas: [tarjeta]);
        Assert.Equal(new Cambio(tarjeta.Id, 2, null), Assert.Single(parte.Encoger));
        Assert.Null(parte.Nueva);

        var todo = Revisar(lecturas, 400, registrados: [new Registrado(S(9), 5, 0)], tarjetas: [tarjeta]);
        Assert.Equal(new Cambio(tarjeta.Id, 0, null), Assert.Single(todo.Encoger));
    }

    [Fact]
    public void Si_la_tarjeta_ya_se_cobro_lo_que_llega_tarde_sobra_y_no_se_toca()
    {
        var cobrada = new Tarjeta(Guid.NewGuid(), S(0), S(10), 5, null, PorCobrar: false);
        var r = Revisar([L(0, 1000), L(10, 1005), L(120, 1005)], 400, registrados: [new Registrado(S(9), 5, 0)], tarjetas: [cobrada]);
        Assert.Empty(r.Encoger);
        Assert.Equal(5, r.Sobran);
    }

    [Fact]
    public void Las_copias_que_se_registran_se_toman_de_la_tarjeta_al_momento_y_primero_de_la_que_se_dice()
    {
        Lectura[] lecturas = [L(0, 1000), L(10, 1004), L(100, 1004), L(200, 1004), L(210, 1007), L(300, 1007)];
        var vieja = new Tarjeta(Guid.NewGuid(), S(0), S(10), 4, null, PorCobrar: true);
        var nueva = new Tarjeta(Guid.NewGuid(), S(200), S(210), 3, null, PorCobrar: true);
        Registrado[] copias = [new Registrado(S(305), 3, 0)];

        // Sin esperar la pausa: la más nueva primero.
        var r = Revisar(lecturas, 305, copias, [vieja, nueva], new Consumir(3, 0));
        Assert.Equal(new Cambio(nueva.Id, 0, null), Assert.Single(r.Encoger));

        // La que se dice («Ya se cobró como copias»), aunque sea la vieja.
        var ligada = Revisar(lecturas, 305, copias, [vieja, nueva], new Consumir(3, 0, vieja.Id));
        Assert.Equal(new Cambio(vieja.Id, 1, null), Assert.Single(ligada.Encoger));
    }

    [Fact]
    public void Las_copias_registradas_antes_de_su_tarjeta_no_quitan_caras_de_otra()
    {
        // Las copias salieron (1004 → 1007) y se registraron antes de que saliera su tarjeta: la del celular de antes se queda.
        Lectura[] lecturas = [L(0, 1000), L(10, 1004), L(100, 1004), L(200, 1004), L(210, 1007)];
        var celular = new Tarjeta(Guid.NewGuid(), S(0), S(10), 4, null, PorCobrar: true);
        var r = Revisar(lecturas, 215, [new Registrado(S(215), 3, 0)], [celular], new Consumir(3, 0));
        Assert.Empty(r.Encoger);
        // Y al cerrarse, no hay tarjeta nueva: ya estaban registradas.
        var despues = Revisar([.. lecturas, L(320, 1007)], 320, [new Registrado(S(215), 3, 0)], [celular]);
        Assert.Null(despues.Nueva);
        Assert.Empty(despues.Encoger);
    }

    [Fact]
    public void Si_contesta_otra_serie_se_cuenta_desde_ella_sin_inventar_caras()
    {
        Lectura[] lecturas = [L(0, 1000), L(10, 1002), L(100, 1002), L(200, 58000, serie: "OTRA"), L(210, 58003, serie: "OTRA"), L(320, 58003, serie: "OTRA")];
        var anterior = new Tarjeta(Guid.NewGuid(), S(0), S(10), 2, null, PorCobrar: true);

        var r = Revisar(lecturas, 320, tarjetas: [anterior]);

        Assert.Equal("contesta otra serie", r.Corte);
        Assert.Equal(3, Assert.IsType<Nueva>(r.Nueva).Caras);
        Assert.Equal(S(200), r.BaseUtc);
    }

    [Fact]
    public void Una_lectura_sin_serie_no_corta_pero_la_otra_si()
    {
        // El ST2 no contestó una vez (sin serie): sigue siendo la misma.
        var r = Revisar([L(0, 1000), L(10, 1002, serie: null), L(20, 1003), L(150, 1003)], 150);
        Assert.Null(r.Corte);
        Assert.Equal(3, Assert.IsType<Nueva>(r.Nueva).Caras);
    }

    [Fact]
    public void Si_el_contador_baja_o_salta_mas_de_lo_que_se_puede_imprimir_se_vuelve_a_contar()
    {
        var reinicio = Revisar([L(0, 1000), L(10, 1004), L(100, 1004), L(200, 12), L(210, 14), L(320, 14)], 320);
        Assert.StartsWith("el contador bajó", reinicio.Corte);
        Assert.Equal(2, Assert.IsType<Nueva>(reinicio.Nueva).Caras);

        // 8,000 caras en un minuto no las saca ninguna: es otro contador (por ejemplo, otra impresora que no dice su serie).
        var salto = Revisar([L(0, 1000, serie: null), L(60, 9000, serie: null), L(70, 9001, serie: null), L(200, 9001, serie: null)], 200);
        Assert.StartsWith("el contador saltó", salto.Corte);
        Assert.Equal(1, Assert.IsType<Nueva>(salto.Nueva).Caras);
    }

    [Fact]
    public void Lo_de_antes_de_contar_no_cuenta()
    {
        // Se pusieron a los 100 s: lo de antes (1000 → 1050) no es tarjeta.
        var r = Revisar([L(0, 1000), L(50, 1050), L(100, 1050), L(110, 1052), L(220, 1052)], 220, desde: 100);
        Assert.Equal(2, Assert.IsType<Nueva>(r.Nueva).Caras);
    }

    [Fact]
    public void El_color_sale_del_ST2_menos_lo_registrado_a_color()
    {
        Lectura[] lecturas = [L(0, 1000, 400), L(10, 1010, 404), L(120, 1010, 404)];
        // Una PC mandó 4 caras, 1 a color: las 6 de más son 3 a color y 3 en B/N.
        var r = Revisar(lecturas, 125, registrados: [new Registrado(S(5), 4, 1)]);
        var nueva = Assert.IsType<Nueva>(r.Nueva);
        Assert.Equal((6, (int?)3), (nueva.Caras, nueva.Color));

        // Sin el ST2 no se sabe el color.
        var sinColor = Revisar([L(0, 1000), L(10, 1006), L(120, 1006)], 125);
        Assert.Null(Assert.IsType<Nueva>(sinColor.Nueva).Color);
    }

    [Fact]
    public void Al_encoger_quita_primero_las_del_color_que_llego()
    {
        var tarjeta = new Tarjeta(Guid.NewGuid(), S(0), S(10), 6, 2, PorCobrar: true);
        // Llegó tarde una de 3 caras, 1 a color: quedan 3, 1 a color.
        var r = Revisar([L(0, 1000, 400), L(10, 1006, 402), L(120, 1006, 402)], 400, registrados: [new Registrado(S(9), 3, 1)], tarjetas: [tarjeta]);
        Assert.Equal(new Cambio(tarjeta.Id, 3, 1), Assert.Single(r.Encoger));
    }
}
