using System.Globalization;
using System.Text.RegularExpressions;

namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>Lo que puede cambiar entre productos que por lo demás se llaman igual.</summary>
public enum EjeFamilia
{
    Color,
    Medida,
    Marca,
    Contenido,
    Rayado,
}

/// <summary>
/// Un pedazo del nombre tal como se escribió («0.7mm»), qué es (null: lo demás del nombre) y cómo se compara («0.7MM»).
/// La clave vacía no cuenta al comparar: las palabras de relleno («de», «con») y lo que acompaña a una medida («P/Fino»
/// junto a «0.7mm»).
/// </summary>
public sealed record ParteDeNombre(string Texto, EjeFamilia? Eje, string Clave);

/// <summary>
/// Lee el nombre de un producto en pedazos: el color, la medida, la marca, el contenido y, en cuadernos, el rayado. Así
/// la búsqueda junta en un renglón lo que solo cambia en una cosa (docs/propuesta-interfaz.md, «Los colores en un
/// renglón»). Las reglas salen de cómo están escritos los nombres que vienen de eleventa (análisis del 2026-10-11).
/// Las marcas no se escriben aquí: se aprenden de lo que se repite al final de los nombres del catálogo, y también
/// valen las dadas de alta.
/// </summary>
public sealed partial class LectorDeNombres
{
    private readonly HashSet<string> _marcas = [];
    private readonly HashSet<string> _marcasDeDos = [];

    /// <param name="nombres">Todo el catálogo: una marca se repite al final de por lo menos 3 nombres de 2 productos distintos.</param>
    /// <param name="marcas">Las marcas dadas de alta.</param>
    public LectorDeNombres(IEnumerable<string> nombres, IEnumerable<string>? marcas = null)
    {
        var veces = new Dictionary<string, (int Nombres, HashSet<string> Productos)>();
        void Contar(string marca, string producto)
        {
            if (!veces.TryGetValue(marca, out var v))
                veces[marca] = v = (0, []);
            v.Productos.Add(producto);
            veces[marca] = (v.Nombres + 1, v.Productos);
        }

        foreach (var nombre in nombres.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var partes = Leer(nombre, conMarcas: false);
            if (partes.Count < 2)
                continue;
            var producto = Normal(partes[0].Texto);
            // La última palabra de lo demás del nombre (sin el color ni la medida que vengan después), y la de antes.
            var resto = partes.Skip(1).Where(p => p.Eje is null && p.Clave.Length > 0).ToList();
            if (resto.Count == 0)
                continue;
            var ultima = resto[^1].Clave;
            if (PuedeSerMarca(ultima))
                Contar(ultima, producto);
            if (resto.Count >= 2 && PuedeSerMarca(resto[^2].Clave)
                && partes.IndexOf(resto[^1]) - partes.IndexOf(resto[^2]) == 1 && SoloLetras().IsMatch(ultima))
                Contar($"{resto[^2].Clave} {ultima}", producto);
        }
        foreach (var (marca, v) in veces)
        {
            if (v.Nombres >= 3 && v.Productos.Count >= 2)
                (marca.Contains(' ') ? _marcasDeDos : _marcas).Add(marca);
        }
        foreach (var marca in marcas ?? [])
        {
            var normal = Normal(marca);
            if (normal.Length > 0)
                (normal.Contains(' ') ? _marcasDeDos : _marcas).Add(normal);
        }
    }

    /// <summary>Los pedazos del nombre, en orden; cada palabra va en uno.</summary>
    public IReadOnlyList<ParteDeNombre> Leer(string nombre) => Leer(nombre, conMarcas: true);

