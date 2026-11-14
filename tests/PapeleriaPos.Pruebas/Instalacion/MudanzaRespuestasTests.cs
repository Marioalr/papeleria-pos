using PapeleriaPos.Instalacion;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// Las respuestas de la página «La base de la tienda» (docs/instalacion.md): <c>mudanza</c> (sacar, traer, terminar o cancelar),
/// <c>mudanza-paquete</c> y <c>mudanza-encima</c>. Son opcionales: sin ellas todo es como antes de que existieran. Con ellas, cada una
/// va solo con lo que necesita, y lo que no cuadra se dice con su clave (la página a la que regresa el asistente).
/// </summary>
public sealed class MudanzaRespuestasTests
{
    private static ArchivoDeClaves Archivo(params (string Clave, string Valor)[] datos)
    {
        var archivo = new ArchivoDeClaves();
        foreach (var (clave, valor) in datos)
            archivo.Poner(clave, valor);
        return archivo;
    }

    /// <summary>Una PC de ese rol, bien contestada (lo de siempre), más lo de la mudanza.</summary>
    private static ArchivoDeClaves Con(string rol, params (string Clave, string Valor)[] deLaMudanza)
    {
        var archivo = rol == "otra"
            ? Archivo(("rol", "otra"), ("impresora", ""), ("lector", "teclado"))
            : Archivo(("rol", rol), ("impresora", ""), ("lector", "teclado"), ("controlprint", "no"), ("eleventa", "no"));
        foreach (var (clave, valor) in deLaMudanza)
            archivo.Poner(clave, valor);
        return archivo;
    }

    [Theory]
    [InlineData("principal")]
    [InlineData("unica")]
    [InlineData("otra")]
    public void Sin_ellas_o_vacias_no_hay_mudanza_y_las_respuestas_son_las_de_siempre(string rol)
    {
        var deSiempre = Respuestas.De(Con(rol));
        // Vacías, como las deja el asistente cuando no aplica; y lo demás de la mudanza, aunque venga, no cuenta sin «mudanza».
        var vacias = Respuestas.De(Con(rol, ("mudanza", ""), ("mudanza-paquete", @"E:\mudanza"), ("mudanza-encima", "tal vez")));

        Assert.Equal(MudanzaPedida.Ninguna, deSiempre.Mudanza);
        Assert.Null(deSiempre.MudanzaPaquete);
        Assert.False(deSiempre.MudanzaEncima);
        // Las mismas, campo por campo (son un record).
        Assert.Equal(deSiempre, vacias);
    }

    [Theory]
    [InlineData("principal", "sacar", MudanzaPedida.Sacar)]
    [InlineData("unica", "sacar", MudanzaPedida.Sacar)]
    [InlineData("principal", "cancelar", MudanzaPedida.Cancelar)]
    [InlineData("unica", " Cancelar ", MudanzaPedida.Cancelar)]
    [InlineData("principal", "TRAER", MudanzaPedida.Traer)]
    [InlineData("unica", "traer", MudanzaPedida.Traer)]
    public void Sacar_traer_y_cancelar_van_con_un_rol_con_servicio(string rol, string mudanza, MudanzaPedida pedida)
    {
        var respuestas = Respuestas.De(Con(rol, ("mudanza", mudanza), ("mudanza-paquete", @" E:\mudanza ")));

        Assert.Equal(pedida, respuestas.Mudanza);
        // La carpeta: donde dejar el paquete al sacar y donde está al traer. Al cancelar no cuenta.
        Assert.Equal(pedida == MudanzaPedida.Cancelar ? null : @"E:\mudanza", respuestas.MudanzaPaquete);
        Assert.False(respuestas.MudanzaEncima);
    }

    [Theory]
    [InlineData("sacar", "La base solo se saca para mudarla de una PC que la tiene")]
    [InlineData("cancelar", "Cancelar la mudanza deja la base en esta PC")]
    [InlineData("traer", "La base de otra PC solo se trae a la PC que va a tener el servicio")]
    public void En_otra_caja_no_se_saca_ni_se_trae_ni_se_cancela(string mudanza, string dice)
    {
        var error = Assert.Throws<RespuestaNoValida>(() => Respuestas.De(Con("otra", ("mudanza", mudanza), ("mudanza-paquete", @"E:\mudanza"), ("principal", "192.0.2.50"))));

        Assert.StartsWith(dice, error.Message);
        Assert.EndsWith("«Otra caja».", error.Message);
        Assert.Equal("mudanza", error.Clave);
    }

