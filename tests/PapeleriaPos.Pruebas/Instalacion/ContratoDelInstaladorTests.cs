using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;
using MudanzaDeLaBase = PapeleriaPos.Servicio.MudanzaDeLaBase;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// El instalador son cuatro partes que se pasan datos por archivos «clave=valor» y por su línea de órdenes: el asistente
/// (instalacion/papeleria-pos.iss, en Pascal), instalar-pc.ps1 (con restaurar.ps1 y los demás scripts), el ayudante (C#) y, para mudar la
/// base a otra PC, el exe del servicio (<c>mudanza-sacar</c>; eso, en <see cref="ContratoDelInstaladorConElServicioTests"/>). El Pascal y
/// el PowerShell solo corren en Windows (el CI): aquí se revisa que lo que uno escribe sea lo que el otro lee, que las órdenes y opciones que
/// uno da sean las que el otro entiende (el ayudante calla las que no conoce), que lo que se puede dar sin preguntar esté en
/// docs/instalacion.md y que las claves no se cuelen a ningún lado.
/// </summary>
public sealed class ContratoDelInstaladorTests
{
    private static readonly string Raiz = ServicioJsonTests.RaizDelRepositorio();

    internal static string Leer(params string[] partes) => File.ReadAllText(Path.Combine([Raiz, .. partes]));

    private static readonly string Asistente = Leer("instalacion", "papeleria-pos.iss");

    private static readonly string Script = Leer("instalacion", "instalar-pc.ps1");

    /// <summary>Lo de [Code], sin los comentarios { … } (para no confundir lo que se dice con lo que se hace).</summary>
    internal static string Codigo => Regex.Replace(Asistente[Asistente.IndexOf("[Code]", StringComparison.Ordinal)..], @"\{[^}]*\}", "");

    /// <summary>Un .ps1 sin sus comentarios (el bloque &lt;# … #&gt; y los renglones que empiezan con «#»): solo lo que hace.</summary>
    internal static string SinComentarios(string ps1) =>
        Regex.Replace(Regex.Replace(ps1, @"<#.*?#>", "", RegexOptions.Singleline), @"^\s*#.*$", "", RegexOptions.Multiline);

    internal static string ScriptSinComentarios => SinComentarios(Script);

    /// <summary>Una función de un .ps1, de su «function Nombre» hasta la que sigue.</summary>
    internal static string FuncionDelScript(string texto, string nombre)
    {
        var desde = Regex.Match(texto, $@"^function {Regex.Escape(nombre)}[ (]", RegexOptions.Multiline);
        Assert.True(desde.Success, $"No está «function {nombre}»");
        var hasta = Regex.Match(texto[(desde.Index + 1)..], "^function ", RegexOptions.Multiline);
        return hasta.Success ? texto.Substring(desde.Index, hasta.Index + 1) : texto[desde.Index..];
    }

    /// <summary>Una función o un procedimiento del [Code] del asistente, hasta su «end;».</summary>
    internal static string FuncionDelAsistente(string nombre)
    {
        var desde = Regex.Match(Codigo, $@"^(?:function|procedure) {Regex.Escape(nombre)}[;(: ]", RegexOptions.Multiline);
        Assert.True(desde.Success, $"No está «{nombre}» en el asistente");
        var texto = Codigo[desde.Index..];
        return texto[..(texto.IndexOf("\nend;", StringComparison.Ordinal) + 1)];
    }

    /// <summary>Que no falte ninguno; si falta, dice cuáles.</summary>
    internal static void Faltan(IEnumerable<string> faltan)
    {
        var lista = faltan.ToList();
        Assert.True(lista.Count == 0, $"Faltan: {string.Join(", ", lista)}");
    }

    internal static HashSet<string> Claves(string texto, string patron) =>
        Regex.Matches(texto, patron).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Las claves que dijo una orden del ayudante.</summary>
    private static HashSet<string> Nombres(ArchivoDeClaves salida) => salida.Datos.Select(d => d.Clave).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Que ese tramo lea algo con ese patrón, y que todo lo que lee esté en lo que se dice.</summary>
    internal static void LeeLoQueDice(string tramo, string patron, IReadOnlySet<string> dice)
    {
        var lee = Claves(tramo, patron);
        Assert.True(lee.Count > 0, $"No se encontró «{patron}»");
        Faltan(lee.Where(c => !dice.Contains(c)));
    }

