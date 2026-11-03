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
        Faltan(rechaza.Where(c => !exactas.Contains(c) && !prefijos.Any(p => c.StartsWith(p, StringComparison.Ordinal))));
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