    private List<ParteDeNombre> Leer(string nombre, bool conMarcas)
    {
        var palabras = nombre.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normales = palabras.Select(Normal).ToArray();
        var partes = new List<ParteDeNombre>(palabras.Length);
        if (palabras.Length == 0)
            return partes;
        // La primera palabra es el producto: nunca un color ni una medida («Rosa C/Oso», «Colores C/12»).
        partes.Add(new ParteDeNombre(palabras[0], null, normales[0]));
        var deCuaderno = Cuadernos.Contains(normales[0]);

        for (var i = 1; i < palabras.Length; i++)
        {
            var w = normales[i];
            var siguiente = i + 1 < palabras.Length ? normales[i + 1] : null;
            var tercera = i + 2 < palabras.Length ? normales[i + 2] : null;

            void Uno(EjeFamilia? eje, string clave) => partes.Add(new ParteDeNombre(palabras[i], eje, clave));
            void Dos(EjeFamilia eje, string clave)
            {
                partes.Add(new ParteDeNombre(palabras[i], eje, clave));
                partes.Add(new ParteDeNombre(palabras[i + 1], eje, ""));
                i++;
            }

            // En cuadernos, el rayado: raya, cuadro (grande, chico, alemán), C7, blanco (sin rayas).
            if (deCuaderno && Rayado(w, siguiente) is { } rayado)
            {
                if (rayado.Palabras == 2)
                    Dos(EjeFamilia.Rayado, rayado.Clave);
                else
                    Uno(EjeFamilia.Rayado, rayado.Clave);
                continue;
            }

            // Los colores surtidos: el mismo producto en un color que no se vende por separado («V/Colores», «Colores Surt.»).
            if (EsSurtido(w) || (w is "V" or "C/V" && siguiente is "COLORES"))
            {
                Uno(EjeFamilia.Color, "SURTIDO");
                while (i + 1 < palabras.Length && EsSurtido(normales[i + 1]))
                {
                    i++;
                    partes.Add(new ParteDeNombre(palabras[i], EjeFamilia.Color, ""));
                }
                continue;
            }

            if (w == "PALO" && siguiente == "DE" && tercera == "ROSA")
            {
                partes.Add(new ParteDeNombre(palabras[i], EjeFamilia.Color, "PALO DE ROSA"));
                partes.Add(new ParteDeNombre(palabras[i + 1], EjeFamilia.Color, ""));
                partes.Add(new ParteDeNombre(palabras[i + 2], EjeFamilia.Color, ""));
                i += 2;
                continue;
            }
            if (Colores.TryGetValue(w, out var color))
            {
                // El tono compuesto le gana al color solo: «Azul Rey», «Rosa Mexicano».
                if (siguiente is not null && Tonos.TryGetValue(siguiente, out var tono))
                    Dos(EjeFamilia.Color, $"{color} {tono}");
                else
                    Uno(EjeFamilia.Color, color);
                continue;
            }

            if (Medida(w, siguiente) is { } medida)
            {
                if (medida.Palabras == 2)
                    Dos(EjeFamilia.Medida, medida.Clave);
                else
                    Uno(EjeFamilia.Medida, medida.Clave);
                continue;
            }

            if (Contenido(w, siguiente) is { } contenido)
            {
                if (contenido.Palabras == 2)
                    Dos(EjeFamilia.Contenido, contenido.Clave);
                else
                    Uno(EjeFamilia.Contenido, contenido.Clave);
                continue;
            }

            Uno(null, Relleno.Contains(w) ? "" : w);
        }

        // Lo que acompaña a los milímetros es parte de la medida: «0.7mm … P/Fino» y «1.0mm … P/Mediano».
        if (partes.Any(p => p.Eje == EjeFamilia.Medida && p.Clave.EndsWith("MM", StringComparison.Ordinal)))
        {
            for (var k = 1; k < partes.Count; k++)
            {
                if (partes[k].Eje is null && partes[k].Clave.StartsWith("P/", StringComparison.Ordinal))
                    partes[k] = partes[k] with { Eje = EjeFamilia.Medida, Clave = "" };
            }
        }

        // La marca: lo último de lo demás del nombre, aunque después venga el color o la medida («… Politec Rojo»). La de
        // dos palabras se busca primero («Paper Mate»), y pueden ir dos seguidas («Kilométrico Papermate»).
        if (conMarcas)
        {
            var k = partes.FindLastIndex(EsDelNucleo);
            while (k >= 1 && EsDelNucleo(partes[k]))
            {
                if (k >= 2 && EsDelNucleo(partes[k - 1]) && _marcasDeDos.Contains($"{partes[k - 1].Clave} {partes[k].Clave}"))
                {
                    partes[k - 1] = partes[k - 1] with { Eje = EjeFamilia.Marca, Clave = $"{partes[k - 1].Clave} {partes[k].Clave}" };
                    partes[k] = partes[k] with { Eje = EjeFamilia.Marca, Clave = "" };
                    k -= 2;
                }
                else if (_marcas.Contains(partes[k].Clave))
                {
                    partes[k] = partes[k] with { Eje = EjeFamilia.Marca };
                    k--;
                }
                else
                {
                    break;
                }
            }
        }
        return partes;
    }

