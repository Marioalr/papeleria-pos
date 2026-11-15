using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// Los topes de los plazos del puente que viajan en un paquete de mudanza (<c>eleventa-ajustes</c>; Enmiendas 6 y 7.4 del diseño), escritos
/// en dos lados que no se usan entre sí: el servicio que saca el paquete no mete un plazo que no quepa
/// (<see cref="MudanzaDeLaBase.TopesDeLosPlazos"/>), y el ayudante del instalador, en la PC nueva, da por «incompleto» el paquete que trae uno
/// (<c>ResumenDeMudanza.TopesDeLosPlazos</c>). Si uno cambia y el otro no, aquí falla: con un tope más alto en el servicio, el paquete
/// saldría «incompleto» y sacarlo otra vez traería lo mismo; con uno más bajo, un plazo que allá sirve se quedaría sin viajar.
/// </summary>
public sealed class TopesDeLosAjustesDelPuenteTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();
    private readonly PcDePrueba _pc;

    public TopesDeLosAjustesDelPuenteTests() => _pc = new PcDePrueba(_carpeta.Ruta);

    public void Dispose() => _carpeta.Dispose();

    private string Usb => Path.Combine(_carpeta.Ruta, "usb", "mudanza");

    private string Salida => Path.Combine(_carpeta.Ruta, "salida.txt");

    private static string Entero(int valor) => valor.ToString(CultureInfo.InvariantCulture);

    /// <summary>El plazo como lo lee el servicio de su servicio.json: de ahí su tipo (entero, o con decimales).</summary>
    private static PropertyInfo Propiedad(string plazo) =>
        typeof(OpcionesEleventa).GetProperty(plazo) ?? throw new InvalidOperationException($"El puente del servicio no tiene «{plazo}».");

    /// <summary>Los plazos con tope, como los tiene el servicio.</summary>
    public static TheoryData<string, int> PlazosDelServicio()
    {
        var plazos = new TheoryData<string, int>();
        foreach (var (plazo, tope) in MudanzaDeLaBase.TopesDeLosPlazos)
            plazos.Add(plazo, tope);
        return plazos;
    }

    /// <summary>
    /// Lo que la PC nueva dice de un paquete con esos ajustes del puente (<c>mudanza-ver</c>, como lo corre el asistente); lo demás del paquete
    /// es el de la tienda, que está bien.
    /// </summary>
    private async Task<ArchivoDeClaves> VerAsync(string ajustes)
    {
        PaqueteDePrueba.En(Usb, ("eleventa-ajustes", ajustes));
        var red = RedDePrueba.SinNadie();

        Assert.Equal(0, await Ayudante.CorrerAsync(["mudanza-ver", "--paquete", Usb, "--salida", Salida], _pc, red));

        Assert.Empty(red.Preguntadas);
        return ArchivoDeClaves.Leer(Salida);
    }

    /// <summary>
    /// Lo que queda en la PC nueva con el paquete de la carpeta: <c>escribir --mudanza</c> sobre la plantilla que deja instalar-servicio.ps1,
    /// con las respuestas de siempre, y su servicio.json leído como lo lee el servicio al arrancar (Program.cs).
    /// </summary>
    private async Task<OpcionesEleventa> EscribirAsync()
    {
        var ruta = Path.Combine(_pc.Datos, ServicioJson.Archivo);
        Directory.CreateDirectory(_pc.Datos);
        File.Copy(Path.Combine(ServicioJsonTests.RaizDelRepositorio(), "instalacion", "plantilla-servicio.json"), ruta, overwrite: true);
        var respuestas = Path.Combine(_carpeta.Ruta, "respuestas.txt");
        RespuestasDePrueba.ParaTraer(Usb, RespuestasDePrueba.Gbak(_carpeta.Ruta)).Guardar(respuestas);
        var red = RedDePrueba.SinNadie();

        var codigo = await Ayudante.CorrerAsync(["escribir", "--respuestas", respuestas, "--mudanza", Usb, "--salida", Salida], _pc, red);

        var dice = ArchivoDeClaves.Leer(Salida);
        Assert.True(codigo == 0 && dice["error"] == "", $"escribir terminó con {codigo}: {dice["error"]}");
        Assert.Empty(red.Preguntadas);
        using var configuracion = new ConfigurationManager();
        ConfiguracionDeLaPc.Agregar(configuracion, ruta);
        return configuracion.GetSection("Eleventa").Get<OpcionesEleventa>()!;
    }

    // ── Los mismos topes en los dos lados ────────────────────────────────

    /// <summary>
    /// Los topes como los tiene el ayudante (los plazos de su lista cerrada de los ajustes que viajan, con lo más que puede valer cada uno):
    /// los mismos plazos, con los mismos números, que los del servicio. Y cada uno es un plazo del puente del servicio, entero o con decimales.
    /// </summary>
    [Fact]
    public void Los_plazos_y_sus_topes_son_los_mismos_en_el_servicio_y_en_el_ayudante()
    {
        static IEnumerable<string> Dichos(IReadOnlyDictionary<string, int> topes) =>
            topes.Select(p => $"{p.Key}={Entero(p.Value)}").Order(StringComparer.Ordinal);

        Assert.Equal(Dichos(ResumenDeMudanza.TopesDeLosPlazos), Dichos(MudanzaDeLaBase.TopesDeLosPlazos));
        Assert.All(MudanzaDeLaBase.TopesDeLosPlazos.Keys, plazo => Assert.Contains(Propiedad(plazo).PropertyType, new[] { typeof(int), typeof(double) }));
    }

    /// <summary>
    /// Los mismos topes, vistos como la PC nueva lee el paquete: cada plazo con tope del servicio se acepta del 0 a su tope, y con uno más el
    /// paquete está «incompleto» por ese plazo. Falla también si el ayudante cambia cómo compara, aunque su número siga igual.
    /// </summary>
    [Theory]
    [MemberData(nameof(PlazosDelServicio))]
    public async Task La_pc_nueva_acepta_cada_plazo_de_cero_hasta_el_tope_del_servicio_y_uno_mas_no(string plazo, int tope)
    {
        foreach (var cabe in new[] { 0, tope })
        {
            var visto = await VerAsync($"{plazo}={Entero(cabe)}");
            Assert.True(visto["paquete"] == "bien", $"{plazo}={Entero(cabe)}: {visto["error"]}");
        }

        var noCabe = await VerAsync($"{plazo}={Entero(tope + 1)}");

        Assert.Equal("incompleto", noCabe["paquete"]);
        Assert.Contains($"«{plazo}»", noCabe["error"]);
    }

    // ── Lo que mete el servicio en el paquete ────────────────────────────

    /// <summary>
    /// Plazos de un servicio.json editado a mano, de cada tipo: negativos (el servicio de allá los sube a su mínimo), el 0, el tope y lo que
    /// pasa de él y enteros que no caben en ningún plazo; en el que lleva decimales, además, los que .NET escribiría con exponente o con muchas
    /// cifras, los que rozan el tope y lo que no es un número.
    /// </summary>
    public static TheoryData<string, double> PlazosEditadosAMano()
    {
        var plazos = new TheoryData<string, double>();
        foreach (var (plazo, tope) in MudanzaDeLaBase.TopesDeLosPlazos)
        {
            List<double> valores = [int.MinValue, -1, 0, tope, tope + 1, int.MaxValue];
            if (Propiedad(plazo).PropertyType == typeof(double))
                valores.AddRange([double.NegativeInfinity, -2.5, -0.000000000001, double.NegativeZero, 0.00000000001, 0.00001, 1.0 / 7000, 0.1 + 0.2, 2.5,
                    tope - 0.0000001, tope + 0.0000000001, tope + 0.0000001, 1e20, double.MaxValue, double.PositiveInfinity, double.NaN]);
            foreach (var valor in valores)
                plazos.Add(plazo, valor);
        }
        return plazos;
    }

    /// <summary>
    /// Lo que el servicio mete en el paquete (<see cref="MudanzaDeLaBase.AjustesDelPaquete"/>, lo mismo que escribe en mudanza.txt) lo acepta
    /// siempre la PC nueva y le llega como estaba: leído como lo lee el servicio de allá al arrancar, el plazo queda con su valor (redondeado,
    /// si traía más cifras de las que viajan). El que no cabe no viaja: lo dice un aviso y allá queda el de siempre. Escrito como se escribía
    /// antes, ese habría dejado el paquete «incompleto».
    /// </summary>
    [Theory]
    [MemberData(nameof(PlazosEditadosAMano))]
    public async Task Lo_que_el_servicio_mete_en_el_paquete_lo_acepta_la_pc_nueva_y_le_llega_como_estaba(string plazo, double valor)
    {
        var propiedad = Propiedad(plazo);
        var esEntero = propiedad.PropertyType == typeof(int);
        var tope = MudanzaDeLaBase.TopesDeLosPlazos[plazo];
        var alla = new OpcionesEleventa();
        propiedad.SetValue(alla, esEntero ? checked((int)valor) : (object)valor);
        // El de siempre ni viaja ni se avisa: aquí ningún valor es ese.
        var deSiempre = Convert.ToDouble(propiedad.GetValue(new OpcionesEleventa()), CultureInfo.InvariantCulture);
        Assert.NotEqual(deSiempre, valor);

        var (ajustes, avisos) = MudanzaDeLaBase.AjustesDelPaquete(alla);

        var viaja = ajustes.Length > 0;
        if (viaja)
        {
            Assert.StartsWith($"{plazo}=", ajustes);
            Assert.Empty(avisos);
        }
        else
        {
            // Solo se queda lo que de veras no cabe: fuera de 0 a su tope, o que no es un número.
            Assert.False(valor >= 0 && valor <= tope, $"{plazo}={valor.ToString(CultureInfo.InvariantCulture)} cabe y no viajó: {string.Join(" ", avisos)}");
            var aviso = Assert.Single(avisos);
            Assert.StartsWith($"El ajuste «{plazo}» del puente con eleventa de esta PC (", aviso);
            Assert.EndsWith($") no cabe en el paquete (la PC nueva acepta de 0 a {Entero(tope)}): ponlo a mano en la PC nueva.", aviso);
            // Metido en el paquete como se escribía antes, tal cual, allá no se habría podido traer.
            var comoAntes = esEntero ? Entero(checked((int)valor)) : valor.ToString(CultureInfo.InvariantCulture);
            Assert.Equal("incompleto", (await VerAsync($"{plazo}={comoAntes}"))["paquete"]);
        }
        var visto = await VerAsync(ajustes);
        Assert.True(visto["paquete"] == "bien", $"{plazo}={valor.ToString(CultureInfo.InvariantCulture)} quedó «{ajustes}»: {visto["error"]}");

        var aqui = await EscribirAsync();

        Assert.Equal(viaja ? valor : deSiempre, Convert.ToDouble(propiedad.GetValue(aqui), CultureInfo.InvariantCulture), 0.00000001);
    }
}
