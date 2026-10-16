using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PapeleriaPos.Nucleo.Catalogo;

/// <summary>Un producto como lo ve la búsqueda: su nombre, su código y todos sus códigos.</summary>
/// <param name="Codigo">El que se enseña en la lista (el de su presentación base).</param>
/// <param name="Codigos">Todos los suyos: los de sus presentaciones y los que comparte con otro producto.</param>
public sealed record ProductoParaBuscar(Guid VarianteId, string Descripcion, string? Codigo, IEnumerable<string> Codigos);

/// <summary>Un producto ya listo para buscarlo.</summary>
public sealed class ArticuloBuscable
{
    internal ArticuloBuscable(int posicion, Guid varianteId, string descripcion, string? codigo, string normalizado, IReadOnlyList<ParteDeNombre> partes,
        byte[] ordenDelNombre)
    {
        Posicion = posicion;
        VarianteId = varianteId;
        Descripcion = descripcion;
        Codigo = codigo;
        Normalizado = normalizado;
        Partes = partes;
        OrdenDelNombre = ordenDelNombre;
    }

    public Guid VarianteId { get; }
    public string Descripcion { get; }
    public string? Codigo { get; }

    /// <summary>El nombre y todos los códigos, sin acentos y en mayúsculas.</summary>
    public string Normalizado { get; }

    /// <summary>El nombre en pedazos (color, medida, marca…): la caja junta con eso las familias de la lista.</summary>
    public IReadOnlyList<ParteDeNombre> Partes { get; }

    /// <summary>En qué lugar llegó: entre dos que empatan en todo lo demás, el de antes.</summary>
    internal int Posicion { get; }

    /// <summary>El nombre para ordenar en español de México sin importar mayúsculas, ya calculado (comparar así es rápido).</summary>
    internal byte[] OrdenDelNombre { get; }
}

/// <summary>Lo que encontró la búsqueda, en su orden.</summary>
/// <param name="Parecido">Nada coincidía exacto: salió por un error de dedo («sobre manial»).</param>
/// <param name="PorSinonimo">La palabra de la tienda por la que salió («boligrafo», si su nombre dice «Lapicero»).</param>
public readonly record struct Encontrado(ArticuloBuscable Articulo, bool Parecido, string? PorSinonimo);

/// <summary>
/// La búsqueda por nombre de la venta, una sola para el servicio y para la copia de cada caja (entrega 5, parte 3,
/// docs/propuesta-interfaz.md, «Rendimiento»): con los mismos productos, códigos, marcas y sinónimos da lo mismo en los dos
/// lados, en el mismo orden. Se arma una vez (al cargar el catálogo en el servicio, al armar la foto de la copia en la
/// caja), fuera de la búsqueda, y después no cambia: se puede buscar desde varios hilos a la vez.
/// <para>
/// Cada pedazo escrito tiene que ser el principio de una palabra del producto, en cualquier orden y sin importar acentos
/// («lus az» = Papel lustre azul), o de lo que vale por él en los sinónimos de la tienda. Las palabras son las del nombre y
/// las de los códigos, tal cual («3/4», «LIB-01») y partidas en lo que no es letra ni número («(azul)» → «azul»), y también
/// lo que lee la caja del nombre: «negro» encuentra «Negra», «surtido» los «V/Colores» y «1mm» el «1.0 Mm». Si nada
/// coincide exacto, un error de dedo (<see cref="ErrorDeDedo"/>).
/// </para>
/// <para>
/// El orden: el código exacto; entre los parecidos, los que coinciden exacto en más palabras; lo que esa caja eligió antes
/// con el mismo texto; lo que se llama como se escribió antes de lo que salió por un sinónimo; lo que empieza con lo
/// escrito; lo más vendido; el nombre (en español de México, sea cual sea el idioma de la PC) y, al final, el orden en que
/// llegaron.
/// </para>
/// <para>
/// Por dentro, cada palabra apunta a los productos que la tienen, y las palabras van ordenadas: las que empiezan con un
/// pedazo quedan juntas y se encuentran sin recorrer todo el catálogo.
/// </para>
/// </summary>
public sealed partial class IndiceDeBusqueda
{
    /// <summary>Para el orden por nombre: el español de México, sin importar mayúsculas.</summary>
    private static readonly CompareInfo Espanol = CultureInfo.GetCultureInfo("es-MX").CompareInfo;