    private static bool EsDelNucleo(ParteDeNombre parte) => parte.Eje is null && parte.Clave.Length > 0;

    /// <summary>Para comparar: sin acentos, en mayúsculas y sin el punto final de las abreviaturas («Surt.»).</summary>
    private static string Normal(string palabra) => Texto.Normalizar(palabra).TrimEnd('.', ',', ';', ':');

    private static bool PuedeSerMarca(string clave) =>
        clave.Length >= 3 && SoloLetras().IsMatch(clave) && !NoSonMarca.Contains(clave) && !Relleno.Contains(clave)
        && !Colores.ContainsKey(clave) && !Tonos.ContainsKey(clave) && !EsSurtido(clave);

    private static bool EsSurtido(string w) =>
        w is "V/COLORES" or "VCOLORES" or "V/COLOR" or "MULTICOLOR" or "MULTICOLORES" or "ARCOIRIS" or "COLORES"
            or "SURTIDO" or "SURTIDOS" or "SURTIDA" or "SURTIDAS" or "SURT";

    private static (string Clave, int Palabras)? Medida(string w, string? siguiente)
    {
        if (NumeroConUnidad().Match(w) is { Success: true } nu && Unidad(nu.Groups[2].Value) is { } unidad)
            return ($"{Numero(nu.Groups[1].Value)}{unidad}", 1);
        if (Dimensiones().Match(w) is { Success: true } d)
            return ($"{Numero(d.Groups[1].Value)}X{Numero(d.Groups[2].Value)}{Unidad(d.Groups[3].Value) ?? ""}", 1);
        if (SoloNumero().IsMatch(w))
        {
            if (siguiente is not null && Unidad(siguiente) is { } suUnidad)
                return ($"{Numero(w)}{suUnidad}", 2);
            // Un decimal sin unidad son milímetros: «Lapicero 1.0» es el de 1.0 mm (por confirmar en la depuración).
            if (w.Contains('.') || w.Contains(','))
                return ($"{Numero(w)}MM", 1);
            return null;
        }
        if (NumeroConGato().Match(w) is { Success: true } g)
            return ($"#{g.Groups[1].Value}", 1);
        if (w is "NO" or "NUM" && siguiente is not null && Entero().IsMatch(siguiente))
            return ($"#{siguiente}", 2);
        if (Fraccion().IsMatch(w))
            return (w, 1);
        if (w == "DOBLE" && siguiente == "CARTA")
            return ("DOBLE CARTA", 2);
        return Tamanos.TryGetValue(w, out var tamano) ? (tamano, 1) : null;
    }

    private static (string Clave, int Palabras)? Contenido(string w, string? siguiente)
    {
        if (ConContenido().Match(w) is { Success: true } c)
            return ($"C/{c.Groups[1].Value}", 1);
        if (Piezas().Match(w) is { Success: true } p)
            return ($"C/{p.Groups[1].Value}", 1);
        if (Entero().IsMatch(w) && siguiente is "PZ" or "PZS" or "PZA" or "PZAS" or "PIEZAS")
            return ($"C/{w}", 2);
        return null;
    }

