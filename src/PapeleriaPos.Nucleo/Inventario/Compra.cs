namespace PapeleriaPos.Nucleo.Inventario;

public enum EstadoCompra
{
    /// <summary>Quien recibe está contando lo que llegó, a ciegas.</summary>
    Recibiendo,

    /// <summary>El conteo se cerró: falta capturar la nota del proveedor (los costos).</summary>
    PorCostear,

    /// <summary>Entró al kardex. Ya no se cambia: un error se corrige con un ajuste.</summary>
    Cerrada,

    Cancelada,
}

/// <summary>
/// Una recepción de mercancía (docs/fase-2-inventario.md, entrega 2). Quien recibe cuenta lo que llegó sin ver la nota;
/// después, quien captura los costos pasa los renglones de la nota y el POS compara las dos cosas. Al cerrarla, lo
/// contado entra al kardex con el costo de la nota y las diferencias quedan para reclamarlas al proveedor.
/// <para>
/// O se recibe con la factura (<see cref="ConFactura"/>; dueño, 2026-11-27, docs/recibir-con-factura.md): primero se carga el
/// XML, que dice lo que debe llegar, y quien recibe escribe en cada renglón lo que llegó, como en un traspaso. No es a ciegas: la
/// factura es la lista. Al recibirla, lo que llegó entra igual que al cerrar una compra.
/// </para>
/// </summary>
public class Compra
{
    /// <summary>Hasta cuántos renglones (de la factura y lo que llegó y no venía).</summary>
    public const int RenglonesMaximos = 500;

    /// <summary>La nota de un renglón («venían de menos», «una rota»).</summary>
    public const int NotaMaxima = 200;

    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid SucursalId { get; init; }
    public Guid ProveedorId { get; init; }

    /// <summary>Consecutivo de la base: C-000123.</summary>
    public int Folio { get; init; }

    /// <summary>El número de la nota o factura del proveedor, si lo trae.</summary>
    public string? Documento { get; set; }

    public EstadoCompra Estado { get; private set; } = EstadoCompra.Recibiendo;

    public Guid RecibioId { get; init; }
    public Guid? CosteoId { get; private set; }
    public DateTime AbiertaUtc { get; init; }
    public DateTime? ContadaUtc { get; private set; }
    public DateTime? CerradaUtc { get; private set; }

    /// <summary>Por qué se canceló.</summary>
    public string? Motivo { get; private set; }

    /// <summary>La orden de compra de la que viene, si se dijo (es opcional).</summary>
    public Guid? OrdenId { get; private set; }

    /// <summary>Lo que se contó, por presentación (la caja de 12 se cuenta por cajas).</summary>
    public List<CompraConteo> Conteo { get; init; } = [];

    /// <summary>Los renglones de la nota: lo que el proveedor dice que mandó y lo que cobra.</summary>
    public List<CompraRenglon> Renglones { get; init; } = [];

    /// <summary>Los documentos del proveedor que se cargaron (el XML de la factura o el PDF de la nota).</summary>
    public List<CompraDocumento> Documentos { get; init; } = [];

    /// <summary>
    /// Se recibe con la factura (el XML del CFDI): la abre la factura misma, ya con sus renglones y en
    /// <see cref="EstadoCompra.PorCostear"/>; nunca se cuenta a ciegas.
    /// </summary>
    public bool ConFactura { get; init; }

    /// <summary>Con la factura: quien no puede recibirla con cambios los dejó marcados para revisión, y cuándo.</summary>
    public Guid? MarcoId { get; private set; }

    public DateTime? MarcadoUtc { get; private set; }

    /// <summary>
    /// Al cerrarla entró al kardex. En el piloto con eleventa, una recibida con su factura es solo el documento: no mueve
    /// existencias aquí (se capturan en eleventa), como los traspasos.
    /// </summary>
    public bool MovioExistencias { get; private set; } = true;