    [Fact]
    public void Traer_necesita_la_carpeta_del_paquete()
    {
        foreach (var carpeta in new[] { null, "", "   " })
        {
            var archivo = Con("principal", ("mudanza", "traer"));
            if (carpeta is not null)
                archivo.Poner("mudanza-paquete", carpeta);

            var error = Assert.Throws<RespuestaNoValida>(() => Respuestas.De(archivo));

            Assert.StartsWith("Falta la carpeta del paquete de mudanza", error.Message);
            Assert.Equal("mudanza-paquete", error.Clave);
        }
    }

    /// <summary>«Sí, apartar la de aquí»: solo al traer, y solo si se dice.</summary>
    [Theory]
    [InlineData("si", true)]
    [InlineData("SÍ", true)]
    [InlineData("no", false)]
    [InlineData("", false)]
    public void Traer_encima_solo_si_se_dice(string encima, bool queda)
    {
        var respuestas = Respuestas.De(Con("principal", ("mudanza", "traer"), ("mudanza-paquete", @"E:\mudanza"), ("mudanza-encima", encima)));

        Assert.Equal(queda, respuestas.MudanzaEncima);
        // Con sacar, terminar o cancelar no hay nada que traer encima.
        Assert.False(Respuestas.De(Con("principal", ("mudanza", "sacar"), ("mudanza-encima", "si"))).MudanzaEncima);
    }

    [Fact]
    public void Un_traer_encima_que_no_es_si_ni_no_se_dice()
    {
        var error = Assert.Throws<RespuestaNoValida>(
            () => Respuestas.De(Con("principal", ("mudanza", "traer"), ("mudanza-paquete", @"E:\mudanza"), ("mudanza-encima", "tal vez"))));

        Assert.Equal("«mudanza-encima» va como si o no (está «tal vez»).", error.Message);
        Assert.Equal("mudanza-encima", error.Clave);
    }

