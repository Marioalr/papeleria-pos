using System.Text.Json;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Impresion;

/// <summary>
/// El ticket limpio y configurable (dueño, 2026-11-21): sin IVA ni «Mayoreo desde …», los nombres con mayúsculas (solo lo impreso)
/// y cada ajuste de Configuración › Ticket › cómo sale, en el texto y en los comandos ESC/POS.
/// </summary>
public class TicketLimpioYConfigurableTests
{
    [Theory]
    [InlineData("CUADERNO PROF 100H", "Cuaderno Prof 100h")]
    [InlineData("CUADERNO PROFESIONAL RAYA 100 HOJAS", "Cuaderno Profesional Raya 100 hojas")]
    [InlineData("cuaderno de dibujo y pintura", "Cuaderno de Dibujo y Pintura")]
    [InlineData("DE COLORES PLUMONES", "De Colores Plumones")]
    [InlineData("PEGAMENTO EN BARRA 21 G", "Pegamento en Barra 21 g")]
    [InlineData("Pegamento en barra 21g", "Pegamento en Barra 21g")]
    [InlineData("LÁPIZ NO. 2 MIRADO", "Lápiz No. 2 Mirado")]
    [InlineData("LAPIZ 2B STAEDTLER", "Lapiz 2B Staedtler")]
    [InlineData("LÁPIZ HB", "Lápiz HB")]
    [InlineData("HOJAS BLANCAS A4 500 PZAS", "Hojas Blancas A4 500 pzas")]
    [InlineData("MEMORIA USB 32GB", "Memoria USB 32GB")]
    [InlineData("ÑANDÚ ACUARELAS ÁRBOL", "Ñandú Acuarelas Árbol")]
    [InlineData("CARTULINA 50 X 70 CM", "Cartulina 50 x 70 cm")]
    [InlineData("Nylon C10 x metro", "Nylon C10 x Metro")]
    [InlineData("FOLDER CARTA (CAJA DE 100)", "Folder Carta (Caja de 100)")]
    [InlineData("PLUMA BLANCO/NEGRO", "Pluma Blanco/Negro")]
    [InlineData("Funda para iPhone", "Funda para iPhone")]
    [InlineData("PLUMA BiC CRISTAL", "Pluma BiC Cristal")]
    [InlineData("pluma  azul", "Pluma  Azul")]
    [InlineData("Copia B/N", "Copia B/N")]
    [InlineData("TALLA XL", "Talla XL")]
    [InlineData("CURP impresa", "CURP Impresa")]
    [InlineData("ACTA DE NACIMIENTO CURP", "Acta de Nacimiento CURP")]
    [InlineData("Impresión PDF a color", "Impresión PDF a Color")]
    [InlineData("", "")]
    public void Los_nombres_salen_con_mayuscula_en_cada_palabra(string guardado, string impreso) =>
        Assert.Equal(impreso, NombreEnElTicket.ConMayusculas(guardado));

    private static TicketDto Ticket(FormatoTicket? formato = null) => new(
        "A-000123", new DateTime(2026, 11, 21, 12, 47, 0), "Caja 1", "Ana",
        [
            new LineaTicketDto("12", "PLUMA BIC AZUL", 800, 9600, AhorroMayoreoCentavos: 2400, Mayoreo: "Mayoreo desde 12"),
            new LineaTicketDto("3", "CUADERNO PROF 100H", 4500, 9000, AhorroPromocionCentavos: 4500, Promocion: "3×2 en cuadernos"),
            new LineaTicketDto("1", "Goma", 500, 450, 50, "10 %"),
        ],
        16, 19050, 2627, [new CobroTicketDto(FormaPago.Efectivo, 19050, 20000, 950, null)],
        ["PAPELERÍA DE PRUEBA", "Calle 1"], ["Gracias por su compra"], Formato: formato);

    private static string[] Renglones(TicketDto t) => TicketTexto.Generar(t).Split(Environment.NewLine);

    [Fact]
    public void No_sale_el_iva_ni_desde_cuantas_es_el_mayoreo_y_si_lo_que_necesita_el_cliente()
    {
        var texto = TicketTexto.Generar(Ticket());

        Assert.DoesNotContain("IVA", texto);
        Assert.DoesNotContain("desde", texto, StringComparison.OrdinalIgnoreCase);
        foreach (var lo in new[]
        {
            "PAPELERÍA DE PRUEBA", "Folio A-000123", "21/11/2026", "Atendió: Ana", "Pluma Bic Azul", "$120.00", "Ahorro por mayoreo",
            "Promoción 3×2 en cuadernos", "Descuento 10 %", "Usted ahorró: $69.50", "Total: $190.50", "Efectivo: $200.00  Cambio: $9.50",
            "Gracias por su compra",
        })
            Assert.Contains(lo, texto);
    }

