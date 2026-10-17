using System.Collections.Frozen;
using System.Collections.Immutable;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// La foto del catálogo de la copia local de la caja, para escanear sin la red (entrega 5, docs/propuesta-interfaz.md,
/// «Rendimiento»): un diccionario por cada código (también los de las presentaciones y los códigos de más de un producto),
/// con la versión del catálogo con que se armó la copia. Escanear es buscar en ella: fracciones de microsegundo, también en
/// línea; la red no entra en el escaneo.
/// <para>
/// No cambia nunca: se arma fuera del hilo de la pantalla cuando la copia se carga o se baja, y la contingencia la cambia
/// por la nueva de un jalón. Lo que llega después de la caja principal (un producto recién dado de alta en otra caja, o
/// los precios nuevos que dijo el cobro) va en una foto nueva con eso encima (<see cref="Con"/>), sin tocar esta.
/// </para>
/// </summary>
public sealed class FotoCatalogo
{
    /// <summary>Cada código con su producto, ya con la presentación de ese código (el paquete, la caja de 12…).</summary>
    private readonly FrozenDictionary<string, ProductoVentaDto?> _porCodigo;

    private readonly FrozenDictionary<Guid, ProductoVentaDto> _porVariante;

    /// <summary>Lo que llegó de la caja principal después de la copia, por variante (con su presentación base).</summary>
    private readonly ImmutableDictionary<Guid, ProductoVentaDto> _nuevos;

    /// <summary>Los códigos que la copia no tiene (de los productos recién dados de alta), con su presentación.</summary>
    private readonly ImmutableDictionary<string, (Guid VarianteId, Guid PresentacionId)> _codigosNuevos;

    private FotoCatalogo(long version, FrozenDictionary<string, ProductoVentaDto?> porCodigo, FrozenDictionary<Guid, ProductoVentaDto> porVariante,
        ImmutableDictionary<Guid, ProductoVentaDto> nuevos, ImmutableDictionary<string, (Guid, Guid)> codigosNuevos)
    {
        Version = version;
        _porCodigo = porCodigo;
        _porVariante = porVariante;
        _nuevos = nuevos;
        _codigosNuevos = codigosNuevos;
    }

    /// <summary>La versión del catálogo de la copia (<see cref="PaqueteContingenciaDto.Version"/>): la que lleva el cobro.</summary>
    public long Version { get; }

    /// <summary>Cuántos productos trae la copia.</summary>
    public int Productos => _porVariante.Count;

    /// <summary>Cuántos códigos trae la copia.</summary>
    public int Codigos => _porCodigo.Count;

    /// <summary>Cuántos productos llegaron de la caja principal después de la copia (van encima de ella).</summary>
    public int Nuevos => _nuevos.Count;

