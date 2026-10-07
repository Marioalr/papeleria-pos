using System.Text;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Datos.Documentos;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Telegram;

namespace PapeleriaPos.Datos;

/// <summary>
/// F4 Inventario, compras (docs/fase-2-inventario.md, entrega 2): quien recibe cuenta la mercancía a ciegas; quien
/// captura los costos pasa la nota del proveedor y cierra la compra. Al cerrar, lo contado entra al kardex con el costo
/// de la nota (el costo promedio se ajusta solo), cada proveedor guarda su último costo, las diferencias quedan para
/// reclamarlas y, si un producto queda debajo del margen mínimo, se sugiere precio: el precio no cambia solo.
/// </summary>
public sealed class RegistroCompras(PosDbContext db)
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    // ── Proveedores ──────────────────────────────────────────────────────────

    public async Task<List<ProveedorDto>> ProveedoresAsync(bool conInactivos, CancellationToken ct = default) =>
        (await db.Proveedores.AsNoTracking().Where(p => conInactivos || p.Activo).ToListAsync(ct))
            .OrderBy(p => p.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .Select(Dto).ToList();

    /// <summary>Los dan de alta y los editan quienes editan el catálogo (el dueño y el encargado).</summary>
    public async Task<ProveedorDto> GuardarProveedorAsync(SolicitudGuardarProveedor s, CancellationToken ct = default)
    {
        await RequerirAsync(s.UsuarioId, s.SucursalId, AccionControlada.EditarCatalogo, "Los proveedores los dan de alta el dueño y el encargado.", ct);
        var f = s.Proveedor;
        if (string.IsNullOrWhiteSpace(f.Nombre))
            throw new ArgumentException("El proveedor lleva nombre.");
        var nombre = f.Nombre.Trim();
        var otros = await db.Proveedores.AsNoTracking().Where(p => p.Id != f.Id).Select(p => p.Nombre).ToListAsync(ct);
        if (otros.Any(o => Texto.Normalizar(o) == Texto.Normalizar(nombre)))
            throw new InvalidOperationException($"Ya existe el proveedor «{nombre}».");

        var proveedor = f.Id is { } id
            ? await db.Proveedores.FindAsync([id], ct) ?? throw new InvalidOperationException("El proveedor ya no existe.")
            : null;
        if (proveedor is null)
        {
            proveedor = new Proveedor { Nombre = nombre };
            db.Proveedores.Add(proveedor);
        }
        (proveedor.Nombre, proveedor.Telefono, proveedor.Rfc, proveedor.Notas, proveedor.Activo) =
            (nombre, Limpio(f.Telefono), Limpio(f.Rfc)?.ToUpperInvariant(), Limpio(f.Notas), f.Activo);
        new ServicioControl(db).Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.EditarCatalogo,
            f.Id is null ? $"Alta del proveedor «{nombre}»" : $"Editó el proveedor «{nombre}»");
        await db.SaveChangesAsync(ct);
        return Dto(proveedor);
    }

    // ── Compras ──────────────────────────────────────────────────────────────

    /// <summary>Las que siguen abiertas y las últimas 50 cerradas o canceladas. El total, solo para quien ve los costos.</summary>
    public async Task<List<CompraResumenDto>> ListaAsync(Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        var veCostos = await VeCostosAsync(usuarioId, sucursalId, ct);
        var compras = await db.Compras.AsNoTracking().Include(c => c.Conteo).Include(c => c.Renglones).AsSplitQuery()
            .Where(c => c.SucursalId == sucursalId && (c.Estado == EstadoCompra.Recibiendo || c.Estado == EstadoCompra.PorCostear))
            .ToListAsync(ct);
        compras.AddRange(await db.Compras.AsNoTracking().Include(c => c.Conteo).Include(c => c.Renglones).AsSplitQuery()
            .Where(c => c.SucursalId == sucursalId && (c.Estado == EstadoCompra.Cerrada || c.Estado == EstadoCompra.Cancelada))
            .OrderByDescending(c => c.Folio).Take(50).ToListAsync(ct));
        var proveedores = await db.Proveedores.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Nombre, ct);
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        return compras
            .OrderBy(c => c.Estado is EstadoCompra.Cerrada or EstadoCompra.Cancelada).ThenByDescending(c => c.Folio)
            .Select(c => new CompraResumenDto(c.Id, c.FolioTexto, proveedores.GetValueOrDefault(c.ProveedorId, "?"), c.Documento, c.Estado,
                HoraMexico.ALocal(c.AbiertaUtc), usuarios.GetValueOrDefault(c.RecibioId, "?"), c.Conteo.Select(x => x.VarianteId).Distinct().Count(),
                veCostos && c.Estado != EstadoCompra.Recibiendo ? c.TotalNotaCentavos : null))
            .ToList();
    }

    /// <summary>Quien recibe abre la compra con el proveedor (y el número de su nota, si lo trae) y empieza a contar.</summary>
    public async Task<CompraDto> AbrirAsync(SolicitudAbrirCompra s, CancellationToken ct = default)
    {
        await RequerirAsync(s.UsuarioId, s.SucursalId, AccionControlada.RecibirMercancia, "No tienes permiso para recibir mercancía.", ct);
        if (await db.Proveedores.FindAsync([s.ProveedorId], ct) is not { Activo: true })
            throw new ArgumentException("Elige el proveedor.");
        var compra = new Compra
        {
            SucursalId = s.SucursalId, ProveedorId = s.ProveedorId, Folio = await SiguienteFolioAsync(s.SucursalId, ct), Documento = Limpio(s.Documento),
            RecibioId = s.UsuarioId, AbiertaUtc = Ahora,
        };
        db.Compras.Add(compra);
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compra.Id, s.UsuarioId, ct))!;
    }

    /// <summary>Pone lo que se contó de una presentación (cero la quita). A ciegas: la respuesta no trae la nota.</summary>
    public async Task<CompraDto> ContarAsync(Guid compraId, SolicitudConteo s, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirAsync(s.UsuarioId, compra.SucursalId, AccionControlada.RecibirMercancia, "No tienes permiso para recibir mercancía.", ct);
        var presentacion = await PresentacionAsync(s.VarianteId, s.PresentacionId, ct);
        var cantidad = new Cantidad(s.CantidadMilesimas);
        if (!presentacion.Variante!.AdmiteCantidad(cantidad.Por(presentacion.Factor)))
            throw new ArgumentException($"«{Nombre(presentacion.Variante)}» se cuenta en piezas enteras.");

        var antes = compra.Conteo.ToList();
        compra.Contar(s.VarianteId, s.PresentacionId, cantidad, presentacion.Factor);
        Sincronizar(antes, compra.Conteo);
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, s.UsuarioId, ct))!;
    }

    public async Task<CompraDto> TerminarConteoAsync(Guid compraId, Guid usuarioId, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirAsync(usuarioId, compra.SucursalId, AccionControlada.RecibirMercancia, "No tienes permiso para recibir mercancía.", ct);
        compra.TerminarConteo(Ahora);
        var proveedor = await NombreProveedorAsync(compra.ProveedorId, ct);
        var productos = compra.Conteo.Select(c => c.VarianteId).Distinct().Count();
        new ServicioControl(db).Registrar(compra.SucursalId, null, usuarioId, AccionControlada.RecibirMercancia,
            $"Recibió la compra {compra.FolioTexto} de «{proveedor}»: {productos} {(productos == 1 ? "producto contado" : "productos contados")}");
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, usuarioId, ct))!;
    }

    /// <summary>Agrega o cambia un renglón de la nota. Lo hace quien captura los costos, con el conteo ya cerrado.</summary>
    public async Task<CompraDto> RenglonAsync(Guid compraId, SolicitudRenglonCompra s, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirCosteoAsync(s.UsuarioId, compra.SucursalId, ct);
        var presentacion = await PresentacionAsync(s.VarianteId, s.PresentacionId, ct);
        var cantidad = new Cantidad(s.CantidadMilesimas);
        if (!presentacion.Variante!.AdmiteCantidad(cantidad.Por(presentacion.Factor)))
            throw new ArgumentException($"«{Nombre(presentacion.Variante)}» se cuenta en piezas enteras.");

        var antes = compra.Renglones.ToList();
        var renglon = compra.Renglon(s.RenglonId, s.VarianteId, s.PresentacionId, cantidad, presentacion.Factor, s.ImporteCentavos,
            $"{Nombre(presentacion.Variante)} ({presentacion.Nombre})", s.CodigoProveedor);
        Sincronizar(antes, compra.Renglones);
        // Un renglón del documento: la próxima vez que este proveedor mande esa clave (o esa descripción), llega ligado.
        if (renglon.DelDocumento)
            await LigarAsync(compra.ProveedorId, LigaProveedor.ClaveDe(renglon.CodigoProveedor, renglon.Descripcion), presentacion.Id, ct);
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, s.UsuarioId, ct))!;
    }

    /// <summary>
    /// El documento del proveedor (XML de la factura o PDF de la nota) llena los renglones. Los que el POS reconoce (por
    /// la clave que ya se ligó con este proveedor, o por código de barras) llegan ligados; los demás se ligan a mano.
    /// </summary>
    public async Task<CompraDto> CargarDocumentoAsync(Guid compraId, SolicitudDocumentoCompra s, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirCosteoAsync(s.UsuarioId, compra.SucursalId, ct);
        if (compra.Estado != EstadoCompra.PorCostear)
            throw new InvalidOperationException(compra.Estado == EstadoCompra.Recibiendo ? "La nota se captura cuando termina el conteo." : "La nota ya no se puede cambiar.");
        byte[] contenido;
        try
        {
            contenido = Convert.FromBase64String(s.ContenidoBase64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("El archivo no llegó completo: vuelve a cargarlo.");
        }
        var leido = LectorDocumentos.Leer(contenido);
        if (leido.Uuid is { } uuid
            && await db.ComprasDocumentos.Where(d => d.Uuid == uuid).Join(db.Compras, d => d.CompraId, c => c.Id, (d, c) => c)
                .FirstOrDefaultAsync(c => c.Estado != EstadoCompra.Cancelada, ct) is { } otra)
            throw new InvalidOperationException($"Esa factura ya se cargó en la compra {otra.FolioTexto}.");

        var avisos = leido.Avisos.ToList();
        var proveedor = await db.Proveedores.FindAsync([compra.ProveedorId], ct) ?? throw new InvalidOperationException("El proveedor ya no existe.");
        if (leido.RfcEmisor is { } rfc)
        {
            if (proveedor.Rfc is null)
            {
                proveedor.Rfc = rfc;
                avisos.Add($"Se guardó el RFC del proveedor: {rfc}.");
            }
            else if (!string.Equals(proveedor.Rfc, rfc, StringComparison.OrdinalIgnoreCase))
                avisos.Add($"La factura es de {leido.NombreEmisor ?? "otro"} (RFC {rfc}) y el proveedor de esta compra tiene RFC {proveedor.Rfc}.");
        }
        if (compra.Documento is null && leido.Folio is { } folio)
            compra.Documento = folio;

        var renglones = new List<RenglonDocumento>();
        foreach (var l in leido.Lineas)
            renglones.Add(new RenglonDocumento(Limpio(l.Codigo), l.Descripcion, l.Unidad, Cantidad.Unidades(l.Cantidad), l.ImporteCentavos,
                await ReconocerAsync(compra.ProveedorId, l, ct)));
        var documento = new CompraDocumento
        {
            CompraId = compra.Id, Nombre = Path.GetFileName(s.Nombre.Trim()) is { Length: > 0 } nombre ? nombre : "documento", Tipo = leido.Tipo,
            Contenido = contenido, Folio = leido.Folio, Uuid = leido.Uuid, RfcEmisor = leido.RfcEmisor, TotalCentavos = leido.TotalCentavos,
            Avisos = avisos.Count == 0 ? null : string.Join('\n', avisos), UsuarioId = s.UsuarioId, CargadoUtc = Ahora,
        };
        var antes = compra.Renglones.ToList();
        compra.AgregarDocumento(documento, renglones);
        Sincronizar(antes, compra.Renglones);
        db.ComprasDocumentos.Add(documento);
        new ServicioControl(db).Registrar(compra.SucursalId, null, s.UsuarioId, AccionControlada.CostearCompra,
            $"Cargó «{documento.Nombre}» en la compra {compra.FolioTexto}: {renglones.Count} renglones, {renglones.Count(r => r.Liga is null)} por ligar");
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, s.UsuarioId, ct))!;
    }

    /// <summary>
    /// Qué presentación es un renglón del documento: la que se ligó antes con esta clave del proveedor (o esta descripción),
    /// o la del código de barras si la clave es uno. Si no se sabe, o no se puede llevar al inventario, va sin ligar.
    /// </summary>
    private async Task<LigaRenglon?> ReconocerAsync(Guid proveedorId, LineaLeida l, CancellationToken ct)
    {
        var clave = LigaProveedor.ClaveDe(l.Codigo, l.Descripcion);
        var presentacionId = await db.LigasProveedor.Where(x => x.ProveedorId == proveedorId && x.Clave == clave).Select(x => (Guid?)x.PresentacionId)
            .FirstOrDefaultAsync(ct);
        if (presentacionId is null && Limpio(l.Codigo) is { } codigo)
            presentacionId = await db.CodigosBarras.Where(c => c.Codigo == codigo).Select(c => (Guid?)c.PresentacionId).FirstOrDefaultAsync(ct);
        if (presentacionId is null)
            return null;
        var presentacion = await db.Presentaciones.AsNoTracking().Include(p => p.Variante).ThenInclude(v => v!.Producto)
            .SingleOrDefaultAsync(p => p.Id == presentacionId, ct);
        if (presentacion?.Variante is not { Activa: true, Producto.LlevaInventario: true } variante
            || !variante.AdmiteCantidad(Cantidad.Unidades(l.Cantidad).Por(presentacion.Factor)))
            return null;
        return new LigaRenglon(variante.Id, presentacion.Id, presentacion.Factor);
    }

    private async Task LigarAsync(Guid proveedorId, string clave, Guid presentacionId, CancellationToken ct)
    {
        var liga = await db.LigasProveedor.FirstOrDefaultAsync(x => x.ProveedorId == proveedorId && x.Clave == clave, ct);
        if (liga is null)
            db.LigasProveedor.Add(new LigaProveedor { ProveedorId = proveedorId, Clave = clave, PresentacionId = presentacionId, ModificadaUtc = Ahora });
        else
            (liga.PresentacionId, liga.ModificadaUtc) = (presentacionId, Ahora);
    }

    public async Task<CompraDto> QuitarRenglonAsync(Guid compraId, Guid renglonId, Guid usuarioId, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirCosteoAsync(usuarioId, compra.SucursalId, ct);
        var antes = compra.Renglones.ToList();
        compra.QuitarRenglon(renglonId);
        Sincronizar(antes, compra.Renglones);
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, usuarioId, ct))!;
    }

    /// <summary>
    /// Lo contado entra al kardex con el costo de la nota (lo que faltó no entra ni se paga: se reclama; lo que no viene
    /// en la nota entra a su último costo). Cada proveedor guarda su último costo por unidad base. Las diferencias y lo
    /// que quedó debajo del margen mínimo van a la bitácora y por Telegram al dueño.
    /// </summary>
    public async Task<CompraDto> CerrarAsync(Guid compraId, Guid usuarioId, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirCosteoAsync(usuarioId, compra.SucursalId, ct);
        var resumen = (await DetalleAsync(compraId, usuarioId, ct))!;
        compra.Cerrar(usuarioId, Ahora);

        var comparacion = ComparacionCompra.Comparar(compra.Conteo, compra.Renglones);
        var ids = comparacion.Select(c => c.VarianteId).ToList();
        var variantes = await db.Variantes.Include(v => v.Producto).ThenInclude(p => p!.Marca).Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var proveedores = await db.ProveedoresVariantes.Where(p => ids.Contains(p.VarianteId)).ToListAsync(ct);
        var inventario = new RegistroInventario(db);
        var total = 0L;
        foreach (var c in comparacion.Where(c => c.Contado.EsPositiva))
        {
            var variante = variantes[c.VarianteId];
            var costoConocido = c.Nota.EsPositiva ? 0 : await CostoConocidoAsync(compra, variante, proveedores, inventario, ct);
            var costo = c.CostoDeEntrada(costoConocido);
            await inventario.AgregarAsync(Kardex.Entrada(compra.SucursalId, variante, TipoMovimiento.Compra, c.Contado, costo, Ahora,
                documentoTipo: nameof(Compra), documentoId: compra.Id), ct);
            total += costo;
        }

        foreach (var c in comparacion.Where(c => c.Nota.EsPositiva))
        {
            var pv = proveedores.FirstOrDefault(p => p.ProveedorId == compra.ProveedorId && p.VarianteId == c.VarianteId);
            if (pv is null)
            {
                pv = new ProveedorVariante { ProveedorId = compra.ProveedorId, VarianteId = c.VarianteId };
                db.ProveedoresVariantes.Add(pv);
                proveedores.Add(pv);
            }
            pv.UltimoCostoCentavos = (long)Math.Round(c.CostoUnitario!.Value, MidpointRounding.AwayFromZero);
            pv.UltimaCompraUtc = Ahora;
            if (compra.Renglones.Where(r => r.VarianteId == c.VarianteId).Select(r => r.CodigoProveedor).FirstOrDefault(x => x is not null) is { } codigo)
                pv.CodigoProveedor = codigo;
        }

        var diferencias = resumen.Comparacion!.Where(c => c.Diferencia != DiferenciaCompra.Ninguna).Select(TextoDiferencia).ToList();
        var margenBajo = resumen.Comparacion!.Where(c => c.PrecioSugeridoCentavos is not null).Select(TextoMargen).ToList();
        var productos = comparacion.Count(c => c.Contado.EsPositiva);
        var detalle = $"Cerró la compra {compra.FolioTexto} de «{resumen.Proveedor}»{(compra.Documento is { } d ? $" (nota {d})" : "")}: "
            + $"{productos} {(productos == 1 ? "producto" : "productos")} por {AlertasTelegram.Dinero(total)}";
        if (diferencias.Count > 0)
            detalle += $". Diferencias: {string.Join("; ", diferencias)}";
        new ServicioControl(db).Registrar(compra.SucursalId, null, usuarioId, AccionControlada.CostearCompra, detalle, total);

        if (diferencias.Count > 0 || margenBajo.Count > 0)
        {
            var texto = new StringBuilder($"{AlertasTelegram.Titulo(TipoAlerta.Compra)} · {compra.FolioTexto} · {resumen.Proveedor}").AppendLine();
            texto.AppendLine($"{await NombreUsuarioAsync(usuarioId, ct)} la cerró: {productos} {(productos == 1 ? "producto" : "productos")} por {AlertasTelegram.Dinero(total)}");
            if (diferencias.Count > 0)
            {
                texto.AppendLine("Diferencias contra la nota (para reclamar):");
                diferencias.ForEach(x => texto.AppendLine($"  {x}"));
            }
            if (margenBajo.Count > 0)
            {
                texto.AppendLine("Debajo del margen mínimo (el precio no cambió):");
                margenBajo.ForEach(x => texto.AppendLine($"  {x}"));
            }
            AlertasTelegram.Avisar(db, compra.SucursalId, TipoAlerta.Compra, texto.ToString().TrimEnd(), $"compra:{compra.Id}");
        }
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, usuarioId, ct))!;
    }

    /// <summary>Antes de cerrarla. Quien la abrió puede cancelarla mientras cuenta; después, solo quien captura costos.</summary>
    public async Task<CompraDto> CancelarAsync(Guid compraId, SolicitudCancelarCompra s, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        var propia = compra.Estado == EstadoCompra.Recibiendo && compra.RecibioId == s.UsuarioId;
        if (!propia)
            await RequerirCosteoAsync(s.UsuarioId, compra.SucursalId, ct);
        compra.Cancelar(s.Motivo, Ahora);
        new ServicioControl(db).Registrar(compra.SucursalId, null, s.UsuarioId, propia ? AccionControlada.RecibirMercancia : AccionControlada.CostearCompra,
            $"Canceló la compra {compra.FolioTexto} de «{await NombreProveedorAsync(compra.ProveedorId, ct)}». Motivo: {compra.Motivo}");
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, s.UsuarioId, ct))!;
    }

    /// <summary>
    /// La compra como la ve cada quien: lo contado siempre; la nota, la comparación y los costos solo quien los captura y
    /// con el conteo cerrado (quien cuenta lo hace a ciegas).
    /// </summary>
    public async Task<CompraDto?> DetalleAsync(Guid compraId, Guid usuarioId, CancellationToken ct = default)
    {
        var compra = await db.Compras.AsNoTracking().Include(c => c.Conteo).Include(c => c.Renglones).AsSplitQuery()
            .SingleOrDefaultAsync(c => c.Id == compraId, ct);
        if (compra is null)
            return null;
        var veNota = compra.Estado != EstadoCompra.Recibiendo && await VeCostosAsync(usuarioId, compra.SucursalId, ct);
        var ids = compra.Conteo.Select(c => c.VarianteId).Union(compra.Renglones.Where(r => r.VarianteId is not null).Select(r => r.VarianteId!.Value)).ToList();
        var variantes = await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca).Include(v => v.Presentaciones)
            .Where(v => ids.Contains(v.Id)).AsSplitQuery().ToDictionaryAsync(v => v.Id, ct);
        string Presentacion(Guid varianteId, Guid presentacionId) =>
            variantes.GetValueOrDefault(varianteId)?.Presentaciones.FirstOrDefault(p => p.Id == presentacionId)?.Nombre ?? "?";
        string Producto(Guid varianteId) => variantes.TryGetValue(varianteId, out var v) ? Nombre(v) : "?";

        var conteo = compra.Conteo.OrderBy(c => Producto(c.VarianteId), StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ConteoCompraDto(c.VarianteId, c.PresentacionId, Producto(c.VarianteId), Presentacion(c.VarianteId, c.PresentacionId),
                c.Cantidad.Milesimas, c.CantidadBase.EnUnidades))
            .ToList();
        var renglones = veNota
            ? compra.Renglones.OrderBy(r => r.Orden).Select(r => new RenglonCompraDto(r.Id, r.Orden, r.CodigoProveedor, r.Descripcion, r.Unidad, r.DelDocumento,
                r.VarianteId, r.PresentacionId, r.VarianteId is { } v ? Producto(v) : null,
                r is { VarianteId: { } rv, PresentacionId: { } rp } ? Presentacion(rv, rp) : null, r.Cantidad.Milesimas, r.ImporteCentavos)).ToList()
            : null;
        var comparacion = veNota ? await CompararAsync(compra, variantes, ct) : null;
        var documentos = veNota
            ? (await db.ComprasDocumentos.AsNoTracking().Where(d => d.CompraId == compraId)
                    .Select(d => new { d.Id, d.Nombre, d.Tipo, d.Folio, d.TotalCentavos, d.Avisos, d.CargadoUtc }).ToListAsync(ct))
                .OrderBy(d => d.CargadoUtc)
                .Select(d => new DocumentoCompraDto(d.Id, d.Nombre, d.Tipo, d.Folio, d.TotalCentavos,
                    d.Avisos?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [], HoraMexico.ALocal(d.CargadoUtc)))
                .ToList()
            : null;
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        return new CompraDto(compra.Id, compra.FolioTexto, compra.ProveedorId, await NombreProveedorAsync(compra.ProveedorId, ct), compra.Documento,
            compra.Estado, HoraMexico.ALocal(compra.AbiertaUtc), usuarios.GetValueOrDefault(compra.RecibioId, "?"),
            compra.CosteoId is { } c ? usuarios.GetValueOrDefault(c) : null, compra.CerradaUtc is { } cerrada ? HoraMexico.ALocal(cerrada) : null, compra.Motivo,
            conteo, renglones, comparacion, veNota ? compra.TotalNotaCentavos : null, documentos);
    }

    private async Task<List<ComparacionCompraDto>> CompararAsync(Compra compra, Dictionary<Guid, Variante> variantes, CancellationToken ct)
    {
        var comparacion = ComparacionCompra.Comparar(compra.Conteo, compra.Renglones);
        var ids = comparacion.Select(c => c.VarianteId).ToList();
        var precios = (await db.Precios.AsNoTracking()
                .Where(p => ids.Contains(p.VarianteId) && p.PresentacionId == null && (p.SucursalId == null || p.SucursalId == compra.SucursalId))
                .ToListAsync(ct))
            .ToLookup(p => p.VarianteId);
        var anteriores = compra.Estado == EstadoCompra.PorCostear
            ? await db.ProveedoresVariantes.AsNoTracking().Where(p => p.ProveedorId == compra.ProveedorId && ids.Contains(p.VarianteId))
                .ToDictionaryAsync(p => p.VarianteId, p => p.UltimoCostoCentavos, ct)
            : [];
        var general = AlertasTelegram.Configuracion(db, compra.SucursalId).MargenMinimoCentesimas;
        var departamentos = await db.Departamentos.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.MargenMinimoCentesimas, ct);

        return comparacion.Select(c =>
            {
                var variante = variantes.GetValueOrDefault(c.VarianteId);
                var deLaVariante = precios[c.VarianteId].ToList();
                var menudeo = (deLaVariante.Any(p => p.SucursalId == compra.SucursalId) ? deLaVariante.Where(p => p.SucursalId == compra.SucursalId) : deLaVariante)
                    .MinBy(p => p.CantidadMinima)?.PrecioCentavos;
                var minimo = variante?.Producto is { } p ? departamentos.GetValueOrDefault(p.DepartamentoId) ?? general : general;
                int? margen = menudeo is { } precio && c.CostoUnitario is { } costo ? Margen.Calcular(precio, costo) : null;
                long? sugerido = margen < minimo ? Margen.PrecioSugerido(c.CostoUnitario!.Value, minimo) : null;
                return new ComparacionCompraDto(c.VarianteId, variante is null ? "?" : Nombre(variante), variante?.UnidadBase ?? UnidadBase.Pieza,
                    c.Nota.EnUnidades, c.Contado.EnUnidades, c.Tipo, c.CostoUnitario, anteriores.GetValueOrDefault(c.VarianteId), menudeo, margen, minimo,
                    sugerido);
            })
            .OrderBy(c => c.Producto, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ── Margen mínimo ────────────────────────────────────────────────────────

    public async Task<MargenesDto> MargenesAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var general = AlertasTelegram.Configuracion(db, sucursalId).MargenMinimoCentesimas;
        var departamentos = (await db.Departamentos.AsNoTracking().Where(d => d.Activo).ToListAsync(ct))
            .OrderBy(d => d.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new MargenDepartamentoDto(d.Id, d.Nombre, d.MargenMinimoCentesimas)).ToList();
        return new MargenesDto(general, departamentos);
    }

    /// <summary>Solo el dueño. El general es de la sucursal; el de cada departamento, de todas.</summary>
    public async Task<MargenesDto> GuardarMargenesAsync(Guid sucursalId, SolicitudMargenes s, CancellationToken ct = default)
    {
        await RequerirAsync(s.UsuarioId, sucursalId, AccionControlada.ConfigurarMargen, "El margen mínimo lo pone el dueño.", ct);
        var m = s.Margenes;
        if (m.MargenMinimoCentesimas is < 0 or >= 10000 || m.Departamentos.Any(d => d.MargenMinimoCentesimas is < 0 or >= 10000))
            throw new ArgumentException("El margen va de 0 a 99.99 %.");
        var configuracion = await db.ConfiguracionesSucursal.FindAsync([sucursalId], ct);
        if (configuracion is null)
        {
            configuracion = new ConfiguracionSucursal { SucursalId = sucursalId };
            db.ConfiguracionesSucursal.Add(configuracion);
        }
        var cambios = new List<string>();
        if (configuracion.MargenMinimoCentesimas != m.MargenMinimoCentesimas)
            cambios.Add($"general {Margen.Texto(configuracion.MargenMinimoCentesimas)} → {Margen.Texto(m.MargenMinimoCentesimas)}");
        configuracion.MargenMinimoCentesimas = m.MargenMinimoCentesimas;
        foreach (var d in m.Departamentos)
        {
            if (await db.Departamentos.FindAsync([d.DepartamentoId], ct) is not { } departamento || departamento.MargenMinimoCentesimas == d.MargenMinimoCentesimas)
                continue;
            cambios.Add($"{departamento.Nombre} {Texto(departamento.MargenMinimoCentesimas)} → {Texto(d.MargenMinimoCentesimas)}");
            departamento.MargenMinimoCentesimas = d.MargenMinimoCentesimas;
        }
        if (cambios.Count > 0)
            new ServicioControl(db).Registrar(sucursalId, null, s.UsuarioId, AccionControlada.ConfigurarMargen, $"Margen mínimo: {string.Join("; ", cambios)}");
        await db.SaveChangesAsync(ct);
        return await MargenesAsync(sucursalId, ct);

        static string Texto(int? centesimas) => centesimas is { } c ? Margen.Texto(c) : "el general";
    }

    // ── Apoyo ────────────────────────────────────────────────────────────────

    /// <summary>¿Ve la nota y los costos? Quien puede capturarlos (por omisión, el dueño y el encargado).</summary>
    public async Task<bool> VeCostosAsync(Guid usuarioId, Guid sucursalId, CancellationToken ct = default) =>
        await db.Usuarios.AnyAsync(u => u.Id == usuarioId && u.Activo, ct)
        && (await new ServicioControl(db).EvaluarAsync(usuarioId, sucursalId, AccionControlada.CostearCompra, ct: ct)).Nivel == NivelPermiso.Permitido;

    private Task RequerirCosteoAsync(Guid usuarioId, Guid sucursalId, CancellationToken ct) =>
        RequerirAsync(usuarioId, sucursalId, AccionControlada.CostearCompra, "La nota y los costos los capturan el dueño y el encargado.", ct);

    private async Task RequerirAsync(Guid usuarioId, Guid sucursalId, AccionControlada accion, string mensaje, CancellationToken ct)
    {
        var decision = await new ServicioControl(db).EvaluarAsync(usuarioId, sucursalId, accion, ct: ct);
        if (decision.Nivel != NivelPermiso.Permitido)
            throw new PermisoRequerido(accion, NivelPermiso.NoPermitido, mensaje);
    }

    /// <summary>
    /// Lo que llegó sin estar en la nota: el último costo de este proveedor, si no el costo promedio de la sucursal, si
    /// no el último de cualquier proveedor. Sin ninguno, hay que agregarlo a la nota.
    /// </summary>
    private async Task<long> CostoConocidoAsync(
        Compra compra, Variante variante, List<ProveedorVariante> proveedores, RegistroInventario inventario, CancellationToken ct)
    {
        if (proveedores.FirstOrDefault(p => p.ProveedorId == compra.ProveedorId && p.VarianteId == variante.Id)?.UltimoCostoCentavos is { } propio)
            return propio;
        if ((await inventario.ExistenciaAsync(compra.SucursalId, variante.Id, ct)).CostoPromedio is { } promedio)
            return (long)Math.Round(promedio, MidpointRounding.AwayFromZero);
        return proveedores.Where(p => p.VarianteId == variante.Id && p.UltimoCostoCentavos is not null)
                   .OrderByDescending(p => p.UltimaCompraUtc).Select(p => p.UltimoCostoCentavos).FirstOrDefault()
               ?? throw new InvalidOperationException($"«{Nombre(variante)}» llegó sin estar en la nota y no tiene costo: agrégalo a la nota.");
    }

    private static string TextoDiferencia(ComparacionCompraDto c) => c.Diferencia switch
    {
        DiferenciaCompra.Falto => $"faltó {Formato(c.Nota - c.Contado)} de «{c.Producto}»",
        DiferenciaCompra.Sobro => $"sobró {Formato(c.Contado - c.Nota)} de «{c.Producto}»",
        DiferenciaCompra.NoLlego => $"no llegó «{c.Producto}» ({Formato(c.Nota)} en la nota)",
        _ => $"llegó «{c.Producto}» ({Formato(c.Contado)}) sin estar en la nota",
    };

    private static string TextoMargen(ComparacionCompraDto c) =>
        $"«{c.Producto}»: margen {Margen.Texto(c.MargenCentesimas ?? 0)} (mínimo {Margen.Texto(c.MargenMinimoCentesimas)}), "
        + $"precio {AlertasTelegram.Dinero(c.PrecioCentavos ?? 0)} → sugerido {AlertasTelegram.Dinero(c.PrecioSugeridoCentavos ?? 0)}";

    private static string Formato(decimal cantidad) => cantidad.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Agrega a la base lo nuevo de una colección y quita lo que salió (el modelo no borra en cascada).</summary>
    private void Sincronizar<T>(List<T> antes, List<T> despues) where T : class
    {
        foreach (var nuevo in despues.Except(antes))
            db.Add(nuevo);
        foreach (var quitado in antes.Except(despues))
            db.Remove(quitado);
    }

    private async Task<Compra> CompraAsync(Guid compraId, CancellationToken ct) =>
        await db.Compras.Include(c => c.Conteo).Include(c => c.Renglones).AsSplitQuery().SingleOrDefaultAsync(c => c.Id == compraId, ct)
        ?? throw new InvalidOperationException("La compra ya no existe.");

    private async Task<Presentacion> PresentacionAsync(Guid varianteId, Guid presentacionId, CancellationToken ct)
    {
        var presentacion = await db.Presentaciones.Include(p => p.Variante).ThenInclude(v => v!.Producto).ThenInclude(p => p!.Marca)
            .SingleOrDefaultAsync(p => p.Id == presentacionId && p.VarianteId == varianteId, ct)
            ?? throw new InvalidOperationException("El producto ya no existe.");
        if (!presentacion.Variante!.Producto!.LlevaInventario)
            throw new InvalidOperationException($"«{Nombre(presentacion.Variante)}» es un servicio: no lleva inventario.");
        return presentacion;
    }

    private async Task<int> SiguienteFolioAsync(Guid sucursalId, CancellationToken ct)
    {
        var nombre = $"compras:{sucursalId}";
        var secuencia = await db.Secuencias.FindAsync([nombre], ct);
        if (secuencia is null)
        {
            secuencia = new Secuencia { Nombre = nombre };
            db.Secuencias.Add(secuencia);
        }
        return checked((int)++secuencia.Valor);
    }

    private async Task<string> NombreProveedorAsync(Guid proveedorId, CancellationToken ct) =>
        await db.Proveedores.AsNoTracking().Where(p => p.Id == proveedorId).Select(p => p.Nombre).FirstOrDefaultAsync(ct) ?? "?";

    private async Task<string> NombreUsuarioAsync(Guid usuarioId, CancellationToken ct) =>
        await db.Usuarios.AsNoTracking().Where(u => u.Id == usuarioId).Select(u => u.Nombre).FirstOrDefaultAsync(ct) ?? "?";

    /// <summary>Como lo muestra la caja: producto, marca y variante.</summary>
    private static string Nombre(Variante v) =>
        string.Join(" ", new[] { v.Producto?.Nombre, v.Producto?.Marca?.Nombre, v.Descripcion }.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string? Limpio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    private static ProveedorDto Dto(Proveedor p) => new(p.Id, p.Nombre, p.Telefono, p.Rfc, p.Notas, p.Activo);
}