    /// <summary>Con la factura, por recibir y con cambios marcados para que los revise quien puede recibirla así.</summary>
    public bool EnRevision => ConFactura && Estado == EstadoCompra.PorCostear && MarcoId is not null;

    public string FolioTexto => $"C-{Folio}";

    /// <summary>
    /// Una compra que se recibe con la factura del proveedor: empieza lista para cargarle el XML (<see cref="AgregarDocumento"/>) y
    /// escribir lo que llegó.
    /// </summary>
    public static Compra DeLaFactura(Guid sucursalId, Guid proveedorId, int folio, string? documento, Guid recibioId, DateTime ahoraUtc) => new()
    {
        SucursalId = sucursalId, ProveedorId = proveedorId, Folio = folio, Documento = documento, RecibioId = recibioId, AbiertaUtc = ahoraUtc,
        ConFactura = true, Estado = EstadoCompra.PorCostear,
    };

    public long TotalNotaCentavos => Renglones.Sum(r => r.ImporteCentavos);

    /// <summary>Pone la cantidad contada de una presentación (cero la quita). Solo mientras se recibe.</summary>
    public void Contar(Guid varianteId, Guid presentacionId, Cantidad cantidad, Cantidad factor)
    {
        RequerirEstado(EstadoCompra.Recibiendo, "El conteo ya se cerró.");
        if (cantidad.Milesimas < 0)
            throw new ArgumentException("La cantidad no puede ser negativa.");
        var linea = Conteo.FirstOrDefault(c => c.PresentacionId == presentacionId);
        if (cantidad.Milesimas == 0)
        {
            if (linea is not null)
                Conteo.Remove(linea);
            return;
        }
        if (linea is null)
        {
            linea = new CompraConteo { CompraId = Id, VarianteId = varianteId, PresentacionId = presentacionId };
            Conteo.Add(linea);
        }
        linea.Cantidad = cantidad;
        linea.CantidadBase = cantidad.Por(factor);
    }

    /// <summary>
    /// Lo que llegó y no está en el catálogo (se escaneó un código que no existe): se cuenta por su código, con una
    /// descripción opcional. Al dar de alta el producto desde la nota (o ligar el renglón con ese código) se le asigna.
    /// </summary>
    public void ContarDesconocido(string codigo, string? descripcion, Cantidad cantidad)
    {
        RequerirEstado(EstadoCompra.Recibiendo, "El conteo ya se cerró.");
        if (string.IsNullOrWhiteSpace(codigo))
            throw new ArgumentException("Escanea o escribe el código.");
        if (cantidad.Milesimas < 0)
            throw new ArgumentException("La cantidad no puede ser negativa.");
        var limpio = codigo.Trim();
        var linea = Conteo.FirstOrDefault(c => c.VarianteId is null && string.Equals(c.Codigo, limpio, StringComparison.OrdinalIgnoreCase));
        if (cantidad.Milesimas == 0)
        {
            if (linea is not null)
                Conteo.Remove(linea);
            return;
        }
        if (linea is null)
        {
            linea = new CompraConteo { CompraId = Id, Codigo = limpio };
            Conteo.Add(linea);
        }
        linea.Descripcion = string.IsNullOrWhiteSpace(descripcion) ? linea.Descripcion : descripcion.Trim();
        linea.Cantidad = cantidad;
    }