    private static (string Clave, int Palabras)? Rayado(string w, string? siguiente)
    {
        if (w is "RAYA" or "RAYAS" or "RAYADO" or "RAYADA")
            return ("RAYA", 1);
        if (w == "DOBLE" && siguiente is "RAYA" or "RAYAS")
            return ("DOBLE RAYA", 2);
        if (w is "CUADRO" or "CUADROS" or "CUADRICULA")
            return (siguiente is "GRANDE" or "CHICO" or "ALEMAN") ? ($"CUADRO {siguiente}", 2) : ("CUADRO", 1);
        if (w is "BLANCO" or "BLANCA" or "BLANCAS" or "BLANCOS")
            return ("BLANCO", 1);
        return CuadroDeCuaderno().IsMatch(w) ? (w, 1) : null;
    }

    /// <summary>«1.0» es «1», «0.70» es «0.7» y «.5» es «0.5».</summary>
    private static string Numero(string texto) =>
        decimal.Parse(texto.Replace(',', '.') is ['.', ..] t ? "0" + t : texto.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture)
            .ToString("0.###", CultureInfo.InvariantCulture);

    private static string? Unidad(string u) => u switch
    {
        "MM" => "MM",
        "CM" => "CM",
        "M" or "MT" or "MTS" => "M",
        "ML" => "ML",
        "L" or "LT" or "LTS" => "L",
        "G" or "GR" or "GRS" or "GMS" => "G",
        "KG" or "K" => "KG",
        "OZ" => "OZ",
        "H" or "HJ" or "HJS" or "HOJAS" => "H",
        "PULG" => "PULG",
        "" => null,
        _ => null,
    };

    /// <summary>Para ordenar las medidas de menor a mayor: el primer número, o el lugar de la palabra.</summary>
    public static double OrdenDeMedida(string clave)
    {
        if (Tamanos.ContainsValue(clave) || clave == "DOBLE CARTA")
            return clave switch
            {
                "MINI" => 1, "CHICO" => 2, "MEDIANO" => 3, "GRANDE" => 4, "JUMBO" => 5, "GIGANTE" => 6,
                "ESQUELA" => 10, "CARTA" => 11, "A4" => 11.5, "OFICIO" => 12, _ => 13,
            };
        var numero = PrimerNumero().Match(clave);
        if (!numero.Success)
            return double.MaxValue;
        var valor = double.Parse(numero.Value, CultureInfo.InvariantCulture);
        return Fraccion().Match(clave) is { Success: true } f && f.Value == clave
            ? valor / double.Parse(clave[(clave.IndexOf('/') + 1)..], CultureInfo.InvariantCulture)
            : valor;
    }

    private static readonly HashSet<string> Cuadernos = ["CUADERNO", "CUADERNOS", "LIBRETA", "LIBRETAS", "BLOCK", "BLOC", "CUADERNILLO"];

    private static readonly HashSet<string> Relleno = ["DE", "DEL", "Y", "O", "X", "CON", "PARA", "EL", "LA", "LOS", "LAS", "EN", "-", "/"];

