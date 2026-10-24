using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>Lo de cada renglón de la hoja «Productos» (una variante que se vende), por su columna.</summary>
public enum CampoDeProducto
{
    /// <summary>El código de su pieza (o metro, kilo, litro): con él se reconoce el producto al importar.</summary>
    Codigo,

    /// <summary>Los demás códigos de su pieza, separados por comas.</summary>
    OtrosCodigos,

    Producto,

    /// <summary>La descripción de la variante («Azul»); vacía si el producto tiene una sola.</summary>
    Variante,

    Departamento,
    Marca,

    /// <summary>«piezas», «metros», «kilos» o «litros».</summary>
    Unidad,

    /// <summary>El de menudeo, por unidad.</summary>
    Precio,

    /// <summary>Solo se lee para decir que no se importa: cambia con las compras, los conteos y los ajustes.</summary>
    Existencia,

    Minimo,
    Maximo,

    /// <summary>«Sí» o «No».</summary>
    SeVende,

    /// <summary>Solo con «Ver costos, utilidad y margen»; no se importa (sale de las compras).</summary>
    CostoPromedio,

    /// <summary>Solo con «Ver costos, utilidad y margen»; no se importa.</summary>
    Margen,

    /// <summary>
    /// La clave de la variante, en una columna escondida al final: con ella se reconoce el renglón de un producto sin código (y se
    /// sabe de quién era un renglón al exportarlo). Un renglón que se escribe a mano no la trae.
    /// </summary>
    Clave,
}

/// <summary>Lo de cada renglón de la hoja «Presentaciones» (las cajas y paquetes, con sus códigos y su precio propio).</summary>
public enum CampoDePresentacion
{
    /// <summary>El código de la pieza del producto, el de la columna «Código» de la hoja «Productos».</summary>
    CodigoDelProducto,

    /// <summary>Solo para leerlo: el producto se reconoce por su código.</summary>
    Producto,

    Presentacion,

    /// <summary>Cuántas unidades (piezas, metros…) trae.</summary>
    Contiene,

    Codigos,

    /// <summary>Su precio propio, por presentación; vacío: se cobra por sus piezas.</summary>
    Precio,

    /// <summary>La clave de la variante de su producto, escondida al final: con ella se reconocen las presentaciones de uno sin código.</summary>
    Clave,
}

/// <summary>Por qué un renglón del Excel no se importa.</summary>
public enum PorQueSeIgnora
{
    /// <summary>Sin código, y no hay un producto sin código que se llame así.</summary>
    SinCodigo,

    /// <summary>El mismo código en dos renglones (o dos veces en un renglón).</summary>
    CodigoRepetido,

    /// <summary>El mismo producto (o la misma presentación) en dos renglones, con códigos distintos.</summary>
    Repetido,

    /// <summary>Uno de sus códigos ya es de otro producto, de otra variante o de una presentación.</summary>
    CodigoDeOtroProducto,

    /// <summary>Un código escrito como número al que Excel le quitó los ceros de adelante.</summary>
    CodigoSinCeros,

    /// <summary>Un código que no se puede leer: Excel lo guardó como número y perdió cifras, o es demasiado largo.</summary>
    CodigoInvalido,

    /// <summary>Le falta un dato que no puede quedar vacío: el nombre, el departamento, el precio, la mitad de una escala.</summary>
    FaltaDato,

    /// <summary>Un número que no se entiende, en cero o negativo, demasiado grande, o con decimales en piezas.</summary>
    NumeroInvalido,

    /// <summary>Un número con coma que podría leerse de dos maneras: «12,50».</summary>
    ComaDecimal,

    /// <summary>Otro dato que no se entiende: la unidad, «Se vende», un error de Excel, una fórmula sin calcular.</summary>
    DatoInvalido,

    /// <summary>Su mayoreo quedaría igual o más caro que el menudeo (o un paquete, más caro que sus piezas).</summary>
    MayoreoMasCaro,

    /// <summary>Un precio que cambia quedaría debajo de su costo promedio.</summary>
    DebajoDelCosto,

    /// <summary>Su departamento no existe y no se puede dar de alta.</summary>
    DepartamentoNoExiste,

    /// <summary>Su departamento está desactivado (Productos › Departamentos).</summary>
    DepartamentoDesactivado,

    /// <summary>Las variantes de un mismo producto traen distinto nombre, departamento o marca.</summary>
    DatosDistintos,

    /// <summary>Algo que no se cambia desde Excel: la unidad, lo que contiene una presentación, un kit.</summary>
    NoSeCambiaDesdeExcel,