    /// <summary>Los parámetros de un .ps1 de instalacion/ (su param(…)).</summary>
    private static HashSet<string> ParametrosDe(string archivo)
    {
        var bloque = Regex.Match(Leer("instalacion", archivo), @"^\s*param\s*\((.*?)\)\s*$", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(bloque.Success, $"{archivo} no tiene param(…)");
        return Claves(bloque.Groups[1].Value, @"\]\s*\$([A-Za-z]+)");
    }

    /// <summary>Auditoría del 2026-11-04: sin la caja, nadie quita de la cola lo ya impreso y Windows lo guarda para siempre.</summary>
    [Fact]
    public void Al_quitar_la_caja_su_cola_deja_de_conservar_los_documentos()
    {
        var script = Leer("instalacion", "instalar-caja.ps1");
        var quitar = script[script.IndexOf("if ($Quitar)", StringComparison.Ordinal)..script.IndexOf("$exe = Join-Path", StringComparison.Ordinal)];

        Assert.Contains("-KeepPrintedJobs $false", quitar);
        Assert.Contains("Remove-PrintJob", quitar);
    }

    /// <summary>
    /// Revisión de la rama junta (2026-11-06): la guía apaga «Conservar documentos impresos» mientras la caja no se usa; actualizar con el
    /// instalador lo volvía a prender aunque no se cambiara nada, y cada documento de cliente se quedaba en la cola.
    /// </summary>
    [Fact]
    public void Volver_a_correr_el_instalador_con_la_misma_cola_no_la_vuelve_a_prender()
    {
        var cola = Script[Script.IndexOf("$cola = [string]$r['cola']", StringComparison.Ordinal)..Script.IndexOf("# servicio.json y caja.json: solo lo que cambio", StringComparison.Ordinal)];

        var comparar = cola.IndexOf("$cola -ne $colaDeAntes", StringComparison.Ordinal);
        Assert.True(comparar >= 0, cola);
        Assert.Contains("ColaImpresion", cola[..comparar]);
        Assert.True(comparar < cola.IndexOf("-KeepPrintedJobs $true", StringComparison.Ordinal), cola);
    }

    /// <summary>Auditoría del instructivo: corrido parado en su carpeta, el borrado fallaba callado y decía «Caja quitada».</summary>
    [Fact]
    public void Al_quitar_la_caja_no_se_queda_parado_en_su_carpeta_ni_dice_que_la_quito_sin_quitarla()
    {
        var script = Leer("instalacion", "instalar-caja.ps1");
        var quitar = script[script.IndexOf("if ($Quitar)", StringComparison.Ordinal)..script.IndexOf("$exe = Join-Path", StringComparison.Ordinal)];

        var salir = quitar.IndexOf("Set-Location -LiteralPath $env:SystemRoot", StringComparison.Ordinal);
        var borrar = quitar.IndexOf("Remove-Item $destino", StringComparison.Ordinal);
        var revisar = quitar.LastIndexOf("Test-Path -LiteralPath $destino", StringComparison.Ordinal);
        var dice = quitar.IndexOf("Caja quitada", StringComparison.Ordinal);
        Assert.True(salir >= 0 && borrar > salir && revisar > borrar && dice > revisar, quitar);
    }

    /// <summary>Auditoría del 2026-11-04: por nombre de proceso, desinstalar la prueba también mataba al servicio de la tienda, que se llama igual.</summary>
    [Fact]
    public void Desinstalar_la_prueba_solo_cierra_el_servicio_de_su_carpeta()
    {
        var prueba = Leer("instalacion", "prueba.iss");

        Assert.DoesNotContain("/IM PapeleriaPos.Servicio.exe", prueba);
        Assert.Contains("$_.ExecutablePath -like '{app}\\*'", prueba);
    }

    /// <summary>
    /// Auditoría del 2026-11-04: una carpeta tomada (la caja abierta a media actualización) no se deja renombrar, y regresar lo que no cambió
    /// fallaba igual: terminaba en «NO VENDAS» sin haber tocado nada. Solo se regresa lo que dejó su lugar.
    /// </summary>
    [Fact]
    public void Actualizar_solo_regresa_la_carpeta_que_ya_cambio()
    {
        var script = Leer("instalacion", "actualizar.ps1");

        Assert.DoesNotContain("$script:cambiado.Caja = $true", script);
        Assert.DoesNotContain("$script:cambiado.Servicio = $true", script);
        Assert.True(script.IndexOf("Mover-Carpeta $destino $anterior", StringComparison.Ordinal) < script.IndexOf("$script:cambiado[$Marca] = $true", StringComparison.Ordinal));
        Assert.Contains("(Join-Path $paquete 'servicio') 'Servicio'", script);
        Assert.Contains("(Join-Path $paquete 'caja') 'Caja'", script);
    }

    /// <summary>
    /// Lo que el asistente deja en respuestas.txt (GuardarRespuestas) es todo lo que leen el ayudante y instalar-pc.ps1, también lo de la
    /// mudanza (diseno-v2, 3.3 h y la Enmienda 5), con los valores que cada uno entiende.
    /// </summary>
    [Fact]
    public void Las_respuestas_que_escribe_el_asistente_son_las_que_se_leen()
    {
        var escribe = Claves(FuncionDelAsistente("GuardarRespuestas"), @"Agregar\(Lineas, '([a-z-]+)'");
        string[] leeElAyudante = ["rol", "principal", "impresora", "cola", "lector", "lector-puerto", "lector-baudios", "controlprint",
            "controlprint-servidor", "eleventa", "eleventa-servidor", "eleventa-base", "eleventa-caja", "eleventa-cajero", "eleventa-gbak",
            "eleventa-respaldos", "mudanza", "mudanza-paquete", "mudanza-encima"];
        var leeElScript = Claves(Script, @"\$r\['([a-z-]+)'\]");

        Faltan(leeElAyudante.Where(c => !escribe.Contains(c)));
        Faltan(leeElScript.Where(c => !escribe.Contains(c)));
        Assert.Contains("cerrar-caja", leeElScript);
        // La mudanza: cuál, su carpeta, traerla encima de una tienda y cancelar aunque la USB diga que la base ya se trajo.
        foreach (var clave in new[] { "mudanza", "mudanza-paquete", "mudanza-encima", "mudanza-aunque-traida" })
            Assert.Contains(clave, leeElScript);
        // La lista de arriba es la que de verdad lee el ayudante (Respuestas.cs): ni una más ni una menos.
        var respuestasCs = Leer("src", "PapeleriaPos.Instalacion", "Respuestas.cs");
        var leeRespuestasCs = Claves(respuestasCs, @"(?:archivo\[|\(archivo, )""([a-z-]+)""");
        Assert.True(leeRespuestasCs.SetEquals(leeElAyudante), $"Respuestas.cs lee: {string.Join(", ", leeRespuestasCs.Order())}");

        // Las mudanzas que puede pedir el asistente las entienden el script y el ayudante.
        var pide = Claves(FuncionDelAsistente("MudanzaElegida"), @"Result := '([a-z-]+)'");
        Assert.True(pide.SetEquals(["sacar", "traer", "terminar", "cancelar"]), string.Join(", ", pide));
        var aceptaElScript = Claves(Regex.Match(Script, @"\$mudanza -notin @\(([^)]*)\)").Groups[1].Value, @"'([a-z-]+)'");
        var entiendeElAyudante = Claves(respuestasCs, @"""([a-z]+)"" => MudanzaPedida\.");
        Faltan(pide.Where(p => !aceptaElScript.Contains(p)));
        Faltan(pide.Where(p => !entiendeElAyudante.Contains(p)));
        // Las de sí o no: el asistente solo las escribe con «si», y así las compara el script.
        Assert.Contains("Agregar(Lineas, 'mudanza-encima', 'si')", Codigo);
        Assert.Contains("Agregar(Lineas, 'mudanza-aunque-traida', 'si')", Codigo);
        Assert.Contains("$r['mudanza-encima'] -eq 'si'", Script);
        Assert.Contains("([string]$r['mudanza-aunque-traida']).Trim() -ne 'si'", Script);

        // Y el ayudante entiende un archivo con todas ellas...
        var respuestas = new ArchivoDeClaves();
        foreach (var clave in leeElAyudante)
            respuestas.Poner(clave, "");
        respuestas.Poner("rol", "principal").Poner("lector", "teclado").Poner("controlprint", "no").Poner("eleventa", "no");
        Assert.Equal(RolDeLaPc.Principal, Respuestas.De(respuestas).Rol);
        // ... y las de cada mudanza como las guarda el asistente (con ellas corre «revisar» antes de cambiar nada): la carpeta también al
        // cancelar, y la respuesta que solo lee el script no le estorba.
        (string Rol, string Mudanza, (string Clave, string Valor)[] Mas)[] mudanzas =
        [
            ("unica", "sacar", [("mudanza-paquete", @"E:\mudanza")]),
            ("principal", "traer", [("mudanza-paquete", @"E:\mudanza"), ("mudanza-encima", "si")]),
            ("otra", "terminar", [("principal", "192.0.2.50"), ("mudanza-paquete", @"E:\mudanza")]),
            ("principal", "cancelar", [("mudanza-paquete", @"E:\mudanza"), ("mudanza-aunque-traida", "si")]),
        ];
        foreach (var (rol, mudanza, mas) in mudanzas)
        {
            var conMudanza = new ArchivoDeClaves().Poner("rol", rol).Poner("impresora", "").Poner("cola", "").Poner("lector", "teclado");
            if (rol != "otra")
                conMudanza.Poner("controlprint", "no").Poner("eleventa", "no");
            conMudanza.Poner("mudanza", mudanza);
            foreach (var (clave, valor) in mas)
                conMudanza.Poner(clave, valor);
            var (_, errores) = Respuestas.Revisar(conMudanza);
            Assert.True(errores.Count == 0, $"{mudanza}: {string.Join(" ", errores.Select(e => $"({e.Clave}) {e.Message}"))}");
        }
    }

    /// <summary>Lo que el asistente lee de esta PC (Valor(Actual, …)) lo dice el ayudante (leer), también lo de la mudanza.</summary>
    [Fact]
    public void Lo_que_el_asistente_lee_de_esta_pc_lo_dice_el_ayudante()
    {
        using var carpeta = new CarpetaDePrueba();
        var leido = LecturaDeLaPc.Leer(new EquipoConUnoDeCada(carpeta.Ruta));
        var dice = leido.Datos.Select(d => d.Clave).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pide = Claves(Codigo, @"Valor\(Actual, '([a-z0-9.-]+)'\)");
        Assert.NotEmpty(pide);
        Faltan(pide.Where(c => !dice.Contains(c)));
        // Las de las listas, con su número.
        foreach (var lista in new[] { "impresora.1", "impresora.1.aviso", "gbak.1", "gbak.1.version", "gbak.1.aviso" })
            Assert.Contains(lista, dice);
        Assert.Contains("'impresora.' + IntToStr(I) + '.aviso'", Codigo);
        Assert.Contains("'gbak.' + IntToStr(I) + '.version'", Codigo);
        // La mudanza (diseno-v2, 3.3 a): si esta PC está a media mudanza, de qué paquete, desde cuándo y a qué carpeta se sacó, y qué
        // paquete ya se puso aquí. Con la marca junto a la base, «sacada»: lo que el asistente toma por «a media mudanza».
        foreach (var clave in new[] { "mudanza", "mudanza-cuando", "mudanza-carpeta", "mudanza-paquete", "mudanza-puesta" })
            Assert.Contains(clave, pide);
        var conLaMarca = new PcDePrueba(Path.Combine(carpeta.Ruta, "con-la-marca")).ConServicio();
        conLaMarca.DejarLaMarca(@"E:\mudanza");
        Assert.Equal("sacada", LecturaDeLaPc.Leer(conLaMarca)["mudanza"]);
        Assert.Contains("Valor(Actual, 'mudanza') = 'sacada'", Codigo);
    }

    /// <summary>Lo que el asistente lee de cada paso (Valor(Paso1, …), Valor(Paso2, …)) lo escribe instalar-pc.ps1 en su resultado.</summary>
    [Fact]
    public void Lo_que_el_asistente_lee_de_cada_paso_lo_escribe_instalar_pc()
    {
        var escribe = Claves(Script, @"Resultado '([a-z-]+)'");
        // Revisar-Con escribe «$que» y «$que-mensaje» de eleventa y Control Print.
        foreach (var que in new[] { "eleventa", "controlprint" })
            escribe.UnionWith([que, $"{que}-mensaje"]);

        var pide = Claves(Codigo, @"Valor\(Paso[12], '([a-z-]+)'\)");
        Assert.NotEmpty(pide);
        Faltan(pide.Where(c => !escribe.Contains(c)));
        Assert.Contains("Resultado \"$que-mensaje\"", Script);
        // Lo de la mudanza (diseno-v2, 3.4 y 3.5): cómo quedó y su mensaje, el paquete, sus avisos, el alta, la base apartada y de qué PC viene.
        foreach (var clave in new[] { "mudanza", "mudanza-mensaje", "mudanza-paquete", "mudanza-avisos", "mudanza-alta", "mudanza-apartada", "mudanza-equipo" })
            Assert.Contains(clave, pide);
        // Y al revés: cada clave de la mudanza que dice el script la lee el asistente. «mudanza-cuando» (al sacar) solo queda dicha: «Cómo
        // quedó» no enseña la hora.
        var deLaMudanza = Claves(ScriptSinComentarios, @"Resultado '(mudanza[a-z-]*)'");
        Faltan(deLaMudanza.Where(c => c != "mudanza-cuando" && !pide.Contains(c)));
    }

    /// <summary>
    /// Cómo quedó la mudanza (diseno-v2, 3.4 y 3.5): cada estado que instalar-pc.ps1 dice en «mudanza» y en «mudanza-alta» lo entiende el
    /// asistente («Cómo quedó», la última página, el código de salida), el asistente no espera uno que el script no diga, y lo demás que
    /// pasa de uno al otro (el servicio «mudada», los avisos del paquete) va como lo lee el otro.
    /// </summary>
    [Fact]
    public void Cada_estado_de_la_mudanza_que_dice_instalar_pc_lo_entiende_el_asistente()
    {
        string[] estados = ["sacada", "traida", "ya-estaba", "convertida", "cancelada", "fallo", "a-medias"];
        var script = ScriptSinComentarios;
        // Lo que dice: tal cual en su resultado, al no traer la base (No-Se-Trajo) y, al traerla, lo que regresa Mudanza-Traer.
        var dice = Claves(script, @"Resultado 'mudanza' '([a-z-]+)'");
        dice.UnionWith(Claves(script, @"No-Se-Trajo '([a-z-]+)'"));
        dice.UnionWith(Claves(FuncionDelScript(script, "Mudanza-Traer"), @"return '([a-z-]+)'"));
        Assert.Contains("Resultado 'mudanza' $traida", script);
        Assert.True(dice.SetEquals(estados), $"instalar-pc.ps1 dice: {string.Join(", ", dice.Order())}");
        var entiende = Claves(Codigo, @"Quedo = '([a-z-]+)'");
        Assert.True(entiende.SetEquals(estados), $"El asistente entiende: {string.Join(", ", entiende.Order())}");

        // Con qué alta queda la caja al terminar.
        var altas = Claves(FuncionDelScript(script, "Mudanza-Terminar"), @"\$alta = '([a-z-]+)'");
        Assert.True(altas.SetEquals(["se-conserva", "no-habia", "otra-vez"]), string.Join(", ", altas));
        Assert.True(Claves(Codigo, @"Alta = '([a-z-]+)'").SetEquals(altas), "El asistente no distingue las mismas altas que dice el script.");

        // Con la marca, el servicio está detenido a propósito; y los avisos del paquete van juntos con « | ».
        Assert.Contains("Resultado 'servicio' 'mudada'", script);
        Assert.Contains("Valor(Paso2, 'servicio') = 'mudada'", Codigo);
        Assert.Contains("Resultado 'mudanza-avisos' ($avisos -join ' | ')", script);
        Assert.Contains("AvisosEnRenglones(Valor(Paso1, 'mudanza-avisos'))", Codigo);
        Assert.Contains("Dividir(Unidos, ' | ', Lista)", FuncionDelAsistente("AvisosEnRenglones"));
    }

    /// <summary>
    /// «Terminar» (diseno-v2, 3.4): lo que instalar-pc.ps1 le pide al ayudante, tal como lo escribe, lo entiende el ayudante de verdad
    /// (probar --esta-caja dice si la principal conoce la llave de esta caja; escribir --mudada-a deja la caja hablándole a la principal,
    /// con su llave pública y, con --alta otra-vez, sin su alta), y lo que lee de esas salidas lo dicen. Los valores de «conoce» que el
    /// script distingue son los que dice el ayudante. Y lo que lee de «probar» en el paso revisar, como siempre.
    /// </summary>
    [Fact]
    public async Task Al_terminar_lo_que_instalar_pc_le_pide_al_ayudante_lo_entiende_y_lo_que_lee_lo_dice()
    {
        using var carpeta = new CarpetaDePrueba();
        var pc = new PcDePrueba(carpeta.Ruta) { NombreDeLaPc = "CAJA2" };
        var salida = Path.Combine(carpeta.Ruta, "salida.txt");
        var script = ScriptSinComentarios;
        var terminar = FuncionDelScript(script, "Mudanza-Terminar");

        // 1. probar --esta-caja: sin alta y sin nadie en la red, con todas sus claves.
        Assert.Contains("Ayudante @('probar', '--direccion', $direccion, '--esta-caja', 'si')", terminar);
        Assert.Equal(0, await Ayudante.CorrerAsync(["probar", "--direccion", "192.0.2.50", "--esta-caja", "si", "--salida", salida], pc, RedDePrueba.SinNadie()));
        var probado = ArchivoDeClaves.Leer(salida);
        Assert.Equal("sin-alta", probado["conoce"]);
        LeeLoQueDice(terminar, @"\$dijo\['([a-z-]+)'\]", Nombres(probado));
        Assert.Contains("conoce-motivo", Claves(terminar, @"\$dijo\['([a-z-]+)'\]"));
        var distingue = Claves(terminar, @"\$conoce -eq '([a-z-]+)'");
        var conoce = Enum.GetValues<ConoceALaCaja>().Select(c => new PruebaDeIdentidad(c, null).Nombre).ToHashSet();
        Assert.True(distingue.SetEquals(conoce), $"instalar-pc.ps1 distingue {string.Join(", ", distingue.Order())}; el ayudante dice {string.Join(", ", conoce.Order())}");

        // 2. escribir --mudada-a, con la llave pública del servicio de allá y --alta otra-vez, sobre una caja dada de alta.
        Assert.Contains("$argumentos = @('escribir', '--respuestas', $Respuestas, '--mudada-a', $direccion)", terminar);
        Assert.Contains("$argumentos += @('--llave-del-servicio', [string]$dijo['llave-del-servicio'])", terminar);
        Assert.Contains("$argumentos += @('--alta', 'otra-vez')", terminar);
        Assert.Contains("$convertida = Ayudante $argumentos", terminar);
        var cajaJson = CajaJson.Ruta(pc.Datos);
        Directory.CreateDirectory(Path.GetDirectoryName(cajaJson)!);
        File.WriteAllText(cajaJson, """
            { "Servicio": "http://localhost:5080/", "CajaId": "5a1d4c3e-0000-4000-8000-000000000002", "Impresora": "",
              "LlaveProtegida": "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA", "LlaveDelServicio": "LA-DE-ANTES", "DireccionDeConfianza": "http://localhost:5080/" }
            """);
        var respuestas = Path.Combine(carpeta.Ruta, "respuestas.txt");
        new ArchivoDeClaves().Poner("rol", "otra").Poner("principal", "192.0.2.50").Poner("impresora", "").Poner("cola", "").Poner("lector", "teclado")
            .Poner("mudanza", "terminar").Guardar(respuestas);
        using var deAlla = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var llave = Convert.ToBase64String(deAlla.ExportSubjectPublicKeyInfo());

        Assert.Equal(0, await Ayudante.CorrerAsync(
            ["escribir", "--respuestas", respuestas, "--mudada-a", "192.0.2.50", "--llave-del-servicio", llave, "--alta", "otra-vez", "--salida", salida], pc,
            RedDePrueba.SinNadie()));

        var convertida = ArchivoDeClaves.Leer(salida);
        Assert.Equal("", convertida["error"]);
        var caja = JsonNode.Parse(File.ReadAllText(cajaJson))!;
        Assert.Contains("192.0.2.50", (string?)caja["Servicio"]);
        Assert.Equal(llave, (string?)caja["LlaveDelServicio"]);
        Assert.Null((string?)caja["CajaId"]);
        LeeLoQueDice(terminar, @"\$convertida\.Claves\['([a-z-]+)'\]", Nombres(convertida));

        // 3. En el paso revisar de «Otra caja», probar a secas.
        var revisar = FuncionDelScript(script, "Revisar");
        Assert.Contains("$probado = Ayudante @('probar', '--direccion', [string]$r['principal'])", revisar);
        LeeLoQueDice(revisar, @"\$probado\.Claves\['([a-z-]+)'\]", Nombres(await Ayudante.ProbarAsync("192.0.2.50", RedDePrueba.SinNadie())));
    }

    /// <summary>
    /// «Cancelar» (diseno-v2, 3.4 y la Enmienda 5): mudanza-viva, tal como la pide instalar-pc.ps1 (la tienda de la marca y las IP del
    /// traida.txt de este paquete, unidas con «;»), la entiende el ayudante de verdad, lo que lee de su salida lo dice, y de «viva» solo
    /// distingue lo que dice: «si» (no se cancela) y «no» (se cancela).
    /// </summary>
    [Fact]
    public async Task Al_cancelar_lo_que_instalar_pc_le_pide_al_ayudante_lo_entiende_y_lo_que_lee_lo_dice()
    {
        using var carpeta = new CarpetaDePrueba();
        var pc = new PcDePrueba(carpeta.Ruta);
        var salida = Path.Combine(carpeta.Ruta, "salida.txt");
        var cancelar = FuncionDelScript(ScriptSinComentarios, "Mudanza-Cancelar");
        Assert.Contains("$argumentos = @('mudanza-viva', '--tienda', $marca.Tienda)", cancelar);
        Assert.Contains("$argumentos += @('--direcciones', ($ips -join ';'))", cancelar);
        Assert.Contains("$viva = Ayudante $argumentos", cancelar);
        // Sin preguntar en la red de verdad: ahí no contesta nadie.
        static Task<IReadOnlyList<Uri>> NadieEnLaRed(TimeSpan _) => Task.FromResult<IReadOnlyList<Uri>>([]);

        // La segunda IP del traida.txt contesta con la tienda: está viva.
        var red = new RedDePrueba().Contesta("192.0.2.61:5080", RedDePrueba.Estado(["Matriz"]));
        Assert.Equal(0, await Ayudante.CorrerAsync(["mudanza-viva", "--tienda", "Matriz", "--direcciones", "192.0.2.60;192.0.2.61", "--salida", salida], pc, red,
            RespuestasDePrueba.SinEsperar, NadieEnLaRed));
        var viva = ArchivoDeClaves.Leer(salida);
        Assert.Equal(("si", "192.0.2.61"), (viva["viva"], viva["donde"]));
        Assert.Contains("192.0.2.60:5080", red.Preguntadas);
        LeeLoQueDice(cancelar, @"\$dijo\['([a-z-]+)'\]", Nombres(viva));
        // Nadie con esa tienda: «no», con lo único que el script cancela.
        Assert.Equal(0, await Ayudante.CorrerAsync(["mudanza-viva", "--tienda", "Matriz", "--salida", salida], pc, RedDePrueba.SinNadie(), RespuestasDePrueba.SinEsperar,
            NadieEnLaRed));
        Assert.Equal("no", ArchivoDeClaves.Leer(salida)["viva"]);
        var distingue = Claves(cancelar, @"\$dijo\['viva'\] -(?:eq|ne) '([a-z-]+)'");
        Assert.True(distingue.SetEquals(["si", "no"]), string.Join(", ", distingue));
    }

    /// <summary>
    /// «Traer» (diseno-v2, 3.4): mudanza-revisar (con la versión del programa que trae el instalador), escribir --mudanza y leer, tal como
    /// los pide instalar-pc.ps1, los entiende el ayudante de verdad, y lo que lee de sus salidas lo dicen.
    /// </summary>
    [Fact]
    public async Task Al_traer_lo_que_instalar_pc_le_pide_al_ayudante_lo_entiende_y_lo_que_lee_lo_dice()
    {
        using var carpeta = new CarpetaDePrueba();
        var pc = new PcDePrueba(Path.Combine(carpeta.Ruta, "principal"));
        var usb = Path.Combine(carpeta.Ruta, "usb", "mudanza");
        var paquete = PaqueteDePrueba.En(usb);
        var respuestas = Path.Combine(carpeta.Ruta, "respuestas.txt");
        RespuestasDePrueba.ParaTraer(usb, RespuestasDePrueba.Gbak(carpeta.Ruta)).Guardar(respuestas);
        var salida = Path.Combine(carpeta.Ruta, "salida.txt");
        var script = ScriptSinComentarios;
        var instalar = FuncionDelScript(script, "Instalar");

        // 1. mudanza-revisar. Con un programa más viejo que el que sacó el paquete no se trae: así se ve que lee la versión.
        Assert.Contains("Ayudante @('mudanza-revisar', '--paquete', $carpetaDelPaquete, '--respuestas', $Respuestas, '--version', $versionDelPaquete)", instalar);
        string[] revisar = ["mudanza-revisar", "--paquete", usb, "--respuestas", respuestas, "--version", paquete["version"], "--salida", salida];
        Assert.Equal(0, await Ayudante.CorrerAsync(revisar, pc, RedDePrueba.SinNadie(), RespuestasDePrueba.SinEsperar));
        var revisado = ArchivoDeClaves.Leer(salida);
        Assert.Equal("si", revisado["bien"]);
        LeeLoQueDice(instalar, @"\$revisado\.Claves\['([a-z-]+)'\]", Nombres(revisado));
        Assert.Equal(2, await Ayudante.CorrerAsync([.. revisar[..6], "1.0.100", .. revisar[7..]], pc, RedDePrueba.SinNadie(), RespuestasDePrueba.SinEsperar));
        Assert.Contains("1.0.180", ArchivoDeClaves.Leer(salida)["error"]);

        // 2. escribir --mudanza, sobre la plantilla: «Escribir» solo lo pone el paquete (es el único camino), así se ve que lo leyó.
        Assert.Contains("$argumentosDeEscribir = @('escribir', '--respuestas', $Respuestas)", instalar);
        Assert.Contains("$argumentosDeEscribir += @('--mudanza', $carpetaDelPaquete)", instalar);
        Assert.Contains("$escrito = Ayudante $argumentosDeEscribir", instalar);
        var servicioJson = Path.Combine(pc.Datos, ServicioJson.Archivo);
        Directory.CreateDirectory(pc.Datos);
        File.Copy(Path.Combine(Raiz, "instalacion", "plantilla-servicio.json"), servicioJson);
        Assert.Equal(0, await Ayudante.CorrerAsync(["escribir", "--respuestas", respuestas, "--mudanza", usb, "--salida", salida], pc, RedDePrueba.SinNadie()));
        var escrito = ArchivoDeClaves.Leer(salida);
        Assert.Equal(("", "cambio"), (escrito["error"], escrito["servicio-json"]));
        Assert.Matches(@"""Escribir""\s*:\s*true", File.ReadAllText(servicioJson));
        LeeLoQueDice(instalar, @"\$escrito\.Claves\['([a-z-]+)'\]", Nombres(escrito));

        // 3. leer, al cerrar la traída: las IP de esta PC, para el traida.txt.
        var cerrar = FuncionDelScript(script, "Mudanza-Cerrar-La-Traida");
        Assert.Contains("$leido = Ayudante @('leer')", cerrar);
        Assert.Equal(0, await Ayudante.CorrerAsync(["leer", "--salida", salida], pc, RedDePrueba.SinNadie()));
        LeeLoQueDice(cerrar, @"\$leido\.Claves\['([a-z-]+)'\]", Nombres(ArchivoDeClaves.Leer(salida)));
    }

    /// <summary>
    /// «La base de la tienda» (diseno-v2, 3.5): las órdenes con que el asistente ve y revisa un paquete, tal como las escribe, las entiende
    /// el ayudante (con --version, el de un programa más nuevo no se trae); lo que lee de sus salidas lo dicen; y el único error con
    /// pregunta, «Esta PC ya tiene una tienda» (clave «mudanza»), lo reconoce por el texto que de verdad dice el ayudante, y de ahí saca la
    /// tienda de esta PC.
    /// </summary>
    [Fact]
    public async Task Lo_que_el_asistente_le_pide_al_ayudante_del_paquete_lo_entiende_y_lo_que_lee_lo_dice()
    {
        using var carpeta = new CarpetaDePrueba();
        var usb = Path.Combine(carpeta.Ruta, "usb", "mudanza");
        var paquete = PaqueteDePrueba.En(usb);
        var salida = Path.Combine(carpeta.Ruta, "salida.txt");
        var pc = new PcDePrueba(Path.Combine(carpeta.Ruta, "principal"));

        // mudanza-ver: cada clave que lee del paquete (también las de cada caja y cada aviso) y el estado que compara.
        Assert.Contains("CorrerAyudante('mudanza-ver --paquete ' + EntreComillas(Carpeta) + ' --version \"' + VersionDelInstalador + '\"'", Codigo);
        Assert.Equal(0, await Ayudante.CorrerAsync(["mudanza-ver", "--paquete", usb, "--version", paquete["version"], "--salida", salida], pc));
        var visto = ArchivoDeClaves.Leer(salida);
        Assert.Equal("bien", visto["paquete"]);
        var dice = Nombres(visto);
        var pide = Claves(Codigo, @"Valor\(Paquete, '([a-z0-9.-]+)'\)");
        Assert.True(pide.Count >= 10, string.Join(", ", pide));
        Faltan(pide.Where(c => !dice.Contains(c)));
        var deCadaCaja = Claves(Codigo, @"Valor\(Paquete, 'caja\.' \+ IntToStr\(I\) \+ '\.([a-z-]+)'\)");
        Assert.NotEmpty(deCadaCaja);
        Faltan(deCadaCaja.Select(dato => $"caja.1.{dato}").Where(c => !dice.Contains(c)));
        Assert.Contains("Valor(Paquete, 'aviso.' + IntToStr(I))", Codigo);
        Assert.Contains("aviso.1", dice);
        var estados = Claves(Leer("src", "PapeleriaPos.Instalacion", "PaqueteDeMudanza.cs"), @"EstadoDelPaquete\.\w+ => ""([a-z-]+)""");
        var compara = Claves(Codigo, @"Valor\(Paquete, 'paquete'\) (?:=|<>) '([a-z-]+)'");
        Assert.NotEmpty(compara);
        Faltan(compara.Where(e => !estados.Contains(e)));
        Assert.Equal(0, await Ayudante.CorrerAsync(["mudanza-ver", "--paquete", usb, "--version", "1.0.100", "--salida", salida], pc));
        Assert.Equal("otra-version", ArchivoDeClaves.Leer(salida)["paquete"]);

        // mudanza-revisar, en una PC que ya tiene el servicio con su tienda: el único error con pregunta.
        Assert.Contains("CorrerAyudante('mudanza-revisar --paquete ' + EntreComillas(CarpetaDelPaquete) + ' --respuestas \"' + RespuestasArchivo + '\" --version \"'",
            Codigo);
        var conTienda = new PcDePrueba(Path.Combine(carpeta.Ruta, "con-tienda")).ConServicio();
        var respuestas = Path.Combine(carpeta.Ruta, "respuestas.txt");
        RespuestasDePrueba.ParaTraer(usb, RespuestasDePrueba.Gbak(carpeta.Ruta)).Guardar(respuestas);
        var red = new RedDePrueba().Contesta(RedDePrueba.EstaPc, RedDePrueba.Estado(["Centro"]));
        Assert.Equal(2, await Ayudante.CorrerAsync(["mudanza-revisar", "--paquete", usb, "--respuestas", respuestas, "--version", paquete["version"], "--salida", salida],
            conTienda, red, RespuestasDePrueba.SinEsperar));
        var revisado = ArchivoDeClaves.Leer(salida);
        Assert.Equal("mudanza", revisado["clave"]);
        var revisar = FuncionDelAsistente("RevisarElPaquete");
        Assert.Contains("(Clave <> 'mudanza')", revisar);
        LeeLoQueDice(revisar, @"Valor\(Salida, '([a-z-]+)'\)", Nombres(revisado));
        var reconoce = Regex.Match(revisar, @"Pos\('([^']+)', Lowercase\(Texto\)\)").Groups[1].Value;
        Assert.NotEqual("", reconoce);
        var error = revisado["error"]!;
        Assert.Contains(reconoce, error.ToLowerInvariant());
        Assert.Contains(reconoce, RevisionDeLaMudanza.YaTieneUnaTienda.ToLowerInvariant());
        // TiendaDeEstaPc: después de ese texto, « (», la tienda y «): la base de ».
        var tienda = FuncionDelAsistente("TiendaDeEstaPc");
        Assert.Contains($"Pos('{reconoce}', Lowercase(Dicho))", tienda);
        Assert.Contains("Copy(Resto, 1, 2) <> ' ('", tienda);
        var cierra = Regex.Match(tienda, @"Pos\('([^']+)', Resto\)").Groups[1].Value;
        Assert.NotEqual("", cierra);
        var resto = error[(error.IndexOf(reconoce, StringComparison.OrdinalIgnoreCase) + reconoce.Length)..];
        Assert.StartsWith(" (", resto);
        Assert.True(resto.IndexOf(cierra, StringComparison.Ordinal) > 2, error);
        Assert.Equal("Centro", resto[2..resto.IndexOf(cierra, StringComparison.Ordinal)]);
    }

    /// <summary>
    /// Las órdenes y opciones que el asistente e instalar-pc.ps1 le dan al ayudante, al exe del servicio y a los otros scripts las conoce
    /// quien las recibe. El ayudante calla las opciones que no conoce (un nombre cambiado de un lado solo se vería en Windows, o el sábado);
    /// a un script, un parámetro que no tiene lo detiene.
    /// </summary>
    [Fact]
    public void Las_ordenes_y_opciones_que_se_dan_las_conoce_quien_las_recibe()
    {
        var ayudante = Directory.GetFiles(Path.Combine(Raiz, "src", "PapeleriaPos.Instalacion"), "*.cs").Select(File.ReadAllText).ToList();
        var ordenes = Claves(Leer("src", "PapeleriaPos.Instalacion", "Ayudante.cs"), @"case ""([a-z-]+)""");
        var opciones = ayudante.SelectMany(texto => Claves(texto, @"opciones\.(?:TryGetValue|GetValueOrDefault|ContainsKey)\(""([a-z-]+)""")).ToHashSet();
        var delServicio = Claves(Leer("src", "PapeleriaPos.Servicio", "MudanzaDeLaBase.cs"), @"args\[i\] == ""--([a-z-]+)""");
        Assert.True(ordenes.Count >= 8, string.Join(", ", ordenes));
        Assert.True(opciones.Count >= 14, string.Join(", ", opciones));
        Assert.True(delServicio.SetEquals(["carpeta", "salida"]), string.Join(", ", delServicio));

        // El asistente: cada CorrerAyudante('<orden> --opción …', …).
        var delAsistente = Regex.Matches(Codigo, @"CorrerAyudante\('([a-z-]+)((?:(?!ExpandConstant).)*)", RegexOptions.Singleline);
        Assert.True(delAsistente.Count >= 6, $"{delAsistente.Count} órdenes del asistente");
        foreach (Match orden in delAsistente)
        {
            Assert.Contains(orden.Groups[1].Value, ordenes);
            Faltan(Claves(orden.Groups[2].Value, "--([a-z-]+)").Where(o => !opciones.Contains(o)).Select(o => $"--{o} ({orden.Groups[1].Value})"));
        }

        // instalar-pc.ps1: cada orden que le da al ayudante, y cada «--opción», al ayudante o al exe del servicio (mudanza-sacar).
        var script = ScriptSinComentarios;
        var delScript = Claves(script, @"(?:Ayudante|\$argumentos(?:DeEscribir)? =) @\('([a-z-]+)'");
        Assert.True(delScript.SetEquals(["probar", "escribir", "mudanza-viva", "mudanza-revisar", "leer"]), string.Join(", ", delScript));
        Faltan(delScript.Where(o => !ordenes.Contains(o)));
        Faltan(Claves(script, "'--([a-z-]+)'").Where(o => !opciones.Contains(o) && !delServicio.Contains(o)).Select(o => $"--{o}"));
        // Al exe del servicio, su verbo (Program.cs le pasa lo de después) y, como al ayudante, --salida (Con-Salida).
        Assert.Contains($"Con-Salida $exe @('{MudanzaDeLaBase.Verbo}', '--carpeta', $carpeta)", script);
        Assert.Contains("& $exe @argumentos --salida $salida", FuncionDelScript(script, "Con-Salida"));
        Assert.Contains("args is [MudanzaDeLaBase.Verbo, .. var mudanzaSacar]", Leer("src", "PapeleriaPos.Servicio", "Program.cs"));

        // Los otros scripts: cada «-Parámetro» que les pasa instalar-pc.ps1 está en su param(…) (restaurar.ps1 -PorMudanza, al traer).
        var llamadas = Regex.Matches(script, @"Correr \(Join-Path \$(?:Paquete|PSScriptRoot) '(?:[a-z]+\\)?([a-z-]+\.ps1)'\) @\(([^)]*)\)");
        Assert.True(llamadas.Count >= 2, $"{llamadas.Count} llamadas a otros scripts");
        foreach (Match llamada in llamadas)
        {
            var suyos = ParametrosDe(llamada.Groups[1].Value);
            Faltan(Claves(llamada.Groups[2].Value, "'-([A-Za-z]+)'").Where(p => !suyos.Contains(p)).Select(p => $"-{p} ({llamada.Groups[1].Value})"));
        }
        Assert.Contains("'-PorMudanza'", script);
        Assert.Contains("PorMudanza", ParametrosDe("restaurar.ps1"));
        Assert.Contains("$argumentos += '-SinBuscarEnLaRed'", script);
        Assert.Contains("SinBuscarEnLaRed", ParametrosDe("instalar-servicio.ps1"));
        // Y el asistente a instalar-pc.ps1.
        var deInstalarPc = ParametrosDe("instalar-pc.ps1");
        var alScript = Regex.Matches(Codigo, @"instalar-pc\.ps1'\)\s*\+\s*'""((?:(?!ewWaitUntilTerminated).)*)", RegexOptions.Singleline);
        Assert.Equal(2, alScript.Count);
        var leDa = alScript.SelectMany(m => Claves(m.Groups[1].Value, @" -([A-Z][A-Za-z]+)")).ToHashSet();
        leDa.UnionWith(Claves(Codigo, @"Mas := ' -([A-Z][A-Za-z]+)'"));
        Assert.True(leDa.IsSupersetOf(["Paso", "Respuestas", "Paquete", "Ayudante", "Registro", "Resultado", "Reiniciar", "BorrarDatos"]), string.Join(", ", leDa));
        Faltan(leDa.Where(p => !deInstalarPc.Contains(p)).Select(p => $"-{p}"));
    }

    /// <summary>
    /// Los avisos de la mudanza en archivos (diseno-v2, 3.1 y las Enmiendas 1 y 5): traida.txt (lo deja en el paquete la PC que trajo la
    /// base; lo leen el asistente, instalar-pc.ps1 y el ayudante en la PC de donde salió), mudanza-puesta.txt (lo deja instalar-pc.ps1
    /// junto a la base que trajo; lo lee el ayudante) y los nombres de la marca y de la nota de «sacando»: lo que uno escribe es lo que los
    /// demás leen, con el mismo nombre de archivo. El traida.txt de mentiras del CI es como el de verdad.
    /// </summary>
    [Fact]
    public void Los_avisos_de_la_mudanza_en_archivos_los_escribe_uno_como_los_leen_los_demas()
    {
        using var carpeta = new CarpetaDePrueba();
        var script = ScriptSinComentarios;

        // traida.txt, como lo escribe instalar-pc.ps1 al cerrar la traída.
        var cerrar = FuncionDelScript(script, "Mudanza-Cerrar-La-Traida");
        Assert.Contains($"$traida = En-La-Carpeta $carpeta '{PaqueteDeMudanza.ArchivoDeTraida}'", cerrar);
        Assert.Contains($"'{PaqueteDeMudanza.ArchivoDeTraida}'", FuncionDelScript(script, "Traida-De"));
        Assert.Contains($"'{PaqueteDeMudanza.ArchivoDeTraida}'", FuncionDelAsistente("LaTraida"));
        var escribe = Claves(cerrar, @"(?m)^\s*[""']([a-z-]+)=");
        Assert.True(escribe.SetEquals(["paquete", "equipo", "ips", "puerto", "cuando"]), string.Join(", ", escribe));
        var leeElScript = Claves(FuncionDelScript(script, "Traida-De"), @"\$claves\['([a-z-]+)'\]");
        leeElScript.UnionWith(Claves(FuncionDelScript(script, "Se-Trajo-Alla"), @"\$traida\['([a-z-]+)'\]"));
        leeElScript.UnionWith(Claves(FuncionDelScript(script, "A-Donde-Se-Trajo") + FuncionDelScript(script, "Mudanza-Cancelar"), @"\$traida\.Claves\['([a-z-]+)'\]"));
        var leeElAsistente = Claves(FuncionDelAsistente("LaTraida") + FuncionDelAsistente("ConfirmarCancelar"), @"Valor\(Lineas, '([a-z-]+)'\)");
        var leeElAyudante = Claves(Leer("src", "PapeleriaPos.Instalacion", "PaqueteDeMudanza.cs") + Leer("src", "PapeleriaPos.Instalacion", "BaseVivaEnOtraPc.cs"),
            @"(?:traida\[|Dato\(traida, )""([a-z-]+)""");
        foreach (var (quien, lee) in new[] { ("instalar-pc.ps1", leeElScript), ("el asistente", leeElAsistente), ("el ayudante", leeElAyudante) })
        {
            Assert.True(lee.Count >= 3, $"{quien} lee: {string.Join(", ", lee)}");
            Faltan(lee.Where(c => !escribe.Contains(c)).Select(c => $"{c} ({quien})"));
        }
        var deMentiras = FuncionDelScript(SinComentarios(Leer(".github", "instalacion", "mudanza.ps1")), "Dejar-Traida-De-Mentiras");
        Assert.True(Claves(deMentiras, @"(?m)^\s*[""']([a-z-]+)=").SetEquals(escribe), deMentiras);
        // El ayudante lo entiende como lo escribe el script (UTF-8 con BOM, un comentario primero): con él, el paquete ya se trajo.
        var usb = Path.Combine(carpeta.Ruta, "usb", "mudanza");
        var paquete = PaqueteDePrueba.En(usb);
        File.Delete(paquete.RutaDeLaBase);
        var valores = new Dictionary<string, string>
        {
            ["paquete"] = PaqueteDePrueba.Id, ["equipo"] = "PRINCIPAL", ["ips"] = "192.0.2.50;198.51.100.9", ["puerto"] = "5080", ["cuando"] = "2026-11-14 08:40",
        };
        File.WriteAllLines(Path.Combine(usb, PaqueteDeMudanza.ArchivoDeTraida),
            ["# Papeleria POS: la base de este paquete de mudanza ya se trajo a esta PC (instalar-pc.ps1).", .. escribe.Select(c => $"{c}={valores[c]}")],
            new UTF8Encoding(true));
        var visto = PaqueteDeMudanza.Ver(usb, "1.0.180");
        Assert.Equal(EstadoDelPaquete.YaTraido, visto.Estado);
        Assert.Contains("PRINCIPAL", visto.Error);
        Assert.Contains("2026-11-14 08:40", visto.Error);
        Assert.Equal(["192.0.2.50", "198.51.100.9"], ResumenDeMudanza.Direcciones(PaqueteDeMudanza.Traida(usb, PaqueteDePrueba.Id)!["ips"]).Select(ip => ip.ToString()));

        // mudanza-puesta.txt, como lo deja instalar-pc.ps1 en cuanto la base del paquete ya es la de esta PC: el ayudante sabe de qué paquete es.
        Assert.Contains($"$notaDePuesta = Join-Path $datos '{NotaDeLaBasePuesta.NombreDelArchivo}'", script);
        var enLaNota = Claves(FuncionDelScript(script, "Dejar-Nota-De-Puesta"), @"(?m)^\s*""([a-z0-9-]+)=");
        Assert.True(enLaNota.SetEquals(["paquete", "equipo", "archivo", "sha256", "cuando"]), string.Join(", ", enLaNota));
        var principal = new PcDePrueba(Path.Combine(carpeta.Ruta, "principal"));
        Directory.CreateDirectory(principal.Datos);
        File.WriteAllLines(NotaDeLaBasePuesta.Ruta(principal.Datos),
            ["# Papeleria POS: la base de este paquete de mudanza ya se puso en esta PC (instalar-pc.ps1)", .. enLaNota.Select(c => c == "paquete" ? $"paquete={PaqueteDePrueba.Id}" : $"{c}=x")],
            new UTF8Encoding(true));
        Assert.Equal(PaqueteDePrueba.Id, LecturaDeLaPc.Leer(principal)["mudanza-puesta"]);

        // La marca y la nota de «sacando»: el mismo nombre en cada pieza (instalar-pc.ps1 deja la nota; restaurar.ps1 y actualizar.ps1 la quitan).
        Assert.Contains($"$marcaDeMudanza = Join-Path $datos '{MarcaDeMudanza.NombreDelArchivo}'", script);
        Assert.Contains($@"\PapeleriaPos\{MarcaDeMudanza.NombreDelArchivo}')", Asistente);
        Assert.Contains("$notaDeSacando = Join-Path $datos 'mudanza-sacando.txt'", script);
        foreach (var otro in new[] { "restaurar.ps1", "actualizar.ps1" })
        {
            var texto = Leer("instalacion", otro);
            Assert.Contains($"(Join-Path $datos '{MarcaDeMudanza.NombreDelArchivo}')", texto);
            Assert.Contains("Remove-Item -LiteralPath (Join-Path $datos 'mudanza-sacando.txt')", texto);
        }
    }

    /// <summary>Lo que el CI lee del resultado.txt del asistente (también el de cada mudanza) lo escribe el asistente.</summary>
    [Fact]
    public void Lo_que_el_ci_lee_del_resultado_lo_escribe_el_asistente()
    {
        var escribe = Claves(FuncionDelAsistente("Terminar") + FuncionDelAsistente("GuardarResultadoSinCambios"), @"Agregar\(Lineas, '([a-z-]+)'");
        var lee = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archivo in Directory.GetFiles(Path.Combine(Raiz, ".github", "instalacion"), "*.ps1").Append(Path.Combine(Raiz, ".github", "workflows", "prueba.yml")))
            lee.UnionWith(Claves(File.ReadAllText(archivo), @"\$resultado\['([a-z-]+)'\]"));
        foreach (var clave in new[] { "mudanza", "mudanza-alta", "mudanza-paquete", "mudanza-mensaje", "motivo", "programa" })
            Assert.Contains(clave, lee);
        Faltan(lee.Where(c => !escribe.Contains(c)));
    }

    /// <summary>Lo que el asistente lee de «buscar» y «probar» lo dice el ayudante.</summary>
    [Fact]
    public async Task Lo_que_el_asistente_lee_al_buscar_y_al_probar_lo_dice_el_ayudante()
    {
        var probado = (await Ayudante.ProbarAsync("127.0.0.1:1")).Datos.Select(d => d.Clave).ToHashSet();
        foreach (var clave in new[] { "contesta", "texto", "error", "aviso" })
        {
            Assert.Contains(clave, probado);
            Assert.Contains($"Valor(Salida, '{clave}')", Codigo);
        }
        var buscado = await Ayudante.BuscarAsync(TimeSpan.FromMilliseconds(300), destinos: []);
        Assert.Equal("0", buscado["encontradas"]);
        Assert.Contains("Valor(Salida, 'encontradas')", Codigo);
        Assert.Contains("'encontrada.' + IntToStr(I) + '.texto'", Codigo);
        Assert.Contains("'encontrada.' + IntToStr(I) + '.aviso'", Codigo);
        Assert.Contains(".Poner($\"encontrada.{i + 1}.aviso\"", Leer("src", "PapeleriaPos.Instalacion", "Ayudante.cs"));
    }

    /// <summary>
    /// Antes de cambiar nada, el asistente corre «revisar» con las mismas respuestas que después escribe: lee de su salida «error» y
    /// «clave», y cada clave que puede decir el ayudante lleva a una página (PaginaDeLaClave).
    /// </summary>
    [Fact]
    public void Lo_que_el_asistente_lee_al_revisar_lo_dice_el_ayudante_y_cada_clave_tiene_su_pagina()
    {
        using var carpeta = new CarpetaDePrueba();
        var bien = Ayudante.Revisar(new ArchivoDeClaves().Poner("rol", "otra").Poner("lector", "teclado"), carpeta.Ruta);
        var mal = Ayudante.Revisar(new ArchivoDeClaves().Poner("rol", "otra").Poner("lector", "com").Poner("lector-puerto", "COM0"), carpeta.Ruta);
        Assert.Equal("", bien["error"]);
        Assert.NotNull(bien["cambios"]);
        Assert.Equal("lector-puerto", mal["clave"]);
        foreach (var clave in new[] { "error", "clave", "cambios" })
            Assert.Contains($"Valor(Salida, '{clave}')", Codigo);
        Assert.Contains("CorrerAyudante('revisar --respuestas \"' + RespuestasArchivo + '\"'", Codigo);

        // Las claves de las respuestas que puede rechazar el ayudante (Respuestas.Revisar): cada una, con su página.
        var funcion = Codigo[Codigo.IndexOf("function PaginaDeLaClave", StringComparison.Ordinal)..];
        funcion = funcion[..funcion.IndexOf("\nend;", StringComparison.Ordinal)];
        var exactas = Claves(funcion, @"Clave = '([a-z-]+)'");
        var prefijos = Claves(funcion, @"EmpiezaCon\(Clave, '([a-z-]+)'\)");
        var rechaza = Claves(Leer("src", "PapeleriaPos.Instalacion", "Respuestas.cs"), @"(?:Error|SiNo|Servidor|Numero)\((?:archivo, )?""([a-z-]+)""");
        Assert.True(rechaza.Count >= 12, string.Join(", ", rechaza));
        // Y las de la mudanza (diseno-v2, 3.3 y 3.5): las del servicio.json que va a quedar y de la conversión de la caja (RespuestaNoValida),
        // las de escribir --mudanza y las de mudanza-revisar (el paquete, la base de esta PC, «Esta PC ya tiene una tienda»).
        foreach (var archivo in Directory.GetFiles(Path.Combine(Raiz, "src", "PapeleriaPos.Instalacion"), "*.cs"))
            rechaza.UnionWith(Claves(File.ReadAllText(archivo), @"(?:RespuestaNoValida\(|Poner\(""clave"", )""([a-z-]+)"""));
        rechaza.UnionWith(Claves(Leer("src", "PapeleriaPos.Instalacion", "RevisionDeLaMudanza.cs"), @", ""([a-z-]+)""\);"));
        foreach (var clave in new[] { "mudanza", "mudanza-paquete", "mudanza-encima", "eleventa-base", "eleventa-gbak", "eleventa-caja", "eleventa-cajero" })
            Assert.Contains(clave, rechaza);
        Faltan(rechaza.Where(c => !exactas.Contains(c) && !prefijos.Any(p => c.StartsWith(p, StringComparison.Ordinal))));
    }

    /// <summary>Todo lo que se puede dar sin preguntar (/ROL=…), las variables de los secretos y los códigos de salida están en el instructivo.</summary>
    [Fact]
    public void Lo_de_sin_preguntar_esta_en_docs_instalacion()
    {
        var guia = Leer("docs", "instalacion.md");
        var parametros = Claves(Codigo, @"Parametro\('([A-Z]+)'\)");
        parametros.Add("BORRARDATOS");
        Assert.True(parametros.Count >= 17, string.Join(", ", parametros));
        Faltan(parametros.Where(p => !guia.Contains($"/{p}=", StringComparison.Ordinal)));
        // La mudanza sin preguntar (diseno-v2, 3.5): cuál, la carpeta del paquete y traerla encima de una tienda. Cada mudanza que entiende
        // el asistente está en el renglón de /MUDANZA=.
        foreach (var mudanza in new[] { "MUDANZA", "MUDANZAPAQUETE", "MUDANZAENCIMA" })
            Assert.Contains(mudanza, parametros);
        var entiende = Claves(Codigo, @"Pedida <> '([a-z]+)'");
        Assert.True(entiende.SetEquals(["sacar", "traer", "terminar", "cancelar"]), string.Join(", ", entiende));
        var renglon = guia.Split('\n').Single(r => r.StartsWith("| `/MUDANZA=` |", StringComparison.Ordinal));
        Faltan(entiende.Where(m => !renglon.Contains($"`{m}`", StringComparison.Ordinal)));
        Assert.Contains("{param:BORRARDATOS|no}", Asistente);
        foreach (var variable in new[] { "PAPELERIAPOS_CLAVE_ELEVENTA", "PAPELERIAPOS_CODIGO_CONTROLPRINT" })
        {
            Assert.Contains($"GetEnv('{variable}')", Codigo);
            Assert.Contains(variable, guia);
        }
        foreach (var codigo in new[] { "CodigoDeSalida := 10", "CodigoDeSalida := 11", "CodigoDeSalida := 12" })
        {
            Assert.Contains(codigo, Codigo);
            Assert.Contains($"| {codigo[^2..]} |", guia);
        }
        // El 7 es de Inno (PrepareToInstall): sin preguntar, lo que no se puede usar termina ahí y resultado.txt lo dice.
        Assert.Contains("Agregar(Lineas, 'codigo', '7')", Codigo);
        Assert.Contains("| 7 |", guia);
        // El 12, con su propio texto en la pantalla y en el instructivo.
        Assert.Contains("NO VENDAS EN ESTA CAJA", Codigo);
        Assert.Contains("NO VENDAS EN ESTA CAJA", guia);
        Assert.Contains("resultado.txt", guia);
    }

    /// <summary>
    /// La clave de eleventa y el código de Control Print van de su casilla al registro de Windows y a nada más: ni al registro de Inno, ni a
    /// una línea de órdenes, ni a respuestas.txt. instalar-pc.ps1 ni los conoce.
    /// </summary>
    [Fact]
    public void Las_claves_solo_van_a_las_variables_del_servicio()
    {
        foreach (var renglon in Codigo.Split('\n').Where(r => r.Contains("EditClave.Text") || r.Contains("EditCodigo.Text")))
        {
            Assert.DoesNotContain("Log(", renglon);
            Assert.DoesNotContain("Exec(", renglon);
            Assert.DoesNotContain("Agregar(", renglon);
            Assert.DoesNotContain("Caption", renglon);
        }
        Assert.Contains("PonerVariable('Eleventa__Clave', EditClave.Text)", Codigo);
        Assert.Contains("PonerVariable('ControlPrint__Codigo', CodigoNormal(EditCodigo.Text))", Codigo);
        Assert.DoesNotContain("Eleventa__Clave", Script);
        Assert.DoesNotContain("ControlPrint__Codigo", Script);
        // En silencio, de las variables de entorno: solo en ese caso.
        Assert.Matches(@"if WizardSilent then\s+begin\s+EditClave\.Text := GetEnv", Codigo);
    }

    [Fact]
    public void Es_otro_programa_que_el_de_la_prueba_y_va_donde_lo_buscan_los_scripts()
    {
        var prueba = Leer("instalacion", "prueba.iss");
        var appId = Regex.Match(Asistente, @"AppId=\{\{([0-9A-F-]+)\}").Groups[1].Value;
        Assert.Equal(36, appId.Length);
        Assert.DoesNotContain(appId, prueba);
        // El de [Setup] y el de su clave de desinstalar (con la que sabe si lo instaló este instalador o los scripts).
        Assert.Equal(2, Regex.Matches(Asistente, appId).Count);
        Assert.Contains(appId, Leer(".github", "instalacion", "instalador.ps1"));
        // Los scripts usan C:\Program Files\PapeleriaPos y C:\ProgramData\PapeleriaPos.
        Assert.Contains(@"DefaultDirName={autopf}\PapeleriaPos", Asistente);
        Assert.Contains("ArchitecturesInstallIn64BitMode=x64compatible", Asistente);
        Assert.Contains("PrivilegesRequired=admin", Asistente);
        Assert.Contains("runasoriginaluser", Asistente);
        // Lo que lleva adentro: los scripts del repositorio y lo que publica prueba.yml.
        var flujo = Leer(".github", "workflows", "prueba.yml");
        foreach (Match fuente in Regex.Matches(Asistente, @"^Source: ""([^""]+)""", RegexOptions.Multiline))
        {
            var ruta = fuente.Groups[1].Value.Replace('\\', '/');
            if (ruta.StartsWith("../publicar/", StringComparison.Ordinal))
                Assert.Contains(ruta[3..].Split('*')[0].TrimEnd('/').Split('/')[0..2].Aggregate((a, b) => $"{a}/{b}"), flujo);
            else
                Assert.True(File.Exists(Path.Combine(Raiz, "instalacion", ruta)), ruta);
        }
        Assert.Contains("PapeleriaPos-Instalador-1.0.${{ github.run_number }}", flujo);
        Assert.Contains("https://go.microsoft.com/fwlink/p/?LinkId=2124703", flujo);
    }

    /// <summary>Windows PowerShell 5.1 lee un .ps1 sin BOM como si no fuera UTF-8 (los acentos y las «» salen mal); Inno, igual con el .iss.</summary>
    /// <summary>Una sola copia a la vez: el mismo mutex en [Setup], en lo que revisa el asistente antes y en la prueba del CI.</summary>
    [Fact]
    public void Una_sola_copia_a_la_vez()
    {
        var mutex = Regex.Match(Asistente, @"^SetupMutex=(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        Assert.Equal(@"PapeleriaPosInstalador,Global\PapeleriaPosInstalador", mutex);
        Assert.Contains($"MutexDelInstalador = '{mutex}'", Codigo);
        Assert.Contains("CheckForMutexes(MutexDelInstalador)", Codigo);
        Assert.Contains(@"'Global\PapeleriaPosInstalador'", Leer(".github", "workflows", "prueba.yml"));
    }

    /// <summary>
    /// Lo de eleventa que «Borrar también los datos» nunca borra: lo dice instalar-pc.ps1, y el CI lo revisa (con una junction que no se
    /// sigue).
    /// </summary>
    [Fact]
    public void Borrar_los_datos_deja_lo_de_eleventa()
    {
        foreach (var carpeta in new[] { "respaldos-eleventa", "firebird20", "ensayo" })
        {
            Assert.Contains($"'{carpeta}'", Script);
            Assert.Contains(carpeta, Leer(".github", "workflows", "prueba.yml"));
            Assert.Contains($"`{carpeta}`", Leer("docs", "instalacion.md"));
        }
        Assert.Contains("CarpetaRespaldos", Script);
        Assert.DoesNotContain("Remove-Item -LiteralPath $datos -Recurse", Script);
        Assert.Contains("New-Item -ItemType Junction", Leer(".github", "workflows", "prueba.yml"));
    }

    /// <summary>
    /// En PowerShell los nombres no distinguen mayúsculas: una variable del script con el nombre de un parámetro es ese parámetro, con su
    /// tipo. Así falló instalar-pc.ps1 en su primera corrida en Windows: «$script:resultado = New-Object List[string]» era el parámetro
    /// «[string]$Resultado», la lista se volvió texto vacío y «.Add» tronó (y se perdió la ruta del archivo del resultado). Ninguno de
    /// los scripts de instalacion/ asigna, fuera de sus funciones o con $script: o $global:, una variable que se llame como uno de sus
    /// parámetros con otras mayúsculas o con ese prefijo (eso es querer otra variable). Cambiar el valor del mismo parámetro, escrito
    /// igual («$Salida = …» en armar-paquete.ps1), sí se puede.
    /// </summary>
    [Fact]
    public void Ningun_script_pisa_uno_de_sus_parametros()
    {
        var revisados = 0;
        foreach (var archivo in Directory.GetFiles(Path.Combine(Raiz, "instalacion"), "*.ps1"))
        {
            var texto = File.ReadAllText(archivo);
            var bloque = Regex.Match(texto, @"^\s*param\s*\((.*?)\)\s*$", RegexOptions.Multiline | RegexOptions.Singleline);
            if (!bloque.Success)
                continue;
            var escritos = Regex.Matches(bloque.Groups[1].Value, @"\$([A-Za-z_]\w*)").Select(m => m.Groups[1].Value)
                .Where(p => !p.Equals("true", StringComparison.OrdinalIgnoreCase) && !p.Equals("false", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.Ordinal);
            var parametros = escritos.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nivel = 0;
            var renglones = texto.Split('\n');
            for (var i = 0; i < renglones.Length; i++)
            {
                // Sin lo de adentro de las comillas ni los comentarios, para contar las llaves.
                var limpio = Regex.Replace(renglones[i], @"'[^']*'|""[^""]*""|#.*$", "");
                // Con $script: o $global: (en cualquier lado), o sin prefijo fuera de las funciones.
                var asignadas = Regex.Matches(limpio, @"\$(?:script|global):([A-Za-z_]\w*)\s*[+-]?=(?!=)").Select(m => (Nombre: m.Groups[1].Value, Prefijo: true)).ToList();
                if (nivel == 0)
                    asignadas.AddRange(Regex.Matches(limpio, @"^\s*\$([A-Za-z_]\w*)\s*[+-]?=(?!=)").Select(m => (Nombre: m.Groups[1].Value, Prefijo: false)));
                foreach (var (nombre, prefijo) in asignadas)
                    if (parametros.TryGetValue(nombre, out var parametro) && (prefijo || !escritos.Contains(nombre)))
                        Assert.Fail($"{Path.GetFileName(archivo)}, renglón {i + 1}: «${nombre}» es el parámetro «${parametro}»");
                nivel += limpio.Count(c => c == '{') - limpio.Count(c => c == '}');
            }
            revisados++;
        }
        Assert.True(revisados >= 6, $"se revisaron {revisados}");
    }

    [Fact]
    public void Los_scripts_y_el_asistente_van_con_bom()
    {
        var archivos = Directory.GetFiles(Path.Combine(Raiz, "instalacion"), "*.ps1")
            .Concat(Directory.GetFiles(Path.Combine(Raiz, ".github", "instalacion"), "*.ps1"))
            .Append(Path.Combine(Raiz, "instalacion", "papeleria-pos.iss"));
        foreach (var archivo in archivos)
            Assert.True(File.ReadAllBytes(archivo) is [0xEF, 0xBB, 0xBF, ..], $"{Path.GetFileName(archivo)} va sin BOM");
    }

    /// <summary>Una PC con una impresora y un gbak, para que salgan las claves con número.</summary>
    private sealed class EquipoConUnoDeCada(string raiz) : IEquipo
    {
        public string Datos => Path.Combine(raiz, "datos");

        public string Programa => Path.Combine(raiz, "programa");

        public string? ExeDelServicio() => null;

        public string? Version(string exe) => "2.0.1.12855";

        public IReadOnlyList<ImpresoraDeWindows> Impresoras() => [new("WF-6590", "IP_192.0.2.20", false, false)];

        public IReadOnlyList<string> PuertosCom() => ["COM3"];

        public IReadOnlyList<string> Ips() => ["192.0.2.50"];

        public IReadOnlyList<string> Gbak() => [@"C:\FirebirdSQL20\bin\gbak.exe"];

        public string? IniDeEleventa() => null;
    }
}