    /// <summary>Los colores y cómo se comparan: sin género ni plural («Negra» = «Negro»).</summary>
    private static readonly Dictionary<string, string> Colores = Lista(
        ("NEGRO", ["NEGRO", "NEGRA", "NEGROS", "NEGRAS"]),
        ("BLANCO", ["BLANCO", "BLANCA", "BLANCOS", "BLANCAS"]),
        ("AZUL", ["AZUL", "AZULES"]),
        ("ROJO", ["ROJO", "ROJA", "ROJOS", "ROJAS"]),
        ("VERDE", ["VERDE", "VERDES"]),
        ("AMARILLO", ["AMARILLO", "AMARILLA", "AMARILLOS", "AMARILLAS"]),
        ("ROSA", ["ROSA", "ROSAS", "ROSADO", "ROSADA"]),
        ("MORADO", ["MORADO", "MORADA", "MORADOS", "MORADAS"]),
        ("NARANJA", ["NARANJA", "NARANJAS"]),
        ("CAFE", ["CAFE", "CAFES"]),
        ("GRIS", ["GRIS", "GRISES"]),
        ("LILA", ["LILA", "LILAS"]),
        ("VIOLETA", ["VIOLETA", "VIOLETAS"]),
        ("TURQUESA", ["TURQUESA", "TURQUESAS"]),
        ("FUCSIA", ["FUCSIA", "FIUSHA"]),
        ("MAGENTA", ["MAGENTA"]),
        ("DORADO", ["DORADO", "DORADA", "DORADOS", "DORADAS", "ORO"]),
        ("PLATA", ["PLATA", "PLATEADO", "PLATEADA", "PLATEADOS", "PLATEADAS"]),
        ("COBRE", ["COBRE"]),
        ("TRANSPARENTE", ["TRANSPARENTE", "TRANSPARENTES"]),
        ("CREMA", ["CREMA"]),
        ("HUESO", ["HUESO"]),
        ("MARFIL", ["MARFIL"]),
        ("BEIGE", ["BEIGE"]),
        ("OCRE", ["OCRE"]),
        ("VINO", ["VINO"]),
        ("GUINDA", ["GUINDA"]),
        ("CORAL", ["CORAL"]),
        ("LAVANDA", ["LAVANDA"]),
        ("MALVA", ["MALVA"]),
        ("CELESTE", ["CELESTE"]),
        ("MARINO", ["MARINO"]),
        ("MELON", ["MELON"]),
        ("TERRACOTA", ["TERRACOTA"]),
        ("CARMIN", ["CARMIN"]),
        ("CIAN", ["CIAN"]),
        ("SEPIA", ["SEPIA"]));

    /// <summary>Lo que hace compuesto a un color («Azul Rey», «Verde Limón»). Neón, pastel solo o metálico son el acabado.</summary>
    private static readonly Dictionary<string, string> Tonos = Lista(
        ("REY", ["REY"]), ("MARINO", ["MARINO"]), ("CIELO", ["CIELO"]), ("CLARO", ["CLARO", "CLARA"]),
        ("OBSCURO", ["OBSCURO", "OBSCURA", "OSCURO", "OSCURA"]), ("ELECTRICO", ["ELECTRICO"]), ("PASTEL", ["PASTEL"]),
        ("CELESTE", ["CELESTE"]), ("COBALTO", ["COBALTO"]), ("ULTRAMAR", ["ULTRAMAR"]), ("TURQUESA", ["TURQUESA"]),
        ("ROYAL", ["ROYAL"]), ("LIMON", ["LIMON"]), ("BANDERA", ["BANDERA"]), ("MANZANA", ["MANZANA"]),
        ("ESMERALDA", ["ESMERALDA"]), ("HOJA", ["HOJA"]), ("OLIVO", ["OLIVO"]), ("MILITAR", ["MILITAR"]), ("JADE", ["JADE"]),
        ("MEXICANO", ["MEXICANO"]), ("FUERTE", ["FUERTE"]), ("BEBE", ["BEBE"]), ("CEREZA", ["CEREZA"]),
        ("CANARIO", ["CANARIO"]), ("HUEVO", ["HUEVO"]), ("MEDIO", ["MEDIO"]), ("INTENSO", ["INTENSO"]),
        ("NIEVE", ["NIEVE"]), ("TITANIO", ["TITANIO"]));

