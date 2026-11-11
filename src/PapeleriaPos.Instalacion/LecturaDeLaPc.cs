using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PapeleriaPos.Instalacion;

/// <summary>Una impresora de Windows de esta PC.</summary>
/// <param name="SoloRaw">«Habilitar características de impresión avanzadas» apagado: los trabajos quedan en RAW y la tinta no se mide.</param>
/// <param name="DeOtraPc">La compartida desde otra PC (\\PRINCIPAL\…): con ella la tinta tampoco se mide.</param>
public sealed record ImpresoraDeWindows(string Nombre, string? Puerto, bool SoloRaw, bool DeOtraPc);

/// <summary>Lo que se pregunta a Windows de esta PC (<see cref="EquipoWindows"/>); las pruebas ponen uno a mano.</summary>
public interface IEquipo
{
    /// <summary>C:\ProgramData\PapeleriaPos: la base, servicio.json y la carpeta de la caja.</summary>
    string Datos { get; }

    /// <summary>C:\Program Files\PapeleriaPos: el servicio y la caja.</summary>
    string Programa { get; }

    /// <summary>El .exe con que está registrado el servicio PapeleriaPos; vacío si no está instalado.</summary>
    string? ExeDelServicio();

    /// <summary>La versión del programa («1.0.57»), como la lee actualizar.ps1.</summary>
    string? Version(string exe);

    IReadOnlyList<ImpresoraDeWindows> Impresoras();

    IReadOnlyList<string> PuertosCom();

    /// <summary>Las IP de esta PC en la red de la tienda (cable o Wi-Fi).</summary>
    IReadOnlyList<string> Ips();

    /// <summary>Los gbak.exe que hay en esta PC, en los lugares de siempre.</summary>
    IReadOnlyList<string> Gbak();

    /// <summary>El texto del pdventa.ini de eleventa en esta PC; vacío si no está.</summary>
    string? IniDeEleventa();

    /// <summary>El nombre de esta PC en la red (el de Windows): con él se sabe si «el servidor de allá» era esta misma PC (la mudanza).</summary>
    string Nombre() => Environment.MachineName;

    /// <summary>
    /// Todas las IPv4 de esta PC, también las de las tarjetas que no son de la red de la tienda (una VPN, una red virtual): para no
    /// preguntarse a sí misma creyendo que es otra PC. Si no se dicen, las de <see cref="Ips"/>.
    /// </summary>
    IReadOnlyList<string> IpsPropias() => Ips();
}

/// <summary>
/// Lo que el asistente necesita saber antes de preguntar (<c>leer</c>): si ya está el POS y qué versión, qué puede ser esta PC, lo que
/// dicen servicio.json y caja.json (para que cada página empiece con lo que ya tiene), las impresoras, los puertos COM y el gbak de
/// Firebird; y de la mudanza de la base a otra PC, si la de esta ya se sacó (la marca <see cref="MarcaDeMudanza"/>) o si aquí ya se
/// puso la de un paquete (<see cref="NotaDeLaBasePuesta"/>). Solo lee, y nunca toca la red. Nunca las claves: si el servicio ya tiene
/// la de eleventa o el código de Control Print, lo ve el asistente en el registro de Windows, sin leer su valor.
/// </summary>
public static class LecturaDeLaPc
{
    /// <summary>Donde está la base de eleventa en una instalación normal (docs/pruebas-en-tienda.md, el paso 6 de la instalación).</summary>
    public const string BaseDeEleventa = @"C:\Program Files (x86)\AbarrotesMultiCaja\db\PDVDATA.FDB";

