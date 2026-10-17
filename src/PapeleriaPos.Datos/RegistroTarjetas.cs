using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Datos;

/// <summary>
/// Las tarjetas de precio y lo que más se pregunta (entrega 13, parte 3; docs/propuesta-interfaz.md, «Lo que se señala»,
/// puntos 5 y 6), en Productos (F3) › Tarjetas de precio y en el paso de las tarjetas de cada temporada:
/// <list type="bullet">
/// <item>Una tarjeta por producto, con el precio grande para el cliente y el código para que el cajero la escanee en lugar del
/// producto. Las arma y las imprime la caja (<c>PapeleriaPos.Impresion.HojaDeTarjetas</c>), en hojas carta para recortar; al
/// imprimirlas queda anotado con qué precio y qué código salió cada una. A lo que no tiene código se le da uno interno.</item>
/// <item>Si su precio de hoy ya no es el de la tarjeta (o su código ya no es suyo), sale en «Por reimprimir» hasta que se
/// vuelve a imprimir; o se quita, si ya no tiene tarjeta.</item>
/// <item>El verificador (F9) cuenta cada consulta: lo más preguntado sin tarjeta es lo primero a lo que conviene ponérsela.</item>
/// </list>
/// Imprimirlas y quitarlas pide «Productos, precios y etiquetas» (como las etiquetas); contar una consulta, nada. En el piloto
/// con eleventa todo igual: las tarjetas son del POS, y el precio que cambia en eleventa llega con la lectura y la marca.
/// </summary>
/// <param name="reloj">La hora (la del servicio; las pruebas la fijan). Sin él, la de la base.</param>
public sealed class RegistroTarjetas(PosDbContext db, TimeProvider? reloj = null)
{
    private const AccionControlada Permiso = AccionControlada.EditarCatalogo;

    /// <summary>Más tarjetas que esto de una vez es un error de dedo (son 17 hojas).</summary>
    public const int Maximo = 300;

    /// <summary>Lo más preguntado: cuántos productos se enseñan.</summary>
    public const int MasPreguntados = 30;

    private DateTime Ahora => (reloj ?? db.Reloj).GetUtcNow().UtcDateTime;

    /// <summary>Las tarjetas por volver a imprimir (por nombre) y cuántas tarjetas hay en la sucursal.</summary>
    public async Task<TarjetasDto> LeerAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var tarjetas = await db.TarjetasDePrecio.AsNoTracking().Where(t => t.SucursalId == sucursalId).ToListAsync(ct);
        var datos = await DatosAsync(tarjetas.Select(t => t.VarianteId), sucursalId, ct);
        var porReimprimir = tarjetas
            .Where(t => datos.ContainsKey(t.VarianteId))
            .Select(t => (Tarjeta: t, Datos: datos[t.VarianteId], PorQue: t.PorQueReimprimir(datos[t.VarianteId].Precio, datos[t.VarianteId].Codigos)))
            .Where(x => x.PorQue is not null)
            .Select(x => Dto(x.Datos, x.Tarjeta, x.PorQue))
            .OrderBy(p => p.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new TarjetasDto(porReimprimir, tarjetas.Count);
    }

    /// <summary>Los productos de un exhibidor o de una temporada, en su acomodo, cada uno con su tarjeta (si tiene) y si está bien.</summary>
    public async Task<List<ProductoConTarjetaDto>> DeExhibidorAsync(Guid exhibidorId, Guid sucursalId, CancellationToken ct = default)
    {
        var (_, puestos) = await new RegistroTemporadas(db).TemporadaAsync(exhibidorId, ct, soloTemporada: false);
        var ids = puestos.Select(p => p.VarianteId).ToList();
        var datos = await DatosAsync(ids, sucursalId, ct);
        var tarjetas = await db.TarjetasDePrecio.AsNoTracking().Where(t => t.SucursalId == sucursalId && ids.Contains(t.VarianteId))
            .ToDictionaryAsync(t => t.VarianteId, ct);
        return puestos.Where(p => datos.TryGetValue(p.VarianteId, out var d) && d.Activo)
            .Select(p =>
            {
                var d = datos[p.VarianteId];
                var tarjeta = tarjetas.GetValueOrDefault(p.VarianteId);
                return Dto(d, tarjeta, tarjeta?.PorQueReimprimir(d.Precio, d.Codigos)) with { Donde = p.Lugar };
            })
            .ToList();
    }