    private static readonly Dictionary<string, string> Tamanos = Lista(
        ("CARTA", ["CARTA"]), ("OFICIO", ["OFICIO"]), ("ESQUELA", ["ESQUELA"]), ("A4", ["A4"]),
        ("MINI", ["MINI"]), ("CHICO", ["CHICO", "CHICA", "CHICOS", "CHICAS"]),
        ("MEDIANO", ["MEDIANO", "MEDIANA", "MEDIANOS", "MEDIANAS"]), ("GRANDE", ["GRANDE", "GRANDES"]),
        ("JUMBO", ["JUMBO"]), ("GIGANTE", ["GIGANTE", "GIGANTES"]));

    /// <summary>Palabras que se repiten al final de muchos nombres sin ser marca: acabados, empaques, para quién y sabores.</summary>
    private static readonly HashSet<string> NoSonMarca =
    [
        "ESCOLAR", "ESCOLARES", "INFANTIL", "PROFESIONAL", "PROF", "PLASTICO", "PLASTICA", "METALICO", "METALICA", "ADHESIVO",
        "ADHESIVA", "ESPIRAL", "COSIDO", "DOBLE", "SENCILLO", "GRUESO", "GRUESA", "DELGADO", "DELGADA", "FINO", "FINA",
        "LARGO", "LARGOS", "CORTO", "CORTOS", "REDONDO", "REDONDA", "TRIANGULAR", "TRIANGULARES", "HEXAGONAL", "BORRABLE",
        "PERMANENTE", "LAVABLE", "RETRACTIL", "NEON", "PASTEL", "METALIZADO", "GLITTER", "DIAMANTADO", "HOLOGRAFICO", "MATE",
        "BRILLANTE", "LISO", "LISA", "ESTAMPADO", "NIÑO", "NIÑA", "NIÑOS", "NIÑAS", "DAMA", "CABALLERO", "HOMBRE", "MUJER",
        "BEBE", "PIEZA", "PIEZAS", "PZ", "PZA", "PZS", "PAQ", "PAQUETE", "BOLSA", "CAJA", "BLISTER", "ROLLO", "METRO",
        "SET", "JUEGO", "JGO", "SUELTO", "SUELTA", "MAYOREO", "MAY", "MENUDEO", "UNIDAD", "GRANEL", "KILO", "LITRO",
        "FRESA", "MANGO", "UVA", "LIMON", "PIÑA", "MANZANA", "CHOCOLATE", "VAINILLA", "COCO", "TAMARINDO", "CHAMOY",
        "CEREZA", "SANDIA", "DURAZNO", "GUAYABA", "NUEZ", "CAJETA", "MORA", "FRAMBUESA", "MENTA", "CANELA", "NATURAL",
        "ORIGINAL", "CLASICO", "CLASICA", "LIGHT", "NUEVO", "NUEVA", "DECORADO", "DECORADA", "DECORATIVO", "DECORATIVA",
    ];

    private static Dictionary<string, string> Lista(params (string Clave, string[] Escrituras)[] valores) =>
        valores.SelectMany(v => v.Escrituras.Select(e => (e, v.Clave))).ToDictionary(x => x.e, x => x.Clave);

    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?|[.,]\d+)(MM|CM|MTS|MT|M|ML|LTS|LT|L|GRS|GR|GMS|G|KG|K|OZ|HJS|HJ|H|HOJAS|PULG)$")]
    private static partial Regex NumeroConUnidad();

    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?|[.,]\d+)X(\d+(?:[.,]\d+)?)(CM|MM|MTS|MT|M)?$")]
    private static partial Regex Dimensiones();

    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?|[.,]\d+)$")]
    private static partial Regex SoloNumero();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Entero();

    [GeneratedRegex(@"^(?:#|NO\.?|N°)(\d+)$")]
    private static partial Regex NumeroConGato();

    [GeneratedRegex(@"^\d+/\d+$")]
    private static partial Regex Fraccion();

    [GeneratedRegex(@"^C/(\d+)$")]
    private static partial Regex ConContenido();

    [GeneratedRegex(@"^(\d+)(PZ|PZS|PZA|PZAS|PIEZAS)$")]
    private static partial Regex Piezas();

    [GeneratedRegex(@"^C\d{1,2}$")]
    private static partial Regex CuadroDeCuaderno();

    [GeneratedRegex(@"^[A-ZÑ]+$")]
    private static partial Regex SoloLetras();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex PrimerNumero();
}

