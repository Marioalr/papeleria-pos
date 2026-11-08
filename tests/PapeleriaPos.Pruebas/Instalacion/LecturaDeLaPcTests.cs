using PapeleriaPos.Instalacion;
using PapeleriaPos.Pruebas.Interfaz;

namespace PapeleriaPos.Pruebas.Instalacion;

/// <summary>
/// Lo que el asistente sabe de esta PC antes de preguntar (<c>leer</c>): qué puede ser, y lo que ya tiene para empezar cada página con eso.
/// </summary>
public sealed class LecturaDeLaPcTests : IDisposable
{
    private readonly CarpetaDePrueba _carpeta = new();

    public void Dispose() => _carpeta.Dispose();

    private sealed class EquipoDePrueba(string raiz) : IEquipo
    {
        public string Datos { get; } = Path.Combine(raiz, "ProgramData", "PapeleriaPos");

        public string Programa { get; } = Path.Combine(raiz, "Program Files", "PapeleriaPos");

        public string? Servicio { get; set; }

        public Dictionary<string, string> Versiones { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<ImpresoraDeWindows> ListaDeImpresoras { get; } = [];

        public List<string> ListaDeGbak { get; } = [];

        public string? Ini { get; set; }

        public string? ExeDelServicio() => Servicio;

        public string? Version(string exe) => Versiones.GetValueOrDefault(exe);

        public IReadOnlyList<ImpresoraDeWindows> Impresoras() => ListaDeImpresoras;

        public IReadOnlyList<string> PuertosCom() => ["COM1", "COM3"];

        public IReadOnlyList<string> Ips() => ["192.168.1.50"];

        public IReadOnlyList<string> Gbak() => ListaDeGbak;

        public string? IniDeEleventa() => Ini;

        public void Archivo(string relativo, string texto)
        {
            var ruta = Path.Combine(Datos, relativo);
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            File.WriteAllText(ruta, texto);
        }

        public string Instalar(string carpeta, string exe)
        {
            var ruta = Path.Combine(Programa, carpeta, exe);
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            File.WriteAllText(ruta, "");
            return ruta;
        }
    }

    [Fact]
    public void En_una_pc_nueva_puede_ser_cualquier_cosa_y_no_se_sugiere_ninguna()
    {
        var equipo = new EquipoDePrueba(_carpeta.Ruta);

        var leido = LecturaDeLaPc.Leer(equipo);

        Assert.Equal("no", leido["servicio-instalado"]);
        Assert.Equal("no", leido["caja-instalada"]);
        Assert.Equal("", leido["rol"]);
        Assert.Equal("principal,unica,otra", leido["roles"]);
        Assert.Equal(LecturaDeLaPc.BaseDeEleventa, leido["eleventa-base"]);
        Assert.Equal(Path.Combine(equipo.Datos, "respaldos-eleventa"), leido["eleventa-respaldos"]);
        Assert.Equal("COM1,COM3", leido["puertos"]);
        Assert.Equal("192.168.1.50", leido["ips"]);
        Assert.Equal("", leido["error"]);
    }

