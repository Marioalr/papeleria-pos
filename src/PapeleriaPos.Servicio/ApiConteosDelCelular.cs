using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Servicio;

/// <summary>
/// Mandar un conteo del celular a una caja (docs/celular-como-caja.md, «Mandar un conteo a una caja»; dueño, 2026-11-22). El celular
/// cuenta escaneando y manda la lista: cada producto con lo que hay en total. Nunca cambia existencias: la lista llega a una caja
/// con un turno abierto de quien tiene «Conteo completo de un departamento», y ahí quien puede la acepta (tal cual, o cambiando
/// cantidades y quitando productos) o la rechaza. Al aceptarla, cada producto queda como en el conteo completo: lo contado
/// reemplaza la existencia de entonces y lo que se vendió (o entró) después de contar sigue contando; lo que no viene no se toca.
/// La diferencia entra al kardex como movimiento de conteo (<see cref="RegistroInventario.AgregarDiferenciaDeConteoAsync"/>).
/// En el piloto con eleventa no se manda ni se aplica: el inventario se lleva allá (<see cref="TrastiendaEnElPiloto"/>).
/// </summary>
public static class ApiConteosDelCelular
{
    /// <summary>Lo más que trae una lista, y lo más de un renglón (99,999 en su presentación).</summary>
    public const int MaximoRenglones = 500;
    public const long MaximoMilesimas = 99_999_000;

    /// <summary>Una lista contada hace más que esto ya no se recibe: lo vendido desde entonces la volvería un cálculo, no un conteo.</summary>
    public static readonly TimeSpan MasVieja = TimeSpan.FromDays(7);

    /// <summary>Las que contesta «conteos» del celular: las más recientes.</summary>
    public const int Recientes = 20;