    /// <summary>
    /// Lo más preguntado en el verificador en los últimos <see cref="ConsultaDePrecio.DiasQueCuentan"/> días que no tiene tarjeta
    /// de precio: lo que más se pregunta primero. Solo lo activo.
    /// </summary>
    public async Task<List<ProductoConTarjetaDto>> MasPreguntadosAsync(Guid sucursalId, int maximo = MasPreguntados, CancellationToken ct = default)
    {
        var desde = Ahora.AddDays(-ConsultaDePrecio.DiasQueCuentan);
        var conTarjeta = db.TarjetasDePrecio.Where(t => t.SucursalId == sucursalId).Select(t => t.VarianteId);
        var veces = await db.ConsultasDePrecio.AsNoTracking()
            .Where(c => c.SucursalId == sucursalId && c.FechaUtc >= desde && !conTarjeta.Contains(c.VarianteId))
            .GroupBy(c => c.VarianteId)
            .Select(g => new { VarianteId = g.Key, Veces = g.Count() })
            .ToListAsync(ct);
        var datos = await DatosAsync(veces.Select(v => v.VarianteId), sucursalId, ct);
        return veces.Where(v => datos.TryGetValue(v.VarianteId, out var d) && d.Activo)
            .Select(v => Dto(datos[v.VarianteId], null, null) with { Veces = v.Veces })
            .OrderByDescending(p => p.Veces).ThenBy(p => p.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(maximo, 1, 200))
            .ToList();
    }

