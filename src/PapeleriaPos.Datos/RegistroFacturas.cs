using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos.Documentos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos;

/// <summary>
/// Inventario › «Recibir con factura» (dueño, 2026-11-27; docs/recibir-con-factura.md): la mercancía de un proveedor se recibe con
/// el XML de su factura (el CFDI), como un traspaso con su archivo.
/// <list type="bullet">
/// <item>Se carga el XML: el proveedor sale de su RFC (si no está, se da de alta con el nombre de la factura), cada concepto es un
/// renglón con lo que dice que llegó y lo que cobra (con descuento e IVA), y el POS empareja lo que reconoce: por la clave del
/// proveedor que ya se emparejó antes, por el código de barras o por la clave guardada en el producto. Lo que es un servicio (el
/// flete) no se toma. Nada entra todavía. La misma factura (su folio fiscal) no se carga dos veces.</item>
/// <item>Quien recibe escribe lo que llegó de cada renglón y empareja lo que el POS no reconoció. Si es lo que dice la factura, la
/// recibe quien tiene «Recibir con la factura (XML)». Si cambia algo (otra cantidad, algo que no venía, otro producto o un
/// emparejamiento a mano), la recibe así solo quien tiene «Recibir con factura: modificar lo que llegó»; quien no, la deja marcada
/// para revisión.</item>
/// <item>Al recibirla se cierra como cualquier compra (<see cref="RegistroCompras.CerrarAsync(Compra, Guid, AccionControlada, bool, Guid?, CancellationToken)"/>):
/// lo que llegó entra al kardex con el costo de la factura (lo que faltó no entra ni se paga: se reclama), el último costo con el
/// proveedor, y el aviso de margen bajo; el precio no cambia solo. Lo emparejado a mano se recuerda para la siguiente factura.</item>
/// <item>En el piloto con eleventa (<paramref name="lecturaDeEleventa"/>) es solo el documento: no mueve existencias aquí, como los
/// traspasos (<see cref="EnElPiloto"/>).</item>
/// </list>
/// </summary>
public sealed class RegistroFacturas(PosDbContext db, bool lecturaDeEleventa = false)
{
    /// <summary>Cuántas trae la lista, como mucho.</summary>
    public const int MaximoDeLaLista = 60;

    public const string EnElPiloto =
        "Mientras dure el piloto con eleventa, el inventario se lleva en eleventa: aquí la factura queda como documento (con sus costos y lo "
        + "que se emparejó) y no mueve las existencias. Lo que llegó se captura en eleventa.";

    public const string SoloElXml = "Elige el XML de la factura (el archivo .xml), no su PDF: el PDF no trae el folio fiscal ni las claves.";

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    private string? Piloto => lecturaDeEleventa ? EnElPiloto : null;

    private RegistroCompras Compras => new(db, lecturaDeEleventa);

    // ── Consultar ────────────────────────────────────────────────────────────

