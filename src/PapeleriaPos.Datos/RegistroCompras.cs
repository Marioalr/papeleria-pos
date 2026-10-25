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
/// reclamarlas y, si un producto queda debajo del margen mínimo, se sugiere precio: el precio no cambia solo. En el piloto
/// con eleventa (<paramref name="lecturaDeEleventa"/>) no se abre, no se cuenta lo recibido ni se cierra una compra: el
/// inventario se lleva en eleventa (<see cref="TrastiendaEnElPiloto"/>). Lo que quedó abierto se consulta y se cancela.
/// </summary>
public sealed class RegistroCompras(PosDbContext db, bool lecturaDeEleventa = false)
{
    /// <summary>Un producto nuevo de la nota necesita departamento: se dan de alta en Productos › Departamentos.</summary>
    public const string SinDepartamentos =
        "No hay ningún departamento activo: da de alta uno (o reactiva uno) en Productos (F3) › Departamentos.";

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    // ── Lo pendiente de cada tarea, para las tarjetas del inventario (entrega 8) ──

    /// <summary>
    /// Cuántas compras se están contando o esperan su nota, cuántas órdenes siguen abiertas (y cuántas ya llevan productos:
    /// lo que se espera que llegue), cuántos departamentos se cuentan completos y cuántos productos están en su mínimo o
    /// debajo (Bajos en inventario). Solo cuentas, sin montos.
    /// </summary>
    public async Task<PendientesInventarioDto> PendientesAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var compras = await db.Compras.AsNoTracking()
            .Where(c => c.SucursalId == sucursalId && (c.Estado == EstadoCompra.Recibiendo || c.Estado == EstadoCompra.PorCostear))
            .GroupBy(c => c.Estado).Select(g => new { Estado = g.Key, Cuantas = g.Count() }).ToDictionaryAsync(x => x.Estado, x => x.Cuantas, ct);
        var ordenes = await db.OrdenesCompra.AsNoTracking().Where(o => o.SucursalId == sucursalId && o.Estado == EstadoOrden.Abierta)
            .Select(o => o.Renglones.Count).ToListAsync(ct);
        var conteos = await db.ConteosDepartamento.AsNoTracking()
            .CountAsync(c => c.SucursalId == sucursalId && c.Estado == EstadoConteoDepartamento.Abierto, ct);
        return new PendientesInventarioDto(compras.GetValueOrDefault(EstadoCompra.Recibiendo), compras.GetValueOrDefault(EstadoCompra.PorCostear),
            ordenes.Count, ordenes.Count(n => n > 0), conteos, await new RegistroReportesInventario(db).ContarBajosAsync(sucursalId, ct));
    }

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
        TrastiendaEnElPiloto.RevisarInventario(lecturaDeEleventa);
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
        TrastiendaEnElPiloto.RevisarInventario(lecturaDeEleventa);
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

    /// <summary>Quien recibe cuenta un código que no está en el catálogo (cero lo quita). Sigue a ciegas.</summary>
    public async Task<CompraDto> ContarDesconocidoAsync(Guid compraId, SolicitudConteoDesconocido s, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirAsync(s.UsuarioId, compra.SucursalId, AccionControlada.RecibirMercancia, "No tienes permiso para recibir mercancía.", ct);
        TrastiendaEnElPiloto.RevisarInventario(lecturaDeEleventa);
        var cantidad = new Cantidad(s.CantidadMilesimas);
        if (!cantidad.EsEntera)
            throw new ArgumentException("Lo que no está en el catálogo se cuenta en piezas enteras.");
        if (await db.CodigosBarras.AnyAsync(c => c.Codigo == s.Codigo.Trim(), ct))
            throw new InvalidOperationException($"El código «{s.Codigo.Trim()}» sí está en el catálogo: escanéalo de nuevo.");
        var antes = compra.Conteo.ToList();
        compra.ContarDesconocido(s.Codigo, s.Descripcion, cantidad);
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
        {
            await LigarAsync(compra.ProveedorId, LigaProveedor.ClaveDe(renglon.CodigoProveedor, renglon.Descripcion), presentacion.Id, ct);
            await CodigoNuevoAsync(compra, renglon.CodigoProveedor, presentacion, ct);
        }
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, s.UsuarioId, ct))!;
    }

    /// <summary>De qué orden de compra viene (o de ninguna): una abierta del mismo proveedor. La comparación muestra lo pedido.</summary>
    public async Task<CompraDto> OrdenAsync(Guid compraId, SolicitudOrdenDeCompra s, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirCosteoAsync(s.UsuarioId, compra.SucursalId, ct);
        if (s.OrdenId is { } id && await db.OrdenesCompra.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, ct) is var orden
            && (orden is null || orden.ProveedorId != compra.ProveedorId || orden.SucursalId != compra.SucursalId || orden.Estado != EstadoOrden.Abierta))
            throw new InvalidOperationException("Esa orden no está abierta o es de otro proveedor.");
        compra.DeLaOrden(s.OrdenId);
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, s.UsuarioId, ct))!;
    }

    private async Task<List<OrdenRenglon>?> PedidoAsync(Compra compra, CancellationToken ct) =>
        compra.OrdenId is { } id ? await db.OrdenesRenglones.AsNoTracking().Where(r => r.OrdenId == id).ToListAsync(ct) : null;

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
        // Lo que quien recibió contó por un código que no estaba en el catálogo, si el documento lo reconoce, ya es de ese producto.
        foreach (var r in renglones.Where(r => r.Liga is not null))
            compra.AsignarDesconocidos(r.CodigoProveedor, r.Liga!.VarianteId, r.Liga.PresentacionId, r.Liga.Factor);
        compra.AgregarDocumento(documento, renglones);
        var cargados = compra.Renglones.Except(antes).ToList();
        await PrecargarAsync(compra, cargados, ct);
        Sincronizar(antes, compra.Renglones);
        db.ComprasDocumentos.Add(documento);
        new ServicioControl(db).Registrar(compra.SucursalId, null, s.UsuarioId, AccionControlada.CostearCompra,
            $"Cargó «{documento.Nombre}» en la compra {compra.FolioTexto}: {cargados.Count} renglones, {cargados.Count(r => r.Ligado)} reconocidos, "
            + $"{cargados.Count(r => r.SugerenciaId is not null)} parecidos a uno del catálogo y {cargados.Count(r => r.Nuevo is not null)} nuevos");
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, s.UsuarioId, ct))!;
    }

    // ── Todo el pedido desde el documento: lo que no está en el catálogo ──────

    /// <summary>
    /// Lo que el documento no reconoció se precarga para revisarlo: si se parece a un producto del catálogo se sugiere
    /// ese (no se liga solo); si no, queda como producto nuevo en borrador con lo que se sabe (nombre, costo, precio
    /// sugerido, código). Quien captura la nota lo corrige y después lo da de alta.
    /// </summary>
    private async Task PrecargarAsync(Compra compra, IReadOnlyList<CompraRenglon> cargados, CancellationToken ct)
    {
        var pendientes = cargados.Where(r => !r.Ligado).ToList();
        if (pendientes.Count == 0)
            return;
        var delProveedor = (await db.ProveedoresVariantes.AsNoTracking().Where(p => p.ProveedorId == compra.ProveedorId).Select(p => p.VarianteId).ToListAsync(ct))
            .ToHashSet();
        var catalogo = (await db.Variantes.AsNoTracking().Where(v => v.Activa && v.Producto!.Activo && v.Producto.Tipo == TipoProducto.Mercancia)
                .Select(v => new { v.Id, Producto = v.Producto!.Nombre, Marca = v.Producto.Marca == null ? null : v.Producto.Marca.Nombre, v.Descripcion })
                .ToListAsync(ct))
            .Select(v => (v.Id, Palabras: NombresParecidos.Palabras($"{v.Producto} {v.Marca} {v.Descripcion}")))
            .ToList();
        foreach (var r in pendientes)
        {
            var palabras = NombresParecidos.Palabras(r.Descripcion);
            var mejor = catalogo.Select(c => (c.Id, Puntaje: NombresParecidos.Parecido(palabras, c.Palabras) + (delProveedor.Contains(c.Id) ? 0.05 : 0)))
                .Where(x => x.Puntaje >= 0.6).OrderByDescending(x => x.Puntaje).FirstOrDefault();
            if (mejor.Id != Guid.Empty)
                r.SugerenciaId = mejor.Id;
            else
                compra.ProponerNuevo(r.Id, await PropuestaAsync(compra, r, ct));
        }
    }

    /// <summary>
    /// Lo que se precarga de un producto nuevo: el nombre del documento, el departamento (activo) donde más productos tiene
    /// ese proveedor, una pieza por unidad, el precio que deja el margen mínimo y el código si es un código de barras libre.
    /// </summary>
    private async Task<ProductoNuevo> PropuestaAsync(Compra compra, CompraRenglon r, CancellationToken ct)
    {
        var departamento = await (from pv in db.ProveedoresVariantes
                                  join v in db.Variantes on pv.VarianteId equals v.Id
                                  where pv.ProveedorId == compra.ProveedorId && v.Producto!.Departamento!.Activo
                                  group v by v.Producto!.DepartamentoId into g
                                  orderby g.Count() descending
                                  select (Guid?)g.Key).FirstOrDefaultAsync(ct)
            ?? (await db.Departamentos.AsNoTracking().Where(d => d.Activo).ToListAsync(ct)).OrderBy(d => d.Nombre, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => (Guid?)d.Id).FirstOrDefault()
            ?? throw new InvalidOperationException(SinDepartamentos);
        var una = Cantidad.Piezas(1);
        var minimos = await MargenesMinimosAsync(compra.SucursalId, ct);
        var codigo = EsCodigoDeBarras(r.CodigoProveedor) && !await db.CodigosBarras.AnyAsync(c => c.Codigo == r.CodigoProveedor, ct) ? r.CodigoProveedor : null;
        return new ProductoNuevo(Titulo(r.Descripcion), departamento, Margen.PrecioSugerido(CostoPorPieza(r, una), minimos.De(departamento)), codigo, una, null);
    }

    /// <summary>
    /// Un renglón del documento como producto nuevo. Sin nombre en la solicitud se precarga; con él, se guarda lo que manda
    /// quien revisa (todo, para corregir cualquier dato).
    /// </summary>
    public async Task<CompraDto> ProponerNuevoAsync(Guid compraId, Guid renglonId, SolicitudProductoNuevo s, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirCosteoAsync(s.UsuarioId, compra.SucursalId, ct);
        var renglon = compra.Renglones.FirstOrDefault(r => r.Id == renglonId) ?? throw new InvalidOperationException("El renglón ya no existe.");
        ProductoNuevo nuevo;
        if (string.IsNullOrWhiteSpace(s.Nombre))
            nuevo = await PropuestaAsync(compra, renglon, ct);
        else
        {
            if (s.DepartamentoId is not { } departamento || !await db.Departamentos.AnyAsync(d => d.Id == departamento, ct))
                throw new ArgumentException("Elige el departamento.");
            if (Limpio(s.Codigo) is { } codigo && await db.CodigosBarras.AsNoTracking().Where(c => c.Codigo == codigo)
                    .Select(c => c.Presentacion!.Variante!.Producto!.Nombre).FirstOrDefaultAsync(ct) is { } de)
                throw new InvalidOperationException($"El código «{codigo}» ya es de «{de}».");
            nuevo = new ProductoNuevo(s.Nombre, departamento, s.PrecioCentavos ?? 0, Limpio(s.Codigo), new Cantidad(s.ContieneMilesimas ?? Cantidad.Escala),
                s.LlegoMilesimas is { } llego ? new Cantidad(llego) : null);
        }
        compra.ProponerNuevo(renglonId, nuevo);
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, s.UsuarioId, ct))!;
    }

    /// <summary>Corrige la cantidad y el importe de un renglón de la nota (ligado, sin ligar o nuevo).</summary>
    public async Task<CompraDto> CorregirRenglonAsync(Guid compraId, Guid renglonId, SolicitudCorregirRenglon s, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirCosteoAsync(s.UsuarioId, compra.SucursalId, ct);
        var renglon = compra.Renglones.FirstOrDefault(r => r.Id == renglonId) ?? throw new InvalidOperationException("El renglón ya no existe.");
        var cantidad = new Cantidad(s.CantidadMilesimas);
        if (renglon is { VarianteId: { } varianteId, PresentacionId: { } presentacionId })
        {
            var presentacion = await PresentacionAsync(varianteId, presentacionId, ct);
            if (!presentacion.Variante!.AdmiteCantidad(cantidad.Por(presentacion.Factor)))
                throw new ArgumentException($"«{Nombre(presentacion.Variante)}» se cuenta en piezas enteras.");
            compra.Renglon(renglon.Id, varianteId, presentacionId, cantidad, presentacion.Factor, s.ImporteCentavos, renglon.Descripcion, renglon.CodigoProveedor);
        }
        else
            compra.CorregirSinLigar(renglon.Id, cantidad, s.ImporteCentavos);
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, s.UsuarioId, ct))!;
    }

    /// <summary>Ya no es un producto nuevo: vuelve a estar sin ligar (para decir de qué producto del catálogo es).</summary>
    public async Task<CompraDto> NoEsNuevoAsync(Guid compraId, Guid renglonId, Guid usuarioId, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirCosteoAsync(usuarioId, compra.SucursalId, ct);
        compra.NoEsNuevo(renglonId);
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, usuarioId, ct))!;
    }

    /// <summary>
    /// Los renglones que siguen sin ligar se precargan como productos nuevos. Los que se parecen a uno del catálogo no:
    /// se confirman o se marcan como nuevos uno por uno, para no dar de alta dos veces el mismo producto.
    /// </summary>
    public async Task<CompraDto> ProponerNuevosAsync(Guid compraId, Guid usuarioId, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirCosteoAsync(usuarioId, compra.SucursalId, ct);
        foreach (var r in compra.Renglones.Where(r => !r.Ligado && r.Nuevo is null && r.SugerenciaId is null).ToList())
            compra.ProponerNuevo(r.Id, await PropuestaAsync(compra, r, ct));
        await db.SaveChangesAsync(ct);
        return (await DetalleAsync(compraId, usuarioId, ct))!;
    }

    /// <summary>
    /// Da de alta en el catálogo los productos nuevos de la nota, como quedaron después de revisarlos: cada uno con su
    /// pieza (y su presentación si la unidad del documento trae varias), su precio y su código. Su renglón queda ligado
    /// (y la próxima vez llega ligado) y lo que llegó entra al conteo: lo que contó quien recibió por su código o lo que
    /// escribió quien captura la nota.
    /// </summary>
    public async Task<CompraDto> DarDeAltaNuevosAsync(Guid compraId, Guid usuarioId, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirCosteoAsync(usuarioId, compra.SucursalId, ct);
        var nuevos = compra.Renglones.Where(r => r.Nuevo is not null).OrderBy(r => r.Orden).ToList();
        if (nuevos.Count == 0)
            throw new InvalidOperationException("No hay productos nuevos por dar de alta.");
        foreach (var r in nuevos.Where(r => r.Nuevo!.Llego is null && ContadoPorCodigo(compra, r) is null))
            throw new ArgumentException($"¿Cuántos llegaron de «{r.Nuevo!.Nombre}»? Quien recibió no lo contó con su código.");

        var catalogo = new RegistroCatalogo(db);
        foreach (var r in nuevos)
        {
            var n = r.Nuevo!;
            var porPaquete = n.Contiene > Cantidad.Piezas(1);
            IReadOnlyList<CodigoFichaDto> codigos = n.Codigo is { } c ? [new CodigoFichaDto(c)] : [];
            var presentaciones = new List<PresentacionFichaDto> { new(null, "Pieza", Cantidad.Escala, true, porPaquete ? [] : codigos) };
            if (porPaquete)
                presentaciones.Add(new PresentacionFichaDto(null, NombrePaquete(r.Unidad), n.Contiene.Milesimas, false, codigos));
            var ficha = new FichaProductoDto(null, n.Nombre, n.DepartamentoId, null, TipoProducto.Mercancia, Impuesto.Iva16, null, true, false,
                [new VarianteFichaDto(null, "", UnidadBase.Pieza, true, presentaciones, [new PrecioFichaDto(null, Cantidad.Escala, n.PrecioCentavos)])]);
            var productoId = await catalogo.GuardarAsync(new SolicitudGuardarProducto(usuarioId, compra.SucursalId, ficha), ct);

            var variante = await db.Variantes.Include(v => v.Presentaciones).SingleAsync(v => v.ProductoId == productoId, ct);
            var presentacion = variante.Presentaciones.Single(p => porPaquete ? !p.EsBase : p.EsBase);
            var antes = compra.Conteo.ToList();
            compra.Renglon(r.Id, variante.Id, presentacion.Id, r.Cantidad, presentacion.Factor, r.ImporteCentavos, r.Descripcion, r.CodigoProveedor);
            compra.AsignarDesconocidos(r.CodigoProveedor, variante.Id, presentacion.Id, presentacion.Factor);
            if (n.Llego is { } llego)
                compra.ContarNuevo(variante.Id, presentacion.Id, llego, presentacion.Factor);
            Sincronizar(antes, compra.Conteo);
            await LigarAsync(compra.ProveedorId, LigaProveedor.ClaveDe(r.CodigoProveedor, r.Descripcion), presentacion.Id, ct);
            new ServicioControl(db).Registrar(compra.SucursalId, null, usuarioId, AccionControlada.CostearCompra,
                $"Dio de alta «{n.Nombre}» desde la compra {compra.FolioTexto}" + (n.Llego is { } l ? $"; contó {Formato(l.EnUnidades)} al darlo de alta" : ""));
            // Uno por uno: si el siguiente falla (un código repetido), los anteriores ya quedaron dados de alta y ligados.
            await db.SaveChangesAsync(ct);
        }
        return (await DetalleAsync(compraId, usuarioId, ct))!;
    }

    /// <summary>
    /// Al ligar un renglón del documento a un producto: si su clave es un código de barras que no está en el catálogo, se
    /// le agrega a esa presentación (el producto cambió de código), y lo que se contó con él ya es de ese producto.
    /// </summary>
    private async Task CodigoNuevoAsync(Compra compra, string? codigo, Presentacion presentacion, CancellationToken ct)
    {
        compra.AsignarDesconocidos(codigo, presentacion.VarianteId, presentacion.Id, presentacion.Factor);
        if (!EsCodigoDeBarras(codigo) || await db.CodigosBarras.AnyAsync(c => c.Codigo == codigo, ct))
            return;
        db.CodigosBarras.Add(new CodigoBarras { Codigo = codigo!, PresentacionId = presentacion.Id, Origen = OrigenCodigo.Fabrica });
        new ServicioControl(db).Registrar(compra.SucursalId, null, null, AccionControlada.EditarCatalogo,
            $"Se agregó el código {codigo} a «{Nombre(presentacion.Variante!)}» ({presentacion.Nombre}) desde la compra {compra.FolioTexto}");
    }

    /// <summary>Lo que contó quien recibió con el código de este renglón, en unidades del documento.</summary>
    private static Cantidad? ContadoPorCodigo(Compra compra, CompraRenglon r) =>
        r.CodigoProveedor is { } codigo && compra.Conteo.Where(c => c.VarianteId is null && string.Equals(c.Codigo, codigo, StringComparison.OrdinalIgnoreCase))
            .ToList() is { Count: > 0 } lineas
            ? lineas.Aggregate(Cantidad.Cero, (s, c) => s + c.Cantidad)
            : null;

    private static decimal CostoPorPieza(CompraRenglon r, Cantidad contiene) =>
        r.Cantidad.Por(contiene) is { EsPositiva: true } piezas ? r.ImporteCentavos * Cantidad.Escala / (decimal)piezas.Milesimas : 0;

    private ProductoNuevoDto NuevoDto(CompraRenglon r, ProductoNuevo n, Compra compra, MargenesMinimos minimos)
    {
        var costo = CostoPorPieza(r, n.Contiene);
        var minimo = minimos.De(n.DepartamentoId);
        return new ProductoNuevoDto(n.Nombre, n.DepartamentoId, n.PrecioCentavos, n.Codigo, n.Contiene.Milesimas, n.Llego?.Milesimas,
            ContadoPorCodigo(compra, r)?.EnUnidades, costo, n.PrecioCentavos > 0 ? Margen.Calcular(n.PrecioCentavos, costo) : null,
            Margen.PrecioSugerido(costo, minimo));
    }

    /// <summary>Los EAN-13 y UPC de fábrica (los que empiezan con 2 son de uso interno de cada tienda).</summary>
    private static bool EsCodigoDeBarras(string? codigo) =>
        codigo is { Length: 13 } c13 && Ean13.EsValido(c13) && c13[0] != '2'
        || codigo is { Length: 12 } c12 && Ean13.EsValido("0" + c12);

    /// <summary>Como los nombres del catálogo, con mayúscula solo al inicio: "SOBRE DE BURBUJA" → "Sobre de burbuja".</summary>
    private static string Titulo(string descripcion) =>
        descripcion.Trim().ToLowerInvariant() is { Length: > 0 } minusculas ? char.ToUpperInvariant(minusculas[0]) + minusculas[1..] : descripcion;

    private static string NombrePaquete(string? unidad) => (unidad ?? "").Trim().ToUpperInvariant().TrimEnd('.') switch
    {
        "CAJA" or "CAJAS" or "CJA" or "CJ" or "XBX" => "Caja",
        "DOC" or "DOCENA" => "Docena",
        "BOLSA" or "BLS" => "Bolsa",
        "BLISTER" => "Blíster",
        _ => "Paquete",
    };

    private sealed record MargenesMinimos(int General, Dictionary<Guid, int?> PorDepartamento)
    {
        public int De(Guid departamentoId) => PorDepartamento.GetValueOrDefault(departamentoId) ?? General;
    }

    private async Task<MargenesMinimos> MargenesMinimosAsync(Guid sucursalId, CancellationToken ct) =>
        new(AlertasTelegram.Configuracion(db, sucursalId).MargenMinimoCentesimas,
            await db.Departamentos.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.MargenMinimoCentesimas, ct));

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
    /// que quedó debajo del margen mínimo van a la bitácora y por Telegram al dueño. Las existencias se leen ya con el candado
    /// de escritura tomado (revisión del PR #138): lo que se agregue o se venda mientras tanto espera, en lugar de perderse.
    /// </summary>
    public async Task<CompraDto> CerrarAsync(Guid compraId, Guid usuarioId, CancellationToken ct = default)
    {
        var compra = await CompraAsync(compraId, ct);
        await RequerirCosteoAsync(usuarioId, compra.SucursalId, ct);
        TrastiendaEnElPiloto.RevisarInventario(lecturaDeEleventa);
        var resumen = (await DetalleAsync(compraId, usuarioId, ct))!;
        compra.Cerrar(usuarioId, Ahora);

        if (compra.OrdenId is { } ordenId)
            (await db.OrdenesCompra.FindAsync([ordenId], ct))?.Recibir(compra.Id);
        // Lo contado por un código que no estaba en el catálogo y ya está (se dio de alta en F3 mientras tanto).
        foreach (var desconocido in compra.Conteo.Where(c => c.VarianteId is null).ToList())
            if (await db.CodigosBarras.Include(c => c.Presentacion).FirstOrDefaultAsync(c => c.Codigo == desconocido.Codigo, ct) is { Presentacion: { } p })
                compra.AsignarDesconocidos(desconocido.Codigo, p.VarianteId, p.Id, p.Factor);
        var comparacion = ComparacionCompra.Comparar(compra.Conteo, compra.Renglones);
        var ids = comparacion.Select(c => c.VarianteId).ToList();
        var variantes = await db.Variantes.Include(v => v.Producto).ThenInclude(p => p!.Marca).Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var proveedores = await db.ProveedoresVariantes.Where(p => ids.Contains(p.VarianteId)).ToListAsync(ct);
        // Desde aquí, con el candado de escritura: la existencia que se lee es la que se escribe.
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
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

        var diferencias = resumen.Comparacion!.Where(c => c.Diferencia != DiferenciaCompra.Ninguna).Select(TextoDiferencia)
            .Concat(compra.Conteo.Where(c => c.VarianteId is null)
                .Select(c => $"llegó el código {c.Codigo}{(c.Descripcion is { } d ? $" («{d}»)" : "")} ({Formato(c.Cantidad.EnUnidades)}), que no está en el catálogo ni en la nota"))
            .ToList();
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
        await transaccion.CommitAsync(ct);
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
        var ids = compra.Conteo.Select(c => c.VarianteId).Concat(compra.Renglones.Select(r => r.VarianteId)).Concat(compra.Renglones.Select(r => r.SugerenciaId))
            .OfType<Guid>().Distinct().ToList();
        var variantes = await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca).Include(v => v.Presentaciones)
            .Where(v => ids.Contains(v.Id)).AsSplitQuery().ToDictionaryAsync(v => v.Id, ct);
        string Presentacion(Guid varianteId, Guid presentacionId) =>
            variantes.GetValueOrDefault(varianteId)?.Presentaciones.FirstOrDefault(p => p.Id == presentacionId)?.Nombre ?? "?";
        string Producto(Guid varianteId) => variantes.TryGetValue(varianteId, out var v) ? Nombre(v) : "?";

        var conteo = compra.Conteo
            .Select(c => c is { VarianteId: { } v, PresentacionId: { } p }
                ? new ConteoCompraDto(v, p, Producto(v), Presentacion(v, p), c.Cantidad.Milesimas, c.CantidadBase.EnUnidades)
                : new ConteoCompraDto(null, null, c.Descripcion ?? $"Código {c.Codigo}", "No está en el catálogo", c.Cantidad.Milesimas, c.Cantidad.EnUnidades,
                    c.Codigo))
            .OrderBy(c => c.VarianteId is null).ThenBy(c => c.Producto, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var minimos = veNota ? await MargenesMinimosAsync(compra.SucursalId, ct) : null;
        var renglones = veNota
            ? compra.Renglones.OrderBy(r => r.Orden).Select(r =>
            {
                var sugerida = r.SugerenciaId is { } s ? variantes.GetValueOrDefault(s) : null;
                var ligada = r.VarianteId is { } v ? variantes.GetValueOrDefault(v) : null;
                return new RenglonCompraDto(r.Id, r.Orden, r.CodigoProveedor, r.Descripcion, r.Unidad, r.DelDocumento, r.VarianteId, r.PresentacionId,
                    ligada is null ? null : Nombre(ligada), r is { VarianteId: { } rv, PresentacionId: { } rp } ? Presentacion(rv, rp) : null,
                    r.Cantidad.Milesimas, r.ImporteCentavos, sugerida?.Id, sugerida is null ? null : Nombre(sugerida),
                    sugerida?.Presentaciones.FirstOrDefault(p => p.EsBase)?.Id, r.Nuevo is { } n ? NuevoDto(r, n, compra, minimos!) : null,
                    ligada?.Presentaciones.OrderByDescending(p => p.EsBase).ThenBy(p => p.Factor)
                        .Select(p => new PresentacionDto(p.Id, p.Nombre, p.Factor.Milesimas, p.EsBase)).ToList());
            }).ToList()
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
            conteo, renglones, comparacion, veNota ? compra.TotalNotaCentavos : null, documentos, veNota ? compra.OrdenId : null,
            veNota && compra.OrdenId is { } orden
                ? await db.OrdenesCompra.AsNoTracking().Where(o => o.Id == orden).Select(o => (int?)o.Folio).FirstOrDefaultAsync(ct) is { } f ? $"OC-{f:D6}" : null
                : null);
    }

    private async Task<List<ComparacionCompraDto>> CompararAsync(Compra compra, Dictionary<Guid, Variante> variantes, CancellationToken ct)
    {
        var comparacion = ComparacionCompra.Comparar(compra.Conteo, compra.Renglones, await PedidoAsync(compra, ct));
        var ids = comparacion.Select(c => c.VarianteId).ToList();
        var faltantes = ids.Where(id => !variantes.ContainsKey(id)).ToList();
        if (faltantes.Count > 0)
            foreach (var v in await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca).Include(v => v.Presentaciones)
                         .Where(v => faltantes.Contains(v.Id)).AsSplitQuery().ToListAsync(ct))
                variantes[v.Id] = v;
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
                    sugerido, c.Pedido?.EnUnidades);
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

    private Task RequerirAsync(Guid usuarioId, Guid sucursalId, AccionControlada accion, string mensaje, CancellationToken ct) =>
        new ServicioControl(db).ExigirAsync(usuarioId, sucursalId, accion, mensaje, ct);

    /// <summary>
    /// Lo que llegó sin estar en la nota: el último costo de este proveedor, si no el costo promedio de la sucursal, si no su
    /// último costo (<see cref="UltimosCostos"/>: el de cualquier proveedor o lo último agregado con «Agregar inventario»). Sin
    /// ninguno, hay que agregarlo a la nota.
    /// </summary>
    private async Task<long> CostoConocidoAsync(
        Compra compra, Variante variante, List<ProveedorVariante> proveedores, RegistroInventario inventario, CancellationToken ct)
    {
        if (proveedores.FirstOrDefault(p => p.ProveedorId == compra.ProveedorId && p.VarianteId == variante.Id)?.UltimoCostoCentavos is { } propio)
            return propio;
        if ((await inventario.ExistenciaAsync(compra.SucursalId, variante.Id, ct)).CostoPromedio is { } promedio)
            return (long)Math.Round(promedio, MidpointRounding.AwayFromZero);
        return await UltimosCostos.DeAsync(db, variante.Id, ct)
               ?? throw new InvalidOperationException($"«{Nombre(variante)}» llegó sin estar en la nota y no tiene costo: agrégalo a la nota.");
    }

    private static string TextoDiferencia(ComparacionCompraDto c) => c.Diferencia switch
    {
        DiferenciaCompra.Falto => $"faltó {Formato(c.Nota - c.Contado)} de «{c.Producto}»",
        DiferenciaCompra.Sobro => $"sobró {Formato(c.Contado - c.Nota)} de «{c.Producto}»",
        DiferenciaCompra.NoLlego when c.Nota == 0 => $"no llegó «{c.Producto}» (se pidieron {Formato(c.Pedido ?? 0)})",
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

    /// <summary>La presentación de una variante que lleva inventario (para contar, la nota o una orden).</summary>
    internal async Task<Presentacion> PresentacionAsync(Guid varianteId, Guid presentacionId, CancellationToken ct)
    {
        var presentacion = await db.Presentaciones.Include(p => p.Variante).ThenInclude(v => v!.Producto).ThenInclude(p => p!.Marca)
            .SingleOrDefaultAsync(p => p.Id == presentacionId && p.VarianteId == varianteId, ct)
            ?? throw new InvalidOperationException("El producto ya no existe.");
        if (!presentacion.Variante!.Producto!.LlevaInventario)
            throw new InvalidOperationException($"«{Nombre(presentacion.Variante)}» es un servicio: no lleva inventario.");
        return presentacion;
    }

    private Task<int> SiguienteFolioAsync(Guid sucursalId, CancellationToken ct) => Secuencia.SiguienteAsync(db, $"compras:{sucursalId}", ct);

    private async Task<string> NombreProveedorAsync(Guid proveedorId, CancellationToken ct) =>
        await db.Proveedores.AsNoTracking().Where(p => p.Id == proveedorId).Select(p => p.Nombre).FirstOrDefaultAsync(ct) ?? "?";

    private async Task<string> NombreUsuarioAsync(Guid usuarioId, CancellationToken ct) =>
        await db.Usuarios.AsNoTracking().Where(u => u.Id == usuarioId).Select(u => u.Nombre).FirstOrDefaultAsync(ct) ?? "?";

    /// <summary>Como lo muestra la caja: producto, marca y variante.</summary>
    internal static string Nombre(Variante v) =>
        string.Join(" ", new[] { v.Producto?.Nombre, v.Producto?.Marca?.Nombre, v.Descripcion }.Where(p => !string.IsNullOrWhiteSpace(p)));

    internal static string? Limpio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    private static ProveedorDto Dto(Proveedor p) => new(p.Id, p.Nombre, p.Telefono, p.Rfc, p.Notas, p.Activo);
}