    /// <summary>En la hoja «Presentaciones»: ningún producto tiene ese código.</summary>
    ProductoNoExiste,

    /// <summary>En la hoja «Presentaciones»: su producto nuevo se ignoró.</summary>
    SuProductoSeIgnoro,
}

/// <summary>Por qué se ignora un renglón, con lo que se le dice a quien lo importa.</summary>
public sealed record Rechazo(PorQueSeIgnora PorQue, string Detalle);

/// <summary>
/// Lo que conviene ver antes de aplicar (en la vista va primero y resaltado): lo que baja, lo que regresa a como estaba al
/// exportar, lo que deja de venderse o vuelve a venderse, lo que cambia de departamento y los códigos que se quitan.
/// </summary>
public enum PorQueSeRevisa
{
    /// <summary>Un precio (el menudeo, una escala o el de una presentación) que baja.</summary>
    BajaDePrecio,

    /// <summary>Regresa un precio a como estaba al exportar el Excel: alguien lo cambió después.</summary>
    RegresaLoDeAntes,

    /// <summary>Deja de venderse o vuelve a venderse.</summary>
    SeVende,

    /// <summary>Pasa a otro departamento.</summary>
    Departamento,

    /// <summary>Se le quitan códigos (de su pieza o de una presentación).</summary>
    QuitaCodigos,
}

/// <summary>
/// Con qué catálogo se exportó el Excel: la versión que tenía (<c>Secuencia</c> «catalogo»), cuándo y de qué sucursal. Va
/// escondido en las propiedades del libro (Excel las guarda al guardarlo); con ella se sabe qué cambió después de exportarlo.
/// </summary>
/// <param name="Version">La versión del catálogo al exportarlo: lo que tiene una mayor cambió después.</param>
/// <param name="ExportadoUtc">Cuándo: para lo que no sube la versión (el departamento de un producto, el nombre de un departamento).</param>
public sealed record ExportacionDelLibro(long Version, DateTime ExportadoUtc, Guid SucursalId);

/// <summary>
/// Productos (F3) › «Productos en Excel» (2026-10-22, como «Exportar» e «Importar» del catálogo de eleventa): el Excel de los
/// productos que arma la caja principal y que se vuelve a importar. Lo que comparten las dos partes: las hojas, los títulos de
/// las columnas y cómo se lee lo que trae cada celda. Así lo que se exporta es exactamente lo que se importa.
/// </summary>
public static class ProductosEnExcel
{
    public const string HojaProductos = "Productos";
    public const string HojaPresentaciones = "Presentaciones";

    /// <summary>
    /// Lo más que se importa de una vez: renglones de cada hoja. Todo va en una sola transacción y la vista previa se lee completa
    /// antes de aplicar; con más, se parte en dos archivos (o se filtra al exportar).
    /// </summary>
    public const int MaximoRenglones = 10_000;

    /// <summary>
    /// Lo más que pesa el archivo que se importa: 8 MB (el Excel de 10,000 productos pesa como 1 MB; lo demás es el formato que
    /// le ponga Excel).
    /// </summary>
    public const int BytesMaximos = 8 * 1024 * 1024;

    /// <summary>Las escalas de mayoreo que lleva siempre la hoja (aunque nadie tenga), para que se pueda agregar una.</summary>
    public const int EscalasMinimas = 2;

    /// <summary>Lo más largo que se acepta en el nombre de un producto o de su variante.</summary>
    public const int LargoNombre = 200;

    /// <summary>Lo más largo que se acepta en el nombre de una marca o de una presentación.</summary>
    public const int LargoCorto = 80;

    /// <summary>Lo más largo de un código: más ya no es de un lector ni de una tienda.</summary>
    public const int LargoCodigo = 64;

    /// <summary>El precio más alto que se acepta: $100,000.00, como en «Cambiar precios». Más parece un error de dedo.</summary>
    public const long MaximoCentavos = CambioDePrecios.MaximoCentavos;

    /// <summary>
    /// Lo que dicen la caja y el servicio en el piloto con eleventa (el dueño, 2026-10-10: las altas y los precios, en eleventa;
    /// #106): aquí no se importa; exportar, sí.
    /// </summary>
    public const string ImportarEnElPiloto =
        "Mientras dure el piloto con eleventa, los productos se dan de alta y se cambian en eleventa: llegan aquí con su lectura, cada pocos "
        + "minutos. Por eso aquí no se importa de Excel; exportar, sí.";