    /// <summary>Lo contado sin catálogo con este código ya es de esta presentación (se dio de alta o se ligó).</summary>
    public void AsignarDesconocidos(string? codigo, Guid varianteId, Guid presentacionId, Cantidad factor)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            return;
        foreach (var linea in Conteo.Where(c => c.VarianteId is null && string.Equals(c.Codigo, codigo.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            linea.VarianteId = varianteId;
            linea.PresentacionId = presentacionId;
            linea.CantidadBase = linea.Cantidad.Por(factor);
        }
    }

    /// <summary>
    /// Un producto nuevo que quien recibió no pudo contar (no tenía código): lo cuenta quien captura la nota al darlo de
    /// alta. Queda marcado como suyo.
    /// </summary>
    public void ContarNuevo(Guid varianteId, Guid presentacionId, Cantidad cantidad, Cantidad factor)
    {
        RequerirEstado(EstadoCompra.PorCostear, "Ya no se puede contar.");
        if (!cantidad.EsPositiva)
            return;
        Conteo.Add(new CompraConteo
        {
            CompraId = Id, VarianteId = varianteId, PresentacionId = presentacionId, Cantidad = cantidad, CantidadBase = cantidad.Por(factor),
            DelEncargado = true,
        });
    }

    public void TerminarConteo(DateTime ahoraUtc)
    {
        RequerirEstado(EstadoCompra.Recibiendo, "El conteo ya se cerró.");
        if (Conteo.Count == 0)
            throw new InvalidOperationException("Cuenta al menos un producto antes de terminar.");
        Estado = EstadoCompra.PorCostear;
        ContadaUtc = ahoraUtc;
    }

    /// <summary>Agrega o cambia un renglón de la nota. Solo con el conteo cerrado: así quien cuenta no ve la nota.</summary>
    public CompraRenglon Renglon(
        Guid? renglonId, Guid varianteId, Guid presentacionId, Cantidad cantidad, Cantidad factor, long importeCentavos, string descripcion,
        string? codigoProveedor)
    {
        RequerirEstado(EstadoCompra.PorCostear, "La nota se captura cuando termina el conteo.");
        if (!cantidad.EsPositiva)
            throw new ArgumentException("La cantidad de la nota va mayor a cero.");
        if (importeCentavos < 0)
            throw new ArgumentException("El importe no puede ser negativo.");
        var renglon = renglonId is { } id
            ? Renglones.FirstOrDefault(r => r.Id == id) ?? throw new InvalidOperationException("El renglón ya no existe.")
            : null;
        if (renglon is null)
        {
            renglon = new CompraRenglon { CompraId = Id, Orden = Renglones.Count == 0 ? 1 : Renglones.Max(r => r.Orden) + 1 };
            Renglones.Add(renglon);
        }
        renglon.VarianteId = varianteId;
        renglon.PresentacionId = presentacionId;
        renglon.Cantidad = cantidad;
        renglon.CantidadBase = cantidad.Por(factor);
        renglon.ImporteCentavos = importeCentavos;
        renglon.QuitarNuevo();
        // Lo que dice el documento se queda como vino: al ligarlo o corregirlo solo cambia a qué producto corresponde.
        if (!renglon.DelDocumento)
        {
            renglon.Descripcion = descripcion;
            renglon.CodigoProveedor = string.IsNullOrWhiteSpace(codigoProveedor) ? null : codigoProveedor.Trim();
        }
        return renglon;
    }

    /// <summary>
    /// Los renglones que leyó el POS del documento del proveedor. Los que reconoció llegan ligados a su producto; los
    /// demás se ligan a mano antes de cerrar.
    /// </summary>
    public void AgregarDocumento(CompraDocumento documento, IEnumerable<RenglonDocumento> renglones)
    {
        RequerirEstado(EstadoCompra.PorCostear, "La nota se captura cuando termina el conteo.");
        Documentos.Add(documento);
        var orden = Renglones.Count == 0 ? 0 : Renglones.Max(r => r.Orden);
        foreach (var r in renglones)
        {
            Renglones.Add(new CompraRenglon
            {
                CompraId = Id, Orden = ++orden, CodigoProveedor = r.CodigoProveedor, Descripcion = r.Descripcion, Unidad = r.Unidad, DelDocumento = true,
                VarianteId = r.Liga?.VarianteId, PresentacionId = r.Liga?.PresentacionId, Cantidad = r.Cantidad,
                CantidadBase = r.Liga is { } liga ? r.Cantidad.Por(liga.Factor) : Cantidad.Cero, ImporteCentavos = r.ImporteCentavos,
                // Con la factura: llegó lo que dice, hasta que quien recibe diga otra cosa; y con qué producto lo reconoció el POS.
                Llego = ConFactura ? r.Cantidad : null, PresentacionReconocidaId = ConFactura ? r.Liga?.PresentacionId : null,
            });
        }
    }

    /// <summary>Dice de qué orden viene (o ninguna). Lo hace quien captura la nota: quien cuenta no ve la orden.</summary>
    public void DeLaOrden(Guid? ordenId)
    {
        RequerirEstado(EstadoCompra.PorCostear, "La orden se indica al capturar la nota.");
        OrdenId = ordenId;
    }

    /// <summary>
    /// Un renglón del documento que no está en el catálogo queda como producto nuevo en borrador, con lo que se precargó
    /// (nombre, departamento, contenido, precio, código y cuántos llegaron); quien captura la nota lo corrige y después lo
    /// da de alta.
    /// </summary>
    public void ProponerNuevo(Guid renglonId, ProductoNuevo nuevo)
    {
        RequerirEstado(EstadoCompra.PorCostear, "La nota ya no se puede cambiar.");
        var renglon = Renglones.FirstOrDefault(r => r.Id == renglonId) ?? throw new InvalidOperationException("El renglón ya no existe.");
        if (renglon.Ligado)
            throw new InvalidOperationException("Ese renglón ya es de un producto del catálogo.");
        if (string.IsNullOrWhiteSpace(nuevo.Nombre))
            throw new ArgumentException("El producto nuevo lleva nombre.");
        if (nuevo.PrecioCentavos <= 0)
            throw new ArgumentException($"Escribe el precio de «{nuevo.Nombre.Trim()}».");
        if (!nuevo.Contiene.EsPositiva || !nuevo.Contiene.EsEntera)
            throw new ArgumentException($"¿Cuántas piezas trae cada {renglon.Unidad ?? "unidad"} de «{nuevo.Nombre.Trim()}»?");
        if (nuevo.Llego is { } llego && (llego.Milesimas < 0 || !llego.Por(nuevo.Contiene).EsEntera))
            throw new ArgumentException($"Lo que llegó de «{nuevo.Nombre.Trim()}» va en piezas enteras.");
        renglon.Nuevo = nuevo with { Nombre = nuevo.Nombre.Trim(), Codigo = string.IsNullOrWhiteSpace(nuevo.Codigo) ? null : nuevo.Codigo.Trim() };
    }

    /// <summary>Corrige la cantidad y el importe de un renglón que todavía no es de un producto (sin ligar o nuevo).</summary>
    public void CorregirSinLigar(Guid renglonId, Cantidad cantidad, long importeCentavos)
    {
        RequerirEstado(EstadoCompra.PorCostear, "La nota ya no se puede cambiar.");
        var renglon = Renglones.FirstOrDefault(r => r.Id == renglonId) ?? throw new InvalidOperationException("El renglón ya no existe.");
        if (renglon.Ligado)
            throw new InvalidOperationException("Ese renglón es de un producto: corrígelo con su presentación.");
        if (!cantidad.EsPositiva)
            throw new ArgumentException("La cantidad de la nota va mayor a cero.");
        if (importeCentavos < 0)
            throw new ArgumentException("El importe no puede ser negativo.");
        renglon.Cantidad = cantidad;
        renglon.ImporteCentavos = importeCentavos;
    }

    public void NoEsNuevo(Guid renglonId)
    {
        RequerirEstado(EstadoCompra.PorCostear, "La nota ya no se puede cambiar.");
        (Renglones.FirstOrDefault(r => r.Id == renglonId) ?? throw new InvalidOperationException("El renglón ya no existe.")).QuitarNuevo();
    }

    public void QuitarRenglon(Guid renglonId)
    {
        RequerirEstado(EstadoCompra.PorCostear, "La nota ya no se puede cambiar.");
        Renglones.Remove(Renglones.FirstOrDefault(r => r.Id == renglonId) ?? throw new InvalidOperationException("El renglón ya no existe."));
    }

    /// <param name="moverExistencias">Falso en el piloto con eleventa (solo con la factura): queda el documento, sin kardex.</param>
    public void Cerrar(Guid costeoId, DateTime ahoraUtc, bool moverExistencias = true)
    {
        RequerirEstado(EstadoCompra.PorCostear, Estado == EstadoCompra.Recibiendo ? "Primero termina el conteo." : "La compra ya se cerró.");
        if (Renglones.Count == 0)
            throw new InvalidOperationException("Captura la nota del proveedor antes de cerrar.");
        if (Renglones.FirstOrDefault(r => r.Nuevo is not null) is { } nuevo)
            throw new InvalidOperationException($"Primero da de alta los productos nuevos («{nuevo.Nuevo!.Nombre}»…) o quítalos de la nota.");
        // Con la factura, lo que no llegó no hace falta emparejarlo: no entra nada.
        if (Renglones.FirstOrDefault(r => !r.Ligado && !(ConFactura && r.Llego is { Milesimas: 0 })) is { } sinLigar)
            throw new InvalidOperationException(ConFactura
                ? $"Falta emparejar «{sinLigar.Descripcion}» con su producto (o poner 0 en lo que llegó)."
                : $"Falta decir qué producto es «{sinLigar.Descripcion}» (o quitarlo si no es mercancía).");
        if (ConFactura && !Renglones.Any(r => r.Llego is { EsPositiva: true }))
            throw new InvalidOperationException("No llegó nada: si la factura no es de esta mercancía, descártala.");
        if (!moverExistencias && !ConFactura)
            throw new InvalidOperationException("Una compra a ciegas siempre entra al inventario.");
        Estado = EstadoCompra.Cerrada;
        CosteoId = costeoId;
        CerradaUtc = ahoraUtc;
        MovioExistencias = moverExistencias;
    }

    // ── Con la factura (2026-11-27) ──────────────────────────────────────────

    /// <summary>Lo que llegó de un renglón, en la unidad de la factura, con su nota. Cero: no llegó.</summary>
    public void Llego(Guid renglonId, Cantidad llego, string? nota)
    {
        RequerirFactura();
        if (llego.Milesimas < 0)
            throw new ArgumentException("Lo que llegó no puede ser negativo: si no llegó, pon 0.");
        var renglon = RenglonDe(renglonId);
        renglon.Llego = llego;
        renglon.Nota = Nota(nota);
    }

    /// <summary>
    /// Con qué producto de aquí va un renglón de la factura: la presentación que es cada unidad de la factura (la pieza, o la caja
    /// de 12 si la factura cobra cajas). Vacía: sin emparejar.
    /// </summary>
    public void Emparejar(Guid renglonId, Guid? varianteId, Guid? presentacionId, Cantidad factor)
    {
        RequerirFactura();
        var renglon = RenglonDe(renglonId);
        if (renglon.Agregado)
            throw new InvalidOperationException("Ese renglón no venía en la factura: quítalo y agrega el producto que llegó.");
        if (varianteId is null != presentacionId is null)
            throw new ArgumentException("Falta la presentación del producto.");
        (renglon.VarianteId, renglon.PresentacionId) = (varianteId, presentacionId);
        renglon.CantidadBase = presentacionId is null ? Cantidad.Cero : renglon.Cantidad.Por(factor);
        if (presentacionId is not null)
            renglon.SugerenciaId = null;
    }

    /// <summary>Llegó y no venía en la factura: un renglón propio, que no cobra nada (entra a su último costo).</summary>
    public CompraRenglon NoVenia(Guid varianteId, Guid presentacionId, string descripcion, Cantidad llego, string? nota)
    {
        RequerirFactura();
        if (!llego.EsPositiva)
            throw new ArgumentException("Escribe cuántos llegaron.");
        if (Renglones.Count >= RenglonesMaximos)
            throw new ArgumentException($"Una compra lleva hasta {RenglonesMaximos} renglones.");
        var renglon = new CompraRenglon
        {
            CompraId = Id, Orden = Renglones.Count == 0 ? 1 : Renglones.Max(r => r.Orden) + 1, Descripcion = descripcion, Agregado = true, VarianteId = varianteId,
            PresentacionId = presentacionId, Cantidad = Cantidad.Cero, CantidadBase = Cantidad.Cero, Llego = llego, Nota = Nota(nota),
        };
        Renglones.Add(renglon);
        return renglon;
    }

    /// <summary>Quita lo que se agregó porque llegó y no venía. Los renglones de la factura no se quitan: se pone 0 en lo que llegó.</summary>
    public void QuitarNoVenia(Guid renglonId)
    {
        RequerirFactura();
        var renglon = RenglonDe(renglonId);
        if (!renglon.Agregado)
            throw new InvalidOperationException("Lo que dice la factura no se quita: si no llegó, pon 0.");
        Renglones.Remove(renglon);
    }

    /// <summary>Quien no puede recibirla con cambios los deja marcados para que los revise quien sí.</summary>
    public void MarcarParaRevision(Guid usuarioId, DateTime ahoraUtc)
    {
        RequerirFactura();
        if (!Renglones.Any(r => r.Cambio || r.SinEmparejar))
            throw new ArgumentException("No marcaste ningún cambio: si llegó lo que dice la factura, recíbela.");
        (MarcoId, MarcadoUtc) = (usuarioId, ahoraUtc);
    }

    /// <summary>
    /// Lo que se contó, sacado de lo que llegó de cada renglón emparejado (por presentación), para cerrarla como cualquier compra.
    /// <paramref name="factores"/>: el factor de cada presentación de los renglones.
    /// </summary>
    public void ConteoDeLaFactura(IReadOnlyDictionary<Guid, Cantidad> factores)
    {
        RequerirFactura();
        Conteo.Clear();
        foreach (var g in Renglones.Where(r => r.Ligado && r.Llego is { EsPositiva: true }).GroupBy(r => (r.VarianteId!.Value, r.PresentacionId!.Value)))
        {
            var cantidad = g.Aggregate(Cantidad.Cero, (s, r) => s + r.Llego!.Value);
            Conteo.Add(new CompraConteo
            {
                CompraId = Id, VarianteId = g.Key.Item1, PresentacionId = g.Key.Item2, Cantidad = cantidad, CantidadBase = cantidad.Por(factores[g.Key.Item2]),
            });
        }
    }

    private CompraRenglon RenglonDe(Guid renglonId) =>
        Renglones.FirstOrDefault(r => r.Id == renglonId) ?? throw new InvalidOperationException("Uno de los renglones ya no es de esta compra: ábrela otra vez.");

    private static string? Nota(string? nota) =>
        string.IsNullOrWhiteSpace(nota) ? null : nota.Trim() is var limpia && limpia.Length > NotaMaxima ? limpia[..NotaMaxima] : limpia;

    private void RequerirFactura()
    {
        if (!ConFactura)
            throw new InvalidOperationException("Esta compra se recibió a ciegas: se captura su nota en «Compras y notas».");
        RequerirEstado(EstadoCompra.PorCostear, "Esa factura ya se recibió: ya no se cambia. Lo equivocado se corrige con un ajuste.");
    }

    public void Cancelar(string motivo, DateTime ahoraUtc)
    {
        if (Estado is EstadoCompra.Cerrada or EstadoCompra.Cancelada)
            throw new InvalidOperationException("Una compra cerrada no se cancela: se corrige con un ajuste.");
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Escribe por qué se cancela.");
        Estado = EstadoCompra.Cancelada;
        Motivo = motivo.Trim();
        CerradaUtc = ahoraUtc;
    }

    private void RequerirEstado(EstadoCompra estado, string mensaje)
    {
        if (Estado != estado)
            throw new InvalidOperationException(Estado == EstadoCompra.Cancelada ? "La compra se canceló." : mensaje);
    }
}

/// <summary>Lo que contó quien recibió, de una presentación (o de un código que no está en el catálogo).</summary>
public class CompraConteo
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid CompraId { get; init; }