    public static ArchivoDeClaves Leer(IEquipo equipo)
    {
        var salida = new ArchivoDeClaves();
        var errores = new List<string>();
        var exeServicio = equipo.ExeDelServicio();
        var exeCaja = Path.Combine(equipo.Programa, "Caja", "PapeleriaPos.Caja.exe");
        var cajaInstalada = File.Exists(exeCaja);
        salida.Poner("servicio-instalado", exeServicio is not null);
        salida.Poner("servicio-ruta", exeServicio);
        salida.Poner("servicio-version", exeServicio is null ? null : equipo.Version(exeServicio));
        salida.Poner("caja-instalada", cajaInstalada);
        salida.Poner("caja-version", cajaInstalada ? equipo.Version(exeCaja) : null);
        salida.Poner("base", File.Exists(Path.Combine(equipo.Datos, "pos.db")));

        var servicio = Leer(Path.Combine(equipo.Datos, ServicioJson.Archivo), "servicio.json", comentarios: true, errores);
        var rutaCaja = File.Exists(CajaJson.Ruta(equipo.Datos)) ? CajaJson.Ruta(equipo.Datos) : CajaJson.RutaDeAntes(equipo.Datos);
        var caja = Leer(rutaCaja, "caja.json", comentarios: false, errores);
        salida.Poner("servicio-json", servicio is not null);
        salida.Poner("caja-json", caja is not null);

        // Qué es esta PC, y qué puede ser.
        var servicioDeLaCaja = caja is null ? null : Json.Texto(Json.Valor(caja, "Servicio"));
        var principal = Uri.TryCreate(servicioDeLaCaja, UriKind.Absolute, out var uri) && !uri.IsLoopback ? uri : null;
        var dadaDeAlta = caja is not null && Guid.TryParse(Json.Texto(Json.Valor(caja, "CajaId")), out _);
        var unaSola = servicio is not null && Json.Valor(servicio, "Instalacion") is JsonObject instalacion
            && Json.SiNo(Json.Valor(instalacion, "UnaSolaCaja"));
        string rol, roles, motivo = "";
        if (exeServicio is not null)
        {
            rol = unaSola ? "unica" : "principal";
            roles = "principal,unica";
            motivo = "En esta PC está el servicio con la base de la tienda: no puede ser «Otra caja».";
        }
        else if (principal is not null && dadaDeAlta)
        {
            rol = "otra";
            roles = "otra";
            motivo = $"Esta caja está dada de alta con la caja principal en {BusquedaDeLaPrincipal.Corta(principal)}: sigue siendo «Otra caja» (para "
                + "que sea la caja principal, docs/instalacion.md, «Cambiar lo que es una PC»).";
        }
        else
        {
            // Sin el servicio ni servicio.json, con la caja instalada, es «Otra caja» aunque todavía no sepa su caja principal (se instaló
            // sin su IP: caja.json se queda en localhost, y la busca al abrirla).
            rol = principal is not null || (cajaInstalada && servicio is null) ? "otra" : servicio is not null ? (unaSola ? "unica" : "principal") : "";
            roles = "principal,unica,otra";
        }
        // La mudanza a otra PC (docs/instalacion.md): con la marca junto a la base, la de esta PC ya se sacó y su servicio no arranca. Lo
        // único que le queda es terminar como «Otra caja» de la PC nueva; «rol» sigue diciendo lo que es hoy, que es lo que vuelve a ser
        // si la mudanza se cancela. Sin tocar la red, como todo lo de aquí.
        var marca = MarcaDeMudanza.Leer(equipo.Datos);
        if (marca is not null)
        {
            roles = "otra";
            motivo = $"La base de esta PC se sacó para mudarla a otra PC{(marca.CuandoLocal is { } cuando ? $" ({cuando})" : "")}: solo le queda terminar la "
                + "mudanza como «Otra caja», o cancelarla.";
        }
        salida.Poner("rol", rol).Poner("roles", roles).Poner("roles-motivo", motivo);
        // Siempre, vacías si no aplica. Lo que cuenta es que la marca exista (así la ven el servicio, que con ella no arranca, y los
        // scripts): si no se deja leer, la PC sigue «sacada» y solo faltan sus datos.
        salida.Poner("mudanza", marca is null ? "" : "sacada");
        salida.Poner("mudanza-cuando", marca?.CuandoLocal);
        salida.Poner("mudanza-carpeta", marca?.Carpeta);
        salida.Poner("mudanza-paquete", marca?.Paquete);
        salida.Poner("mudanza-tienda", marca?.Tienda);
        // En la PC a donde se trajo una base: de qué paquete es la que ya se puso (un segundo intento con ese paquete no la trae otra vez).
        salida.Poner("mudanza-puesta", PaqueteYaPuesto(equipo.Datos));
        salida.Poner("caja-dada-de-alta", dadaDeAlta);
        salida.Poner("principal", principal is null ? null : BusquedaDeLaPrincipal.Corta(principal));

        // La caja: la impresora de tickets, la cola que mide y el lector.
        salida.Poner("ticket", caja is null ? null : Json.Texto(Json.Valor(caja, "Impresora")));
        salida.Poner("cola", caja is null ? null : Json.Texto(Json.Valor(caja, "ColaImpresion")));
        var lector = caja is null ? null : Json.Valor(caja, "Lector") as JsonObject;
        salida.Poner("lector-puerto", lector is null ? null : Json.Texto(Json.Valor(lector, "Puerto")));
        salida.Poner("lector-baudios", lector is null ? null : Json.Texto(Json.Valor(lector, "Baudios")));

        // Control Print.
        var controlPrint = servicio is null ? null : Json.Valor(servicio, "ControlPrint") as JsonObject;
        salida.Poner("controlprint", controlPrint is not null);
        salida.Poner("controlprint-servidor", controlPrint is null ? null : Json.Texto(Json.Valor(controlPrint, "Servidor")));

        // eleventa: lo de servicio.json; si no hay, lo que dice el eleventa de esta PC (su pdventa.ini) y lo de una instalación normal.
        var eleventa = servicio is null ? null : Json.Valor(servicio, "Eleventa") as JsonObject;
        var activo = eleventa is not null && Json.SiNo(Json.Valor(eleventa, "Activo"));
        var escribe = eleventa is not null && Json.SiNo(Json.Valor(eleventa, "Escribir"));
        salida.Poner("eleventa", activo || escribe);
        salida.Poner("eleventa-seccion", eleventa is not null);
        salida.Poner("eleventa-encendido", activo || escribe);
        salida.Poner("eleventa-escribe", escribe);
        // Con la sección, exactamente lo que tiene (sin ponerle lo de siempre a lo que le falta): así, si nada se cambia, servicio.json no
        // se toca. Sin ella, lo de una instalación normal.
        var servidorDelIni = Ini.Valor(equipo.IniDeEleventa(), "EnRed", "IPServidor");
        var gbak = equipo.Gbak().Select(ruta => (Ruta: ruta, Version: equipo.Version(ruta))).ToList();
        string? DeLaSeccion(string clave) => eleventa is null ? null : Json.Texto(Json.Valor(eleventa, clave));
        salida.Poner("eleventa-servidor", eleventa is null ? servidorDelIni : DeLaSeccion("Servidor"));
        salida.Poner("eleventa-servidor-del-ini", eleventa is null && servidorDelIni is not null);
        salida.Poner("eleventa-base", eleventa is null ? BaseDeEleventa : DeLaSeccion("Base"));
        salida.Poner("eleventa-caja", DeLaSeccion("Caja"));
        salida.Poner("eleventa-cajero", DeLaSeccion("Cajero"));
        salida.Poner("eleventa-respaldos", eleventa is null ? Path.Combine(equipo.Datos, "respaldos-eleventa") : DeLaSeccion("CarpetaRespaldos"));
        salida.Poner("eleventa-gbak", eleventa is null ? ElegirGbak(gbak) : DeLaSeccion("Gbak"));
        salida.Poner("gbak", gbak.Count.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < gbak.Count; i++)
        {
            salida.Poner($"gbak.{i + 1}", gbak[i].Ruta);
            salida.Poner($"gbak.{i + 1}.version", gbak[i].Version);
            salida.Poner($"gbak.{i + 1}.aviso", AvisoDeGbak(gbak[i].Version));
        }

        // Las listas de esta PC.
        var impresoras = equipo.Impresoras();
        salida.Poner("impresoras", impresoras.Count.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < impresoras.Count; i++)
        {
            salida.Poner($"impresora.{i + 1}", impresoras[i].Nombre);
            salida.Poner($"impresora.{i + 1}.puerto", impresoras[i].Puerto);
            salida.Poner($"impresora.{i + 1}.aviso", AvisoDeCola(impresoras[i]));
        }
        salida.Poner("puertos", string.Join(",", equipo.PuertosCom()));
        salida.Poner("ips", string.Join(",", equipo.Ips()));
        salida.Poner("error", string.Join(" ", errores));
        return salida;
    }

