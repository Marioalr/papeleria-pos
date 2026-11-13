using System.Text;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// <c>mudanza-ver</c>: lo que la página «La base de la tienda» enseña de un paquete de mudanza antes de traerlo (docs/instalacion.md).
/// Dice cómo está («paquete») sin leer la base entera ni tocar la red, y siempre sale con 0: el asistente decide por «paquete».
/// </summary>
public sealed class MudanzaVerTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();

    public void Dispose() => _carpeta.Dispose();

    private string Usb => Path.Combine(_carpeta.Ruta, "usb", "mudanza");

    private string Salida => Path.Combine(_carpeta.Ruta, "salida.txt");

    private PcDePrueba Pc => new(_carpeta.Ruta);

    /// <summary>Como lo corre el asistente; la red de la prueba no tiene a nadie: si se le pregunta algo, se ve.</summary>
    private async Task<ArchivoDeClaves> VerAsync(string carpeta, string? version = "1.0.180", PcDePrueba? pc = null)
    {
        var red = RedDePrueba.SinNadie();
        string[] argumentos = version is null
            ? ["mudanza-ver", "--paquete", carpeta, "--salida", Salida]
            : ["mudanza-ver", "--paquete", carpeta, "--version", version, "--salida", Salida];

        Assert.Equal(0, await Ayudante.CorrerAsync(argumentos, pc ?? Pc, red));

        Assert.Empty(red.Preguntadas);
        return ArchivoDeClaves.Leer(Salida);
    }

    [Fact]
    public async Task Un_paquete_completo_esta_bien_y_dice_lo_que_trae()
    {
        var paquete = PaqueteDePrueba.En(Usb);

        var visto = await VerAsync(Usb);

        Assert.Equal("bien", visto["paquete"]);
        Assert.Equal("", visto["error"]);
        // «paquete» es cómo está, una sola vez: el identificador del paquete no lo pisa (va como «paquete-id»).
        Assert.Single(visto.Datos, d => d.Clave == "paquete");
        Assert.Equal(PaqueteDePrueba.Id, visto["paquete-id"]);
        // Lo demás de mudanza.txt, repetido tal cual.
        foreach (var (clave, valor) in paquete.Claves.Where(c => c.Clave != "paquete"))
            Assert.Equal(valor, visto[clave]);
        Assert.Equal("La base de CAJA2, sacada el 2026-11-14 08:15. Tienda: Matriz. 4512 productos y 1280 ventas (la última, el 2026-11-13 20:41). "
            + "Caja: Caja 2 (serie B). El puente llega ESCRIBIENDO en " + PaqueteDePrueba.BaseDeEleventa
            + ": lo que cobre «Caja 2» (serie B) entra a eleventa como la caja 3.", visto["texto"]);
        // El eleventa y el Control Print de allá no son esta PC: se propone lo mismo.
        Assert.Equal("192.0.2.106", visto["propone-eleventa-servidor"]);
        Assert.Equal("192.0.2.106", visto["propone-controlprint-servidor"]);
    }

    [Fact]
    public async Task Sin_la_carpeta_o_sin_su_mudanza_txt_no_hay_paquete()
    {
        var sinCarpeta = await VerAsync(Usb);
        Assert.Equal("no-hay", sinCarpeta["paquete"]);
        Assert.Contains("No está la carpeta", sinCarpeta["error"]);
        // Solo «paquete» y «error»: nada de un paquete que no hay.
        Assert.Equal(["paquete", "error"], sinCarpeta.Datos.Select(d => d.Clave));

        // La base sola no es un paquete: mudanza.txt se escribe al final, y sin él quedó a medias.
        var paquete = PaqueteDePrueba.En(Usb);
        File.Delete(paquete.RutaDelResumen);
        var sinResumen = await VerAsync(Usb);
        Assert.Equal("no-hay", sinResumen["paquete"]);
        Assert.Contains("no hay un paquete de mudanza", sinResumen["error"]);
        Assert.EndsWith("corre ahí este instalador y elige «Mudar la base de esta PC a otra PC».", sinResumen["error"]);
    }

    [Fact]
    public async Task Sin_la_base_o_con_otro_tamano_esta_incompleto()
    {
        var paquete = PaqueteDePrueba.En(Usb);
        File.WriteAllBytes(paquete.RutaDeLaBase, new byte[4999]);

        var corta = await VerAsync(Usb);

        Assert.Equal("incompleto", corta["paquete"]);
        Assert.Contains("mide 4999 bytes y debería medir 5000", corta["error"]);
        // Allá sigue la marca: para sacarlo otra vez primero se cancela, y se saca en otra corrida.
        Assert.EndsWith("Para sacarlo otra vez: en CAJA2, corre este instalador y elige «Cancelar la mudanza»; después córrelo otra vez y elige "
            + "«Mudar la base de esta PC a otra PC».", corta["error"]);

        File.Delete(paquete.RutaDeLaBase);
        var sinBase = await VerAsync(Usb);
        Assert.Equal("incompleto", sinBase["paquete"]);
        Assert.Contains($"le falta la base ({PaqueteDePrueba.Archivo})", sinBase["error"]);
    }

    /// <summary>
    /// Lo que de mudanza.txt llega a una ruta, a una línea de órdenes o a servicio.json se revisa al leerlo: si no es lo que escribe quien
    /// saca el paquete, no se adivina ni se usa a medias.
    /// </summary>
    [Theory]
    [InlineData("formato", "2", "formato que este instalador no conoce")]
    [InlineData("paquete", "el-de-hoy", "«paquete» no es el identificador")]
    [InlineData("archivo", @"..\..\Windows\win.ini", "«archivo» no es el nombre de la base")]
    [InlineData("archivo", @"C:\ProgramData\PapeleriaPos\pos.db", "«archivo» no es el nombre de la base")]
    [InlineData("archivo", "pos-2026-11-14-mudanza-0815.db.exe", "«archivo» no es el nombre de la base")]
    [InlineData("bytes", "muchos", "«bytes» no es el tamaño")]
    [InlineData("bytes", "0", "«bytes» no es el tamaño")]
    [InlineData("sha256", "abc123", "«sha256» no es un SHA-256")]
    [InlineData("equipo", "CAJA2\" -Carpeta \"C:\\", "«equipo» no es el nombre de una PC")]
    [InlineData("equipo", "CAJA 2; Remove-Item", "«equipo» no es el nombre de una PC")]
    [InlineData("version", "la última", "«version» no es una versión")]
    [InlineData("tienda", "", "le falta «tienda»")]
    [InlineData("puerto", "70000", "«puerto» no es un número que se pueda usar")]
    [InlineData("eleventa", "", "«eleventa» va como si o no")]
    [InlineData("eleventa-escribir", "tal vez", "«eleventa-escribir» va como si o no")]
    [InlineData("eleventa-puerto", "el de siempre", "«eleventa-puerto» no es un número")]
    [InlineData("eleventa-cajero", "uno", "«eleventa-cajero» no es un número")]
    [InlineData("eleventa-pendientes", "-1", "«eleventa-pendientes» no es un número")]
    [InlineData("eleventa-cajas", "B=tres", "«eleventa-cajas» no se entiende (en «B=tres»)")]
    [InlineData("eleventa-cajas", "B=0", "«eleventa-cajas» no se entiende")]
    [InlineData("eleventa-cajas", "B=3/x", "el cajero va con cifras")]
    [InlineData("eleventa-cajas", "B:Extra=3", "«eleventa-cajas» no se entiende")]
    [InlineData("eleventa-cajas", "B=3;b=4", "trae dos veces la serie b, con números distintos")]
    [InlineData("eleventa-ajustes", @"Gbak=C:\de-alla\gbak.exe", "«Gbak», que no es un ajuste del puente que viaje")]
    [InlineData("eleventa-ajustes", "Minutos=10;Clave=masterkey", "«Clave», que no es un ajuste del puente que viaje")]
    [InlineData("eleventa-ajustes", "Minutos=pocos", "«Minutos» con un valor que no se entiende")]
    [InlineData("eleventa-ajustes", "RespaldoObligatorio=quizá", "«RespaldoObligatorio» con un valor que no se entiende")]
    [InlineData("eleventa-ajustes", "SegundosAlCobrar=1e9", "«SegundosAlCobrar» con un valor que no se entiende")]
    public async Task Un_mudanza_txt_que_no_se_entiende_esta_incompleto_y_dice_que_le_pasa(string clave, string valor, string dice)
    {
        PaqueteDePrueba.En(Usb, (clave, valor));

        var visto = await VerAsync(Usb);

        Assert.Equal("incompleto", visto["paquete"]);
        Assert.Contains(dice, visto["error"]);
        Assert.StartsWith($"El paquete de mudanza de {Usb} no se entiende (mudanza.txt: ", visto["error"]);
        Assert.EndsWith("Sácalo otra vez en la PC de donde viene.", visto["error"]);
        // Nunca el valor de algo con nombre de clave.
        Assert.DoesNotContain("masterkey", visto["error"]);
        Assert.Equal(["paquete", "error"], visto.Datos.Select(d => d.Clave));
    }

    [Fact]
    public async Task Sin_una_clave_que_siempre_va_esta_incompleto()
    {
        PaqueteDePrueba.En(Usb, ("sha256", null));

        var visto = await VerAsync(Usb);

        Assert.Equal("incompleto", visto["paquete"]);
        Assert.Equal($"El paquete de mudanza de {Usb} no se entiende (mudanza.txt: le falta «sha256»). Sácalo otra vez en la PC de donde viene.", visto["error"]);
    }

    /// <summary>Lo que puede venir vacío o de otra forma sin que el paquete deje de servir: no se rechaza una mudanza por eso.</summary>
    [Theory]
    [InlineData("hecho-local", "")]
    [InlineData("ips", "")]
    [InlineData("puerto", "")]
    [InlineData("productos", "")]
    [InlineData("ultima-venta", "")]
    [InlineData("cajas", "0")]
    [InlineData("sha256", "0F8FAD5BD9CB469FA16570867728950E0F8FAD5BD9CB469FA16570867728950E")]
    [InlineData("eleventa", "true")]
    [InlineData("eleventa-escribir", "False")]
    [InlineData("eleventa-puerto", "")]
    [InlineData("eleventa-usuario", "")]
    [InlineData("eleventa-cajero", "")]
    [InlineData("eleventa-cajero", "0")]
    [InlineData("eleventa-cajas", "")]
    [InlineData("eleventa-cajas", " B = 3 / 1 ; A=1;")]
    [InlineData("eleventa-cajas", "B=3/0")]
    [InlineData("eleventa-cajas", "B=3;b=3")]
    [InlineData("eleventa-ajustes", "Minutos=10; ProductoCopias=COPIA;RespaldoObligatorio=False;SegundosAlCobrar=2.5;SegundosEntreEnvios=45;SegundosMaximos=90;DiasDeRespaldos=7;ProductoImpresiones=IMP2")]
    [InlineData("eleventa-pendientes", "")]
    [InlineData("controlprint", "")]
    [InlineData("avisos", "0")]
    public async Task Lo_que_puede_venir_vacio_o_de_otra_forma_no_estorba(string clave, string valor)
    {
        PaqueteDePrueba.En(Usb, (clave, valor));

        var visto = await VerAsync(Usb);

        Assert.True(visto["paquete"] == "bien", visto["error"]);
        // Repetida tal cual, como viene.
        Assert.Equal(valor, visto[clave]);
    }

    [Fact]
    public async Task Con_el_traida_txt_de_este_paquete_y_sin_la_base_ya_se_trajo()
    {
        var paquete = PaqueteDePrueba.En(Usb);
        paquete.DejarTraida();

        var visto = await VerAsync(Usb);

        Assert.Equal("ya-traido", visto["paquete"]);
        Assert.StartsWith("Este paquete ya se trajo a PRINCIPAL (2026-11-14 08:40)", visto["error"]);
        Assert.Contains("terminar la mudanza en CAJA2", visto["error"]);
    }

    /// <summary>Un traida.txt que quedó de otra mudanza no dice nada de este paquete: sin su base, está incompleto.</summary>
    [Fact]
    public async Task El_traida_txt_de_otro_paquete_no_cuenta()
    {
        var paquete = PaqueteDePrueba.En(Usb);
        paquete.DejarTraida(paquete: "11111111111111111111111111111111");

        Assert.Equal("incompleto", (await VerAsync(Usb))["paquete"]);
    }

    /// <summary>Con su base todavía ahí (no se pudo borrar, o se puso otra vez), el paquete sigue completo: lo demás lo decide mudanza-revisar.</summary>
    [Fact]
    public async Task Con_traida_txt_y_la_base_todavia_ahi_esta_bien()
    {
        var paquete = PaqueteDePrueba.En(Usb);
        paquete.DejarTraida(borrarLaBase: false);

        Assert.Equal("bien", (await VerAsync(Usb))["paquete"]);
    }

    /// <summary>Solo se rechaza el que sacó un programa MÁS NUEVO que este instalador: su base traería cosas que el de aquí no conoce.</summary>
    [Theory]
    [InlineData("1.0.181", "1.0.180", "otra-version")]
    [InlineData("1.1", "1.0.999", "otra-version")]
    [InlineData("1.0.5", "0.0.0-local", "otra-version")]
    [InlineData("1.0.180", "1.0.180", "bien")]
    [InlineData("1.0.180", "1.0.180.0", "bien")]
    [InlineData("1.0.99", "1.0.180", "bien")]
    [InlineData("0.0.0-local", "1.0.5", "bien")]
    [InlineData("1.0.181", null, "bien")]
    public async Task El_de_una_version_mas_nueva_que_el_instalador_no_se_trae(string delPaquete, string? delInstalador, string queda)
    {
        PaqueteDePrueba.En(Usb, ("version", delPaquete));

        var visto = await VerAsync(Usb, delInstalador);

        Assert.Equal(queda, visto["paquete"]);
        if (queda == "otra-version")
            Assert.StartsWith($"Este paquete lo sacó la versión {delPaquete} del programa, más nueva que la de este instalador ({delInstalador})", visto["error"]);
    }

    /// <summary>
    /// El de un programa más nuevo puede venir escrito de otra forma: lo que se dice es que es más nuevo, no que «no se entiende».
    /// </summary>
    [Fact]
    public async Task El_de_otro_formato_y_mas_nuevo_dice_que_es_de_otra_version()
    {
        PaqueteDePrueba.En(Usb, ("formato", "2"), ("version", "2.0.1"), ("archivo", "base.sqlite"));

        Assert.Equal("otra-version", (await VerAsync(Usb))["paquete"]);
    }

    /// <summary>Es para una página del asistente: no lee la base (puede pesar cientos de megas en una USB). El SHA-256 lo revisa mudanza-revisar.</summary>
    [Fact]
    public async Task No_lee_la_base_solo_la_mide()
    {
        var paquete = PaqueteDePrueba.En(Usb);
        File.WriteAllBytes(paquete.RutaDeLaBase, new byte[5000]);

        Assert.Equal("bien", (await VerAsync(Usb))["paquete"]);
    }

    /// <summary>
    /// En la tienda, el eleventa y el Control Print de la caja 2 estaban en la principal, que es a donde se trae la base: aquí son esta
    /// misma PC.
    /// </summary>
    [Theory]
    [InlineData("192.0.2.50", "192.0.2.50", "127.0.0.1", "127.0.0.1")]
    [InlineData("principal", "PRINCIPAL:8443", "127.0.0.1", "127.0.0.1:8443")]
    [InlineData("10.99.0.1", "192.0.2.50:8443", "127.0.0.1", "127.0.0.1:8443")]
    [InlineData("192.0.2.106", "OTRA-PC", "192.0.2.106", "OTRA-PC")]
    // El de allá, dicho con el nombre o la IP de aquella PC: desde aquí es el mismo.
    [InlineData("CAJA2", "192.0.2.77:8443", "CAJA2", "192.0.2.77:8443")]
    [InlineData("", "", "", "")]
    public async Task Propone_127_0_0_1_si_el_servidor_de_alla_era_esta_pc(string eleventa, string controlPrint, string proponeEleventa, string proponeControlPrint)
    {
        PaqueteDePrueba.En(Usb, ("eleventa-servidor", eleventa), ("controlprint-servidor", controlPrint));
        var pc = Pc;
        pc.IpsDeOtrasTarjetas.Add("10.99.0.1");

        var visto = await VerAsync(Usb, pc: pc);

        Assert.Equal("bien", visto["paquete"]);
        Assert.Equal(proponeEleventa, visto["propone-eleventa-servidor"]);
        Assert.Equal(proponeControlPrint, visto["propone-controlprint-servidor"]);
    }

    /// <summary>
    /// Una mudanza de regreso (de la principal a la caja 2): allá el eleventa y el Control Print estaban en esa misma PC (127.0.0.1), y
    /// desde esta PC ese servidor es la IP de aquella, no «esta PC». Salvo que el paquete se haya sacado aquí mismo.
    /// </summary>
    [Theory]
    [InlineData("192.0.2.77", "192.0.2.77", "192.0.2.77:8443")]
    // De varias IP de aquella PC, la de la misma red que esta (192.0.2.x).
    [InlineData("10.20.0.7;192.0.2.77", "192.0.2.77", "192.0.2.77:8443")]
    // Si ninguna es de la misma red, la primera.
    [InlineData("10.20.0.7;10.30.0.7", "10.20.0.7", "10.20.0.7:8443")]
    // Si las IP del paquete son las de esta PC (se sacó aquí mismo), o no dice ninguna: se queda como estaba.
    [InlineData("192.0.2.50", "127.0.0.1", "localhost:8443")]
    [InlineData("", "127.0.0.1", "localhost:8443")]
    public async Task Si_alla_el_servidor_era_la_propia_pc_de_alla_propone_la_ip_de_aquella(string ips, string proponeEleventa, string proponeControlPrint)
    {
        PaqueteDePrueba.En(Usb, ("ips", ips), ("eleventa-servidor", "127.0.0.1"), ("controlprint-servidor", "localhost:8443"));

        var visto = await VerAsync(Usb);

        Assert.Equal("bien", visto["paquete"]);
        Assert.Equal(proponeEleventa, visto["propone-eleventa-servidor"]);
        Assert.Equal(proponeControlPrint, visto["propone-controlprint-servidor"]);
    }

    [Theory]
    [InlineData("no", "no", "Allá no había puente con eleventa.")]
    [InlineData("si", "no", "El puente llega SIN escribir: lo que se cobre en el POS no llega a eleventa hasta que tú lo prendas.")]
    public async Task El_texto_dice_como_llega_el_puente(string eleventa, string escribir, string dice)
    {
        PaqueteDePrueba.En(Usb, ("eleventa", eleventa), ("eleventa-escribir", escribir));

        Assert.EndsWith(dice, (await VerAsync(Usb))["texto"]);
    }

    /// <summary>Una tienda recién empezada: una venta, un producto, y ninguna caja con su caja de eleventa todavía.</summary>
    [Fact]
    public async Task El_texto_cuenta_bien_una_sola_venta_y_dice_si_ninguna_caja_tiene_su_caja_de_eleventa()
    {
        PaqueteDePrueba.En(Usb, ("productos", "1"), ("ventas", "1"), ("ultima-venta", ""), ("cajas", "0"), ("eleventa-cajas", ""));

        var texto = (await VerAsync(Usb))["texto"];

        Assert.Equal("La base de CAJA2, sacada el 2026-11-14 08:15. Tienda: Matriz. 1 producto y 1 venta. Sin cajas. El puente llega ESCRIBIENDO en "
            + PaqueteDePrueba.BaseDeEleventa + ": ninguna caja tiene todavía su caja de eleventa (lo que se cobre espera en la bandeja).", texto);
    }

    /// <summary>Lo que no es de un mudanza.txt no se repite: ni puede pisar lo que dice el ayudante, ni pasa algo con nombre de clave.</summary>
    [Fact]
    public async Task Solo_repite_lo_que_trae_un_mudanza_txt()
    {
        PaqueteDePrueba.En(Usb, ("texto", "otro texto"), ("error", "ninguno"), ("propone-eleventa-servidor", "203.0.113.9"), ("eleventa-clave", "masterkey"),
            ("eleventa-gbak", @"C:\de-alla\gbak.exe"), ("caja.2.nombre", "No está en la cuenta"));

        var visto = await VerAsync(Usb);

        Assert.Equal("bien", visto["paquete"]);
        Assert.Equal("", visto["error"]);
        Assert.StartsWith("La base de CAJA2", visto["texto"]);
        Assert.Equal("192.0.2.106", visto["propone-eleventa-servidor"]);
        Assert.DoesNotContain(visto.Datos, d => d.Clave is "eleventa-clave" or "eleventa-gbak" or "caja.2.nombre");
        Assert.DoesNotContain("masterkey", File.ReadAllText(Salida));
        Assert.Equal(visto.Datos.Count, visto.Datos.Select(d => d.Clave).Distinct().Count());
    }

    [Fact]
    public async Task Se_lee_con_bom_con_comentarios_y_con_los_saltos_de_linux()
    {
        var paquete = PaqueteDePrueba.En(Usb);
        File.WriteAllText(paquete.RutaDelResumen, "# El paquete de mudanza de CAJA2\n" + string.Concat(paquete.Claves.Select(c => $"{c.Clave}={c.Valor}\n")),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var visto = await VerAsync(Usb);

        Assert.Equal("bien", visto["paquete"]);
        Assert.Equal("1", visto["formato"]);
    }

    /// <summary>La carpeta como llega de una línea de órdenes de Windows: «"E:\mudanza\"» deja una comilla al final.</summary>
    [Fact]
    public async Task La_carpeta_con_una_comilla_de_mas_es_la_misma()
    {
        PaqueteDePrueba.En(Usb);

        Assert.Equal("bien", (await VerAsync(Usb + "\""))["paquete"]);
    }

    /// <summary>
    /// El ayudante nunca truena: si ni su salida se deja escribir (aquí, su carpeta es un archivo), lo dice en la pantalla y sale con 2,
    /// sin una traza. Quien lo corrió ve el código y el renglón.
    /// </summary>
    [Fact]
    public async Task Si_ni_la_salida_se_deja_escribir_sale_con_2_sin_tronar()
    {
        PaqueteDePrueba.En(Usb);
        var estorbo = Path.Combine(_carpeta.Ruta, "no-es-una-carpeta");
        File.WriteAllText(estorbo, "");

        Assert.Equal(2, await Ayudante.CorrerAsync(["mudanza-ver", "--paquete", Usb, "--salida", Path.Combine(estorbo, "salida.txt")], Pc));
    }

    [Fact]
    public async Task Sin_decir_el_paquete_la_orden_esta_mal_dada()
    {
        Assert.Equal(1, await Ayudante.CorrerAsync(["mudanza-ver", "--salida", Salida], Pc));
        Assert.Equal(1, await Ayudante.CorrerAsync(["mudanza-ver", "--paquete", Usb], Pc));
        Assert.False(File.Exists(Salida));
    }
}