    private readonly ArticuloBuscable[] _articulos;
    private readonly Dictionary<Guid, ArticuloBuscable> _porVariante;

    /// <summary>Todas las palabras, sin repetir, en orden ordinal: las que empiezan igual quedan juntas.</summary>
    private readonly string[] _palabras;

    /// <summary>Los productos (su lugar) que tienen cada palabra de <see cref="_palabras"/>, de menor a mayor.</summary>
    private readonly int[][] _conPalabra;

    private readonly SinonimosDeBusqueda _sinonimos;

    private IndiceDeBusqueda(ArticuloBuscable[] articulos, string[] palabras, int[][] conPalabra, SinonimosDeBusqueda sinonimos)
    {
        _articulos = articulos;
        _palabras = palabras;
        _conPalabra = conPalabra;
        _sinonimos = sinonimos;
        _porVariante = new Dictionary<Guid, ArticuloBuscable>(articulos.Length);
        foreach (var a in articulos)
            _porVariante.TryAdd(a.VarianteId, a);
    }

    /// <summary>Sin productos: no encuentra nada.</summary>
    public static IndiceDeBusqueda Vacio { get; } = new([], [], [], SinonimosDeBusqueda.Ninguno);

    /// <summary>Cuántos productos tiene.</summary>
    public int Productos => _articulos.Length;

    /// <summary>Los productos, en el orden en que llegaron.</summary>
    public IReadOnlyList<ArticuloBuscable> Articulos => _articulos;

    public ArticuloBuscable? PorVariante(Guid varianteId) => _porVariante.GetValueOrDefault(varianteId);

    /// <summary>La clave de lo escrito: sin acentos, mayúsculas ni espacios de más. Con ella se recuerda «lo elegido antes».</summary>
    public static string Clave(string texto) => Texto.Normalizar(texto);

    /// <summary>Lo más largo que se guarda de lo que se buscó (la clave de «lo elegido antes»).</summary>
    public const int LargoGuardado = 100;

    /// <summary>La clave con que se guarda lo que se buscó: la de <see cref="Clave"/>, hasta <see cref="LargoGuardado"/> letras.</summary>
    public static string ClaveGuardada(string texto)
    {
        var clave = Clave(texto);
        return clave.Length > LargoGuardado ? clave[..LargoGuardado].TrimEnd() : clave;
    }

    /// <summary>
    /// Arma el índice. Los productos, en su orden (el desempate final); los códigos, las marcas y los sinónimos pueden venir
    /// en cualquier orden y repetidos: dan lo mismo.
    /// </summary>
    /// <param name="marcas">
    /// Las marcas para leer los nombres (<see cref="LectorDeNombres"/>); también se aprenden de lo que se repite al final de
    /// los nombres.
    /// </param>
    public static IndiceDeBusqueda Armar(IEnumerable<ProductoParaBuscar> productos, IEnumerable<string>? marcas, IEnumerable<(string Palabra, string Equivale)>? sinonimos)
    {
        var lista = productos.ToList();
        var lector = new LectorDeNombres(lista.Select(p => p.Descripcion), marcas);
        var articulos = new ArticuloBuscable[lista.Count];
        // Cada palabra una sola vez en memoria, con los productos que la tienen.
        var conPalabra = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < lista.Count; i++)
        {
            var p = lista[i];
            var partes = lector.Leer(p.Descripcion);
            var codigos = string.Join(" ", p.Codigos.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            var normalizado = Texto.Normalizar($"{p.Descripcion} {codigos}");
            articulos[i] = new ArticuloBuscable(i, p.VarianteId, p.Descripcion, p.Codigo, normalizado, partes,
                Espanol.GetSortKey(p.Descripcion, CompareOptions.IgnoreCase).KeyData);
            foreach (var palabra in Palabras(normalizado, partes))
            {
                if (!conPalabra.TryGetValue(palabra, out var conEsta))
                    conPalabra[palabra] = conEsta = [];
                conEsta.Add(i);
            }
        }
        var palabras = conPalabra.Keys.Order(StringComparer.Ordinal).ToArray();
        var apuntan = palabras.Select(w => conPalabra[w].ToArray()).ToArray();
        var ordenados = (sinonimos ?? []).Distinct().OrderBy(s => s.Palabra, StringComparer.Ordinal).ThenBy(s => s.Equivale, StringComparer.Ordinal);
        return new IndiceDeBusqueda(articulos, palabras, apuntan, new SinonimosDeBusqueda(ordenados));
    }