    /// <summary>La caja 2 de hoy (instalada con los scripts): el servicio, su servicio.json del piloto y su caja dada de alta.</summary>
    [Fact]
    public void La_caja_2_instalada_con_los_scripts_empieza_con_lo_que_tiene()
    {
        var equipo = new EquipoDePrueba(_carpeta.Ruta);
        equipo.Servicio = equipo.Instalar("Servicio", "PapeleriaPos.Servicio.exe");
        var caja = equipo.Instalar("Caja", "PapeleriaPos.Caja.exe");
        equipo.Versiones[equipo.Servicio] = "1.0.25";
        equipo.Versiones[caja] = "1.0.25";
        equipo.Archivo("pos.db", "");
        equipo.Archivo("servicio.json", """
            // A mano, con el Bloc de notas.
            {
              "Eleventa": {
                "Activo": true,
                "Escribir": true,
                "Servidor": "192.168.1.106",
                "Base": "C:\\Program Files (x86)\\AbarrotesMultiCaja\\db\\PDVDATA.FDB",
                "Caja": 3,
                "Cajero": 1,
                "Gbak": "C:\\ProgramData\\PapeleriaPos\\firebird20\\bin\\gbak.exe"
              },
              "ControlPrint": { "Servidor": "192.168.1.106" }
            }
            """);
        equipo.Archivo(Path.Combine("Caja", "caja.json"), """
            { "Servicio": "http://localhost:5080/", "CajaId": "5a1d4c3e-0000-4000-8000-000000000002", "Impresora": "tcp://192.168.1.100:9100",
              "ColaImpresion": "WF-6590", "Lector": { "Puerto": "COM3", "Baudios": 9600 } }
            """);

        var leido = LecturaDeLaPc.Leer(equipo);

        Assert.Equal("si", leido["servicio-instalado"]);
        Assert.Equal("1.0.25", leido["servicio-version"]);
        Assert.Equal("1.0.25", leido["caja-version"]);
        Assert.Equal("si", leido["base"]);
        Assert.Equal("principal", leido["rol"]);
        Assert.Equal("principal,unica", leido["roles"]);
        Assert.Contains("no puede ser «Otra caja»", leido["roles-motivo"]);
        Assert.Equal("tcp://192.168.1.100:9100", leido["ticket"]);
        Assert.Equal("WF-6590", leido["cola"]);
        Assert.Equal("COM3", leido["lector-puerto"]);
        Assert.Equal("9600", leido["lector-baudios"]);
        Assert.Equal("si", leido["controlprint"]);
        Assert.Equal("192.168.1.106", leido["controlprint-servidor"]);
        Assert.Equal("si", leido["eleventa"]);
        Assert.Equal("si", leido["eleventa-encendido"]);
        Assert.Equal("si", leido["eleventa-escribe"]);
        Assert.Equal("192.168.1.106", leido["eleventa-servidor"]);
        Assert.Equal("3", leido["eleventa-caja"]);
        Assert.Equal("1", leido["eleventa-cajero"]);
        Assert.Equal(@"C:\ProgramData\PapeleriaPos\firebird20\bin\gbak.exe", leido["eleventa-gbak"]);
    }

    [Fact]
    public void La_unica_caja_lo_dice_su_servicio_json()
    {
        var equipo = new EquipoDePrueba(_carpeta.Ruta) { Servicio = "x" };
        equipo.Archivo("servicio.json", """{ "Instalacion": { "UnaSolaCaja": true } }""");

        Assert.Equal("unica", LecturaDeLaPc.Leer(equipo)["rol"]);
    }

    /// <summary>Una caja dada de alta con otra PC sigue siendo «Otra caja»: volverla principal le dejaría el alta de la otra.</summary>
    [Fact]
    public void Otra_caja_dada_de_alta_solo_puede_seguir_siendo_otra_caja()
    {
        var equipo = new EquipoDePrueba(_carpeta.Ruta);
        equipo.Instalar("Caja", "PapeleriaPos.Caja.exe");
        equipo.Archivo(Path.Combine("Caja", "caja.json"), """{ "Servicio": "http://192.168.1.50:5080/", "CajaId": "5a1d4c3e-0000-4000-8000-000000000003" }""");

        var leido = LecturaDeLaPc.Leer(equipo);

        Assert.Equal("otra", leido["rol"]);
        Assert.Equal("otra", leido["roles"]);
        Assert.Equal("192.168.1.50", leido["principal"]);
        Assert.Contains("dada de alta con la caja principal en 192.168.1.50", leido["roles-motivo"]);
        Assert.Equal("si", leido["caja-dada-de-alta"]);
    }

    [Fact]
    public void Una_caja_que_apunta_a_otra_sin_alta_puede_ser_cualquier_cosa()
    {
        var equipo = new EquipoDePrueba(_carpeta.Ruta);
        equipo.Archivo(Path.Combine("Caja", "caja.json"), """{ "Servicio": "http://192.168.1.50:5090/", "CajaId": null }""");

        var leido = LecturaDeLaPc.Leer(equipo);

        Assert.Equal("otra", leido["rol"]);
        Assert.Equal("principal,unica,otra", leido["roles"]);
        Assert.Equal("192.168.1.50:5090", leido["principal"]);
    }