/// <summary>Un valor de una familia: el producto, cómo se lee («Negro», «0.7mm P/Fino») y cómo se compara.</summary>
public sealed record ValorDeFamilia<T>(T Elemento, string Texto, string Clave);

/// <summary>
/// Un renglón de la lista: una familia (lo que solo cambia en <see cref="Eje"/>, con un valor por producto) o un producto
/// suelto (sin eje y con un solo valor).
/// </summary>
/// <param name="Titulo">El nombre del más vendido sin lo que cambia.</param>
/// <param name="MasVendido">El valor que agrega Enter.</param>
public sealed record RenglonDeFamilia<T>(EjeFamilia? Eje, string? Titulo, IReadOnlyList<ValorDeFamilia<T>> Valores, int MasVendido)
{
    public bool EsFamilia => Eje is not null;

    public T Elegido => Valores[MasVendido].Elemento;
}

/// <param name="Eje">Lo que se despliega; null si nada se junta.</param>
/// <param name="Ejes">Lo que cambia entre los resultados, en orden: lo que se puede desplegar.</param>
public sealed record AgrupacionDeFamilias<T>(EjeFamilia? Eje, IReadOnlyList<EjeFamilia> Ejes, IReadOnlyList<RenglonDeFamilia<T>> Renglones);

/// <summary>
/// Junta los resultados de una búsqueda en familias: lo que solo cambia en un eje va en un renglón. El eje es el
/// primero que cambia entre los resultados (color, medida, marca, contenido; en cuadernos, el rayado primero), o el que
/// se eligió. Con «lapicero negro» el color ya no cambia: se despliega la medida.
/// </summary>
public static class Familias
{
    private static readonly EjeFamilia[] Orden = [EjeFamilia.Color, EjeFamilia.Medida, EjeFamilia.Marca, EjeFamilia.Contenido, EjeFamilia.Rayado];

    /// <param name="elementos">En el orden de la búsqueda: los renglones salen en el orden de su primer producto.</param>
    /// <param name="vendidos">Cuántas veces se vendió: en cada familia, Enter agrega el más vendido.</param>
    /// <param name="preferido">El eje que se eligió con Tab; si no cambia entre estos resultados, el primero que sí.</param>
    public static AgrupacionDeFamilias<T> Agrupar<T>(
        IReadOnlyList<T> elementos, Func<T, IReadOnlyList<ParteDeNombre>> partes, Func<T, int> vendidos, EjeFamilia? preferido = null)
    {
        var leidos = elementos.Select(e => (Elemento: e, Partes: partes(e))).ToList();
        var orden = leidos.Count > 0 && leidos[0].Partes.FirstOrDefault()?.Clave is { } producto && EsCuaderno(producto)
            ? Orden.OrderBy(e => e == EjeFamilia.Rayado ? 0 : 1).ToArray()
            : Orden;
        var ejes = orden.Where(eje => Grupos(leidos, eje).Any(g => g.Count >= 2 && g.Select(x => Clave(x.Partes, eje)).Distinct().Count() >= 2)).ToList();
        EjeFamilia? elegido = preferido is { } p && ejes.Contains(p) ? p : ejes.Count > 0 ? ejes[0] : null;

        var renglones = new List<(int Primero, RenglonDeFamilia<T> Renglon)>();
        var enFamilia = new HashSet<int>();
        if (elegido is { } eje)
        {
            foreach (var grupo in Grupos(leidos, eje).Where(g => g.Count >= 2))
            {
                var indices = grupo.Select(x => leidos.IndexOf(x)).ToList();
                enFamilia.UnionWith(indices);
                // El más vendido; si empatan, el que salió antes en la búsqueda.
                var masVendido = grupo.OrderByDescending(x => vendidos(x.Elemento)).ThenBy(x => leidos.IndexOf(x)).First();
                var valores = OrdenarValores(grupo, eje, vendidos)
                    .Select(x => new ValorDeFamilia<T>(x.Elemento, Valor(x.Partes, eje), Clave(x.Partes, eje)))
                    .ToList();
                var indice = valores.FindIndex(v => Equals(v.Elemento, masVendido.Elemento));
                renglones.Add((indices.Min(), new RenglonDeFamilia<T>(eje, Titulo(masVendido.Partes, eje), valores, indice)));
            }
        }
        for (var k = 0; k < leidos.Count; k++)
        {
            if (!enFamilia.Contains(k))
                renglones.Add((k, new RenglonDeFamilia<T>(null, null, [new ValorDeFamilia<T>(leidos[k].Elemento, "", "")], 0)));
        }
        return new AgrupacionDeFamilias<T>(elegido, ejes, renglones.OrderBy(r => r.Primero).Select(r => r.Renglon).ToList());
    }