    [Fact]
    public void Por_omision_sale_como_antes_salvo_los_nombres()
    {
        var ticket = Ticket();
        var bytes = TicketEscPos.Generar(ticket, abrirCajon: false);

        // Sin el estilo: ni ESC M (la letra) ni ESC 3 (el interlineado); a la impresora le llega lo de siempre.
        Assert.DoesNotContain(DisenoTicket.Armar(ticket), e => e is Estilo);
        Assert.False(Contiene(bytes, [0x1B, (byte)'M']));
        Assert.False(Contiene(bytes, [0x1B, (byte)'3']));
        Assert.Equal(TicketEscPos.Generar(ticket with { Formato = FormatoTicket.Predeterminado }, false), bytes);
        Assert.All(Renglones(ticket), r => Assert.True(r.Length <= 48, $"«{r}» mide {r.Length}"));
        // El total, grande y en negritas, como siempre.
        Assert.Contains(DisenoTicket.Armar(ticket), e => e is Renglon { Texto: "Total: $190.50", Negrita: true, Grande: true });
    }

    [Fact]
    public void Sin_nombres_con_mayusculas_sale_el_nombre_como_esta_guardado()
    {
        var texto = TicketTexto.Generar(Ticket(new FormatoTicket(NombresConMayusculas: false)));

        Assert.Contains("PLUMA BIC AZUL", texto);
        Assert.Contains("CUADERNO PROF 100H", texto);
    }

    [Fact]
    public void Con_la_letra_b_caben_64_caracteres_y_la_impresora_la_cambia()
    {
        var largo = Ticket(new FormatoTicket(Letra: LetraTicket.B)) with
        {
            Lineas = [new LineaTicketDto("1", "Listón de raso 2.5 cm rojo con orilla dorada para moños", 800, 800)],
        };

        var renglones = Renglones(largo);
        var bytes = TicketEscPos.Generar(largo, abrirCajon: false);

        Assert.Contains(renglones, r => r.Length == 64 && r.StartsWith(new string('-', 64)));
        Assert.Contains(renglones, r => r.Contains("Listón de Raso 2.5 cm Rojo con Orilla Dorada") && r.EndsWith("$8.00"));
        Assert.All(renglones, r => Assert.True(r.Length <= 64, $"«{r}» mide {r.Length}"));
        Assert.True(Contiene(bytes, [0x1B, (byte)'M', 1]));
        // Un renglón de 64 no se recorta a 48 al mandarlo.
        Assert.True(Contiene(bytes, TicketEscPos.Imprimible(new string('-', 64))));
    }

    [Fact]
    public void Con_la_letra_alta_todo_sale_al_doble_de_alto()
    {
        var bytes = TicketEscPos.Generar(Ticket(new FormatoTicket(Tamano: TamanoLetraTicket.Alta)), abrirCajon: false);

        // GS ! 1 en cada renglón; nunca el normal (GS ! 0) antes del cierre.
        var cierre = bytes.Length - 16;
        Assert.False(Contiene(bytes[..cierre], [0x1D, (byte)'!', 0]));
        Assert.True(Contiene(bytes, [0x1D, (byte)'!', 1]));
        Assert.All(Renglones(Ticket(new FormatoTicket(Tamano: TamanoLetraTicket.Alta))), r => Assert.True(r.Length <= 48));
    }

    [Theory]
    [InlineData(InterlineadoTicket.Junto, false, false, 28)]
    [InlineData(InterlineadoTicket.Separado, false, false, 48)]
    [InlineData(InterlineadoTicket.Junto, true, false, 21)]
    [InlineData(InterlineadoTicket.Separado, false, true, 72)]
    public void El_interlineado_va_en_puntos_segun_la_letra(InterlineadoTicket interlineado, bool letraB, bool alta, int puntos)
    {
        var formato = new FormatoTicket(Interlineado: interlineado, Letra: letraB ? LetraTicket.B : LetraTicket.A,
            Tamano: alta ? TamanoLetraTicket.Alta : TamanoLetraTicket.Normal);

        Assert.True(Contiene(TicketEscPos.Generar(Ticket(formato), abrirCajon: false), [0x1B, (byte)'3', (byte)puntos]));
    }