    /// <summary>
    /// El paquete de <see cref="NotaDeLaBasePuesta"/>; vacío también si la nota no se deja leer: de ella solo depende qué opción viene
    /// elegida en el asistente, y por eso no detiene al instalador (quien decide de verdad, <c>mudanza-revisar</c>, sí lo dice).
    /// </summary>
    private static string? PaqueteYaPuesto(string datos)
    {
        try
        {
            return NotaDeLaBasePuesta.Paquete(datos);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JsonObject? Leer(string ruta, string nombre, bool comentarios, List<string> errores)
    {
        if (!File.Exists(ruta))
            return null;
        try
        {
            var bytes = File.ReadAllBytes(ruta);
            if (comentarios && System.Text.Encoding.UTF8.GetString(Json.SinBom(bytes)).Trim().Length == 0)
                return new JsonObject();
            return Json.Objeto(bytes, nombre, comentarios);
        }
        catch (FormatException ex)
        {
            errores.Add($"{ex.Message} ({ruta}): corrígelo o quítalo y vuelve a correr el instalador; no se cambió nada.");
            return null;
        }
        catch (IOException ex)
        {
            errores.Add($"No se pudo leer {ruta}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Por qué esa impresora no sirve como la cola que mide la caja (docs/pruebas-en-tienda.md, «Control Print en la caja 2», paso 3); vacío
    /// si sirve.
    /// </summary>
    public static string? AvisoDeCola(ImpresoraDeWindows impresora) =>
        impresora.DeOtraPc
            ? $"«{impresora.Nombre}» es la impresora compartida desde otra PC: con ella la tinta no se mide. Elige la cola con el puerto TCP/IP de "
                + "la impresora (su IP)."
            : impresora.SoloRaw
                ? $"«{impresora.Nombre}» tiene apagadas las «características de impresión avanzadas» (RAW): la tinta no se mide, solo se estiman "
                    + "las páginas. Préndelas en sus propiedades (Opciones avanzadas)."
                : null;

    /// <summary>Por qué ese gbak no es el que hace falta (el de Firebird 2.0, como el de la tienda); vacío si sirve.</summary>
    public static string? AvisoDeGbak(string? version) =>
        version is not { Length: > 0 }
            ? "No dice su versión: revisa que sea el de Firebird 2.0."
            : version.StartsWith("2.0.", StringComparison.Ordinal) || version == "2.0"
                ? null
                : version.StartsWith("2.5", StringComparison.Ordinal)
                    ? $"Es de Firebird {version}: el de 2.5 deja una base restaurada en un formato que eleventa ya no abre. Usa el de Firebird 2.0."
                    : $"Es de Firebird {version}, no de 2.0 como el de la tienda.";

    /// <summary>El primero que sirve (el de Firebird 2.0); vacío si ninguno.</summary>
    public static string? ElegirGbak(IReadOnlyList<(string Ruta, string? Version)> gbak) =>
        gbak.FirstOrDefault(g => AvisoDeGbak(g.Version) is null).Ruta;
}

/// <summary>Leer un .ini (el pdventa.ini de eleventa), sin cambiarlo.</summary>
public static class Ini
{
    /// <summary>El valor de esa clave en esa sección («[EnRed]», «IPServidor=»); vacío si no está.</summary>
    public static string? Valor(string? texto, string seccion, string clave)
    {
        if (texto is null)
            return null;
        var dentro = false;
        foreach (var crudo in texto.Split('\n'))
        {
            var renglon = crudo.Trim();
            if (renglon.StartsWith('[') && renglon.EndsWith(']'))
            {
                dentro = string.Equals(renglon[1..^1].Trim(), seccion, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var igual = renglon.IndexOf('=');
            if (dentro && igual > 0 && string.Equals(renglon[..igual].Trim(), clave, StringComparison.OrdinalIgnoreCase))
                return renglon[(igual + 1)..].Trim() is { Length: > 0 } valor ? valor : null;
        }
        return null;
    }
}