    /// <summary>Al aplicar, si lo que se vio ya no es lo que quedaría (alguien cambió un precio, un código o un producto).</summary>
    public const string CambioDesdeLaVista =
        "Algo cambió desde que se vio qué cambia (un precio, un código o un producto): revísalo otra vez antes de aplicar.";

    /// <summary>Las propiedades escondidas del libro con que se sabe de cuándo es (<see cref="ExportacionDelLibro"/>).</summary>
    public const string PropiedadVersion = "PapeleriaPos.VersionDelCatalogo";

    public const string PropiedadExportado = "PapeleriaPos.ExportadoUtc";
    public const string PropiedadSucursal = "PapeleriaPos.Sucursal";

    /// <summary>Lo que se dice de un Excel que no trae con qué catálogo se exportó (de antes, o hecho en otro lado).</summary>
    public const string SinVersion =
        "Este Excel no dice cuándo se exportó (lo exportó una versión anterior de la caja, o se armó en otro lado): los productos que ya existen "
        + "pudieron cambiar después, así que lo que cambia en ellos no se aplica hasta que lo revises y elijas «Aplicarlos de todos modos». Lo "
        + "nuevo, sí.";

    /// <summary>Lo que dice cada uno de esos productos.</summary>
    public const string NoSeSabeSiCambio = "No se sabe si cambió después: el Excel no dice cuándo se exportó.";

    /// <summary>
    /// Lo que separa los códigos en «Otros códigos» y en «Códigos» de las presentaciones. Un código que trae uno de ellos no va en
    /// esas columnas (se partiría): se cambia en su ficha, y la importación no lo quita.
    /// </summary>
    public static readonly char[] SeparadoresDeCodigos = [',', ';', '\n', '\r'];

    /// <summary>Si el código no se puede escribir en «Otros códigos» (trae coma o punto y coma).</summary>
    public static bool NoCabeEnUnaLista(string codigo) => codigo.IndexOfAny(SeparadoresDeCodigos) >= 0;

    /// <summary>El título de cada columna de la hoja «Productos», como sale en el Excel.</summary>
    public static string Titulo(CampoDeProducto campo) => campo switch
    {
        CampoDeProducto.Codigo => "Código",
        CampoDeProducto.OtrosCodigos => "Otros códigos",
        CampoDeProducto.Producto => "Producto",
        CampoDeProducto.Variante => "Variante",
        CampoDeProducto.Departamento => "Departamento",
        CampoDeProducto.Marca => "Marca",
        CampoDeProducto.Unidad => "Unidad",
        CampoDeProducto.Precio => "Precio",
        CampoDeProducto.Existencia => "Existencia",
        CampoDeProducto.Minimo => "Mínimo",
        CampoDeProducto.Maximo => "Máximo",
        CampoDeProducto.SeVende => "Se vende",
        CampoDeProducto.CostoPromedio => "Costo promedio",
        CampoDeProducto.Margen => "Margen",
        _ => ClaveInterna,
    };

    /// <summary>El título de la columna escondida con la clave de cada variante, en las dos hojas.</summary>
    public const string ClaveInterna = "Clave interna";

    /// <summary>El título de cada columna de la hoja «Presentaciones».</summary>
    public static string Titulo(CampoDePresentacion campo) => campo switch
    {
        CampoDePresentacion.CodigoDelProducto => "Código del producto",
        CampoDePresentacion.Producto => "Producto",
        CampoDePresentacion.Presentacion => "Presentación",
        CampoDePresentacion.Contiene => "Contiene",
        CampoDePresentacion.Codigos => "Códigos",
        CampoDePresentacion.Precio => "Precio",
        _ => ClaveInterna,
    };

    /// <summary>«Mayoreo 1: desde»: desde cuántas unidades (o presentaciones) va esa escala.</summary>
    public static string MayoreoDesde(int numero) => $"Mayoreo {numero}: desde";

    /// <summary>«Mayoreo 1: precio»: el precio de esa escala, por unidad (o por presentación).</summary>
    public static string MayoreoPrecio(int numero) => $"Mayoreo {numero}: precio";

    private static readonly Regex TituloDeMayoreo = new(@"^MAYOREO (\d{1,2}) (DESDE|PRECIO)$", RegexOptions.CultureInvariant);

    /// <summary>De qué columna de «Productos» es un título, sin importar mayúsculas, acentos ni espacios de más; vacío si no es de ninguna.</summary>
    public static CampoDeProducto? CampoDe(string titulo) => Buscar(titulo, Enum.GetValues<CampoDeProducto>(), Titulo);