    /// <summary>Vacíos si se contó un código que no está en el catálogo, hasta que se da de alta o se liga.</summary>
    public Guid? VarianteId { get; set; }

    public Guid? PresentacionId { get; set; }

    /// <summary>El código escaneado que no está en el catálogo.</summary>
    public string? Codigo { get; init; }

    /// <summary>Lo que escribió quien recibió de lo que no está en el catálogo.</summary>
    public string? Descripcion { get; set; }

    /// <summary>Lo contó quien captura la nota, al dar de alta un producto nuevo que no se pudo contar.</summary>
    public bool DelEncargado { get; init; }

    /// <summary>En presentaciones: 3 cajas.</summary>
    public Cantidad Cantidad { get; set; }

    /// <summary>En unidad base, con el factor de cuando se contó: 36 piezas.</summary>
    public Cantidad CantidadBase { get; set; }
}

/// <summary>Un renglón de la nota del proveedor: qué producto, cuánto dice que mandó y cuánto cobra por todo el renglón.</summary>
public class CompraRenglon
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid CompraId { get; init; }
    public int Orden { get; init; }

    /// <summary>La clave con la que el proveedor conoce el producto, si viene en la nota.</summary>
    public string? CodigoProveedor { get; set; }

    /// <summary>Como viene en la nota (o el nombre del producto si se capturó a mano).</summary>
    public string Descripcion { get; set; } = "";

    /// <summary>La unidad como la escribe el documento (CAJA, PZA, H87), solo para mostrarla.</summary>
    public string? Unidad { get; set; }

    /// <summary>Lo leyó el POS del documento del proveedor (no se capturó a mano).</summary>
    public bool DelDocumento { get; init; }

    /// <summary>Vacíos mientras un renglón del documento no se liga a su producto.</summary>
    public Guid? VarianteId { get; set; }

    public Guid? PresentacionId { get; set; }

    public bool Ligado => PresentacionId is not null;

    /// <summary>El producto del catálogo que se parece a la descripción (se sugiere; no está ligado).</summary>
    public Guid? SugerenciaId { get; set; }

    /// <summary>Un producto que no está en el catálogo, en borrador hasta que se da de alta.</summary>
    public ProductoNuevo? Nuevo { get; set; }

    internal void QuitarNuevo()
    {
        Nuevo = null;
        SugerenciaId = null;
    }

    /// <summary>En la presentación de la nota (cajas, paquetes, piezas).</summary>
    public Cantidad Cantidad { get; set; }

    /// <summary>En unidad base; cero mientras no está ligado.</summary>
    public Cantidad CantidadBase { get; set; }

    /// <summary>Lo que se paga por el renglón, con IVA y descuentos: el costo es lo que sale de la caja.</summary>
    public long ImporteCentavos { get; set; }

    // ── Con la factura (2026-11-27) ──

    /// <summary>Con la factura: lo que llegó, en la unidad de la factura (como <see cref="Cantidad"/>). Vacío en las compras a ciegas.</summary>
    public Cantidad? Llego { get; set; }

    /// <summary>Con la factura: lo que anotó quien recibió («venían de menos», «una rota»).</summary>
    public string? Nota { get; set; }

    /// <summary>Con la factura: llegó y no venía en ella (no cobra nada).</summary>
    public bool Agregado { get; init; }

    /// <summary>Con la factura: con qué presentación lo reconoció el POS al cargarla; vacía si no lo reconoció.</summary>
    public Guid? PresentacionReconocidaId { get; init; }

    /// <summary>
    /// Con la factura, cambió contra lo que dice: llegó otra cantidad, no venía, o va con otro producto del que reconoció el POS (un
    /// emparejamiento a mano también cuenta: la próxima factura de ese proveedor ya llega así).
    /// </summary>
    public bool Cambio => Llego is { } llego && (Agregado || llego != Cantidad || PresentacionId != PresentacionReconocidaId);

    /// <summary>Con la factura, llegó algo de un renglón que todavía no se empareja con su producto.</summary>
    public bool SinEmparejar => Llego is { EsPositiva: true } && !Ligado;
}