    /// <summary>Las últimas de la sucursal, de la más reciente a la más vieja.</summary>
    public async Task<FacturasDto> ListaAsync(Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        await ExigirAsync(usuarioId, sucursalId, ct);
        var veCostos = await Compras.VeCostosAsync(usuarioId, sucursalId, ct);
        var compras = await db.Compras.AsNoTracking().Include(c => c.Renglones).Include(c => c.Documentos.Take(1))
            .Where(c => c.SucursalId == sucursalId && c.ConFactura).OrderByDescending(c => c.Folio).Take(MaximoDeLaLista).AsSplitQuery().ToListAsync(ct);
        var proveedores = await db.Proveedores.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Nombre, ct);
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        return new FacturasDto(compras.Select(c =>
        {
            var (cuando, quien) = Ultimo(c);
            return new FacturaResumenDto(c.Id, c.FolioTexto, proveedores.GetValueOrDefault(c.ProveedorId, "?"), c.Documento, Estado(c), HoraMexico.ALocal(cuando),
                usuarios.GetValueOrDefault(quien, "?"), c.Renglones.Count, c.Renglones.Count(r => r.Cambio),
                veCostos ? c.Documentos.FirstOrDefault()?.TotalCentavos ?? c.TotalNotaCentavos : null);
        }).ToList(), await PuedeModificarAsync(usuarioId, sucursalId, ct), veCostos, Piloto);
    }

    public async Task<FacturaDto> VerAsync(Guid id, Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        await ExigirAsync(usuarioId, sucursalId, ct);
        return await DtoAsync(await DeLaSucursalAsync(id, sucursalId, tracking: false, ct), usuarioId, ct);
    }

    // ── Cargar el XML ────────────────────────────────────────────────────────

    /// <summary>
    /// Carga la factura: queda «por recibir», con lo que el POS reconoció ya emparejado. La misma factura otra vez (su folio fiscal)
    /// no se carga dos veces: regresa la que ya estaba (<see cref="FacturaDto.YaEstaba"/>). Una que ya se cargó en una compra a
    /// ciegas, tampoco.
    /// </summary>
    public async Task<FacturaDto> ImportarAsync(SolicitudImportarFactura s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        byte[] contenido;
        try
        {
            contenido = Convert.FromBase64String(s.ContenidoBase64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("El archivo no llegó completo: vuelve a elegirlo.");
        }
        if (contenido.AsSpan(0, Math.Min(contenido.Length, 1024)).IndexOf("%PDF"u8) >= 0)
            throw new InvalidOperationException(SoloElXml);
        var leido = LectorDocumentos.Leer(contenido);
        if (leido.Tipo != TipoDocumentoProveedor.Xml)
            throw new InvalidOperationException(SoloElXml);
        if (leido.Uuid is not { } uuid)
            throw new InvalidOperationException("La factura no trae su timbre (el folio fiscal del SAT): sin él no se puede revisar que no se reciba dos veces.");
        if (leido.RfcEmisor is not { } rfc)
            throw new InvalidOperationException("La factura no dice el RFC de quien la emitió.");

        var otra = await db.ComprasDocumentos.AsNoTracking().Where(d => d.Uuid == uuid).Join(db.Compras, d => d.CompraId, c => c.Id, (d, c) => c)
            .Where(c => c.Estado != EstadoCompra.Cancelada).FirstOrDefaultAsync(ct);
        if (otra is { ConFactura: true } && otra.SucursalId == s.SucursalId)
            return await DtoAsync(await DeLaSucursalAsync(otra.Id, s.SucursalId, tracking: false, ct), s.UsuarioId, ct) with { YaEstaba = true };
        if (otra is not null)
            throw new InvalidOperationException($"Esa factura ya se cargó en la compra {otra.FolioTexto}{(otra.SucursalId == s.SucursalId ? "" : " de otra sucursal")}.");

        var avisos = leido.Avisos.ToList();
        var proveedor = await ProveedorAsync(rfc, leido.NombreEmisor, s, avisos, ct);
        var compras = Compras;
        var renglones = await compras.RenglonesAsync(proveedor.Id, leido, avisos, ct);
        if (renglones.Count == 0)
            throw new InvalidOperationException("La factura no trae mercancía: todos sus conceptos son servicios.");
        if (renglones.Count > Compra.RenglonesMaximos)
            throw new InvalidOperationException($"La factura trae {renglones.Count} conceptos: se reciben hasta {Compra.RenglonesMaximos}.");

        var compra = Compra.DeLaFactura(s.SucursalId, proveedor.Id, await compras.SiguienteFolioAsync(s.SucursalId, ct), leido.Folio, s.UsuarioId, Ahora);
        var documento = new CompraDocumento
        {
            CompraId = compra.Id, Nombre = Path.GetFileName(s.Archivo.Trim()) is { Length: > 0 } nombre ? nombre : "factura.xml", Tipo = leido.Tipo,
            Contenido = contenido, Folio = leido.Folio, Uuid = uuid, RfcEmisor = rfc, TotalCentavos = leido.TotalCentavos,
            Avisos = avisos.Count == 0 ? null : string.Join('\n', avisos), UsuarioId = s.UsuarioId, CargadoUtc = Ahora,
        };
        compra.AgregarDocumento(documento, renglones);
        // Lo que no reconoció: si se parece a un producto de aquí, se ofrece (no se empareja solo). No se dan de alta productos aquí.
        await compras.PrecargarAsync(compra, compra.Renglones, ct, soloParecidos: true);
        db.Compras.Add(compra);
        var reconocidos = compra.Renglones.Count(r => r.Ligado);
        new ServicioControl(db).Registrar(s.SucursalId, s.CajaId, s.UsuarioId, AccionControlada.RecibirConFactura,
            $"Cargó la factura {leido.Folio ?? uuid} de «{proveedor.Nombre}» («{documento.Nombre}», folio fiscal {uuid}) en la compra {compra.FolioTexto}: "
            + $"{Cuantos(compra.Renglones.Count)}, {reconocidos} {(reconocidos == 1 ? "reconocido" : "reconocidos")}");
        await db.SaveChangesAsync(ct);
        return await DtoAsync(compra, s.UsuarioId, ct);
    }

    /// <summary>
    /// El proveedor de la factura, por su RFC. Si no está, uno sin RFC con el mismo nombre (se le guarda el RFC); si tampoco, se da
    /// de alta con el nombre de la factura: hoy los proveedores están vacíos (en eleventa no se usan).
    /// </summary>
    private async Task<Proveedor> ProveedorAsync(string rfc, string? nombreEmisor, SolicitudImportarFactura s, List<string> avisos, CancellationToken ct)
    {
        var todos = await db.Proveedores.ToListAsync(ct);
        if (todos.Where(p => string.Equals(p.Rfc, rfc, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Activo).FirstOrDefault() is { } porRfc)
            return porRfc;
        // Como lo dice la factura (su razón social): se cambia en Inventario › Proveedores.
        var nombre = string.IsNullOrWhiteSpace(nombreEmisor) ? rfc : nombreEmisor.Trim();
        if (todos.FirstOrDefault(p => p.Rfc is null && Texto.Normalizar(p.Nombre) == Texto.Normalizar(nombre)) is { } porNombre)
        {
            porNombre.Rfc = rfc;
            avisos.Add($"Se guardó el RFC del proveedor «{porNombre.Nombre}»: {rfc}.");
            return porNombre;
        }
        var nuevo = new Proveedor { Nombre = nombre.Length > 120 ? nombre[..120] : nombre, Rfc = rfc };
        db.Proveedores.Add(nuevo);
        avisos.Add($"Se dio de alta el proveedor «{nuevo.Nombre}» (RFC {rfc}): su teléfono y sus notas, en Inventario › Proveedores.");
        new ServicioControl(db).Registrar(s.SucursalId, s.CajaId, s.UsuarioId, AccionControlada.RecibirConFactura,
            $"Alta del proveedor «{nuevo.Nombre}» (RFC {rfc}) con su factura");
        return nuevo;
    }

    // ── Revisar y recibir ────────────────────────────────────────────────────

    /// <summary>
    /// Lo que quien recibe dice de cada renglón, y qué se hace con eso (<see cref="PasoDeRecepcion"/>): guardarlo, dejarlo marcado para
    /// revisión, o recibirla (entra la mercancía). Recibirla distinta de la factura es de quien tiene «Recibir con factura: modificar lo
    /// que llegó»; lo demás, de quien tiene «Recibir con la factura (XML)». Recibirla otra vez regresa la ya recibida.
    /// </summary>
    public async Task<FacturaDto> RecibirAsync(Guid id, SolicitudRecibirFactura s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        // Con el candado de escritura desde el principio: lo que se lee (las existencias, que la factura no se recibió) es lo que se escribe.
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var compra = await DeLaSucursalAsync(id, s.SucursalId, tracking: true, ct);
            if (compra.Estado == EstadoCompra.Cerrada)
                return s.Paso == PasoDeRecepcion.Recibir
                    ? await DtoAsync(compra, s.UsuarioId, ct)
                    : throw new InvalidOperationException("Esa factura ya se recibió: ya no se cambia. Lo equivocado se corrige con un ajuste.");
            if (compra.Estado == EstadoCompra.Cancelada)
                throw new InvalidOperationException("Esa factura se descartó: cárgala otra vez para recibirla.");

            var presentaciones = await AnotarAsync(compra, s.Renglones, ct);
            var cambios = compra.Renglones.Where(r => r.Cambio).OrderBy(r => r.Orden).ToList();
            var sinEmparejar = compra.Renglones.Where(r => r.SinEmparejar).OrderBy(r => r.Orden).ToList();
            var control = new ServicioControl(db);
            var proveedor = await Compras.NombreProveedorAsync(compra.ProveedorId, ct);
            var factura = $"{compra.Documento ?? compra.FolioTexto} de «{proveedor}»";
            switch (s.Paso)
            {
                case PasoDeRecepcion.MandarARevision:
                    compra.MarcarParaRevision(s.UsuarioId, Ahora);
                    control.Registrar(s.SucursalId, s.CajaId, s.UsuarioId, AccionControlada.RecibirConFactura,
                        $"Marcó para revisión la factura {factura} (compra {compra.FolioTexto}): {Resumen(cambios.Concat(sinEmparejar).Distinct().ToList(), presentaciones)}");
                    break;

                case PasoDeRecepcion.Recibir:
                    if ((cambios.Count > 0 || sinEmparejar.Count > 0) && !await PuedeModificarAsync(s.UsuarioId, s.SucursalId, ct))
                        throw new PermisoRequerido(AccionControlada.ModificarRecepcionConFactura, NivelPermiso.NoPermitido,
                            "Lo que marcaste no es lo que dice la factura: déjala marcada para revisión. Recibirla con cambios lo hace quien tiene el permiso "
                            + $"«{Permisos.Nombre(AccionControlada.ModificarRecepcionConFactura)}»"
                            + (await control.QuienesPuedenAsync(s.SucursalId, AccionControlada.ModificarRecepcionConFactura, ct) is { Count: > 0 } quienes
                                ? $": {Permisos.Nombres(quienes)}." : "."));
                    if (sinEmparejar.Count > 0)
                        throw new ArgumentException(
                            $"Falta emparejar «{sinEmparejar[0].Descripcion}»{(sinEmparejar.Count > 1 ? $" y {sinEmparejar.Count - 1} más" : "")} con su producto de "
                            + "aquí, o poner 0 en lo que llegó (ya dado de alta en Productos, se agrega con «Agregar inventario»).");
                    // La misma factura no entra dos veces, aunque se haya cargado otra vez después de descartarla.
                    var uuids = await db.ComprasDocumentos.AsNoTracking().Where(d => d.CompraId == compra.Id && d.Uuid != null).Select(d => d.Uuid).ToListAsync(ct);
                    if (await db.ComprasDocumentos.AsNoTracking().Where(d => d.CompraId != compra.Id && uuids.Contains(d.Uuid))
                            .Join(db.Compras, d => d.CompraId, c => c.Id, (d, c) => c).FirstOrDefaultAsync(c => c.Estado == EstadoCompra.Cerrada, ct) is { } yaRecibida)
                        throw new InvalidOperationException($"Esa factura ya entró en la compra {yaRecibida.FolioTexto}: no se recibe dos veces.");
                    // Lo emparejado a mano: la próxima factura de este proveedor ya llega así (y un código de barras nuevo, al producto).
                    var compras = Compras;
                    foreach (var r in compra.Renglones.Where(r => r is { DelDocumento: true, PresentacionId: { } } && r.PresentacionId != r.PresentacionReconocidaId))
                    {
                        await compras.LigarAsync(compra.ProveedorId, LigaProveedor.ClaveDe(r.CodigoProveedor, r.Descripcion), r.PresentacionId!.Value, ct);
                        await compras.CodigoNuevoAsync(compra, r.CodigoProveedor, presentaciones[r.PresentacionId!.Value], ct);
                    }
                    var antes = compra.Conteo.ToList();
                    compra.ConteoDeLaFactura(presentaciones.ToDictionary(p => p.Key, p => p.Value.Factor));
                    Sincronizar(antes, compra.Conteo);
                    await compras.CerrarAsync(compra, s.UsuarioId,
                        cambios.Count > 0 ? AccionControlada.ModificarRecepcionConFactura : AccionControlada.RecibirConFactura, !lecturaDeEleventa, s.CajaId, ct);
                    if (cambios.Count > 0)
                        control.Registrar(s.SucursalId, s.CajaId, s.UsuarioId, AccionControlada.ModificarRecepcionConFactura,
                            $"Recibió con cambios la factura {factura} (compra {compra.FolioTexto}): {Resumen(cambios, presentaciones)}");
                    break;
            }
            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
            return await DtoAsync(compra, s.UsuarioId, ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Una factura que no se recibió se descarta: no entra nada, y se puede cargar otra vez.</summary>
    public async Task DescartarAsync(Guid id, SolicitudDeFactura s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var compra = await DeLaSucursalAsync(id, s.SucursalId, tracking: true, ct);
        if (compra.Estado == EstadoCompra.Cerrada)
            throw new InvalidOperationException("Esa factura ya se recibió: no se descarta. Lo equivocado se corrige con un ajuste.");
        if (compra.Estado == EstadoCompra.Cancelada)
            return;
        compra.Cancelar("Se descartó la factura sin recibirla", Ahora);
        new ServicioControl(db).Registrar(s.SucursalId, s.CajaId, s.UsuarioId, AccionControlada.RecibirConFactura,
            $"Descartó la factura {compra.Documento ?? compra.FolioTexto} de «{await Compras.NombreProveedorAsync(compra.ProveedorId, ct)}» (compra {compra.FolioTexto}): no entró nada");
        await db.SaveChangesAsync(ct);
    }

    // ── Apoyo ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pone en la compra lo que dice quien recibe: lo que llegó de cada renglón, con qué producto va y su nota; agrega lo que llegó y no
    /// venía, y quita lo agregado que quedó en cero. Los renglones que no se mandan se quedan como están. Regresa las presentaciones de
    /// los renglones emparejados.
    /// </summary>
    private async Task<Dictionary<Guid, Presentacion>> AnotarAsync(Compra compra, IReadOnlyList<RenglonDeFactura> recibidos, CancellationToken ct)
    {
        var compras = Compras;
        var presentaciones = new Dictionary<Guid, Presentacion>();
        async Task<Presentacion> PresentacionAsync(Guid varianteId, Guid presentacionId) =>
            presentaciones.TryGetValue(presentacionId, out var p) && p.VarianteId == varianteId
                ? p
                : presentaciones[presentacionId] = await compras.PresentacionAsync(varianteId, presentacionId, ct);

        var antes = compra.Renglones.ToList();
        foreach (var r in recibidos)
        {
            var llego = new Cantidad(r.LlegoMilesimas);
            if (llego.Milesimas < 0)
                throw new ArgumentException("Lo que llegó no puede ser negativo: si no llegó, pon 0.");
            if (r.VarianteId is null != r.PresentacionId is null)
                throw new ArgumentException("Falta la presentación del producto.");
            if (r.Id is { } renglonId)
            {
                var renglon = compra.Renglones.FirstOrDefault(x => x.Id == renglonId)
                    ?? throw new SolicitudDistinta("Uno de los renglones ya no es de esta factura: ábrela otra vez.");
                if (renglon.Agregado)
                {
                    if (!llego.EsPositiva || r.VarianteId is null)
                    {
                        compra.QuitarNoVenia(renglon.Id);
                        continue;
                    }
                }
                else if (r.PresentacionId != renglon.PresentacionId)
                {
                    var factor = r.PresentacionId is { } pid ? (await PresentacionAsync(r.VarianteId!.Value, pid)).Factor : Cantidad.Cero;
                    compra.Emparejar(renglon.Id, r.VarianteId, r.PresentacionId, factor);
                }
                compra.Llego(renglon.Id, llego, r.Nota);
            }
            else if (r is { VarianteId: { } varianteId, PresentacionId: { } presentacionId } && llego.EsPositiva)
            {
                var presentacion = await PresentacionAsync(varianteId, presentacionId);
                compra.NoVenia(varianteId, presentacionId, $"{RegistroCompras.Nombre(presentacion.Variante!)} ({presentacion.Nombre})", llego, r.Nota);
            }
        }
        Sincronizar(antes, compra.Renglones);
        foreach (var r in compra.Renglones.Where(r => r.Ligado))
        {
            var presentacion = await PresentacionAsync(r.VarianteId!.Value, r.PresentacionId!.Value);
            if (r.Llego is { EsPositiva: true } llego && !presentacion.Variante!.AdmiteCantidad(llego.Por(presentacion.Factor)))
                throw new ArgumentException($"«{RegistroCompras.Nombre(presentacion.Variante)}» se cuenta en piezas enteras: corrige lo que llegó.");
        }
        return presentaciones;
    }

    /// <summary>«llegaron 20 de 25 de «Pistola…»; «Lápiz» no venía (3); «Cartulina» se emparejó con «…»».</summary>
    private static string Resumen(IReadOnlyList<CompraRenglon> renglones, Dictionary<Guid, Presentacion> presentaciones) =>
        string.Join("; ", renglones.Select(r =>
        {
            var llego = Formato(r.Llego ?? Cantidad.Cero);
            if (r.Agregado)
                return $"«{r.Descripcion}» no venía (llegaron {llego})";
            var partes = new List<string>();
            if (r.Llego != r.Cantidad)
                partes.Add($"llegaron {llego} de {Formato(r.Cantidad)}");
            if (r.PresentacionId is { } p && p != r.PresentacionReconocidaId && presentaciones.TryGetValue(p, out var presentacion))
                partes.Add($"se emparejó con «{RegistroCompras.Nombre(presentacion.Variante!)}» ({presentacion.Nombre})");
            if (r.SinEmparejar)
                partes.Add("sin emparejar");
            return $"«{r.Descripcion}»: {string.Join(", ", partes)}{(r.Nota is { } nota ? $" ({nota})" : "")}";
        }));

    private static string Formato(Cantidad c) => c.EnUnidades.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string Cuantos(int renglones) => renglones == 1 ? "1 renglón" : $"{renglones} renglones";

    private static EstadoFactura Estado(Compra c) => c.Estado switch
    {
        EstadoCompra.Cerrada => EstadoFactura.Recibida,
        EstadoCompra.Cancelada => EstadoFactura.Descartada,
        _ => c.EnRevision ? EstadoFactura.EnRevision : EstadoFactura.PorRecibir,
    };

    /// <summary>Lo último que le pasó y quién: la recibió o la descartó, la marcó, o la cargó.</summary>
    private static (DateTime Cuando, Guid Quien) Ultimo(Compra c) =>
        c.CerradaUtc is { } cerrada ? (cerrada, c.CosteoId ?? c.RecibioId)
        : c is { MarcadoUtc: { } marcado, MarcoId: { } marco } ? (marcado, marco)
        : (c.AbiertaUtc, c.RecibioId);

    /// <summary>Quien entra tiene «Recibir con la factura (XML)» en «Puede». Si no, el error dice quién lo tiene.</summary>
    private async Task ExigirAsync(Guid usuarioId, Guid sucursalId, CancellationToken ct)
    {
        var control = new ServicioControl(db);
        if ((await control.EvaluarAsync(usuarioId, sucursalId, AccionControlada.RecibirConFactura, ct: ct)).Nivel == NivelPermiso.Permitido)
            return;
        throw new PermisoRequerido(AccionControlada.RecibirConFactura, NivelPermiso.NoPermitido,
            Permisos.QuienLoHace(AccionControlada.RecibirConFactura, await control.QuienesPuedenAsync(sucursalId, AccionControlada.RecibirConFactura, ct)));
    }

    private async Task<bool> PuedeModificarAsync(Guid usuarioId, Guid sucursalId, CancellationToken ct) =>
        (await new ServicioControl(db).EvaluarAsync(usuarioId, sucursalId, AccionControlada.ModificarRecepcionConFactura, ct: ct)).Nivel == NivelPermiso.Permitido;

    private async Task<Compra> DeLaSucursalAsync(Guid id, Guid sucursalId, bool tracking, CancellationToken ct)
    {
        var consulta = db.Compras.Include(c => c.Renglones).Include(c => c.Conteo).Where(c => c.Id == id && c.SucursalId == sucursalId && c.ConFactura).AsSplitQuery();
        return await (tracking ? consulta : consulta.AsNoTracking()).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Esa factura ya no existe en esta sucursal.");
    }

    /// <summary>Agrega a la base lo nuevo de una colección y quita lo que salió (el modelo no borra en cascada).</summary>
    private void Sincronizar<T>(List<T> antes, List<T> despues) where T : class
    {
        foreach (var nuevo in despues.Except(antes))
            db.Add(nuevo);
        foreach (var quitado in antes.Except(despues))
            db.Remove(quitado);
    }

    private async Task<FacturaDto> DtoAsync(Compra c, Guid usuarioId, CancellationToken ct)
    {
        var veCostos = await Compras.VeCostosAsync(usuarioId, c.SucursalId, ct);
        var ids = c.Renglones.Select(r => r.VarianteId).Concat(c.Renglones.Select(r => r.SugerenciaId)).OfType<Guid>().Distinct().ToList();
        var variantes = await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca).Include(v => v.Presentaciones)
            .Where(v => ids.Contains(v.Id)).AsSplitQuery().ToDictionaryAsync(v => v.Id, ct);
        var existencias = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == c.SucursalId && ids.Contains(e.VarianteId))
            .ToDictionaryAsync(e => e.VarianteId, e => e.Cantidad, ct);
        var documento = await db.ComprasDocumentos.AsNoTracking().Where(d => d.CompraId == c.Id).OrderBy(d => d.CargadoUtc)
            .Select(d => new { d.Uuid, d.RfcEmisor, d.TotalCentavos, d.Avisos }).FirstOrDefaultAsync(ct);
        var proveedor = await db.Proveedores.AsNoTracking().SingleAsync(p => p.Id == c.ProveedorId, ct);
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        string? Quien(Guid? id) => id is { } x ? usuarios.GetValueOrDefault(x, "?") : null;
        var renglones = c.Renglones.OrderBy(r => r.Orden).Select(r =>
        {
            var variante = r.VarianteId is { } v ? variantes.GetValueOrDefault(v) : null;
            var presentacion = variante?.Presentaciones.FirstOrDefault(p => p.Id == r.PresentacionId);
            var sugerida = r is { Ligado: false, SugerenciaId: { } s } ? variantes.GetValueOrDefault(s) : null;
            return new FacturaRenglonDto(r.Id, r.CodigoProveedor, r.Descripcion, r.Unidad, r.Cantidad.EnUnidades, (r.Llego ?? r.Cantidad).EnUnidades, r.Nota,
                r.Agregado, r.Cambio, variante?.Id, presentacion?.Id, variante is null ? null : RegistroCompras.Nombre(variante), presentacion?.Nombre,
                presentacion?.Factor.EnUnidades ?? 1, variante?.UnidadBase ?? UnidadBase.Pieza, r.PresentacionReconocidaId is not null && r.PresentacionId == r.PresentacionReconocidaId,
                sugerida?.Id, sugerida?.Presentaciones.FirstOrDefault(p => p.EsBase)?.Id, sugerida is null ? null : RegistroCompras.Nombre(sugerida),
                veCostos && !r.Agregado ? r.ImporteCentavos : null, variante is null ? null : existencias.GetValueOrDefault(variante.Id).EnUnidades,
                r.PresentacionReconocidaId);
        }).ToList();
        return new FacturaDto(c.Id, c.FolioTexto, proveedor.Id, proveedor.Nombre, documento?.RfcEmisor ?? proveedor.Rfc, c.Documento, documento?.Uuid, Estado(c),
            HoraMexico.ALocal(c.AbiertaUtc), Quien(c.RecibioId)!, c.CerradaUtc is { } cerrada ? HoraMexico.ALocal(cerrada) : null,
            c.Estado == EstadoCompra.Cerrada ? Quien(c.CosteoId) : null, c.MarcadoUtc is { } marcado ? HoraMexico.ALocal(marcado) : null, Quien(c.MarcoId),
            c.Estado == EstadoCompra.Cerrada && c.MovioExistencias, await PuedeModificarAsync(usuarioId, c.SucursalId, ct), veCostos, Piloto,
            veCostos ? documento?.TotalCentavos : null, documento?.Avisos?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [], renglones);
    }
}