    /// <summary>De qué columna de «Presentaciones» es un título; vacío si no es de ninguna.</summary>
    public static CampoDePresentacion? CampoDePresentacionDe(string titulo) => Buscar(titulo, Enum.GetValues<CampoDePresentacion>(), Titulo);

    /// <summary>Si el título es de una escala de mayoreo: cuál (1, 2…) y si es su precio (si no, su «desde»).</summary>
    public static (int Numero, bool Precio)? MayoreoDe(string titulo)
    {
        // Con o sin los dos puntos: «Mayoreo 1: desde», «Mayoreo 1 desde».
        var hallado = TituloDeMayoreo.Match(Texto.Normalizar(titulo.Replace(':', ' ')));
        return hallado.Success && int.TryParse(hallado.Groups[1].Value, CultureInfo.InvariantCulture, out var numero) && numero > 0
            ? (numero, hallado.Groups[2].Value == "PRECIO")
            : null;
    }

    private static T? Buscar<T>(string titulo, IEnumerable<T> campos, Func<T, string> nombre) where T : struct, Enum
    {
        var normal = Texto.Normalizar(titulo);
        foreach (var campo in campos)
        {
            if (Texto.Normalizar(nombre(campo)) == normal)
                return campo;
        }
        return null;
    }

    /// <summary>Cómo se escribe la unidad en el Excel: «piezas», «metros», «kilos» o «litros» (como en los demás Excel).</summary>
    public static string Unidad(UnidadBase unidad) => unidad switch
    {
        UnidadBase.Metro => "metros",
        UnidadBase.Kilogramo => "kilos",
        UnidadBase.Litro => "litros",
        _ => "piezas",
    };

    /// <summary>El nombre de la presentación base de un producto nuevo, como en la depuración: «Pieza», «Metro», «Kilo» o «Litro».</summary>
    public static string PresentacionBase(UnidadBase unidad) => unidad switch
    {
        UnidadBase.Metro => "Metro",
        UnidadBase.Kilogramo => "Kilo",
        UnidadBase.Litro => "Litro",
        _ => "Pieza",
    };
}

/// <summary>Qué hay en una celda del Excel.</summary>
public enum TipoCelda
{
    Vacia,
    Texto,
    Numero,

    /// <summary>VERDADERO o FALSO de Excel.</summary>
    SiNo,

    /// <summary>Un error de Excel (#N/A, #¡REF!…) o una fórmula que no se pudo calcular: <see cref="CeldaLeida.Texto"/> dice cuál.</summary>
    Error,
}

/// <summary>
/// Lo que trae una celda, ya sin Excel de por medio: así se lee y se prueba sin ClosedXML. Una fórmula llega como su valor (el que
/// guardó Excel al calcularla).
/// </summary>
public readonly record struct CeldaLeida(TipoCelda Tipo, string Texto, double Numero, bool SiNo)
{
    public static readonly CeldaLeida Vacia = new(TipoCelda.Vacia, "", 0, false);

    /// <summary>Un texto; solo espacios es una celda vacía.</summary>
    public static CeldaLeida DeTexto(string texto) => string.IsNullOrWhiteSpace(texto) ? Vacia : new(TipoCelda.Texto, texto, 0, false);

    public static CeldaLeida DeNumero(double numero) => new(TipoCelda.Numero, "", numero, false);

    public static CeldaLeida DeSiNo(bool valor) => new(TipoCelda.SiNo, "", 0, valor);

    /// <summary>Un error de Excel o una fórmula sin calcular, con lo que se dice de él.</summary>
    public static CeldaLeida DeError(string error) => new(TipoCelda.Error, error, 0, false);

    public bool EsVacia => Tipo == TipoCelda.Vacia;

    /// <summary>Lo que se ve en la celda, para los mensajes: el texto (sin espacios en las orillas) o el número.</summary>
    public string Escrito => Tipo switch
    {
        TipoCelda.Texto => Texto.Trim(),
        TipoCelda.Numero => Numero.ToString("0.###############", CultureInfo.InvariantCulture),
        TipoCelda.SiNo => SiNo ? "VERDADERO" : "FALSO",
        TipoCelda.Error => Texto,
        _ => "",
    };
}