    public static void MapearApiConteosDelCelular(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // ── El celular ──────────────────────────────────────────────────────────────────────────────

        // Las cajas de su sucursal que abren turnos: si reciben un conteo ahora y, si no, por qué.
        api.MapGet("/cajas/{cajaId:guid}/para-contar", async (Guid cajaId, PosDbContext db, OpcionesEleventa eleventa, CancellationToken ct) =>
        {
            var sucursal = await db.Cajas.Where(c => c.Id == cajaId).Select(c => (Guid?)c.SucursalId).SingleOrDefaultAsync(ct)
                ?? throw new RechazoConCodigo("Esta caja ya no está dada de alta.", "caja-sin-alta", StatusCodes.Status401Unauthorized);
            var cajas = await db.Cajas.Where(c => c.Activa && c.SucursalId == sucursal && c.Id != cajaId && db.Turnos.Any(t => t.CajaId == c.Id))
                .OrderBy(c => c.Serie).Select(c => new { c.Id, c.Nombre, c.Serie }).ToListAsync(ct);
            var ids = cajas.Select(c => c.Id).ToList();
            var pendientes = await db.ConteosDelCelular.Where(c => ids.Contains(c.CajaId) && c.Estado == EstadoConteoDelCelular.Recibida)
                .GroupBy(c => c.CajaId).Select(g => new { g.Key, Total = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Total, ct);
            var lista = new List<CajaParaContarDto>();
            foreach (var c in cajas)
            {
                var (atiende, rechazo) = await QuienRecibeAsync(db, c.Id, c.Nombre, eleventa.Activo, ct);
                lista.Add(new CajaParaContarDto(c.Id, c.Nombre, c.Serie, rechazo is null, atiende, rechazo?.Codigo, rechazo?.Message, pendientes.GetValueOrDefault(c.Id)));
            }
            return lista;
        });

        api.MapPost("/cajas/{cajaId:guid}/mandar-conteo", async (Guid cajaId, SolicitudMandarConteo s, HttpContext http, PosDbContext db,
            CatalogoEnMemoria catalogo, OpcionesEleventa eleventa, CancellationToken ct) =>
        {
            if (!http.Items.TryGetValue(SeguridadCajas.CajaDeLaLlave, out var deLaLlave) || deLaLlave is not Guid)
                throw new RechazoConCodigo("Solo un celular dado de alta manda conteos a las cajas.", "sin-llave", StatusCodes.Status401Unauthorized);
            if (s.Id == Guid.Empty)
                throw new RechazoConCodigo("La lista no trae su número: actualiza la app.", "sin-id");
            // Otra vez la misma (se perdió la respuesta): lo que se guardó, sin volver a revisar nada.
            if (await db.ConteosDelCelular.AsNoTracking().Where(c => c.Id == s.Id).Select(c => (Guid?)c.CelularId).SingleOrDefaultAsync(ct) is { } de)
                return de == cajaId
                    ? await EstadoAsync(db, s.Id, ct)
                    : throw new RechazoConCodigo("Esa lista es de otro celular.", "otro-celular", StatusCodes.Status409Conflict);

            var conteo = await ArmarAsync(cajaId, s, db, catalogo, eleventa.Activo, ct);
            db.ConteosDelCelular.Add(conteo);
            var celular = await db.Cajas.Where(c => c.Id == cajaId).Select(c => c.Nombre).SingleAsync(ct);
            var caja = await db.Cajas.Where(c => c.Id == conteo.CajaId).Select(c => c.Nombre).SingleAsync(ct);
            var conto = conteo.ContoId is { } u ? await db.Usuarios.Where(x => x.Id == u).Select(x => x.Nombre).SingleAsync(ct) : null;
            new ServicioControl(db).Registrar(conteo.SucursalId, cajaId, conteo.ContoId, AccionControlada.ContarInventario,
                $"Mandó un conteo de {celular} a la {caja}: {Productos(conteo.Renglones.Count)}{(conto is null ? "" : $" (contó {conto})")}, contado {Hora(conteo.ContadoUtc, db.Reloj)}");
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // La misma lista mandada dos veces al mismo tiempo: vale la que se guardó primero.
                if (!await OtraVezAsync(db, s.Id, ct))
                    throw;
            }
            return await EstadoAsync(db, s.Id, ct);
        });

        // Cómo va una lista suya: recibida, aceptada, aceptada con cambios o rechazada.
        api.MapGet("/cajas/{cajaId:guid}/conteos/{conteoId:guid}", async (Guid cajaId, Guid conteoId, PosDbContext db, CancellationToken ct) =>
            await db.ConteosDelCelular.AnyAsync(c => c.Id == conteoId && c.CelularId == cajaId, ct)
                ? await EstadoAsync(db, conteoId, ct)
                : throw new RechazoConCodigo("Esa lista no llegó a ninguna caja: mándala otra vez.", "no-esta", StatusCodes.Status404NotFound));

        // Las últimas que mandó, de la más reciente a la más vieja.
        api.MapGet("/cajas/{cajaId:guid}/conteos", async (Guid cajaId, PosDbContext db, CancellationToken ct) =>
        {
            var ids = await db.ConteosDelCelular.Where(c => c.CelularId == cajaId).OrderByDescending(c => c.RecibidoUtc).Take(Recientes)
                .Select(c => c.Id).ToListAsync(ct);
            var lista = new List<EstadoConteoDto>();
            foreach (var id in ids)
                lista.Add(await EstadoAsync(db, id, ct));
            return lista;
        });

        // ── La caja que la recibe ───────────────────────────────────────────────────────────────────

        // Las que esperan en esta caja, de la más vieja a la más nueva (el aviso de la venta y la lista de Inventario).
        api.MapGet("/cajas/{cajaId:guid}/conteos-recibidos", async (Guid cajaId, PosDbContext db, CancellationToken ct) =>
        {
            var conteos = await db.ConteosDelCelular.AsNoTracking().Where(c => c.CajaId == cajaId && c.Estado == EstadoConteoDelCelular.Recibida)
                .OrderBy(c => c.RecibidoUtc).Select(c => new { c.Id, c.CelularId, c.ContoId, c.ContadoUtc, c.RecibidoUtc, Productos = c.Renglones.Count })
                .ToListAsync(ct);
            var nombres = await NombresAsync(db, ct);
            return conteos.Select(c => new ConteoRecibidoDto(c.Id, nombres.Cajas.GetValueOrDefault(c.CelularId, "Celular"),
                c.ContoId is { } u ? nombres.Usuarios.GetValueOrDefault(u) : null, HoraMexico.ALocal(c.ContadoUtc), HoraMexico.ALocal(c.RecibidoUtc), c.Productos)).ToList();
        });

        // Una, con lo que hay ahora de cada producto y lo que se movió desde que se contó.
        api.MapGet("/cajas/{cajaId:guid}/conteos-recibidos/{conteoId:guid}", async (Guid cajaId, Guid conteoId, PosDbContext db, CancellationToken ct) =>
        {
            var conteo = await DeLaCajaAsync(db, cajaId, conteoId, ct);
            var nombres = await NombresAsync(db, ct);
            var calculo = await CalcularAsync(db, conteo, ct);
            var hoy = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == conteo.SucursalId && calculo.Keys.Contains(e.VarianteId))
                .ToDictionaryAsync(e => e.VarianteId, e => e.Cantidad, ct);
            var ids = conteo.Renglones.Select(r => r.VarianteId).ToList();
            var variantes = await db.Variantes.AsNoTracking().Where(v => ids.Contains(v.Id))
                .Select(v => new { v.Id, v.UnidadBase, Codigo = v.Presentaciones.OrderByDescending(p => p.EsBase).SelectMany(p => p.Codigos).Select(c => c.Codigo).FirstOrDefault() })
                .ToDictionaryAsync(v => v.Id, ct);
            return new ConteoPorRevisarDto(Recibido(conteo, nombres),
                conteo.Renglones.OrderBy(r => r.Orden).Select(r =>
                {
                    var (desde, superado) = calculo[r.VarianteId];
                    var v = variantes.GetValueOrDefault(r.VarianteId);
                    return new RenglonPorRevisarDto(r.VarianteId, r.Descripcion, v?.Codigo, v?.UnidadBase ?? UnidadBase.Pieza, r.Detalle, r.Contado.EnUnidades,
                        desde.EnUnidades, hoy.GetValueOrDefault(r.VarianteId).EnUnidades, superado);
                }).ToList());
        });