    /// <summary>
    /// Terminar deja la PC como «Otra caja» de la PC nueva: va con ese rol y con su dirección (ahí no se busca después: la caja queda
    /// apuntando a la PC que ya tiene su base). La carpeta es opcional: donde el asistente vio el traida.txt, si lo vio.
    /// </summary>
    [Theory]
    [InlineData(@"E:\mudanza", @"E:\mudanza")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Terminar_va_con_otra_caja_y_con_la_direccion_de_la_principal(string? carpeta, string? queda)
    {
        var archivo = Con("otra", ("mudanza", "terminar"), ("principal", "192.0.2.50"));
        if (carpeta is not null)
            archivo.Poner("mudanza-paquete", carpeta);

        var respuestas = Respuestas.De(archivo);

        Assert.Equal(MudanzaPedida.Terminar, respuestas.Mudanza);
        Assert.Equal("192.0.2.50", respuestas.Principal);
        Assert.Equal(queda, respuestas.MudanzaPaquete);
    }

    [Theory]
    [InlineData("principal")]
    [InlineData("unica")]
    public void Terminar_no_va_con_un_rol_con_servicio(string rol)
    {
        var error = Assert.Throws<RespuestaNoValida>(() => Respuestas.De(Con(rol, ("mudanza", "terminar"))));

        Assert.Equal("Terminar la mudanza deja esta PC como «Otra caja» de la PC nueva: va con «Otra caja».", error.Message);
        Assert.Equal("mudanza", error.Clave);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Terminar_sin_la_direccion_de_la_principal_no_se_puede(string? principal)
    {
        var archivo = Con("otra", ("mudanza", "terminar"));
        if (principal is not null)
            archivo.Poner("principal", principal);

        var error = Assert.Throws<RespuestaNoValida>(() => Respuestas.De(archivo));

        Assert.StartsWith("Para terminar la mudanza hace falta la dirección de la caja principal", error.Message);
        Assert.Equal("principal", error.Clave);
        // Sin mudanza, «Otra caja» sin dirección sigue valiendo como hoy (la caja la busca al abrirla).
        Assert.Null(Respuestas.De(Con("otra")).Principal);
    }

    [Fact]
    public void Una_direccion_que_no_se_entiende_al_terminar_se_dice_como_siempre()
    {
        var error = Assert.Throws<RespuestaNoValida>(() => Respuestas.De(Con("otra", ("mudanza", "terminar"), ("principal", "la de enfrente"))));

        Assert.StartsWith("La dirección de la caja principal no se entiende", error.Message);
        Assert.Equal("principal", error.Clave);
    }

    [Theory]
    [InlineData("mudar")]
    [InlineData("si")]
    [InlineData("traer, sacar")]
    public void Una_mudanza_que_no_es_ninguna_de_las_cuatro_se_dice(string mudanza)
    {
        var error = Assert.Throws<RespuestaNoValida>(() => Respuestas.De(Con("principal", ("mudanza", mudanza))));

        Assert.Equal($"«mudanza» va como sacar, traer, terminar o cancelar, o vacía (está «{mudanza}»).", error.Message);
        Assert.Equal("mudanza", error.Clave);
    }

    /// <summary>Los errores de la mudanza salen en el orden de las páginas: después de «Qué es esta PC» y antes de lo demás.</summary>
    [Fact]
    public void Los_errores_de_la_mudanza_van_en_el_orden_de_las_paginas()
    {
        var (respuestas, errores) = Respuestas.Revisar(Archivo(("rol", "principal"), ("mudanza", "traer"), ("mudanza-encima", "x"), ("impresora", "tcp://"),
            ("lector", "teclado"), ("controlprint", "no"), ("eleventa", "si")));

        Assert.Null(respuestas);
        Assert.Equal(["mudanza-paquete", "mudanza-encima", "impresora", "eleventa-servidor", "eleventa-base"], errores.Select(e => e.Clave));
    }

    /// <summary>
    /// Las respuestas de la mudanza no llegan solas ni a servicio.json ni a caja.json: lo que escribe «escribir» con ellas es, byte por
    /// byte, lo que escribe sin ellas (lo de la mudanza se pide aparte, con --mudanza).
    /// </summary>
    [Fact]
    public void Con_ellas_escribir_deja_los_mismos_bytes_que_sin_ellas()
    {
        using var carpeta = new PapeleriaPos.Pruebas.Interfaz.CarpetaDePrueba();
        var (sin, con) = (Path.Combine(carpeta.Ruta, "sin"), Path.Combine(carpeta.Ruta, "con"));
        (string Clave, string Valor)[] deSiempre =
        [
            ("rol", "principal"), ("impresora", "tcp://192.0.2.100:9100"), ("cola", "WF-6590"), ("lector", "com"), ("lector-puerto", "COM3"),
            ("controlprint", "si"), ("controlprint-servidor", "192.0.2.106"), ("eleventa", "si"), ("eleventa-servidor", "192.0.2.106"),
            ("eleventa-base", @"C:\eleventa\PDVDATA.FDB"), ("eleventa-caja", "1"), ("eleventa-cajero", "1"),
        ];
        foreach (var datos in new[] { sin, con })
        {
            Directory.CreateDirectory(datos);
            File.WriteAllText(Path.Combine(datos, "servicio.json"), "{\r\n  // la plantilla\r\n}\r\n");
        }

        var escritoSin = Ayudante.Escribir(Archivo(deSiempre), sin);
        var escritoCon = Ayudante.Escribir(Archivo([.. deSiempre, ("mudanza", "traer"), ("mudanza-paquete", @"E:\mudanza"), ("mudanza-encima", "si")]), con);

        Assert.Equal("", escritoCon["error"]);
        Assert.Equal(escritoSin.Datos, escritoCon.Datos);
        Assert.Equal(File.ReadAllBytes(Path.Combine(sin, "servicio.json")), File.ReadAllBytes(Path.Combine(con, "servicio.json")));
        Assert.Equal(File.ReadAllBytes(CajaJson.Ruta(sin)), File.ReadAllBytes(CajaJson.Ruta(con)));
    }
}