    /// <summary>El nombre sin lo que cambia: «Lapicero 0.7mm P/Fino Azor» de «Lapicero 0.7mm Negro P/Fino Azor».</summary>
    public static string Titulo(IReadOnlyList<ParteDeNombre> partes, EjeFamilia eje) =>
        string.Join(" ", partes.Where(p => p.Eje != eje).Select(p => p.Texto));

    /// <summary>Lo que cambia, como se escribió: «Negro», «Colores Surt.», «0.7mm P/Fino».</summary>
    public static string Valor(IReadOnlyList<ParteDeNombre> partes, EjeFamilia eje) =>
        string.Join(" ", partes.Where(p => p.Eje == eje).Select(p => p.Texto));

    public static string Clave(IReadOnlyList<ParteDeNombre> partes, EjeFamilia eje) =>
        string.Join(" ", partes.Where(p => p.Eje == eje && p.Clave.Length > 0).Select(p => p.Clave));

    private static bool EsCuaderno(string producto) => producto is "CUADERNO" or "CUADERNOS" or "LIBRETA" or "LIBRETAS" or "BLOCK" or "BLOC";

    /// <summary>Los que son iguales en todo menos en el eje (sin importar el orden de las palabras). El que no tiene ese eje va solo.</summary>
    private static List<List<(T Elemento, IReadOnlyList<ParteDeNombre> Partes)>> Grupos<T>(
        List<(T Elemento, IReadOnlyList<ParteDeNombre> Partes)> leidos, EjeFamilia eje) =>
        leidos.Where(x => Clave(x.Partes, eje).Length > 0)
            .GroupBy(x => string.Join("|", x.Partes.Where(p => p.Eje != eje && p.Clave.Length > 0).Select(p => $"{p.Eje}:{p.Clave}").Order(StringComparer.Ordinal)))
            .Select(g => g.ToList())
            .ToList();

    /// <summary>Color y marca, del más vendido al menos (el surtido al final); medida y contenido, de menor a mayor.</summary>
    private static IEnumerable<(T Elemento, IReadOnlyList<ParteDeNombre> Partes)> OrdenarValores<T>(
        List<(T Elemento, IReadOnlyList<ParteDeNombre> Partes)> grupo, EjeFamilia eje, Func<T, int> vendidos) => eje switch
    {
        EjeFamilia.Medida => grupo.OrderBy(x => LectorDeNombres.OrdenDeMedida(Clave(x.Partes, eje))).ThenBy(x => Clave(x.Partes, eje), StringComparer.Ordinal),
        EjeFamilia.Contenido => grupo.OrderBy(x => LectorDeNombres.OrdenDeMedida(Clave(x.Partes, eje))),
        _ => grupo.OrderBy(x => Clave(x.Partes, eje) == "SURTIDO").ThenByDescending(x => vendidos(x.Elemento)),
    };
}