        api.MapPost("/cajas/{cajaId:guid}/conteos-recibidos/{conteoId:guid}/aceptar", async (Guid cajaId, Guid conteoId, SolicitudAceptarConteo s,
            PosDbContext db, OpcionesEleventa eleventa, CancellationToken ct) =>
        {
            await AceptarAsync(db, cajaId, conteoId, s, eleventa.Activo, ct);
            return await EstadoAsync(db, conteoId, ct);
        });

        api.MapPost("/cajas/{cajaId:guid}/conteos-recibidos/{conteoId:guid}/rechazar", async (Guid cajaId, Guid conteoId, SolicitudRechazarConteo s,
            PosDbContext db, CancellationToken ct) =>
        {
            // Sin el permiso, el intento queda en la bitácora (antes de la transacción, que se desharía).
            var sucursal = (await DeLaCajaAsync(db, cajaId, conteoId, ct)).SucursalId;
            await new ServicioControl(db).ExigirDeLaSesionAsync(s.UsuarioId, sucursal, cajaId, AccionControlada.ConteoCompleto, ct);
            await using (var transaccion = await db.Database.BeginTransactionAsync(ct))
            {
                var conteo = await DeLaCajaAsync(db, cajaId, conteoId, ct, seguir: true);
                YaResuelta(conteo);
                var motivo = s.Motivo?.Trim() is { Length: > 0 } m ? m[..Math.Min(m.Length, 500)] : null;
                conteo.Rechazar(motivo, s.UsuarioId, db.Reloj.GetUtcNow().UtcDateTime);
                var nombres = await NombresAsync(db, ct);
                new ServicioControl(db).Registrar(conteo.SucursalId, cajaId, s.UsuarioId, AccionControlada.ConteoCompleto,
                    $"Rechazó el conteo de {De(conteo, nombres, db.Reloj)}: {Productos(conteo.Renglones.Count)}, nada cambió{(motivo is null ? "" : $". Motivo: {motivo}")}");
                await db.SaveChangesAsync(ct);
                await transaccion.CommitAsync(ct);
            }
            return await EstadoAsync(db, conteoId, ct);
        });
    }

    // ── Recibir ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Quién puede aceptarla en esa caja: el primero de sus turnos abiertos que tiene «Conteo completo de un departamento» en «Puede».
    /// Si nadie, por qué (con su código). En el piloto con eleventa, nadie.
    /// </summary>
    internal static async Task<(string? Atiende, RechazoConCodigo? Rechazo)> QuienRecibeAsync(PosDbContext db, Guid cajaId, string caja, bool piloto,
        CancellationToken ct)
    {
        var turnos = await db.Turnos.AsNoTracking().Where(t => t.CajaId == cajaId && t.CerradoUtc == null).OrderBy(t => t.AbiertoUtc)
            .Join(db.Usuarios, t => t.CajeroId, u => u.Id, (t, u) => new { u.Id, u.Nombre }).ToListAsync(ct);
        if (piloto)
            return (turnos.FirstOrDefault()?.Nombre, new RechazoConCodigo(
                $"{TrastiendaEnElPiloto.InventarioEnEleventa} El conteo se captura allá.", "piloto-eleventa", StatusCodes.Status409Conflict));
        if (turnos.Count == 0)
            return (null, new RechazoConCodigo($"La {caja} no tiene un turno abierto: que abran turno ahí y vuelve a mandarla.", "sin-turno",
                StatusCodes.Status409Conflict));
        var control = new ServicioControl(db);
        foreach (var t in turnos)
            if (await control.PuedeAsync(t.Id, AccionControlada.ConteoCompleto, ct))
                return (t.Nombre, null);
        return (turnos[0].Nombre, new RechazoConCodigo(
            $"{turnos[0].Nombre} tiene el turno de la {caja} y no puede aceptar conteos (le falta «{Permisos.Nombre(AccionControlada.ConteoCompleto)}»): "
            + "que abra turno ahí quien puede, o mándala a otra caja.", "sin-permiso", StatusCodes.Status409Conflict));
    }

    /// <summary>
    /// La lista como se guarda: cada producto del catálogo del servicio (por su variante y presentación o, sin ellas, por su código,
    /// con la regla de los códigos), en su unidad base, con los renglones del mismo producto sumados.
    /// </summary>
    internal static async Task<ConteoDelCelular> ArmarAsync(Guid celularId, SolicitudMandarConteo s, PosDbContext db, CatalogoEnMemoria catalogo, bool piloto,
        CancellationToken ct)
    {
        if (s.Renglones is not { Count: > 0 } renglones)
            throw new RechazoConCodigo("La lista está vacía.", "vacio");
        if (renglones.Count > MaximoRenglones)
            throw new RechazoConCodigo($"La lista trae {renglones.Count} renglones; van hasta {MaximoRenglones}: pártela en dos.", "demasiados");
        var de = await db.Cajas.Where(c => c.Id == celularId).Select(c => new { c.SucursalId }).SingleOrDefaultAsync(ct)
            ?? throw new RechazoConCodigo("Este celular ya no está dado de alta.", "caja-sin-alta", StatusCodes.Status401Unauthorized);
        var destino = await db.Cajas.Where(c => c.Id == s.Destino && c.Activa).Select(c => new { c.Id, c.Nombre, c.SucursalId }).SingleOrDefaultAsync(ct);
        if (destino is null || destino.Id == celularId || destino.SucursalId != de.SucursalId)
            throw new RechazoConCodigo("Esa caja ya no está en la tienda: elige otra.", "otra-caja-destino");

        var ahora = db.Reloj.GetUtcNow().UtcDateTime;
        // La hora del celular, sin pasar de ahora (su reloj puede ir adelantado).
        var contado = s.ContadoUtc is { } c0 ? (c0.Kind == DateTimeKind.Local ? c0.ToUniversalTime() : DateTime.SpecifyKind(c0, DateTimeKind.Utc)) : ahora;
        if (contado > ahora)
            contado = ahora;
        if (ahora - contado > MasVieja)
            throw new RechazoConCodigo($"Esta lista se contó hace más de {MasVieja.Days} días: desde entonces se vendió mucho; cuenta otra vez.", "contado-viejo");

        var (_, rechazo) = await QuienRecibeAsync(db, destino.Id, destino.Nombre, piloto, ct);
        if (rechazo is not null)
            throw rechazo;

        var productos = new Dictionary<Guid, (ProductoVentaDto Producto, Cantidad Total, List<string> Partes, bool SoloBase)>();
        var faltan = new List<string>();
        var ambiguos = new List<string>();
        var sinInventario = new List<string>();
        foreach (var r in renglones)
        {
            if (r.CantidadMilesimas < 0 || r.CantidadMilesimas > MaximoMilesimas)
                throw new RechazoConCodigo("Una cantidad de la lista no es válida: va de 0 a 99,999.", "cantidad");
            ProductoVentaDto? producto = null;
            if (r.VarianteId is { } varianteId)
                producto = await catalogo.PorVarianteAsync(db, varianteId, de.SucursalId, ct, conExistencia: false);
            else if (!string.IsNullOrWhiteSpace(r.Codigo))
            {
                var (encontrado, varios) = await catalogo.EscanearAsync(db, r.Codigo.Trim(), de.SucursalId, ct);
                if (varios.Count > 0)
                {
                    ambiguos.Add(r.Codigo.Trim());
                    continue;
                }
                producto = encontrado;
            }
            var presentacion = producto is null ? null : producto.Presentaciones.FirstOrDefault(p => p.Id == (r.PresentacionId ?? producto.Presentacion.Id));
            if (producto is null || presentacion is null)
            {
                faltan.Add(r.Codigo ?? producto?.Descripcion ?? r.VarianteId?.ToString() ?? "?");
                continue;
            }
            if (!producto.LlevaInventario)
            {
                sinInventario.Add(producto.Descripcion);
                continue;
            }
            var cantidad = new Cantidad(r.CantidadMilesimas);
            var enBase = cantidad.Por(new Cantidad(presentacion.FactorMilesimas));
            var parte = $"{cantidad.EnUnidades.ToString("#,##0.###", CultureInfo.InvariantCulture)} {presentacion.Nombre}";
            productos[producto.VarianteId] = productos.TryGetValue(producto.VarianteId, out var ya)
                ? (ya.Producto, ya.Total + enBase, [.. ya.Partes, parte], ya.SoloBase && presentacion.EsBase)
                : (producto, enBase, [parte], presentacion.EsBase);
        }
        if (ambiguos.Count > 0)
            throw new RechazoConCodigo($"«{ambiguos[0]}» coincide con más de un producto: búscalo por nombre en el celular.", "ambiguo");
        if (faltan.Count > 0)
            throw new RechazoConCodigo(faltan.Count == 1
                ? $"«{faltan[0]}» ya no está en el catálogo de la caja principal: quítalo de la lista."
                : $"{faltan.Count} productos ya no están en el catálogo de la caja principal ({string.Join(", ", faltan.Take(5))}): quítalos de la lista.",
                "no-esta");
        if (sinInventario.Count > 0)
            throw new RechazoConCodigo(sinInventario.Count == 1
                ? $"«{sinInventario[0]}» no lleva inventario (es un servicio): quítalo de la lista."
                : $"{sinInventario.Count} productos no llevan inventario ({string.Join(", ", sinInventario.Take(5))}): quítalos de la lista.",
                "sin-inventario");
        foreach (var (producto, total, _, _) in productos.Values)
            if (!producto.Unidad.AdmiteFracciones() && !total.EsEntera)
                throw new RechazoConCodigo($"«{producto.Descripcion}» se cuenta en piezas enteras.", "cantidad");

        var conto = s.UsuarioId is { } u && await db.Usuarios.AnyAsync(x => x.Id == u && x.Activo, ct) ? u : (Guid?)null;
        var conteo = new ConteoDelCelular
        {
            Id = s.Id,
            SucursalId = de.SucursalId,
            CelularId = celularId,
            CajaId = destino.Id,
            ContoId = conto,
            ContadoUtc = contado,
            RecibidoUtc = ahora,
        };
        static string Corto(string texto) => texto.Length > 300 ? texto[..300] : texto;
        conteo.Renglones.AddRange(productos.Values.Select((p, i) => new ConteoDelCelularRenglon
        {
            ConteoId = conteo.Id,
            Orden = i + 1,
            VarianteId = p.Producto.VarianteId,
            Descripcion = Corto(p.Producto.Descripcion),
            // Solo si no fue todo en su unidad, en un renglón: «2 Caja 12 + 5 Pieza».
            Detalle = p.SoloBase && p.Partes.Count == 1 ? null : Corto(string.Join(" + ", p.Partes)),
            Contado = p.Total,
        }));
        return conteo;
    }

    private static async Task<bool> OtraVezAsync(PosDbContext db, Guid id, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        return await db.ConteosDelCelular.AnyAsync(c => c.Id == id, ct);
    }

    // ── Aceptar ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Aplica la lista: cada renglón que quedó (con lo contado o lo que se corrigió en la caja) deja la existencia en
    /// <c>lo contado + lo que se movió desde que se contó</c>; la diferencia entra al kardex como conteo. Lo quitado y lo que ya se
    /// contó después no se toca. Con el candado de escritura: lo que se venda mientras tanto espera.
    /// </summary>
    internal static async Task AceptarAsync(PosDbContext db, Guid cajaId, Guid conteoId, SolicitudAceptarConteo s, bool piloto, CancellationToken ct)
    {
        // Sin el permiso, el intento queda en la bitácora (antes de la transacción, que se desharía).
        var sucursal = (await DeLaCajaAsync(db, cajaId, conteoId, ct)).SucursalId;
        await new ServicioControl(db).ExigirDeLaSesionAsync(s.UsuarioId, sucursal, cajaId, AccionControlada.ConteoCompleto, ct);
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var conteo = await DeLaCajaAsync(db, cajaId, conteoId, ct, seguir: true);
        if (piloto)
            throw new RechazoConCodigo(TrastiendaEnElPiloto.InventarioEnEleventa, "piloto-eleventa", StatusCodes.Status409Conflict);
        YaResuelta(conteo);

        var pedidos = new Dictionary<Guid, Cantidad>();
        foreach (var r in s.Renglones ?? [])
        {
            if (conteo.Renglones.All(x => x.VarianteId != r.VarianteId))
                throw new RechazoConCodigo("Ese producto no viene en la lista.", "otro-producto");
            if (r.CantidadMilesimas < 0 || r.CantidadMilesimas > MaximoMilesimas * 1000)
                throw new RechazoConCodigo("Una cantidad no es válida: va de 0 en adelante.", "cantidad");
            pedidos[r.VarianteId] = new Cantidad(r.CantidadMilesimas);
        }
        if (pedidos.Count == 0)
            throw new RechazoConCodigo("No queda ningún producto: si no se aplica nada, recházala.", "vacio");

        var ids = conteo.Renglones.Select(r => r.VarianteId).ToList();
        var variantes = await db.Variantes.Include(v => v.Producto).Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        foreach (var (id, cantidad) in pedidos)
            if (!variantes[id].AdmiteCantidad(cantidad))
                throw new RechazoConCodigo($"«{conteo.Renglones.First(r => r.VarianteId == id).Descripcion}» se cuenta en piezas enteras.", "cantidad");

        var calculo = await CalcularAsync(db, conteo, ct);
        var inventario = new RegistroInventario(db);
        var ahora = db.Reloj.GetUtcNow().UtcDateTime;
        var (cambiados, quitados, superados, conDiferencia) = (0, 0, 0, 0);
        var neto = 0L;
        foreach (var r in conteo.Renglones.OrderBy(r => r.Orden))
        {
            if (!pedidos.TryGetValue(r.VarianteId, out var aceptado))
            {
                quitados++;
                continue;
            }
            r.Aceptado = aceptado;
            if (aceptado != r.Contado)
                cambiados++;
            var (desde, superado) = calculo[r.VarianteId];
            if (superado is not null)
            {
                r.Superado = true;
                superados++;
                continue;
            }
            var sistema = (await inventario.ExistenciaAsync(conteo.SucursalId, r.VarianteId, ct)).Cantidad;
            var diferencia = aceptado + desde - sistema;
            var costo = await inventario.AgregarDiferenciaDeConteoAsync(conteo.SucursalId, variantes[r.VarianteId], diferencia, ahora,
                nameof(ConteoDelCelular), conteo.Id, ct);
            (r.Desde, r.Sistema, r.CostoDiferenciaCentavos) = (desde, sistema, costo);
            neto += costo;
            if (diferencia.Milesimas != 0)
                conDiferencia++;
        }
        conteo.Aceptar(cambiados > 0 || quitados > 0, s.UsuarioId, ahora);

        var nombres = await NombresAsync(db, ct);
        var aplicados = conteo.Renglones.Count(r => r.Aplicado);
        var detalle = $"Aplicó el conteo de {De(conteo, nombres, db.Reloj)}: {Productos(aplicados)}"
            + (conDiferencia == 0 ? ", todo cuadra" : $", {conDiferencia} con diferencia")
            + (cambiados == 0 ? "" : $"; cambió {Productos(cambiados)}")
            + (quitados == 0 ? "" : $"; quitó {Productos(quitados)}")
            + (superados == 0 ? "" : $"; {Productos(superados)} ya se había{(superados == 1 ? "" : "n")} contado después");
        new ServicioControl(db).Registrar(conteo.SucursalId, cajaId, s.UsuarioId, AccionControlada.ConteoCompleto, detalle, neto);
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
    }

    /// <summary>
    /// De cada producto de la lista: lo que se movió desde que se contó (todo el kardex después de esa hora, menos lo que aplicaron las
    /// listas del celular contadas antes, que esta reemplaza) y, si ya se contó después, por qué no se aplica. Lo más nuevo gana: otra
    /// lista contada más tarde y ya aceptada, el conteo del día, el conteo completo o un ajuste hechos después de contar esta.
    /// </summary>
    internal static async Task<Dictionary<Guid, (Cantidad Desde, string? Superado)>> CalcularAsync(PosDbContext db, ConteoDelCelular conteo, CancellationToken ct)
    {
        var t = conteo.ContadoUtc;
        var ids = conteo.Renglones.Select(r => r.VarianteId).Distinct().ToList();
        var movimientos = await db.MovimientosInventario.AsNoTracking()
            .Where(m => m.SucursalId == conteo.SucursalId && ids.Contains(m.VarianteId) && m.FechaUtc > t)
            .Select(m => new { m.VarianteId, m.Cantidad, m.Tipo, m.DocumentoTipo, m.FechaUtc }).ToListAsync(ct);
        var otrasListas = await (from r in db.ConteosDelCelularRenglones.AsNoTracking()
                                 join c in db.ConteosDelCelular.AsNoTracking() on r.ConteoId equals c.Id
                                 where c.SucursalId == conteo.SucursalId && c.Id != conteo.Id && ids.Contains(r.VarianteId) && c.ContadoUtc > t
                                       && (c.Estado == EstadoConteoDelCelular.Aceptada || c.Estado == EstadoConteoDelCelular.AceptadaConCambios)
                                       && r.Aceptado != null && !r.Superado
                                 select new { r.VarianteId, c.ContadoUtc, c.CelularId }).ToListAsync(ct);
        var delDia = await (from r in db.ConteosCiclicosRenglones.AsNoTracking()
                            join c in db.ConteosCiclicos.AsNoTracking() on r.ConteoId equals c.Id
                            where c.SucursalId == conteo.SucursalId && ids.Contains(r.VarianteId) && r.ContadoUtc > t
                            select new { r.VarianteId, Fecha = r.ContadoUtc!.Value }).ToListAsync(ct);
        var completos = await (from r in db.ConteosDepartamentoRenglones.AsNoTracking()
                               join c in db.ConteosDepartamento.AsNoTracking() on r.ConteoId equals c.Id
                               where c.SucursalId == conteo.SucursalId && c.Estado == EstadoConteoDepartamento.Cerrado && ids.Contains(r.VarianteId)
                                     && c.TerminadoUtc > t
                               select new { r.VarianteId, Fecha = c.TerminadoUtc!.Value }).ToListAsync(ct);
        var celulares = otrasListas.Count == 0 ? [] : await db.Cajas.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Nombre, ct);

        var resultado = new Dictionary<Guid, (Cantidad, string?)>();
        foreach (var id in ids)
        {
            string? superado = null;
            if (otrasListas.Where(o => o.VarianteId == id).MaxBy(o => o.ContadoUtc) is { } otra)
                superado = $"Ya se contó después con {celulares.GetValueOrDefault(otra.CelularId, "otro celular")}, {Hora(otra.ContadoUtc, db.Reloj)}: vale ese.";
            else if (delDia.Where(d => d.VarianteId == id).Select(d => (DateTime?)d.Fecha).Max() is { } dia)
                superado = $"Ya se contó después en el conteo del día, {Hora(dia, db.Reloj)}: vale ese.";
            else if (completos.Where(d => d.VarianteId == id).Select(d => (DateTime?)d.Fecha).Max() is { } completo)
                superado = $"Ya se contó después en el conteo completo, {Hora(completo, db.Reloj)}: vale ese.";
            else if (movimientos.Where(m => m.VarianteId == id && m.DocumentoTipo != nameof(ConteoDelCelular)
                         && (m.Tipo == TipoMovimiento.Conteo || (m.Tipo == TipoMovimiento.Ajuste && m.DocumentoTipo == nameof(AjusteInventario))))
                         .Select(m => (DateTime?)m.FechaUtc).Max() is { } ajuste)
                superado = $"Ya se contó o se ajustó después, {Hora(ajuste, db.Reloj)}: vale eso.";
            var desde = movimientos.Where(m => m.VarianteId == id && m.DocumentoTipo != nameof(ConteoDelCelular))
                .Aggregate(Cantidad.Cero, (suma, m) => suma + m.Cantidad);
            resultado[id] = (desde, superado);
        }
        return resultado;
    }

    // ── Lo de todas ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<ConteoDelCelular> DeLaCajaAsync(PosDbContext db, Guid cajaId, Guid conteoId, CancellationToken ct, bool seguir = false)
    {
        var consulta = db.ConteosDelCelular.Include(c => c.Renglones);
        var conteo = await (seguir ? consulta : consulta.AsNoTracking()).SingleOrDefaultAsync(c => c.Id == conteoId, ct);
        return conteo is not null && conteo.CajaId == cajaId
            ? conteo
            : throw new RechazoConCodigo("Esa lista no es de esta caja.", "no-esta", StatusCodes.Status404NotFound);
    }

    private static void YaResuelta(ConteoDelCelular conteo)
    {
        if (!conteo.Pendiente)
            throw new RechazoConCodigo(conteo.Estado == EstadoConteoDelCelular.Rechazada ? "Esa lista ya se rechazó." : "Esa lista ya se aplicó.",
                "ya-resuelta", StatusCodes.Status409Conflict);
    }

    private sealed record Nombres(Dictionary<Guid, string> Cajas, Dictionary<Guid, string> Usuarios);

    private static async Task<Nombres> NombresAsync(PosDbContext db, CancellationToken ct) =>
        new(await db.Cajas.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Nombre, ct),
            await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct));

    private static ConteoRecibidoDto Recibido(ConteoDelCelular c, Nombres n) =>
        new(c.Id, n.Cajas.GetValueOrDefault(c.CelularId, "Celular"), c.ContoId is { } u ? n.Usuarios.GetValueOrDefault(u) : null,
            HoraMexico.ALocal(c.ContadoUtc), HoraMexico.ALocal(c.RecibidoUtc), c.Renglones.Count);

    /// <summary>«Celular de Luis (contó Ana), contado hoy a las 10:42», para la bitácora.</summary>
    private static string De(ConteoDelCelular c, Nombres n, TimeProvider reloj) =>
        $"{n.Cajas.GetValueOrDefault(c.CelularId, "un celular")}"
        + (c.ContoId is { } u && n.Usuarios.GetValueOrDefault(u) is { } conto ? $" (contó {conto})" : "")
        + $", contado {Hora(c.ContadoUtc, reloj)}";

    private static string Productos(int n) => n == 1 ? "1 producto" : $"{n} productos";

    /// <summary>«a las 10:42» si fue hoy (en México); si no, «el 21/11 a las 10:42».</summary>
    private static string Hora(DateTime utc, TimeProvider reloj)
    {
        var local = HoraMexico.ALocal(utc);
        var hoy = HoraMexico.ALocal(reloj.GetUtcNow().UtcDateTime).Date;
        return local.Date == hoy ? $"a las {local:HH:mm}" : $"el {local:dd/MM} a las {local:HH:mm}";
    }

    internal static string Estado(EstadoConteoDelCelular estado) => estado switch
    {
        EstadoConteoDelCelular.Recibida => "recibida",
        EstadoConteoDelCelular.Aceptada => "aceptada",
        EstadoConteoDelCelular.AceptadaConCambios => "aceptada-con-cambios",
        _ => "rechazada",
    };

    internal static async Task<EstadoConteoDto> EstadoAsync(PosDbContext db, Guid conteoId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var c = await db.ConteosDelCelular.AsNoTracking().Include(x => x.Renglones).SingleAsync(x => x.Id == conteoId, ct);
        var nombres = await NombresAsync(db, ct);
        var renglones = c.Renglones.OrderBy(r => r.Orden).Select(r => new RenglonDeConteoDto(r.VarianteId, r.Descripcion, r.Contado.EnUnidades,
            r.Aceptado?.EnUnidades, r.Aplicado, c.Estado switch
            {
                EstadoConteoDelCelular.Recibida or EstadoConteoDelCelular.Rechazada => null,
                _ when r.Aceptado is null => "Lo quitaron en la caja: no cambió.",
                _ when r.Superado => "No se aplicó: ya se había contado después.",
                _ when r.Aceptado != r.Contado => $"En la caja lo cambiaron a {r.Aceptado!.Value.EnUnidades.ToString("#,##0.###", CultureInfo.InvariantCulture)}.",
                _ => null,
            })).ToList();
        return new EstadoConteoDto(c.Id, c.CajaId, nombres.Cajas.GetValueOrDefault(c.CajaId, ""), Estado(c.Estado), c.ContadoUtc, c.RecibidoUtc, c.ResueltoUtc,
            c.ResolvioId is { } u ? nombres.Usuarios.GetValueOrDefault(u) : null, c.Motivo, renglones.Count, renglones.Count(r => r.Aplicado), renglones);
    }
}