public enum TipoDocumentoProveedor
{
    /// <summary>El XML de una factura (CFDI): trae cada concepto con su clave, cantidad, importe e impuestos.</summary>
    Xml,

    /// <summary>El PDF de una nota o factura: los renglones se leen del texto.</summary>
    Pdf,
}

/// <summary>Un documento del proveedor cargado en una compra. Se guarda tal cual para consultarlo después.</summary>
public class CompraDocumento
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid CompraId { get; init; }
    public required string Nombre { get; init; }
    public TipoDocumentoProveedor Tipo { get; init; }
    public required byte[] Contenido { get; init; }

    /// <summary>Serie y folio del documento, si los trae.</summary>
    public string? Folio { get; init; }

    /// <summary>El folio fiscal de una factura: la misma no se carga dos veces.</summary>
    public string? Uuid { get; init; }

    public string? RfcEmisor { get; init; }
    public long? TotalCentavos { get; init; }

    /// <summary>Lo que hay que revisar de la lectura (renglones repartidos, otro RFC…), un aviso por renglón.</summary>
    public string? Avisos { get; init; }

    public Guid UsuarioId { get; init; }
    public DateTime CargadoUtc { get; init; }
}

/// <summary>Un renglón leído del documento, con el producto si el POS lo reconoció.</summary>
public sealed record RenglonDocumento(string? CodigoProveedor, string Descripcion, string? Unidad, Cantidad Cantidad, long ImporteCentavos, LigaRenglon? Liga);