    /// <summary>Sin el puente en servicio.json, la IP del servidor sale del eleventa de esta PC (pdventa.ini, solo se lee).</summary>
    [Fact]
    public void La_ip_de_eleventa_sale_de_su_pdventa_ini_si_servicio_json_no_la_tiene()
    {
        var equipo = new EquipoDePrueba(_carpeta.Ruta) { Ini = "[General]\r\nIPServidor=10.0.0.1\r\n[EnRed]\r\nCaja=2\r\nIPServidor = 192.168.1.106 \r\n" };

        var leido = LecturaDeLaPc.Leer(equipo);

        Assert.Equal("192.168.1.106", leido["eleventa-servidor"]);
        Assert.Equal("si", leido["eleventa-servidor-del-ini"]);
        Assert.Equal("no", leido["eleventa"]);
    }

    [Fact]
    public void Del_gbak_se_elige_el_de_firebird_2_0_y_el_de_2_5_lleva_su_aviso()
    {
        var equipo = new EquipoDePrueba(_carpeta.Ruta);
        equipo.ListaDeGbak.AddRange([@"C:\Firebird_2_5\bin\gbak.exe", @"C:\FirebirdSQL20\bin\gbak.exe", @"C:\Otro\bin\gbak.exe"]);
        equipo.Versiones[@"C:\Firebird_2_5\bin\gbak.exe"] = "2.5.9.27139";
        equipo.Versiones[@"C:\FirebirdSQL20\bin\gbak.exe"] = "2.0.1.12855";

        var leido = LecturaDeLaPc.Leer(equipo);

        Assert.Equal(@"C:\FirebirdSQL20\bin\gbak.exe", leido["eleventa-gbak"]);
        Assert.Equal("3", leido["gbak"]);
        Assert.Contains("el de 2.5 deja una base restaurada en un formato que eleventa ya no abre", leido["gbak.1.aviso"]);
        Assert.Equal("", leido["gbak.2.aviso"]);
        Assert.Contains("No dice su versión", leido["gbak.3.aviso"]);
    }

    [Fact]
    public void De_las_impresoras_se_avisa_la_compartida_de_otra_pc_y_la_que_esta_en_raw()
    {
        var equipo = new EquipoDePrueba(_carpeta.Ruta);
        equipo.ListaDeImpresoras.AddRange([
            new ImpresoraDeWindows(@"\\PRINCIPAL\WF-6590", null, false, true),
            new ImpresoraDeWindows("WF-6590", "IP_192.168.1.20", true, false),
            new ImpresoraDeWindows("WF-6590 (red)", "IP_192.168.1.20", false, false),
        ]);

        var leido = LecturaDeLaPc.Leer(equipo);

        Assert.Equal("3", leido["impresoras"]);
        Assert.Contains("compartida desde otra PC", leido["impresora.1.aviso"]);
        Assert.Contains("características de impresión avanzadas", leido["impresora.2.aviso"]);
        Assert.Equal("", leido["impresora.3.aviso"]);
        Assert.Equal("WF-6590 (red)", leido["impresora.3"]);
    }

    /// <summary>Un servicio.json mal escrito: el asistente no sigue (no se pisa lo que el dueño escribió) y dice cuál y en qué renglón.</summary>
    [Fact]
    public void Un_archivo_mal_escrito_se_dice_y_el_asistente_no_sigue()
    {
        var equipo = new EquipoDePrueba(_carpeta.Ruta);
        equipo.Archivo("servicio.json", "{\n  \"Eleventa\": { \"Activo\": true, }\n  \"ControlPrint\": {}\n}");

        var leido = LecturaDeLaPc.Leer(equipo);

        Assert.Contains("servicio.json está mal escrito (renglón 3", leido["error"]);
        Assert.Contains("no se cambió nada", leido["error"]);
    }
}