/// <summary>Un renglón de una hoja: el número de renglón de Excel y lo de cada columna que se reconoció.</summary>
/// <param name="Fila">Como la ve quien lo abre en Excel (la primera es 1).</param>
/// <param name="Mayoreo">Cada escala de mayoreo que trae la hoja (por su número), con su «desde» y su precio.</param>
public sealed record RenglonLeido<TCampo>(
    int Fila, IReadOnlyDictionary<TCampo, CeldaLeida> Celdas, IReadOnlyDictionary<int, (CeldaLeida Desde, CeldaLeida Precio)> Mayoreo)
    where TCampo : struct, Enum
{
    /// <summary>Lo de esa columna; vacía si la hoja no la trae.</summary>
    public CeldaLeida this[TCampo campo] => Celdas.TryGetValue(campo, out var celda) ? celda : CeldaLeida.Vacia;
}

/// <summary>Una hoja del Excel que se importa, ya leída.</summary>
/// <param name="Columnas">Las columnas que trae (las que no, se quedan como están en cada producto).</param>
/// <param name="Mayoreo">Las escalas de mayoreo que trae (1, 2…); sin ninguna, el mayoreo se queda como está.</param>
/// <param name="SinUsar">Los títulos que no son de ninguna columna conocida: se dice que no se usan.</param>
public sealed record HojaLeida<TCampo>(
    string Nombre, IReadOnlySet<TCampo> Columnas, IReadOnlyList<int> Mayoreo, IReadOnlyList<RenglonLeido<TCampo>> Renglones,
    IReadOnlyList<string> SinUsar)
    where TCampo : struct, Enum
{
    public bool Trae(TCampo campo) => Columnas.Contains(campo);
}

/// <summary>El Excel de los productos que se importa, ya leído: la hoja «Productos» y, si la trae, la de «Presentaciones».</summary>
/// <param name="Exportado">Con qué catálogo se exportó; vacío si no lo dice (de una versión anterior de la caja, o hecho en otro lado).</param>
public sealed record LibroDeProductos(
    HojaLeida<CampoDeProducto> Productos, HojaLeida<CampoDePresentacion>? Presentaciones, ExportacionDelLibro? Exportado = null);

/// <summary>
/// Cómo se lee lo de cada celda al importar el Excel de los productos. Una fórmula llega como su valor. En los números se toleran
/// los espacios, el signo de pesos y la coma de los miles («$1,234.50», «1 234»), pero no una coma que podría ser de decimales
/// («12,50», «1.234,50»): esa se rechaza con un mensaje que dice cómo escribirlo, para que nunca se lea otro precio. El dinero se
/// redondea al centavo (lo que se ve con el formato de pesos de Excel) y las cantidades a la milésima.
/// </summary>
public static class LecturaDeCeldas
{
    /// <summary>
    /// Con la coma de los miles bien puesta (de tres en tres), y quizá decimales con punto: «1,234», «12,345.50». Antes de la
    /// primera coma no va un cero: «0,500» no es de miles (sería 500), es medio con coma de decimales.
    /// </summary>
    private static readonly Regex ConMiles = new(@"^[1-9]\d{0,2}(,\d{3})+(\.\d+)?$", RegexOptions.CultureInvariant);