    [Fact]
    public void Con_los_renglones_normales_y_otra_letra_vuelve_al_interlineado_de_la_impresora()
    {
        var bytes = TicketEscPos.Generar(Ticket(new FormatoTicket(Letra: LetraTicket.B)), abrirCajon: false);

        Assert.True(Contiene(bytes, [0x1B, (byte)'2']));
        Assert.False(Contiene(bytes, [0x1B, (byte)'3']));
    }

    [Fact]
    public void Sin_el_total_resaltado_sale_como_los_demas_renglones()
    {
        var documento = DisenoTicket.Armar(Ticket(new FormatoTicket(TotalResaltado: false)));
        var redondeado = DisenoTicket.Armar(Ticket(new FormatoTicket(TotalResaltado: false)) with { RedondeoCentavos = 50 });

        Assert.Contains(documento, e => e is Renglon { Texto: "Total: $190.50", Negrita: false, Grande: false });
        Assert.Contains(redondeado, e => e is Renglon { Texto: "Total a pagar: $191.00", Negrita: false, Grande: false });
    }

    [Fact]
    public void Sin_quien_atendio_sale_solo_la_caja()
    {
        var renglones = Renglones(Ticket(new FormatoTicket(MostrarCajero: false)));

        Assert.DoesNotContain(renglones, r => r.Contains("Atendió"));
        Assert.Contains("Caja 1", renglones);
    }

    [Fact]
    public void Sin_lo_que_se_ahorro_cada_renglon_dice_lo_que_se_cobro()
    {
        var renglones = Renglones(Ticket(new FormatoTicket(MostrarAhorro: false)));

        Assert.Contains(renglones, r => r.Contains("Pluma Bic Azul") && r.EndsWith("$96.00"));
        Assert.Contains(renglones, r => r.Contains("Cuaderno Prof 100h") && r.EndsWith("$90.00"));
        Assert.Contains(renglones, r => r.Contains("Goma") && r.EndsWith("$4.50"));
        Assert.DoesNotContain(renglones, r => r.Contains("Ahorro") || r.Contains("ahorró") || r.Contains("Promoción") || r.Contains("Descuento"));
        Assert.Contains(renglones, r => r.EndsWith("Total: $190.50"));
    }

    [Fact]
    public void La_vista_previa_de_la_letra_b_tambien_alinea_a_64()
    {
        var renglones = Renglones(Ticket(new FormatoTicket(Letra: LetraTicket.B)));

        Assert.Contains(renglones, r => r == "Total: $190.50".PadLeft(64));
    }

    /// <summary>Lo que guarda la base de una sucursal de antes («{}») y lo que manda una caja de antes (sin «formato»): como siempre.</summary>
    [Fact]
    public void Un_formato_sin_nada_es_el_de_siempre_y_uno_que_no_existe_no_se_guarda()
    {
        Assert.Equal(FormatoTicket.Predeterminado, JsonSerializer.Deserialize<FormatoTicket>("{}", JsonSerializerOptions.Web));
        Assert.Equal(new FormatoTicket(Letra: LetraTicket.B),
            JsonSerializer.Deserialize<FormatoTicket>("""{"letra":1}""", JsonSerializerOptions.Web));
        Assert.True(FormatoTicket.Predeterminado.NombresConMayusculas);
        Assert.Throws<ArgumentException>(() => new FormatoTicket(Letra: (LetraTicket)7).Validado());
        var configuracion = new ConfiguracionTicket();
        Assert.Throws<ArgumentException>(() => configuracion.CambiarFormato(new FormatoTicket(Interlineado: (InterlineadoTicket)9)));
        Assert.Equal(FormatoTicket.Predeterminado, configuracion.Formato);
    }

    [Fact]
    public void La_bitacora_dice_solo_lo_que_cambio()
    {
        var antes = FormatoTicket.Predeterminado;

        Assert.Empty(antes.Cambios(antes));
        Assert.Equal(["la letra: B", "quién atendió: no"], (antes with { Letra = LetraTicket.B, MostrarCajero = false }).Cambios(antes));
    }

    private static bool Contiene(byte[] bytes, byte[] buscado)
    {
        for (var i = 0; i + buscado.Length <= bytes.Length; i++)
            if (bytes.AsSpan(i, buscado.Length).SequenceEqual(buscado))
                return true;
        return false;
    }
}
