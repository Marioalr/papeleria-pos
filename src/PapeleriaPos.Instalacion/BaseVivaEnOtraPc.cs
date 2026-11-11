using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PapeleriaPos.Instalacion;

/// <summary>
/// <c>mudanza-viva</c>: antes de cancelar una mudanza en la PC de donde salió la base (docs/instalacion.md), si esa base ya está viva en
/// OTRA PC. Cancelar vuelve a arrancar el servicio de esta PC con su base; si la PC nueva ya trabaja con la que se le llevó, serían dos
/// bases vivas de la misma tienda, y lo que se cobre en una no estaría en la otra. Se busca en la red como <c>buscar</c> (la pregunta
/// por UDP) y además se les pregunta a las direcciones que se den (las de traida.txt, que deja la PC nueva en el paquete). De cada PC
/// que conteste y NO sea esta, su <c>api/estado</c>: está viva si dice la misma tienda (<see cref="NombreDeLaTienda"/>; api/estado no
/// tiene un identificador de la base que dure).
/// <para>
/// Solo dice «viva=no» si pudo preguntar todo lo que se le pidió. Lo que no se entiende (sin tienda, una dirección que no es una IP)
/// o no se pudo ni intentar es un «error», no un «no»: con un «no» se cancela.
/// </para>
/// </summary>
public static class BaseVivaEnOtraPc
{
    /// <param name="direcciones">Las IP a las que además se les pregunta («ip;ip»), en <paramref name="puerto"/> (vacío, el 5080).</param>
    /// <param name="enLaRed">Las pruebas, lo que contestó en la red; sin él, la pregunta de verdad (<see cref="BusquedaDeLaPrincipal.BuscarAsync"/>).</param>
    public static async Task<ArchivoDeClaves> BuscarAsync(string? tienda, string? direcciones, string? puerto, IEquipo equipo, HttpMessageHandler? red,
        PlazosDeLaMudanza plazos, Func<TimeSpan, Task<IReadOnlyList<Uri>>>? enLaRed = null)
    {
        // Siempre las mismas claves; sin saberlo, «viva» va vacía (nunca «no»).
        static ArchivoDeClaves Salida(string viva, string? donde, string? deQuien, string? error) =>
            new ArchivoDeClaves().Poner("viva", viva).Poner("donde", donde).Poner("equipo", deQuien).Poner("error", error);
        static ArchivoDeClaves NoSeSabe(string error) => Salida("", null, null, $"{error} No se sabe si la base ya está viva en otra PC.");

        // Una tienda sin nombre no es «la misma» que ninguna: nadie contestaría con ella, y parecería que nadie la tiene.
        var nombre = (tienda ?? "").Trim();
        if (!NombreDeLaTienda.EsElMismo(nombre, nombre))
            return NoSeSabe("Falta el nombre de la tienda («--tienda»).");
        var numero = BusquedaDeLaPrincipal.PuertoDelServicio;
        if (puerto is not null
            && !(int.TryParse(puerto.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out numero) && numero is > 0 and <= 65535))
            return NoSeSabe($"El puerto no se entiende (está «{puerto.Trim()}»): va de 1 a 65535.");
        // Una dirección que se dio y no se entiende no se salta: no preguntarle sería decir «no» sin saber.
        var dadas = (direcciones ?? "").Split(';', ',').Select(parte => parte.Trim()).Where(parte => parte.Length > 0).Distinct().ToList();
        var ips = ResumenDeMudanza.Direcciones(direcciones);
        if (ips.Count != dadas.Count)
            return NoSeSabe($"Las direcciones no se entienden (está «{(direcciones ?? "").Trim()}»): van las IP de la otra PC, con números y separadas con «;» "
                + "(hasta 16).");

        // Las dos preguntas a la vez: en la red (UDP) y a las direcciones dadas. Lo de esta misma PC no cuenta: su propio servicio, si
        // contestara, es justo el que se quiere volver a arrancar.
        var deLasDadas = PreguntasDeLaMudanza.AOtrasPcAsync(ips, numero, equipo, red, plazos.ALaPrincipal);
        IReadOnlyList<Uri> encontradas = [];
        string? sinBuscar = null;
        try
        {
            encontradas = await (enLaRed ?? (espera => BusquedaDeLaPrincipal.BuscarAsync(espera)))(plazos.AlBuscar);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            sinBuscar = ex.Message;
        }
        var preguntadas = ips.Select(ip => new UriBuilder("http", ip.ToString(), numero, "/").Uri).ToHashSet();
        var deLasEncontradas = await Task.WhenAll(encontradas
            .Where(uri => IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !PreguntasDeLaMudanza.EsDeEstaPc(ip, equipo) && !preguntadas.Contains(uri))
            .Distinct()
            .Select(uri => PreguntasDeLaMudanza.PreguntarAsync(uri, red, plazos.ALaPrincipal)));

        if ((await deLasDadas).Concat(deLasEncontradas).FirstOrDefault(estado => estado.TieneLaTienda(nombre)) is { } viva)
            return Salida("si", BusquedaDeLaPrincipal.Corta(viva.Direccion), EquipoDe(viva.Direccion, equipo), null);
        return sinBuscar is null ? Salida("no", null, null, null) : NoSeSabe($"No se pudo buscar en la red ({sinBuscar}).");
    }

    /// <summary>
    /// Cómo se llama la PC que contestó, si se sabe: api/estado no lo dice, pero sí el traida.txt que esa PC dejó en el paquete (la
    /// carpeta que dice la marca de esta PC), si es de este paquete y trae esa IP. Vacío si no está a la mano.
    /// </summary>
    private static string? EquipoDe(Uri direccion, IEquipo equipo)
    {
        try
        {
            if (IPAddress.TryParse(direccion.Host.Trim('[', ']'), out var ip)
                && MarcaDeMudanza.Leer(equipo.Datos) is { Paquete: { } paquete, Carpeta: { } carpeta }
                && PaqueteDeMudanza.Traida(carpeta, paquete) is { } traida
                && ResumenDeMudanza.Direcciones(traida["ips"]).Contains(ip)
                && traida["equipo"]?.Trim() is { Length: > 0 and <= 63 } nombre
                // Solo lo que parece el nombre de una PC: viene de un archivo de la USB y va a un mensaje.
                && nombre.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
                return nombre;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // La USB ya no está o no se deja leer: no se sabe, y no hace falta para decir que está viva.
        }
        return null;
    }
}