    /// <summary>
    /// Las palabras de un producto: las del nombre y los códigos tal cual («3/4», «LIB-01») y partidas en lo que no es letra
    /// ni número («(azul)» → «azul»), y las de lo que lee la caja del nombre. Cada pedazo escrito tiene que ser el principio
    /// de una.
    /// </summary>
    private static IEnumerable<string> Palabras(string normalizado, IReadOnlyList<ParteDeNombre> partes) =>
        normalizado.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(NoLetraNiNumero().Split(normalizado))
            .Concat(partes.SelectMany(p => p.Clave.Split(' ')))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal);

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NoLetraNiNumero();

    /// <summary>
    /// Lo que coincide con lo escrito, hasta <paramref name="maximo"/>, en su orden (ver la clase).
    /// </summary>
    /// <param name="elegido">Lo que esa caja eligió antes con el mismo texto: sale primero.</param>
    /// <param name="vendidos">Cuántas veces se vendió cada producto (en 90 días): ordena cuando el texto no distingue.</param>
    public List<Encontrado> Buscar(string texto, int maximo = 30, Guid? elegido = null, Func<Guid, int>? vendidos = null)
    {
        var consulta = Clave(texto);
        var palabras = consulta.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (palabras.Length == 0 || maximo <= 0 || _articulos.Length == 0)
            return [];

        // Cada pedazo vale por sí mismo o por los sinónimos de la tienda («bolig» → lapicero).
        var sinonimos = palabras.Select(_sinonimos.De).ToArray();
        var directos = palabras.Select(ConPrincipio).ToArray();
        var porSinonimo = sinonimos.Select(lista => lista.Select(s => ConPrincipio(s.Equivale)).ToArray()).ToArray();

        var candidatos = Coinciden(directos, porSinonimo);
        // Si nada coincide exacto, un error de dedo: lo parecido, en lugar de «no existe».
        var parecido = false;
        if (!HayAlguno(candidatos))
        {
            candidatos = Coinciden(palabras.Select(Parecidos).ToArray(), porSinonimo);
            parecido = HayAlguno(candidatos);
        }

        var codigo = texto.Trim();
        var lugares = new List<Lugar>();
        for (var i = 0; i < _articulos.Length; i++)
        {
            if (!candidatos[i])
                continue;
            var a = _articulos[i];
            // La palabra de la tienda por la que salió, si algún pedazo no estaba en el nombre.
            string? sinonimo = null;
            for (var k = 0; k < palabras.Length && sinonimo is null; k++)
            {
                if (directos[k][i])
                    continue;
                for (var j = 0; j < sinonimos[k].Count; j++)
                {
                    if (!porSinonimo[k][j][i])
                        continue;
                    sinonimo = sinonimos[k][j].Palabra.ToLowerInvariant();
                    break;
                }
            }
            var coinciden = 0;
            if (parecido)
            {
                for (var k = 0; k < palabras.Length; k++)
                    coinciden += directos[k][i] ? 1 : 0;
            }
            lugares.Add(new Lugar(a, a.Codigo is not null && string.Equals(a.Codigo, codigo, StringComparison.OrdinalIgnoreCase), coinciden,
                a.VarianteId == elegido, sinonimo, a.Normalizado.StartsWith(consulta, StringComparison.Ordinal), vendidos?.Invoke(a.VarianteId) ?? 0));
        }
        lugares.Sort(Comparar);
        var encontrados = new List<Encontrado>(Math.Min(maximo, lugares.Count));
        foreach (var lugar in lugares.Take(maximo))
            encontrados.Add(new Encontrado(lugar.Articulo, parecido, lugar.PorSinonimo));
        return encontrados;
    }

    /// <summary>Un producto encontrado, con lo que decide su lugar en la lista.</summary>
    private readonly record struct Lugar(
        ArticuloBuscable Articulo, bool CodigoExacto, int Coinciden, bool Elegido, string? PorSinonimo, bool Empieza, int Vendidos);

    private static int Comparar(Lugar x, Lugar y)
    {
        // Primero el código exacto; entre los parecidos, los que coinciden exacto en más palabras («pluma pumto» antes que
        // el plumón); lo elegido antes; lo que se llama como se escribió antes de lo que salió por un sinónimo; lo que empieza
        // con lo escrito; lo más vendido; el nombre, y el orden en que llegaron.
        var c = y.CodigoExacto.CompareTo(x.CodigoExacto);
        if (c == 0)
            c = y.Coinciden.CompareTo(x.Coinciden);
        if (c == 0)
            c = y.Elegido.CompareTo(x.Elegido);
        if (c == 0)
            c = (x.PorSinonimo is not null).CompareTo(y.PorSinonimo is not null);
        if (c == 0)
            c = y.Empieza.CompareTo(x.Empieza);
        if (c == 0)
            c = y.Vendidos.CompareTo(x.Vendidos);
        if (c == 0)
            c = x.Articulo.OrdenDelNombre.AsSpan().SequenceCompareTo(y.Articulo.OrdenDelNombre);
        return c != 0 ? c : x.Articulo.Posicion.CompareTo(y.Articulo.Posicion);
    }

    /// <summary>Los que valen por cada pedazo: por sí mismo o por alguno de sus sinónimos; tienen que valer todos.</summary>
    private BitArray Coinciden(BitArray[] porPedazo, BitArray[][] porSinonimo)
    {
        var todos = new BitArray(_articulos.Length, true);
        for (var k = 0; k < porPedazo.Length; k++)
        {
            var vale = new BitArray(porPedazo[k]);
            foreach (var sinonimo in porSinonimo[k])
                vale.Or(sinonimo);
            todos.And(vale);
        }
        return todos;
    }

    private static bool HayAlguno(BitArray conjunto) => conjunto.HasAnySet();

    /// <summary>Los productos con alguna palabra que empieza con <paramref name="pedazo"/>.</summary>
    private BitArray ConPrincipio(string pedazo)
    {
        var conjunto = new BitArray(_articulos.Length);
        for (var i = PrimeraDesde(pedazo); i < _palabras.Length && _palabras[i].StartsWith(pedazo, StringComparison.Ordinal); i++)
            Marcar(conjunto, _conPalabra[i]);
        return conjunto;
    }

    /// <summary>Los productos con alguna palabra que coincide con el pedazo por un error de dedo (<see cref="ErrorDeDedo.Coincide"/>).</summary>
    private BitArray Parecidos(string pedazo)
    {
        var conjunto = new BitArray(_articulos.Length);
        for (var i = 0; i < _palabras.Length; i++)
        {
            if (ErrorDeDedo.Coincide(pedazo, _palabras[i]))
                Marcar(conjunto, _conPalabra[i]);
        }
        return conjunto;
    }

    private static void Marcar(BitArray conjunto, int[] lugares)
    {
        foreach (var lugar in lugares)
            conjunto[lugar] = true;
    }

    /// <summary>El lugar de la primera palabra que no va antes de <paramref name="pedazo"/> (orden ordinal).</summary>
    private int PrimeraDesde(string pedazo)
    {
        int desde = 0, hasta = _palabras.Length;
        while (desde < hasta)
        {
            var medio = (desde + hasta) / 2;
            if (string.CompareOrdinal(_palabras[medio], pedazo) < 0)
                desde = medio + 1;
            else
                hasta = medio;
        }
        return desde;
    }
}
