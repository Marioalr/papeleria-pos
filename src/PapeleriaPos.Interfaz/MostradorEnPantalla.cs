using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que se señala en la pantalla (Ctrl+M, entrega 13): qué se ve primero, cuántas piezas por renglón, la letra de la pieza
/// mientras no tiene foto, a dónde van las flechas y cuánto se agrega. Sin la página: lo usan el panel y la venta, y lo revisan
/// las pruebas.
/// </summary>
public static class MostradorEnPantalla
{
    /// <summary>
    /// Lo que sale en Ctrl+M, en orden: primero las temporadas que corren hoy y luego los exhibidores (docs/propuesta-interfaz.md:
    /// «mientras está activa, sale primero»). La temporada que no corre no sale: se oculta sola al terminar. Al acomodar, sí
    /// (después de los exhibidores): se prepara antes de que empiece.
    /// </summary>
    public static List<ExhibidorDto> EnOrden(IEnumerable<ExhibidorDto> exhibidores, DateOnly hoy, bool conLasQueNoCorren = false)
    {
        var lista = exhibidores.ToList();
        var corren = lista.Where(e => e.CorreEl(hoy)).ToList();
        var resto = lista.Where(e => e.Tipo == TipoExhibidor.Exhibidor);
        var lasDemas = conLasQueNoCorren ? lista.Where(e => e.Tipo == TipoExhibidor.Temporada && !e.CorreEl(hoy)) : [];
        return [.. corren, .. resto, .. lasDemas];
    }

    /// <summary>Las temporadas que corren hoy, en su orden: cada una con su chip arriba de la venta.</summary>
    public static List<ExhibidorDto> TemporadasQueCorren(IEnumerable<ExhibidorDto>? exhibidores, DateOnly hoy) =>
        (exhibidores ?? []).Where(e => e.CorreEl(hoy)).ToList();

    /// <summary>Cuántas piezas por renglón: la mesa de la temporada, 3 y más grandes; la vitrina, 4 (como el prototipo).</summary>
    public static int Columnas(ExhibidorDto exhibidor) => exhibidor.Tipo == TipoExhibidor.Temporada ? 3 : 4;

    /// <summary>Lo que mide el lugar de la foto, en píxeles.</summary>
    public static int Lado(ExhibidorDto exhibidor) => Columnas(exhibidor) == 4 ? 60 : 72;

    private static readonly HashSet<string> Chicas = ["DE", "DEL", "LA", "LAS", "EL", "LOS", "Y", "CON", "PARA", "A", "EN", "SIN", "POR", "P/", "C/"];

    /// <summary>
    /// Mientras no hay foto (llegan en la parte 2): las iniciales de las dos primeras palabras que cuentan («Calculadora básica
    /// 8 dígitos» → «CB»), sin acentos.
    /// </summary>
    public static string Iniciales(string nombre)
    {
        var palabras = PapeleriaPos.Nucleo.Texto.Normalizar(nombre).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => char.IsLetterOrDigit(p[0]) && !Chicas.Contains(p)).ToList();
        return palabras.Count switch
        {
            0 => "?",
            1 => palabras[0][..Math.Min(2, palabras[0].Length)],
            _ => $"{palabras[0][0]}{palabras[1][0]}",
        };
    }

    /// <summary>
    /// La pieza elegida después de una flecha. <paramref name="porLugar"/> dice cuántas piezas tiene cada lugar, en orden; la
    /// elegida se cuenta de corrido. ← y → van a la de al lado (y pasan de un lugar al siguiente); ↑ y ↓, a la de arriba o la
    /// de abajo en la misma columna, también del lugar de arriba o de abajo (si ese renglón es más corto, a la última). En la
    /// orilla se queda.
    /// </summary>
    public static int Siguiente(IReadOnlyList<int> porLugar, int columnas, int actual, string tecla)
    {
        var total = porLugar.Sum();
        if (total == 0)
            return 0;
        actual = Math.Clamp(actual, 0, total - 1);
        switch (tecla)
        {
            case "ArrowLeft":
                return Math.Max(actual - 1, 0);
            case "ArrowRight":
                return Math.Min(actual + 1, total - 1);
            case "ArrowDown" or "ArrowUp":
                var (lugar, posicion) = Lugar(porLugar, actual);
                var (renglon, columna) = (posicion / columnas, posicion % columnas);
                var ultimo = (porLugar[lugar] - 1) / columnas;
                if (tecla == "ArrowDown" && renglon < ultimo)
                    return actual - posicion + Math.Min(posicion + columnas, porLugar[lugar] - 1);
                if (tecla == "ArrowUp" && renglon > 0)
                    return actual - columnas;
                // Al lugar de abajo (su primer renglón) o al de arriba (su último), los que tienen piezas.
                var paso = tecla == "ArrowDown" ? 1 : -1;
                for (var otro = lugar + paso; otro >= 0 && otro < porLugar.Count; otro += paso)
                {
                    if (porLugar[otro] == 0)
                        continue;
                    var inicio = porLugar.Take(otro).Sum();
                    var deRenglon = paso > 0 ? 0 : (porLugar[otro] - 1) / columnas * columnas;
                    return inicio + Math.Min(deRenglon + columna, porLugar[otro] - 1);
                }
                return actual;
            default:
                return actual;
        }
    }

    /// <summary>En qué lugar está una pieza contada de corrido, y en qué posición dentro de él.</summary>
    public static (int Lugar, int Posicion) Lugar(IReadOnlyList<int> porLugar, int indice)
    {
        for (var lugar = 0; lugar < porLugar.Count; lugar++)
        {
            if (indice < porLugar[lugar])
                return (lugar, indice);
            indice -= porLugar[lugar];
        }
        return (Math.Max(porLugar.Count - 1, 0), 0);
    }

    /// <summary>
    /// Cuánto se agrega al tocar una pieza: lo escrito en el cuadro de captura (docs/propuesta-interfaz.md: «con un número
    /// antes, agrega esa cantidad»), o una pieza si no hay nada. Vacío si lo escrito no es una cantidad.
    /// </summary>
    public static decimal? Cantidad(string texto)
    {
        texto = texto.Trim().TrimEnd('*').Replace(',', '.');
        if (texto.Length == 0)
            return 1;
        return decimal.TryParse(texto, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? Math.Round(n, 3)
            : null;
    }

    /// <summary>
    /// Si lo escrito antes de Enter es una cantidad para la pieza elegida (hasta cuatro cifras, con decimales): lo demás (un
    /// código, un nombre, «3*código») va como siempre, como un escaneo.
    /// </summary>
    public static bool EsCantidad(string texto) =>
        System.Text.RegularExpressions.Regex.IsMatch(texto.Trim(), @"^(\d{1,4}([.,]\d{1,3})?\*?)?$") && Cantidad(texto) is not null;
}
