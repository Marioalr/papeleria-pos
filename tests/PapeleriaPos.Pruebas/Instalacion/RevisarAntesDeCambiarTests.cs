using System.Text;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// Lo que el asistente revisa con el ayudante antes de cambiar nada en la PC (la orden <c>revisar</c>): todas las respuestas que no se
/// pueden usar, cada una con su clave (la que dice a qué página regresar), sin escribir nada; y que el asistente y el ayudante dejen pasar
/// lo mismo (el 0 de la plantilla es «falta», el puerto COM de 1 a 255, los puertos de red de 1 a 65535).
/// </summary>
public sealed class RevisarAntesDeCambiarTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();

    public void Dispose() => _carpeta.Dispose();

    private string Datos => Path.Combine(_carpeta.Ruta, "datos");

    private string Salida => Path.Combine(_carpeta.Ruta, "salida.txt");

    private IEquipo Equipo => new EquipoSinWindows(Datos, Path.Combine(_carpeta.Ruta, "programa"));

    private static ArchivoDeClaves Archivo(params (string Clave, string Valor)[] datos)
    {
        var archivo = new ArchivoDeClaves();
        foreach (var (clave, valor) in datos)
            archivo.Poner(clave, valor);
        return archivo;
    }

    private string Guardar(ArchivoDeClaves archivo)
    {
        var ruta = Path.Combine(_carpeta.Ruta, "respuestas.txt");
        archivo.Guardar(ruta);
        return ruta;
    }

    /// <summary>Una caja principal con todo, bien contestado; cada prueba cambia lo suyo.</summary>
    private static ArchivoDeClaves ConTodo(params (string Clave, string Valor)[] cambios)
    {
        var archivo = Archivo(("rol", "principal"), ("impresora", "tcp://192.168.1.100:9100"), ("cola", ""), ("lector", "com"),
            ("lector-puerto", "COM3"), ("lector-baudios", "9600"), ("controlprint", "si"), ("controlprint-servidor", "192.168.1.106"),
            ("eleventa", "si"), ("eleventa-servidor", "192.168.1.106"), ("eleventa-base", @"C:\eleventa\PDVDATA.FDB"), ("eleventa-caja", "3"),
            ("eleventa-cajero", "1"), ("eleventa-gbak", ""), ("eleventa-respaldos", @"C:\ProgramData\PapeleriaPos\respaldos-eleventa"));
        foreach (var (clave, valor) in cambios)
            archivo.Poner(clave, valor);
        return archivo;
    }

    [Fact]
    public void Dice_todas_las_que_no_se_pueden_usar_en_el_orden_de_las_paginas_cada_una_con_su_clave()
    {
        var (respuestas, errores) = Respuestas.Revisar(ConTodo(("eleventa-respaldos", @"C:\Users\Caja\OneDrive\respaldos"), ("eleventa-caja", "tres"),
            ("controlprint-servidor", "192.168.1.106:99999"), ("lector-puerto", "COM0"), ("impresora", "tcp://192.168.1.100:70000")));

        Assert.Null(respuestas);
        Assert.Equal(["impresora", "lector-puerto", "controlprint-servidor", "eleventa-caja", "eleventa-respaldos"], errores.Select(e => e.Clave));
        Assert.Contains("OneDrive", errores[^1].Message);
        // De() dice la primera, con su clave.
        var primera = Assert.Throws<RespuestaNoValida>(() => Respuestas.De(ConTodo(("lector-puerto", "COM256"), ("eleventa-caja", "x"))));
        Assert.Equal("lector-puerto", primera.Clave);
    }

    /// <summary>Lo mismo que deja pasar el asistente (RevisarLector, RevisarEleventa y EsDireccion, en papeleria-pos.iss).</summary>
    [Theory]
    [InlineData("lector-puerto", "COM1", true)]
    [InlineData("lector-puerto", "com255", true)]
    [InlineData("lector-puerto", "3", true)]
    [InlineData("lector-puerto", "COM0", false)]
    [InlineData("lector-puerto", "COM256", false)]
    [InlineData("lector-puerto", "COM3a", false)]
    [InlineData("lector-baudios", "50", true)]
    [InlineData("lector-baudios", "921600", true)]
    [InlineData("lector-baudios", "921601", false)]
    [InlineData("controlprint-servidor", "192.168.1.106:65535", true)]
    [InlineData("controlprint-servidor", "caja-principal", true)]
    [InlineData("controlprint-servidor", "192.168.1.106:65536", false)]
    [InlineData("controlprint-servidor", "192.168.1.106:0", false)]
    [InlineData("controlprint-servidor", "192.168.1.106:abc", false)]
    [InlineData("controlprint-servidor", ":9100", false)]
    [InlineData("controlprint-servidor", "192.168.1.106/x", false)]
    [InlineData("eleventa-servidor", "192.168.1.106 ", true)]
    [InlineData("eleventa-servidor", "192.168.1.106:3050", true)]
    [InlineData("eleventa-servidor", "192.168 .1.106", false)]
    [InlineData("eleventa-servidor", "a..b", false)]
    [InlineData("eleventa-servidor", ".a", false)]
    [InlineData("eleventa-servidor", "caja_2.local", true)]
    [InlineData("eleventa-caja", "0", true)]
    [InlineData("eleventa-caja", "00", true)]
    [InlineData("eleventa-caja", "", true)]
    [InlineData("eleventa-caja", "12", true)]
    [InlineData("eleventa-caja", "-1", false)]
    [InlineData("eleventa-caja", "999999999", true)]
    [InlineData("eleventa-caja", "1000000000", false)]
    [InlineData("eleventa-caja", "99999999999", false)]
    [InlineData("eleventa-cajero", "0", true)]
    [InlineData("eleventa-cajero", "uno", false)]
    [InlineData("impresora", "tcp://192.168.1.100:9100", true)]
    [InlineData("impresora", "tcp://192.168.1.100:65536", false)]
    public void Lo_que_deja_pasar(string clave, string valor, bool pasa)
    {
        var (_, errores) = Respuestas.Revisar(ConTodo((clave, valor)));

        Assert.Equal(pasa ? [] : [clave], errores.Select(e => e.Clave));
    }

    [Fact]
    public void La_caja_y_el_cajero_en_0_son_falta()
    {
        var respuestas = Respuestas.De(ConTodo(("eleventa-caja", "0"), ("eleventa-cajero", "00")));

        Assert.Null(respuestas.EleventaCaja);
        Assert.Null(respuestas.EleventaCajero);
    }

    /// <summary>
    /// La plantilla con su «"Caja": 0, "Cajero": 0»: «falta» deja el 0 como está (servicio.json no se toca); un número que se quita (la
    /// casilla vacía) sí se quita, como antes.
    /// </summary>
    [Fact]
    public void Con_el_0_de_la_plantilla_servicio_json_no_cambia()
    {
        Directory.CreateDirectory(Datos);
        var ruta = Path.Combine(Datos, ServicioJson.Archivo);
        var plantilla = "{\r\n  \"Eleventa\": {\r\n    \"Activo\": true,\r\n    \"Servidor\": \"192.0.2.10\",\r\n    \"Base\": \"C:\\\\B.FDB\",\r\n"
            + "    \"Caja\": 0,\r\n    \"Cajero\": 0 // de la plantilla\r\n  }\r\n}\r\n";
        File.WriteAllText(ruta, plantilla, new UTF8Encoding(true));
        var antes = File.ReadAllBytes(ruta);

        foreach (var caja in new[] { "0", "" })
        {
            var escrito = ServicioJson.Escribir(ruta, Respuestas.De(Archivo(("rol", "principal"), ("eleventa", "si"), ("eleventa-servidor", "192.0.2.10"),
                ("eleventa-base", @"C:\B.FDB"), ("eleventa-caja", caja), ("eleventa-cajero", caja))));

            Assert.False(escrito.Cambio, string.Join("; ", escrito.Cambios));
            Assert.Equal(antes, File.ReadAllBytes(ruta));
        }

        File.WriteAllText(ruta, plantilla.Replace("\"Caja\": 0", "\"Caja\": 5"), new UTF8Encoding(true));
        var quitada = ServicioJson.Escribir(ruta, Respuestas.De(Archivo(("rol", "principal"), ("eleventa", "si"), ("eleventa-servidor", "192.0.2.10"),
            ("eleventa-base", @"C:\B.FDB"), ("eleventa-caja", ""), ("eleventa-cajero", ""))));
        Assert.Equal(["Eleventa:Caja (se quita)"], quitada.Cambios);
        Assert.Contains("\"Cajero\": 0 // de la plantilla", File.ReadAllText(ruta));
    }

    [Fact]
    public async Task Revisar_no_escribe_nada_y_dice_lo_que_cambiaria()
    {
        Directory.CreateDirectory(Path.Combine(Datos, "Caja"));
        var servicio = Path.Combine(Datos, ServicioJson.Archivo);
        File.WriteAllText(servicio, "{\r\n  // la plantilla\r\n}\r\n", new UTF8Encoding(true));
        File.WriteAllText(CajaJson.Ruta(Datos), """{ "Servicio": "http://localhost:5080", "CajaId": null, "Impresora": "" }""");
        var antesServicio = File.ReadAllBytes(servicio);
        var antesCaja = File.ReadAllBytes(CajaJson.Ruta(Datos));
        var respuestas = Guardar(ConTodo());

        Assert.Equal(0, await Ayudante.CorrerAsync(["revisar", "--respuestas", respuestas, "--salida", Salida], Equipo));

        var salida = ArchivoDeClaves.Leer(Salida);
        Assert.Equal("", salida["error"]);
        Assert.Equal("cambio", salida["servicio-json"]);
        Assert.Equal("cambio", salida["caja-json"]);
        Assert.Contains("Eleventa (se agrega)", salida["cambios"]);
        Assert.Equal(antesServicio, File.ReadAllBytes(servicio));
        Assert.Equal(antesCaja, File.ReadAllBytes(CajaJson.Ruta(Datos)));
        Assert.Equal(["caja.json"], Directory.GetFiles(Path.Combine(Datos, "Caja")).Select(Path.GetFileName));
        Assert.Equal(["servicio.json"], Directory.GetFiles(Datos).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Revisar_con_respuestas_que_no_se_pueden_usar_sale_con_2_y_dice_cuales()
    {
        var respuestas = Guardar(ConTodo(("eleventa-caja", "tres"), ("lector-puerto", "COM0")));

        Assert.Equal(2, await Ayudante.CorrerAsync(["revisar", "--respuestas", respuestas, "--salida", Salida], Equipo));

        var salida = ArchivoDeClaves.Leer(Salida);
        Assert.StartsWith("Una respuesta no se puede usar: El puerto del lector", salida["error"]);
        Assert.Equal("lector-puerto", salida["clave"]);
        Assert.Equal("2", salida["errores"]);
        Assert.Equal("eleventa-caja", salida["error.2.clave"]);
        Assert.False(Directory.Exists(Datos));
    }

    [Fact]
    public async Task La_principal_que_es_la_unica_caja_o_no_tiene_tienda_se_avisa()
    {
        var unica = await BusquedaDeLaPrincipal.ProbarAsync(new Uri("http://192.0.2.10:5080/"),
            new Contesta("""{ "servicio": "Papelería POS", "version": "1.0.70", "sucursales": ["Matriz"], "unaSolaCaja": true }"""));
        var nueva = await BusquedaDeLaPrincipal.ProbarAsync(new Uri("http://192.0.2.10:5080/"),
            new Contesta("""{ "servicio": "Papelería POS", "version": "1.0.70", "sucursales": [], "tiendaNueva": true, "unaSolaCaja": false }"""));
        var deSiempre = await BusquedaDeLaPrincipal.ProbarAsync(new Uri("http://192.0.2.10:5080/"),
            new Contesta("""{ "servicio": "Papelería POS", "version": "1.0.60", "sucursales": ["Matriz"] }"""));

        Assert.True(unica.Contesta && unica.UnaSolaCaja);
        Assert.StartsWith("Esa PC está instalada como «La única caja»: no acepta otras cajas.", unica.Aviso);
        Assert.True(nueva.Contesta && nueva.TiendaNueva && !nueva.UnaSolaCaja);
        Assert.StartsWith("Esa caja principal todavía no tiene tienda: termina primero su instalación", nueva.Aviso);
        // Sin esos campos (un servicio de antes de «arranque»): no.
        Assert.False(deSiempre.UnaSolaCaja || deSiempre.TiendaNueva);
        Assert.Null(deSiempre.Aviso);

        var probado = await Ayudante.ProbarAsync("192.0.2.10", new Contesta("""{ "servicio": "Papelería POS", "unaSolaCaja": true }"""));
        Assert.Equal("si", probado["una-sola-caja"]);
        Assert.Equal("no", probado["tienda-nueva"]);
        Assert.Equal(unica.Aviso, probado["aviso"]);
    }

    [Fact]
    public void Una_caja_instalada_con_servicio_json_y_sin_el_servicio_no_es_otra_caja()
    {
        Directory.CreateDirectory(Datos);
        File.WriteAllText(Path.Combine(Datos, ServicioJson.Archivo), "{ }");
        var programa = Path.Combine(_carpeta.Ruta, "programa");
        Directory.CreateDirectory(Path.Combine(programa, "Caja"));
        File.WriteAllText(Path.Combine(programa, "Caja", "PapeleriaPos.Caja.exe"), "");

        var leido = LecturaDeLaPc.Leer(new EquipoSinWindows(Datos, programa));

        Assert.Equal("principal", leido["rol"]);
    }

    /// <summary>Contesta siempre lo mismo, como lo contestaría el servicio en su api/estado.</summary>
    private sealed class Contesta(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

/// <summary>Solo donde se pueden hacer enlaces simbólicos (Linux; en Windows, como administrador o con el modo de desarrollador).</summary>
public sealed class FactConEnlacesAttribute : FactAttribute
{
    public FactConEnlacesAttribute()
    {
        var prueba = Path.Combine(Path.GetTempPath(), $"enlace-{Guid.NewGuid():N}");
        try
        {
            File.CreateSymbolicLink(prueba, Path.GetTempPath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip = "Esta cuenta no puede hacer enlaces simbólicos (en Windows: como administrador o con el modo de desarrollador).";
        }
        finally
        {
            try
            {
                File.Delete(prueba);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>
/// El ayudante corre como administrador, y la carpeta de la caja la puede cambiar cualquier cuenta de Windows: nunca escribe caja.json,
/// su «.nuevo» o su «.anterior» (ni servicio.json) a través de un enlace, que lo mandaría a otro lado.
/// </summary>
public sealed class SinEnlacesTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();

    public void Dispose() => _carpeta.Dispose();

    private string Datos => Path.Combine(_carpeta.Ruta, "datos");

    private string Otro => Path.Combine(_carpeta.Ruta, "otro-lado");

    private static ArchivoDeClaves Respuestas() =>
        new ArchivoDeClaves().Poner("rol", "otra").Poner("principal", "192.0.2.10").Poner("impresora", "").Poner("lector", "teclado");

    [FactConEnlaces]
    public void Si_la_carpeta_de_la_caja_es_un_enlace_no_escribe()
    {
        Directory.CreateDirectory(Datos);
        Directory.CreateDirectory(Otro);
        Directory.CreateSymbolicLink(Path.Combine(Datos, "Caja"), Otro);

        var revisado = Ayudante.Revisar(Respuestas(), Datos);
        var escrito = Ayudante.Escribir(Respuestas(), Datos);

        Assert.Contains("es un enlace", revisado["error"]);
        Assert.Contains("es un enlace", escrito["error"]);
        Assert.Empty(Directory.GetFileSystemEntries(Otro));
    }

    [FactConEnlaces]
    public void Si_la_carpeta_de_los_datos_es_un_enlace_no_escribe()
    {
        Directory.CreateDirectory(Otro);
        Directory.CreateSymbolicLink(Datos, Otro);

        Assert.Contains("es un enlace", Ayudante.Escribir(Respuestas(), Datos)["error"]);
        Assert.Empty(Directory.GetFileSystemEntries(Otro));
    }

    [FactConEnlaces]
    public void Si_el_anterior_o_el_nuevo_es_un_enlace_no_escribe()
    {
        Directory.CreateDirectory(Path.Combine(Datos, "Caja"));
        Directory.CreateDirectory(Otro);
        var ajeno = Path.Combine(Otro, "de-otro.txt");
        File.WriteAllText(ajeno, "no se toca");
        File.WriteAllText(CajaJson.Ruta(Datos), """{ "Servicio": "http://localhost:5080", "CajaId": null, "Impresora": "" }""");
        var caja = File.ReadAllBytes(CajaJson.Ruta(Datos));

        foreach (var cual in new[] { ".anterior", ".nuevo", ".anterior.nuevo" })
        {
            var enlace = CajaJson.Ruta(Datos) + cual;
            File.CreateSymbolicLink(enlace, ajeno);

            Assert.Contains("es un enlace", Ayudante.Escribir(Respuestas(), Datos)["error"]);
            Assert.Equal("no se toca", File.ReadAllText(ajeno));
            Assert.Equal(caja, File.ReadAllBytes(CajaJson.Ruta(Datos)));
            File.Delete(enlace);
        }

        // Sin el enlace, escribe (y deja el de antes como «.anterior», un archivo de verdad).
        Assert.Equal("", Ayudante.Escribir(Respuestas(), Datos)["error"]);
        Assert.Equal(caja, File.ReadAllBytes(CajaJson.Ruta(Datos) + ".anterior"));
        Assert.Null(new FileInfo(CajaJson.Ruta(Datos) + ".anterior").LinkTarget);
    }

    [FactConEnlaces]
    public void Un_enlace_que_no_lleva_a_nada_tambien_es_un_enlace()
    {
        Directory.CreateDirectory(Datos);
        var colgante = Path.Combine(Datos, "colgante");
        File.CreateSymbolicLink(colgante, Path.Combine(Otro, "no-existe"));

        Assert.True(Archivos.EsEnlace(colgante));
        Assert.False(Archivos.EsEnlace(Datos));
        Assert.False(Archivos.EsEnlace(Path.Combine(Datos, "no-existe")));
    }
}
