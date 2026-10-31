using System.Text.RegularExpressions;
using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// El instalador son tres partes que se pasan datos por archivos «clave=valor»: el asistente (instalacion/papeleria-pos.iss, en Pascal),
/// instalar-pc.ps1 y el ayudante (C#). El Pascal y el PowerShell solo corren en Windows (el CI): aquí se revisa que lo que uno escribe sea
/// lo que el otro lee, que lo que se puede dar sin preguntar esté en docs/instalacion.md y que las claves no se cuelen a ningún lado.
/// </summary>
public sealed class ContratoDelInstaladorTests
{
    private static readonly string Raiz = ServicioJsonTests.RaizDelRepositorio();

    private static string Leer(params string[] partes) => File.ReadAllText(Path.Combine([Raiz, .. partes]));

    private static readonly string Asistente = Leer("instalacion", "papeleria-pos.iss");

    private static readonly string Script = Leer("instalacion", "instalar-pc.ps1");

    /// <summary>Lo de [Code], sin los comentarios { … } (para no confundir lo que se dice con lo que se hace).</summary>
    private static string Codigo => Regex.Replace(Asistente[Asistente.IndexOf("[Code]", StringComparison.Ordinal)..], @"\{[^}]*\}", "");

    /// <summary>Que no falte ninguno; si falta, dice cuáles.</summary>
    private static void Faltan(IEnumerable<string> faltan)
    {
        var lista = faltan.ToList();
        Assert.True(lista.Count == 0, $"Faltan: {string.Join(", ", lista)}");
    }

    private static HashSet<string> Claves(string texto, string patron) =>
        Regex.Matches(texto, patron).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lo que el asistente deja en respuestas.txt (GuardarRespuestas) es todo lo que leen el ayudante y instalar-pc.ps1.</summary>
    [Fact]
    public void Las_respuestas_que_escribe_el_asistente_son_las_que_se_leen()
    {
        var escribe = Claves(Codigo, @"Agregar\(Lineas, '([a-z-]+)'");
        string[] leeElAyudante = ["rol", "principal", "impresora", "cola", "lector", "lector-puerto", "lector-baudios", "controlprint",
            "controlprint-servidor", "eleventa", "eleventa-servidor", "eleventa-base", "eleventa-caja", "eleventa-cajero", "eleventa-gbak",
            "eleventa-respaldos"];
        var leeElScript = Claves(Script, @"\$r\['([a-z-]+)'\]");

        Faltan(leeElAyudante.Where(c => !escribe.Contains(c)));
        Faltan(leeElScript.Where(c => !escribe.Contains(c)));
        Assert.Contains("cerrar-caja", leeElScript);
        // Y el ayudante entiende un archivo con todas ellas.
        var respuestas = new ArchivoDeClaves();
        foreach (var clave in leeElAyudante)
            respuestas.Poner(clave, "");
        respuestas.Poner("rol", "principal").Poner("lector", "teclado").Poner("controlprint", "no").Poner("eleventa", "no");
        Assert.Equal(RolDeLaPc.Principal, Respuestas.De(respuestas).Rol);
    }

    /// <summary>Lo que el asistente lee de esta PC (Valor(Actual, …)) lo dice el ayudante (leer).</summary>
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
    }

    /// <summary>Lo que el asistente lee de «buscar» y «probar» lo dice el ayudante.</summary>
    [Fact]
    public async Task Lo_que_el_asistente_lee_al_buscar_y_al_probar_lo_dice_el_ayudante()
    {
        var probado = (await Ayudante.ProbarAsync("127.0.0.1:1")).Datos.Select(d => d.Clave).ToHashSet();
        foreach (var clave in new[] { "contesta", "texto", "error" })
        {
            Assert.Contains(clave, probado);
            Assert.Contains($"Valor(Salida, '{clave}')", Codigo);
        }
        var buscado = await Ayudante.BuscarAsync(TimeSpan.FromMilliseconds(300), destinos: []);
        Assert.Equal("0", buscado["encontradas"]);
        Assert.Contains("Valor(Salida, 'encontradas')", Codigo);
        Assert.Contains("'encontrada.' + IntToStr(I) + '.texto'", Codigo);
    }

    /// <summary>Todo lo que se puede dar sin preguntar (/ROL=…), las variables de los secretos y los códigos de salida están en el instructivo.</summary>
    [Fact]
    public void Lo_de_sin_preguntar_esta_en_docs_instalacion()
    {
        var guia = Leer("docs", "instalacion.md");
        var parametros = Claves(Codigo, @"Parametro\('([A-Z]+)'\)");
        parametros.Add("BORRARDATOS");
        Assert.True(parametros.Count >= 14, string.Join(", ", parametros));
        Faltan(parametros.Where(p => !guia.Contains($"/{p}=", StringComparison.Ordinal)));
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