public sealed record LigaRenglon(Guid VarianteId, Guid PresentacionId, Cantidad Factor);

/// <summary>
/// Lo que aprendió el POS de cada proveedor: su clave (o su descripción, si no trae clave) es tal presentación. Se guarda
/// al ligar un renglón del documento; la siguiente vez ese renglón llega ligado.
/// </summary>
public class LigaProveedor
{
    public Guid Id { get; init; } = Ids.Nuevo();
    public Guid ProveedorId { get; init; }

    /// <summary>"codigo:…" o "descripcion:…", normalizado.</summary>
    public required string Clave { get; init; }

    public Guid PresentacionId { get; set; }
    public DateTime ModificadaUtc { get; set; }

    /// <summary>Con qué se busca un renglón: su clave del proveedor; sin ella, su descripción.</summary>
    public static string ClaveDe(string? codigoProveedor, string descripcion) =>
        string.IsNullOrWhiteSpace(codigoProveedor) ? $"descripcion:{Texto.Normalizar(descripcion)}" : $"codigo:{Texto.Normalizar(codigoProveedor)}";
}

/// <summary>
/// Un producto nuevo precargado desde un renglón del documento del proveedor. Se da de alta con una variante: la pieza y,
/// si cada unidad del documento trae varias, su presentación (la caja de 12).
/// </summary>
/// <param name="Contiene">Piezas en cada unidad del documento (1 si viene por pieza).</param>
/// <param name="PrecioCentavos">El de menudeo, por pieza.</param>
/// <param name="Codigo">El código de barras que se le da (el del documento si es uno); vacío, sin código.</param>
/// <param name="Llego">Cuántos llegaron, en unidades del documento. Vacío: lo que contó quien recibió por su código.</param>
public sealed record ProductoNuevo(string Nombre, Guid DepartamentoId, long PrecioCentavos, string? Codigo, Cantidad Contiene, Cantidad? Llego);