    /// <summary>
    /// Las tarjetas listas para la hoja, en el orden pedido: el nombre, el precio de menudeo de hoy y el código (a lo que no
    /// tiene se le da uno interno, como al imprimir etiquetas). Lo que no tiene precio no sale: se dice cuáles.
    /// </summary>
    public async Task<TarjetasParaImprimirDto> PrepararAsync(SolicitudPrepararTarjetas s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, s.SucursalId, Permiso, "Las tarjetas de precio las imprime quien tiene «Productos, precios y etiquetas».", ct);
        var ids = s.Variantes.Distinct().ToList();
        if (ids.Count == 0)
            throw new ArgumentException("Elige al menos un producto para su tarjeta.");
        if (ids.Count > Maximo)
            throw new ArgumentException($"Son más de {Maximo} tarjetas: imprímelas en partes.");
        var etiquetas = new RegistroEtiquetas(db);
        var variantes = await etiquetas.VariantesAsync(ids, tracking: true, ct);
        if (ids.Any(id => !variantes.ContainsKey(id)))
            throw new InvalidOperationException("Uno de los productos ya no existe.");
        var precios = await etiquetas.MenudeoAsync(ids, s.SucursalId, ct);
        // Código solo para lo que sale (lo que tiene precio).
        var nuevos = await etiquetas.DarCodigosAsync(ids.Where(id => precios.GetValueOrDefault(id) > 0).Select(id => variantes[id]), s.UsuarioId, s.SucursalId,
            "para sus tarjetas de precio", ct);
        var tarjetas = new List<TarjetaParaImprimirDto>();
        var sinPrecio = new List<string>();
        foreach (var id in ids)
        {
            var v = variantes[id];
            if (precios.TryGetValue(id, out var precio) && precio > 0)
                tarjetas.Add(new TarjetaParaImprimirDto(id, RegistroCompras.Nombre(v), precio, RegistroEtiquetas.Codigo(v)!));
            else
                sinPrecio.Add(RegistroCompras.Nombre(v));
        }
        return new TarjetasParaImprimirDto(tarjetas, nuevos, sinPrecio);
    }

    /// <summary>
    /// Las que se mandaron a imprimir, con el precio y el código con que salieron: cada producto queda con su tarjeta (la nueva
    /// reemplaza a la anterior) y deja de estar por reimprimir. Si su precio cambió mientras tanto, la tarjeta salió con el de
    /// antes: queda por reimprimir, como debe.
    /// </summary>
    public async Task<TarjetasDto> ImpresasAsync(SolicitudTarjetasImpresas s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, s.SucursalId, Permiso, "Las tarjetas de precio las imprime quien tiene «Productos, precios y etiquetas».", ct);
        if (s.Tarjetas.Count == 0)
            return await LeerAsync(s.SucursalId, ct);
        if (s.Tarjetas.Count > Maximo)
            throw new ArgumentException($"Son más de {Maximo} tarjetas: imprímelas en partes.");
        if (s.Tarjetas.Any(t => t.PrecioCentavos <= 0 || string.IsNullOrWhiteSpace(t.Codigo) || t.Codigo.Trim().Length > TarjetaDePrecio.LargoCodigo))
            throw new ArgumentException("Cada tarjeta lleva su precio y su código.");
        var ids = s.Tarjetas.Select(t => t.VarianteId).Distinct().ToList();
        if (await db.Variantes.CountAsync(v => ids.Contains(v.Id), ct) != ids.Count)
            throw new InvalidOperationException("Uno de los productos ya no existe.");

        // Dos cajas a la vez (o un doble clic) se forman: la segunda ve la tarjeta que dejó la primera.
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var actuales = await db.TarjetasDePrecio.Where(t => t.SucursalId == s.SucursalId && ids.Contains(t.VarianteId)).ToDictionaryAsync(t => t.VarianteId, ct);
        var ahora = Ahora;
        foreach (var t in s.Tarjetas.GroupBy(t => t.VarianteId).Select(g => g.Last()))
        {
            if (!actuales.TryGetValue(t.VarianteId, out var tarjeta))
            {
                tarjeta = new TarjetaDePrecio { SucursalId = s.SucursalId, VarianteId = t.VarianteId, Codigo = t.Codigo.Trim() };
                db.TarjetasDePrecio.Add(tarjeta);
            }
            (tarjeta.PrecioCentavos, tarjeta.Codigo, tarjeta.ImpresaUtc, tarjeta.ImpresaPorId) = (t.PrecioCentavos, t.Codigo.Trim(), ahora, s.UsuarioId);
        }
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return await LeerAsync(s.SucursalId, ct);
    }

    /// <summary>Ya no tiene tarjeta (se quitó del exhibidor o se tiró): deja de estar por reimprimir.</summary>
    public async Task<TarjetasDto> QuitarAsync(Guid varianteId, SolicitudQuitarTarjeta s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, s.SucursalId, Permiso, "Las tarjetas de precio las quita quien tiene «Productos, precios y etiquetas».", ct);
        if (await db.TarjetasDePrecio.SingleOrDefaultAsync(t => t.SucursalId == s.SucursalId && t.VarianteId == varianteId, ct) is { } tarjeta)
        {
            db.TarjetasDePrecio.Remove(tarjeta);
            await db.SaveChangesAsync(ct);
        }
        return await LeerAsync(s.SucursalId, ct);
    }

    /// <summary>
    /// Una consulta del verificador: cuenta para lo más preguntado. Cualquier caja; si el producto ya no existe, no cuenta. La
    /// sucursal es la de la caja, si se dice cuál.
    /// </summary>
    public async Task ContarConsultaAsync(SolicitudConsulta s, CancellationToken ct = default)
    {
        if (!await db.Variantes.AnyAsync(v => v.Id == s.VarianteId, ct))
            return;
        var sucursalId = s.CajaId is { } cajaId
            ? await db.Cajas.AsNoTracking().Where(c => c.Id == cajaId).Select(c => (Guid?)c.SucursalId).SingleOrDefaultAsync(ct)
            : null;
        if (sucursalId is null && !await db.Sucursales.AnyAsync(x => x.Id == s.SucursalId, ct))
            return;
        db.ConsultasDePrecio.Add(new ConsultaDePrecio
        {
            SucursalId = sucursalId ?? s.SucursalId,
            CajaId = sucursalId is null ? null : s.CajaId,
            VarianteId = s.VarianteId,
            FechaUtc = Ahora,
        });
        await db.SaveChangesAsync(ct);
    }

    // ── Lo de cada producto ─────────────────────────────────────────────────

    /// <summary>Lo que se dice de un producto: su nombre, el código de su tarjeta, los de su pieza, su precio de hoy y dónde está.</summary>
    private sealed record Datos(Guid VarianteId, string Nombre, string? Codigo, IReadOnlyCollection<string> Codigos, long? Precio, string? Donde, bool Activo);

    private async Task<Dictionary<Guid, Datos>> DatosAsync(IEnumerable<Guid> variantes, Guid sucursalId, CancellationToken ct)
    {
        var ids = variantes.Distinct().ToList();
        if (ids.Count == 0)
            return [];
        var etiquetas = new RegistroEtiquetas(db);
        var lista = await etiquetas.VariantesAsync(ids, tracking: false, ct);
        var precios = await etiquetas.MenudeoAsync(ids, sucursalId, ct);
        // Dónde está: el primer exhibidor o temporada activo que lo tiene, con su lugar («Mostrador · Repisa de arriba»).
        var donde = (await (from p in db.ProductosExhibidos.AsNoTracking()
                            join l in db.LugaresExhibidor.AsNoTracking() on p.LugarId equals l.Id
                            join e in db.Exhibidores.AsNoTracking() on l.ExhibidorId equals e.Id
                            where e.Activo && ids.Contains(p.VarianteId)
                            select new { p.VarianteId, Exhibidor = e.Nombre, e.Tipo, e.Orden, Lugar = l.Nombre, LugarOrden = l.Orden }).ToListAsync(ct))
            .GroupBy(x => x.VarianteId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Tipo).ThenBy(x => x.Orden).ThenBy(x => x.LugarOrden).Select(x => $"{x.Exhibidor} · {x.Lugar}").First());
        return lista.Values.ToDictionary(v => v.Id, v => new Datos(v.Id, RegistroCompras.Nombre(v), RegistroEtiquetas.Codigo(v),
            v.Presentaciones.Where(p => p.EsBase).SelectMany(p => p.Codigos).Select(c => c.Codigo).ToList(),
            precios.TryGetValue(v.Id, out var precio) ? precio : null, donde.GetValueOrDefault(v.Id), v.Activa && v.Producto!.Activo));
    }

    private static ProductoConTarjetaDto Dto(Datos d, TarjetaDePrecio? tarjeta, string? porQue) =>
        new(d.VarianteId, d.Nombre, d.Codigo, d.Precio, tarjeta?.PrecioCentavos, tarjeta is null ? null : HoraMexico.ALocal(tarjeta.ImpresaUtc), porQue, d.Donde);
}