    private static readonly Regex Sencillo = new(@"^(\d+(\.\d*)?|\.\d+)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Un número escrito como texto: sin espacios (también los que no se ven), con «$» al principio si lo trae, y la coma solo de
    /// los miles. Vacío si no es un número; <paramref name="comaDecimal"/> si trae una coma que podría ser de decimales.
    /// </summary>
    public static decimal? NumeroDeTexto(string escrito, out bool comaDecimal)
    {
        comaDecimal = false;
        var limpio = new StringBuilder(escrito.Length);
        foreach (var c in escrito)
        {
            if (!char.IsWhiteSpace(c) && c is not ('​' or '‌' or '‍' or '﻿'))
                limpio.Append(c);
        }
        var texto = limpio.ToString();
        var negativo = texto.StartsWith('-') || texto.StartsWith('−');
        if (negativo)
            texto = texto[1..];
        if (texto.StartsWith('$'))
            texto = texto[1..];
        if (!negativo && (texto.StartsWith('-') || texto.StartsWith('−')))
        {
            negativo = true;
            texto = texto[1..];
        }
        if (texto.Contains(','))
        {
            if (!ConMiles.IsMatch(texto))
            {
                comaDecimal = Regex.IsMatch(texto, @"^[\d.,]+$", RegexOptions.CultureInvariant);
                return null;
            }
            texto = texto.Replace(",", "");
        }
        if (!Sencillo.IsMatch(texto) || !decimal.TryParse(texto, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var valor))
            return null;
        return negativo ? -valor : valor;
    }

    /// <summary>
    /// El número de una celda: el de Excel tal cual, o el escrito como texto (<see cref="NumeroDeTexto"/>). Vacío si la celda
    /// está vacía; si no se puede leer, el rechazo dice por qué.
    /// </summary>
    public static decimal? Numero(CeldaLeida celda, string columna, out Rechazo? rechazo)
    {
        rechazo = null;
        switch (celda.Tipo)
        {
            case TipoCelda.Vacia:
                return null;
            case TipoCelda.Numero when double.IsFinite(celda.Numero) && Math.Abs(celda.Numero) < 1e15:
                return (decimal)celda.Numero;
            case TipoCelda.Numero:
                rechazo = new Rechazo(PorQueSeIgnora.NumeroInvalido, $"«{columna}» dice «{celda.Escrito}»: es demasiado grande.");
                return null;
            case TipoCelda.Texto:
                if (NumeroDeTexto(celda.Texto, out var comaDecimal) is { } valor)
                    return valor;
                rechazo = comaDecimal
                    ? new Rechazo(PorQueSeIgnora.ComaDecimal,
                        $"«{columna}» dice «{celda.Escrito}»: la coma solo separa los miles (1,250) y los decimales van con punto (12.50). Corrígelo para que no se lea otro número.")
                    : new Rechazo(PorQueSeIgnora.NumeroInvalido, $"«{columna}» dice «{celda.Escrito}»: no es un número.");
                return null;
            case TipoCelda.Error:
                rechazo = ErrorDeExcel(celda, columna);
                return null;
            default:
                rechazo = new Rechazo(PorQueSeIgnora.NumeroInvalido, $"«{columna}» dice «{celda.Escrito}»: no es un número.");
                return null;
        }
    }

    /// <summary>
    /// Dinero, en centavos y redondeado al centavo: mayor que cero y hasta <see cref="ProductosEnExcel.MaximoCentavos"/>. Vacío si
    /// la celda está vacía.
    /// </summary>
    public static long? Centavos(CeldaLeida celda, string columna, out Rechazo? rechazo)
    {
        if (Numero(celda, columna, out rechazo) is not { } pesos)
            return null;
        // El tope se revisa antes de pasar a centavos: un número de 28 cifras escrito como texto no cabe al multiplicarlo.
        var centavos = Math.Abs(pesos) > ProductosEnExcel.MaximoCentavos / 100m + 1 ? Math.Sign(pesos) * decimal.MaxValue
            : Math.Round(pesos * 100, MidpointRounding.AwayFromZero);
        if (centavos <= 0)
            rechazo = new Rechazo(PorQueSeIgnora.NumeroInvalido, $"«{columna}» dice «{celda.Escrito}»: el precio va mayor que cero.");
        else if (centavos > ProductosEnExcel.MaximoCentavos)
            rechazo = new Rechazo(PorQueSeIgnora.NumeroInvalido,
                $"«{columna}» dice «{celda.Escrito}»: revisa el precio (se aceptan hasta {(ProductosEnExcel.MaximoCentavos / 100m).ToString("$#,##0.00", CultureInfo.InvariantCulture)}).");
        return rechazo is null ? (long)centavos : null;
    }

    /// <summary>
    /// Una cantidad (desde cuántas, el mínimo, el máximo, lo que contiene una presentación), en unidades y redondeada a la milésima;
    /// cero o más. Vacía si la celda está vacía. Si va en piezas lo revisa quien la usa, que sabe la unidad (<see cref="Enteras"/>).
    /// </summary>
    public static decimal? Cantidad(CeldaLeida celda, string columna, out Rechazo? rechazo)
    {
        if (Numero(celda, columna, out rechazo) is not { } valor)
            return null;
        var redondeada = Math.Round(valor, 3, MidpointRounding.AwayFromZero);
        if (redondeada < 0)
        {
            rechazo = new Rechazo(PorQueSeIgnora.NumeroInvalido, $"«{columna}» dice «{celda.Escrito}»: no puede ser negativo.");
            return null;
        }
        if (redondeada > Variante.TopeMinimoYMaximo)
        {
            rechazo = new Rechazo(PorQueSeIgnora.NumeroInvalido, $"«{columna}» dice «{celda.Escrito}»: es demasiado grande.");
            return null;
        }
        return redondeada;
    }

    /// <summary>Si una cantidad va en piezas enteras con esa unidad: el rechazo, si no.</summary>
    public static Rechazo? Enteras(decimal cantidad, UnidadBase unidad, string columna) =>
        !unidad.AdmiteFracciones() && cantidad != decimal.Truncate(cantidad)
            ? new Rechazo(PorQueSeIgnora.NumeroInvalido,
                $"«{columna}» dice «{cantidad.ToString("0.###", CultureInfo.InvariantCulture)}»: se vende por pieza, así que va en piezas enteras.")
            : null;

    /// <summary>
    /// Un código: el texto sin espacios en las orillas, o el número que Excel guardó (sin decimales). Vacío si la celda está vacía.
    /// Un número con decimales o de más de 15 cifras ya perdió cifras en Excel: se rechaza.
    /// </summary>
    public static string? Codigo(CeldaLeida celda, string columna, out Rechazo? rechazo)
    {
        rechazo = null;
        switch (celda.Tipo)
        {
            case TipoCelda.Vacia:
                return null;
            case TipoCelda.Texto:
                return Revisar(celda.Texto.Trim(), columna, out rechazo);
            case TipoCelda.Numero when double.IsFinite(celda.Numero) && celda.Numero >= 0 && celda.Numero < 1e15 && celda.Numero == Math.Floor(celda.Numero):
                return ((decimal)celda.Numero).ToString("0", CultureInfo.InvariantCulture);
            case TipoCelda.Numero:
                rechazo = new Rechazo(PorQueSeIgnora.CodigoInvalido,
                    $"«{columna}» dice «{celda.Escrito}»: Excel guardó el código como número y perdió cifras. Escríbelo como texto (con un apóstrofo ' antes).");
                return null;
            case TipoCelda.Error:
                rechazo = ErrorDeExcel(celda, columna);
                return null;
            default:
                rechazo = new Rechazo(PorQueSeIgnora.CodigoInvalido, $"«{columna}» dice «{celda.Escrito}»: no es un código.");
                return null;
        }
    }

    /// <summary>Varios códigos en una celda, separados por comas, punto y coma o renglones; sin repetir (sin importar mayúsculas).</summary>
    public static IReadOnlyList<string> Codigos(CeldaLeida celda, string columna, out Rechazo? rechazo)
    {
        rechazo = null;
        if (celda.Tipo != TipoCelda.Texto)
            return Codigo(celda, columna, out rechazo) is { } uno ? [uno] : [];
        var codigos = new List<string>();
        foreach (var parte in celda.Texto.Split(ProductosEnExcel.SeparadoresDeCodigos, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Revisar(parte, columna, out rechazo) is not { } codigo)
                return [];
            if (!codigos.Contains(codigo, StringComparer.OrdinalIgnoreCase))
                codigos.Add(codigo);
        }
        return codigos;
    }

    private static string? Revisar(string codigo, string columna, out Rechazo? rechazo)
    {
        rechazo = null;
        if (codigo.Length > ProductosEnExcel.LargoCodigo)
            rechazo = new Rechazo(PorQueSeIgnora.CodigoInvalido,
                $"«{columna}» trae un código de {codigo.Length} letras: no parece un código (van de hasta {ProductosEnExcel.LargoCodigo}).");
        else if (codigo.Any(char.IsControl))
            rechazo = new Rechazo(PorQueSeIgnora.CodigoInvalido, $"«{columna}» trae un código con caracteres que no se ven: escríbelo otra vez.");
        return rechazo is null && codigo.Length > 0 ? codigo : null;
    }

    /// <summary>
    /// La clave escondida de un renglón (la de su variante, <see cref="CampoDeProducto.Clave"/>). Vacía si la celda está vacía
    /// (un renglón escrito a mano); si no es una clave, el renglón se ignora: alguien la cambió.
    /// </summary>
    public static Guid? Clave(CeldaLeida celda, string columna, out Rechazo? rechazo)
    {
        rechazo = null;
        if (celda.EsVacia)
            return null;
        if (celda.Tipo == TipoCelda.Texto && Guid.TryParseExact(celda.Texto.Trim(), "N", out var clave))
            return clave;
        rechazo = new Rechazo(PorQueSeIgnora.DatoInvalido, $"«{columna}» (la columna escondida al final) dice «{celda.Escrito}»: es la del Excel que se "
            + "exportó y no se cambia. Bórrala en ese renglón, o cópiala del Excel que se exportó.");
        return null;
    }

    /// <summary>
    /// Si a un código escrito como número Excel le quitó los ceros de adelante: con los ceros, sería uno de 8, 12, 13 o 14 cifras
    /// que ya existe. Vacío si no.
    /// </summary>
    public static string? ConLosCeros(string codigo, Func<string, bool> existe)
    {
        if (codigo.Length == 0 || codigo.Length >= 14 || !codigo.All(char.IsAsciiDigit))
            return null;
        foreach (var largo in new[] { 8, 12, 13, 14 })
        {
            if (largo > codigo.Length && codigo.PadLeft(largo, '0') is var conCeros && existe(conCeros))
                return conCeros;
        }
        return null;
    }

    /// <summary>«Sí» o «No» (también S, N, X, 1, 0, VERDADERO y FALSO). Vacío si la celda está vacía.</summary>
    public static bool? SiNo(CeldaLeida celda, string columna, out Rechazo? rechazo)
    {
        rechazo = null;
        switch (celda.Tipo)
        {
            case TipoCelda.Vacia:
                return null;
            case TipoCelda.SiNo:
                return celda.SiNo;
            case TipoCelda.Numero when celda.Numero is 0 or 1:
                return celda.Numero == 1;
            case TipoCelda.Texto:
                switch (Texto.Normalizar(celda.Texto))
                {
                    case "SI" or "S" or "X" or "1" or "VERDADERO" or "TRUE":
                        return true;
                    case "NO" or "N" or "0" or "FALSO" or "FALSE":
                        return false;
                }
                break;
            case TipoCelda.Error:
                rechazo = ErrorDeExcel(celda, columna);
                return null;
        }
        rechazo = new Rechazo(PorQueSeIgnora.DatoInvalido, $"«{columna}» dice «{celda.Escrito}»: va «Sí» o «No».");
        return null;
    }

    /// <summary>La unidad: piezas, metros, kilos o litros (también en singular y abreviada: pza, m, kg, l). Vacía si la celda está vacía.</summary>
    public static UnidadBase? Unidad(CeldaLeida celda, string columna, out Rechazo? rechazo)
    {
        rechazo = null;
        if (celda.EsVacia)
            return null;
        if (celda.Tipo == TipoCelda.Error)
        {
            rechazo = ErrorDeExcel(celda, columna);
            return null;
        }
        switch (Texto.Normalizar(celda.Escrito).TrimEnd('.'))
        {
            case "PIEZA" or "PIEZAS" or "PZ" or "PZA" or "PZAS" or "PZS":
                return UnidadBase.Pieza;
            case "METRO" or "METROS" or "M" or "MT" or "MTS" or "MTR":
                return UnidadBase.Metro;
            case "KILO" or "KILOS" or "KG" or "KGS" or "KILOGRAMO" or "KILOGRAMOS":
                return UnidadBase.Kilogramo;
            case "LITRO" or "LITROS" or "L" or "LT" or "LTS":
                return UnidadBase.Litro;
        }
        rechazo = new Rechazo(PorQueSeIgnora.DatoInvalido, $"«{columna}» dice «{celda.Escrito}»: va «piezas», «metros», «kilos» o «litros».");
        return null;
    }

    /// <summary>
    /// Un texto (un nombre, el departamento, la marca), con los espacios de más quitados (también los renglones de una celda).
    /// Vacío si la celda está vacía. Un número se lee como se ve.
    /// </summary>
    public static string? TextoDe(CeldaLeida celda, string columna, int largo, out Rechazo? rechazo)
    {
        rechazo = null;
        if (celda.Tipo == TipoCelda.Error)
        {
            rechazo = ErrorDeExcel(celda, columna);
            return null;
        }
        var limpio = Limpio(celda.Escrito);
        if (limpio.Length > largo)
        {
            rechazo = new Rechazo(PorQueSeIgnora.DatoInvalido, $"«{columna}» es demasiado largo: va de hasta {largo} letras.");
            return null;
        }
        return limpio.Length == 0 ? null : limpio;
    }

    /// <summary>Sin espacios en las orillas ni dos seguidos (los renglones de una celda cuentan como espacios).</summary>
    public static string Limpio(string texto) => string.Join(' ', texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static Rechazo ErrorDeExcel(CeldaLeida celda, string columna) =>
        new(PorQueSeIgnora.DatoInvalido, celda.Texto.StartsWith('#')
            ? $"«{columna}» tiene un error de Excel ({celda.Texto}): corrígelo en Excel (si ahí se ve bien, guárdalo otra vez en Excel)."
            : $"«{columna}» {celda.Texto}");
}