    /// <summary>
    /// La foto de una copia. Resuelve cada código igual que lo hacía <c>Contingencia.PorCodigo</c>: sin importar mayúsculas
    /// ni los espacios de las orillas; si un código se repite, vale el primero; y el producto sale con la presentación de ese
    /// código (si ya no la tiene, con la suya). Cada producto queda con la versión de la copia si no trae la propia.
    /// </summary>
    public static FotoCatalogo Armar(PaqueteContingenciaDto paquete)
    {
        var porVariante = new Dictionary<Guid, ProductoVentaDto>(paquete.Productos.Count);
        foreach (var p in paquete.Productos)
            porVariante.TryAdd(p.VarianteId, p.Version is null ? p with { Version = paquete.Version } : p);
        var porCodigo = new Dictionary<string, ProductoVentaDto?>(paquete.Codigos.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var c in paquete.Codigos)
        {
            // El primero se queda, aunque su producto no esté en la copia: así lo resolvía la contingencia.
            if (porCodigo.ContainsKey(c.Codigo))
                continue;
            porCodigo[c.Codigo] = porVariante.TryGetValue(c.VarianteId, out var p) ? ConPresentacion(p, c.PresentacionId) : null;
        }
        return new FotoCatalogo(paquete.Version, porCodigo.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), porVariante.ToFrozenDictionary(),
            ImmutableDictionary<Guid, ProductoVentaDto>.Empty, ImmutableDictionary.Create<string, (Guid, Guid)>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>El producto de un código, con la presentación de ese código; vacío si no está en la copia.</summary>
    public ProductoVentaDto? PorCodigo(string codigo)
    {
        codigo = codigo.Trim();
        if (_codigosNuevos.TryGetValue(codigo, out var nuevo) && _nuevos.TryGetValue(nuevo.VarianteId, out var producto))
            return ConPresentacion(producto, nuevo.PresentacionId);
        if (!_porCodigo.TryGetValue(codigo, out var enLaCopia) || enLaCopia is null)
            return null;
        // Si sus precios cambiaron después de la copia (lo dijo el cobro), los de ahora, con la presentación del código.
        return _nuevos.TryGetValue(enLaCopia.VarianteId, out var alDia) ? ConPresentacion(alDia, enLaCopia.Presentacion.Id) : enLaCopia;
    }

    /// <summary>El producto de una variante, con su presentación base; vacío si no está en la copia.</summary>
    public ProductoVentaDto? PorVariante(Guid varianteId) =>
        _nuevos.TryGetValue(varianteId, out var nuevo) ? nuevo : _porVariante.GetValueOrDefault(varianteId);

    /// <summary>
    /// Una foto nueva con lo que llegó de la caja principal encima de esta: un producto que la copia no tenía (con el código
    /// con que se escaneó) o los precios de ahora de uno que sí tenía. Esta no cambia.
    /// </summary>
    /// <param name="codigo">El código con que se escaneó: con él se encuentra la próxima vez, aunque la copia no lo tenga.</param>
    public FotoCatalogo Con(ProductoVentaDto producto, string? codigo = null)
    {
        var baseDelProducto = ConPresentacion(producto, producto.Presentaciones.FirstOrDefault(p => p.EsBase)?.Id ?? producto.Presentacion.Id);
        var codigos = _codigosNuevos;
        if (codigo?.Trim() is { Length: > 0 } limpio && !_porCodigo.ContainsKey(limpio))
            codigos = codigos.SetItem(limpio, (producto.VarianteId, producto.Presentacion.Id));
        return new FotoCatalogo(Version, _porCodigo, _porVariante, _nuevos.SetItem(producto.VarianteId, baseDelProducto), codigos);
    }

    /// <summary>
    /// Esta foto (la de una copia nueva) con lo que la anterior tenía encima y sigue siendo más nuevo que la copia: lo que la
    /// copia ya trae, ya no hace falta. Una copia más vieja que la anterior quiere decir que la base de la caja principal
    /// cambió entera (la prueba empezó de cero): lo de encima era de la otra base y no se queda.
    /// </summary>
    public FotoCatalogo ConLoNuevoDe(FotoCatalogo? anterior)
    {
        if (anterior is null || anterior._nuevos.IsEmpty || Version < anterior.Version)
            return this;
        var nuevos = anterior._nuevos.Where(x => x.Value.Version is { } v && v > Version).ToImmutableDictionary();
        if (nuevos.IsEmpty)
            return this;
        var codigos = anterior._codigosNuevos.Where(x => nuevos.ContainsKey(x.Value.VarianteId) && !_porCodigo.ContainsKey(x.Key))
            .ToImmutableDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        return new FotoCatalogo(Version, _porCodigo, _porVariante, nuevos, codigos);
    }

    private static ProductoVentaDto ConPresentacion(ProductoVentaDto p, Guid presentacionId) =>
        p.Presentacion.Id == presentacionId ? p : p with { Presentacion = p.Presentaciones.FirstOrDefault(x => x.Id == presentacionId) ?? p.Presentacion };
}
