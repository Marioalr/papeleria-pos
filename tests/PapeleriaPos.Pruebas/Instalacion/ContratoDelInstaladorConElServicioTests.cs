using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Servicio;
using PapeleriaPos.Servicio;
using static PapeleriaPos.Pruebas.Instalacion.ContratoDelInstaladorTests;
using MarcaDeMudanza = PapeleriaPos.Instalacion.MarcaDeMudanza;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// La cuarta parte del instalador: el exe del servicio, que en la PC de donde sale la base saca el paquete de mudanza (<c>mudanza-sacar</c>,
/// <see cref="MudanzaDeLaBase"/>; diseno-v2, 3.2). Como en <see cref="ContratoDelInstaladorTests"/>, lo que uno escribe es lo que el otro
/// lee, aquí con el exe de verdad sacando una base de verdad: lo que instalar-pc.ps1 le pide y lee de su salida, de la marca y de
/// mudanza.txt, y lo que el asistente lee de mudanza.txt. (Lo que lee de ahí el ayudante: <see cref="MudanzaConElPaqueteDelServicioTests"/>.)
/// </summary>
public sealed class ContratoDelInstaladorConElServicioTests(BaseParaMudar semilla) : IClassFixture<BaseParaMudar>
{
    [Fact]
    public async Task Lo_que_instalar_pc_le_pide_a_mudanza_sacar_lo_entiende_y_lo_que_lee_lo_escribe_el_servicio()
    {
        using var pc = new PcQueSeMuda(semilla.Archivo);
        var script = ScriptSinComentarios;
        var sacar = FuncionDelScript(script, "Mudanza-Sacar");
        // La orden como la da el script (Con-Salida le agrega --salida; Program.cs le pasa al verbo lo de después de su nombre).
        Assert.Contains($"Con-Salida $exe @('{MudanzaDeLaBase.Verbo}', '--carpeta', $carpeta)", sacar);
        var archivoDeSalida = Path.Combine(pc.Carpeta, "salida", "sacar.txt");
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Pos"] = $"Data Source={pc.Ruta}",
            ["Urls"] = "http://*:5080",
        }).Build();

        Assert.Equal(0, await MudanzaDeLaBase.CorrerAsync(configuracion, ["--carpeta", pc.Usb, "--salida", archivoDeSalida], new StringWriter(), new StringWriter(),
            pc.SegunLaPrueba));

        // Lo que lee de su salida: si quedó, la carpeta, cuándo y cada aviso hasta «avisos».
        var salida = ClavesEscritas.De(archivoDeSalida);
        var dice = salida.Nombres.ToHashSet(StringComparer.OrdinalIgnoreCase);
        LeeLoQueDice(sacar, @"\$claves\['([a-z-]+)'\]", dice.Append("error").ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.Contains("$claves[\"aviso.$i\"]", sacar);
        var avisos = int.Parse(salida["avisos"]);
        Assert.True(avisos >= 1, $"avisos={avisos}");
        Faltan(Enumerable.Range(1, avisos).Select(i => $"aviso.{i}").Where(c => !dice.Contains(c)));
        // Si no se saca (aquí, porque ya tiene la marca), «bien=no» y su «error», con 1.
        Assert.Contains("$claves['bien'] -ne 'no'", sacar);
        Assert.Equal(1, await MudanzaDeLaBase.CorrerAsync(configuracion, ["--carpeta", pc.Usb, "--salida", archivoDeSalida], new StringWriter(), new StringWriter(),
            pc.SegunLaPrueba));
        var noSeSaco = ClavesEscritas.De(archivoDeSalida);
        Assert.Equal("no", noSeSaco["bien"]);
        Assert.NotEqual("", noSeSaco["error"]);

        // La marca, junto a la base, con el nombre que buscan los scripts, el ayudante y el asistente; y lo que lee de ella instalar-pc.ps1.
        var marca = Path.Combine(pc.Datos, MarcaDeMudanza.NombreDelArchivo);
        Assert.True(File.Exists(marca), marca);
        var enLaMarca = ((JsonObject)JsonNode.Parse(File.ReadAllText(marca))!).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leeDeLaMarca = Claves(FuncionDelScript(script, "Leer-Marca"), @"\$json\.([A-Za-z]+)");
        Assert.True(leeDeLaMarca.Count >= 4, string.Join(", ", leeDeLaMarca));
        Faltan(leeDeLaMarca.Where(c => !enLaMarca.Contains(c)));

        // mudanza.txt: lo que de él lee instalar-pc.ps1 (al traer, al terminar y al cancelar) y el asistente (cuál era la caja de esta PC).
        var resumen = ClavesEscritas.De(pc.Resumen);
        var enElResumen = resumen.Nombres.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leeElScript = Claves(FuncionDelScript(script, "Instalar"), @"\$resumenDelPaquete\['([a-z-]+)'\]");
        leeElScript.UnionWith(Claves(FuncionDelScript(script, "Mudanza-Traer") + FuncionDelScript(script, "Dejar-Nota-De-Puesta"), @"\$resumen\['([a-z0-9-]+)'\]"));
        leeElScript.UnionWith(Claves(FuncionDelScript(script, "Trae-Su-Base") + FuncionDelScript(script, "Mudanza-Cancelar"), @"\$delPaquete\['([a-z-]+)'\]"));
        Assert.True(leeElScript.Count >= 5, string.Join(", ", leeElScript));
        Faltan(leeElScript.Where(c => !enElResumen.Contains(c)));
        var cajaDeEstaPc = FuncionDelAsistente("CajaDeEstaPc");
        var leeElAsistente = Claves(cajaDeEstaPc, @"Valor\(Lineas, '([a-z0-9.-]+)'\)");
        leeElAsistente.UnionWith(Claves(cajaDeEstaPc, @"Valor\(Lineas, 'caja\.' \+ IntToStr\(I\) \+ '\.([a-z-]+)'\)").Select(dato => $"caja.1.{dato}"));
        Assert.True(leeElAsistente.Count >= 4, string.Join(", ", leeElAsistente));
        Faltan(leeElAsistente.Where(c => !enElResumen.Contains(c)));
        // La base del paquete: un nombre como los que el script acepta para armar una ruta con él (lo demás de un paquete viene de una USB).
        var nombreDeLaBase = Regex.Match(script, @"\$nombreDeBaseMudada = '([^']+)'").Groups[1].Value;
        Assert.NotEqual("", nombreDeLaBase);
        Assert.Matches(nombreDeLaBase, resumen["archivo"]);
    }

    /// <summary>
    /// El pedido que deja restaurar.ps1 junto a la base (<c>pos.db.restaurar</c>), también con <c>-PorMudanza</c> al traer la base de otra
    /// PC (diseno-v2, 3.2 c): cada dato es uno que lee el servicio (<see cref="PedidoDeRestauracion"/>, con los nombres de la web, sin
    /// distinguir mayúsculas). Uno con otro nombre se perdería callado: con <c>porMudanza</c>, la bitácora ya no diría «Se mudó aquí…».
    /// </summary>
    [Fact]
    public void El_pedido_que_deja_restaurar_ps1_lo_entiende_el_servicio()
    {
        var restaurar = SinComentarios(Leer("instalacion", "restaurar.ps1"));
        var pedido = Regex.Match(restaurar, @"\$pedido = \[ordered\]@\{(.*?)\n\s*\}", RegexOptions.Singleline);
        Assert.True(pedido.Success, "restaurar.ps1 ya no arma así su pedido");
        var escribe = Claves(pedido.Groups[1].Value, @"(?:^|[;{\s])([A-Za-z]+) = ");
        escribe.UnionWith(Claves(restaurar, @"\$pedido\.Add\('([A-Za-z]+)'"));
        Assert.True(escribe.Count >= 15, string.Join(", ", escribe));
        Assert.Contains("porMudanza", escribe);
        Assert.Contains("if ($PorMudanza) { $pedido.Add('porMudanza', $PorMudanza) }", restaurar);
        var lee = typeof(PedidoDeRestauracion).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Faltan(escribe.Where(c => !lee.Contains(c)));
    }
}
