using System.Globalization;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// Lo que <c>leer</c> le dice al asistente de la mudanza de la base a otra PC (docs/instalacion.md): si la base de esta PC ya se sacó
/// (la marca <c>pos.db.mudada</c>, con la que su servicio no arranca) y si aquí ya se puso la de un paquete (<c>mudanza-puesta.txt</c>).
/// Siempre dice sus claves, vacías si no aplica, y nunca toca la red.
/// </summary>
public sealed class MudanzaLeerTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();
    private readonly PcDePrueba _pc;

    public MudanzaLeerTests() => _pc = new PcDePrueba(_carpeta.Ruta);

    public void Dispose() => _carpeta.Dispose();

    private static readonly string[] ClavesDeLaMudanza = ["mudanza", "mudanza-cuando", "mudanza-carpeta", "mudanza-paquete", "mudanza-tienda", "mudanza-puesta"];

    /// <summary>Esa hora (UTC) en la de esta PC, como la enseña el asistente.</summary>
    private static string HoraLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>La caja 2 de hoy: el servicio registrado, su base, su servicio.json y su caja dada de alta con su propio servicio.</summary>
    private PcDePrueba LaCaja2(string servicioJson = """{ "Eleventa": { "Activo": true, "Escribir": true, "Servidor": "192.0.2.106", "Caja": 3, "Cajero": 1 } }""")
    {
        _pc.ConServicio();
        _pc.Archivo("pos.db", "");
        _pc.Archivo("servicio.json", servicioJson);
        _pc.Archivo(Path.Combine("Caja", "caja.json"), """{ "Servicio": "http://localhost:5080/", "CajaId": "5a1d4c3e-0000-4000-8000-000000000002", "Impresora": "" }""");
        return _pc;
    }

    [Fact]
    public void Sin_la_marca_todo_sigue_como_hoy_y_las_claves_de_la_mudanza_salen_vacias()
    {
        var leido = LecturaDeLaPc.Leer(LaCaja2());

        // Siempre están (el asistente las lee sin preguntar si existen), y vacías.
        foreach (var clave in ClavesDeLaMudanza)
            Assert.Equal("", leido[clave]);
        Assert.Equal("principal", leido["rol"]);
        Assert.Equal("principal,unica", leido["roles"]);
        Assert.Equal("En esta PC está el servicio con la base de la tienda: no puede ser «Otra caja».", leido["roles-motivo"]);
        Assert.Equal("", leido["error"]);
        // También en una PC sin nada.
        var nueva = LecturaDeLaPc.Leer(new PcDePrueba(Path.Combine(_carpeta.Ruta, "otra-pc")));
        foreach (var clave in ClavesDeLaMudanza)
            Assert.Equal("", nueva[clave]);
        Assert.Equal("principal,unica,otra", nueva["roles"]);
    }

    /// <summary>
    /// Con la marca, la base de esta PC ya se sacó: solo puede terminar como «Otra caja» (o cancelar). «rol» sigue diciendo lo que la PC
    /// es hoy: es lo que vuelve a ser si la mudanza se cancela.
    /// </summary>
    [Theory]
    [InlineData("""{ "Eleventa": { "Activo": true, "Escribir": true } }""", "principal")]
    [InlineData("""{ "Instalacion": { "UnaSolaCaja": true } }""", "unica")]
    public void Con_la_marca_dice_que_la_base_se_saco_y_solo_queda_ser_otra_caja(string servicioJson, string rol)
    {
        var usb = Path.Combine(_carpeta.Ruta, "usb", "mudanza");
        LaCaja2(servicioJson).DejarLaMarca(usb, cuandoUtc: "2026-11-14T14:15:07.1234567Z", tienda: "Centro; Matriz");

        var leido = LecturaDeLaPc.Leer(_pc);

        var cuando = HoraLocal(new DateTime(2026, 11, 14, 14, 15, 7, DateTimeKind.Utc));
        Assert.Equal("sacada", leido["mudanza"]);
        Assert.Equal(cuando, leido["mudanza-cuando"]);
        Assert.Equal(usb, leido["mudanza-carpeta"]);
        Assert.Equal(PaqueteDePrueba.Id, leido["mudanza-paquete"]);
        Assert.Equal("Centro; Matriz", leido["mudanza-tienda"]);
        Assert.Equal("", leido["mudanza-puesta"]);
        Assert.Equal("otra", leido["roles"]);
        Assert.Equal(rol, leido["rol"]);
        Assert.Equal($"La base de esta PC se sacó para mudarla a otra PC ({cuando}): solo le queda terminar la mudanza como «Otra caja», o cancelarla.",
            leido["roles-motivo"]);
        // Lo demás, como siempre: el servicio sigue registrado y la base sigue ahí (no se borra nada al sacarla).
        Assert.Equal("si", leido["servicio-instalado"]);
        Assert.Equal("si", leido["base"]);
        Assert.Equal("", leido["error"]);
    }

    /// <summary>La hora de la marca se entiende con o sin fracciones de segundo y con «Z» o con su desplazamiento.</summary>
    [Theory]
    [InlineData("2026-11-14T14:15:07Z")]
    [InlineData("2026-11-14T14:15:07.5+00:00")]
    [InlineData("2026-11-14T08:15:07-06:00")]
    [InlineData("2026-11-14T14:15:07")]
    public void La_hora_de_la_marca_sale_en_la_hora_de_esta_pc(string cuandoUtc)
    {
        LaCaja2().DejarLaMarca(@"E:\mudanza", cuandoUtc);

        Assert.Equal(HoraLocal(new DateTime(2026, 11, 14, 14, 15, 7, DateTimeKind.Utc)), LecturaDeLaPc.Leer(_pc)["mudanza-cuando"]);
    }

    /// <summary>
    /// Lo que cuenta es que la marca exista (con ella el servicio no arranca, y los scripts se niegan a todo lo que no sea terminar o
    /// cancelar): si no se entiende, la PC sigue «sacada» y solo faltan sus datos. Si <c>leer</c> dijera que no hay mudanza, el asistente
    /// no ofrecería ni terminar ni cancelar, y la PC quedaría sin salida.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("esto no es JSON")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "paquete": "uno", "paquete": "otro" }""")]
    [InlineData("""{ "cuandoUtc": "ayer", "carpeta": 7 }""")]
    public void Una_marca_que_no_se_entiende_sigue_siendo_la_marca(string marca)
    {
        LaCaja2().Archivo(MarcaDeMudanza.NombreDelArchivo, marca);

        var leido = LecturaDeLaPc.Leer(_pc);

        Assert.Equal("sacada", leido["mudanza"]);
        Assert.Equal("otra", leido["roles"]);
        Assert.Equal("principal", leido["rol"]);
        Assert.Equal("", leido["mudanza-cuando"]);
        Assert.Equal("", leido["mudanza-paquete"]);
        Assert.Equal("", leido["mudanza-tienda"]);
        Assert.StartsWith("La base de esta PC se sacó para mudarla a otra PC: solo le queda", leido["roles-motivo"]);
        // No es un error que detenga al instalador: es justo la PC que necesita correrlo.
        Assert.Equal("", leido["error"]);
    }

    /// <summary>
    /// Una conversión que se quedó a medias (caja.json ya apunta a la PC nueva y el servicio ya se quitó, pero la marca sigue): se repite
    /// como «Otra caja». Ya no hay servicio al que volver: «rol» dice lo que es.
    /// </summary>
    [Fact]
    public void Con_la_marca_y_ya_sin_el_servicio_es_otra_caja()
    {
        _pc.Archivo("pos.db", "");
        _pc.Archivo("servicio.json", "{ }");
        _pc.Archivo(Path.Combine("Caja", "caja.json"), """{ "Servicio": "http://192.0.2.50:5080/", "CajaId": "5a1d4c3e-0000-4000-8000-000000000002" }""");
        _pc.DejarLaMarca(@"E:\mudanza");

        var leido = LecturaDeLaPc.Leer(_pc);

        Assert.Equal("sacada", leido["mudanza"]);
        Assert.Equal("otra", leido["roles"]);
        Assert.Equal("otra", leido["rol"]);
        Assert.Equal("192.0.2.50", leido["principal"]);
    }

    /// <summary>En la PC a donde se trajo una base: de qué paquete es. Con eso el asistente sabe que es un segundo intento y deja elegido «Traer».</summary>
    [Fact]
    public void Dice_el_paquete_de_la_base_que_ya_se_puso_aqui()
    {
        _pc.ConServicio().DejarLaNota();

        var leido = LecturaDeLaPc.Leer(_pc);

        Assert.Equal(PaqueteDePrueba.Id, leido["mudanza-puesta"]);
        // Esta PC no es de donde salió una base: no hay marca.
        Assert.Equal("", leido["mudanza"]);
        Assert.Equal("principal,unica", leido["roles"]);
    }

    /// <summary>
    /// De la nota solo depende qué opción viene elegida: si no se deja leer o no dice su paquete, sale vacía y el instalador sigue (quien
    /// decide de verdad si la base ya está es mudanza-revisar, y ese sí lo dice).
    /// </summary>
    [Fact]
    public void Una_nota_que_no_se_deja_leer_o_no_dice_su_paquete_sale_vacia_sin_detener_al_instalador()
    {
        var nota = _pc.DejarLaNota();
        using (new FileStream(nota, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var leido = LecturaDeLaPc.Leer(_pc);

            Assert.Equal("", leido["mudanza-puesta"]);
            Assert.Equal("", leido["error"]);
        }

        File.WriteAllText(nota, "equipo=CAJA2\r\npaquete=   \r\n");
        Assert.Equal("", LecturaDeLaPc.Leer(_pc)["mudanza-puesta"]);
    }

    /// <summary>Como lo corre el asistente al abrir: con la marca sale con 0 (no es un error) y sin preguntarle nada a la red.</summary>
    [Fact]
    public async Task Leer_con_la_marca_sale_con_0_y_no_toca_la_red()
    {
        LaCaja2().DejarLaMarca(@"E:\mudanza");
        _pc.DejarLaNota("11111111111111111111111111111111");
        var red = RedDePrueba.SinNadie();
        var salida = Path.Combine(_carpeta.Ruta, "leer.txt");

        Assert.Equal(0, await Ayudante.CorrerAsync(["leer", "--salida", salida], _pc, red));

        var leido = ArchivoDeClaves.Leer(salida);
        Assert.Equal("sacada", leido["mudanza"]);
        Assert.Equal(@"E:\mudanza", leido["mudanza-carpeta"]);
        Assert.Equal("11111111111111111111111111111111", leido["mudanza-puesta"]);
        // Cada clave de la mudanza, una sola vez.
        foreach (var clave in ClavesDeLaMudanza)
            Assert.Single(leido.Datos, d => d.Clave == clave);
        Assert.Empty(red.Preguntadas);
    }
}
