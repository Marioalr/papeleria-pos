using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos;

/// <summary>
/// El conteo completo por departamento (docs/fase-2-inventario.md, entrega 3, parte 3). El encargado abre un
/// departamento; quienes estén presentes capturan desde el celular lo que cuentan, a ciegas, y las capturas se suman.
/// Al cerrarlo, lo contado reemplaza la existencia: la diferencia entra al kardex como movimiento de conteo, contra lo
/// que decía el sistema cuando se contó cada producto (lo que se vendió después sigue contando). Lo que nadie contó
/// queda en cero. En el piloto con eleventa (<paramref name="lecturaDeEleventa"/>) no se abre, no se captura ni se cierra:
/// el inventario se lleva en eleventa (<see cref="TrastiendaEnElPiloto"/>); lo que quedó abierto se consulta y se cancela.
/// </summary>
public sealed class RegistroConteoCompleto(PosDbContext db, bool lecturaDeEleventa = false)
{
    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    /// <summary>Los departamentos con productos de inventario: si se están contando y cuándo se cerró el último conteo.</summary>
    public async Task<List<DepartamentoConteoDto>> DepartamentosAsync(Guid sucursalId, CancellationToken ct = default)
    {
        var productos = await DeInventario().GroupBy(v => v.Producto!.DepartamentoId)
            .Select(g => new { Departamento = g.Key, Total = g.Count() }).ToDictionaryAsync(x => x.Departamento, x => x.Total, ct);
        var conteos = await db.ConteosDepartamento.AsNoTracking().Where(c => c.SucursalId == sucursalId && c.Estado != EstadoConteoDepartamento.Cancelado)
            .ToListAsync(ct);
        var abiertos = conteos.Where(c => c.Estado == EstadoConteoDepartamento.Abierto).Select(c => c.Id).ToList();
        var capturas = await db.CapturasConteo.AsNoTracking().Where(c => abiertos.Contains(c.ConteoId) && c.QuitadaUtc == null)
            .Select(c => new { c.ConteoId, c.VarianteId, c.UsuarioId }).ToListAsync(ct);
        var cerrados = conteos.Where(c => c.Estado == EstadoConteoDepartamento.Cerrado).Select(c => c.Id).ToList();
        var contadosAlCerrar = await db.ConteosDepartamentoRenglones.AsNoTracking().Where(r => cerrados.Contains(r.ConteoId) && r.Contado != null)
            .GroupBy(r => r.ConteoId).Select(g => new { Conteo = g.Key, Total = g.Count() }).ToDictionaryAsync(x => x.Conteo, x => x.Total, ct);
        var usuarios = await Usuarios(ct);

        var departamentos = await db.Departamentos.AsNoTracking().Where(d => d.Activo).ToListAsync(ct);
        return departamentos
            .Where(d => productos.ContainsKey(d.Id) || conteos.Any(c => c.DepartamentoId == d.Id && c.Estado == EstadoConteoDepartamento.Abierto))
            .Select(d =>
            {
                var abierto = conteos.FirstOrDefault(c => c.DepartamentoId == d.Id && c.Estado == EstadoConteoDepartamento.Abierto);
                var ultimo = conteos.Where(c => c.DepartamentoId == d.Id && c.Estado == EstadoConteoDepartamento.Cerrado).MaxBy(c => c.TerminadoUtc);
                var deAbierto = abierto is null ? [] : capturas.Where(c => c.ConteoId == abierto.Id).ToList();
                return new DepartamentoConteoDto(d.Id, d.Nombre, productos.GetValueOrDefault(d.Id),
                    abierto is null ? null : new ConteoResumenDto(abierto.Id, HoraMexico.ALocal(abierto.AbiertoUtc), usuarios.GetValueOrDefault(abierto.AbiertoPorId, ""),
                        deAbierto.Select(c => c.VarianteId).Distinct().Count(),
                        deAbierto.Select(c => c.UsuarioId).Distinct().Select(u => usuarios.GetValueOrDefault(u, "")).Order().ToList()),
                    ultimo is null ? null : new ConteoResumenDto(ultimo.Id, HoraMexico.ALocal(ultimo.TerminadoUtc!.Value),
                        usuarios.GetValueOrDefault(ultimo.TerminadoPorId!.Value, ""), contadosAlCerrar.GetValueOrDefault(ultimo.Id), []));
            })
            .OrderBy(d => d.Departamento, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<ConteoDepartamentoDto> AbrirAsync(Guid sucursalId, SolicitudAbrirConteo s, CancellationToken ct = default)
    {
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, sucursalId, AccionControlada.ConteoCompleto, "El conteo completo lo abre el encargado.", ct);
        TrastiendaEnElPiloto.RevisarInventario(lecturaDeEleventa);
        var departamento = await db.Departamentos.FindAsync([s.DepartamentoId], ct) ?? throw new InvalidOperationException("El departamento no existe.");
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        if (await db.ConteosDepartamento.AnyAsync(c => c.SucursalId == sucursalId && c.DepartamentoId == s.DepartamentoId
                && c.Estado == EstadoConteoDepartamento.Abierto, ct))
            throw new InvalidOperationException($"Ya se está contando «{departamento.Nombre}».");
        var conteo = new ConteoDepartamento { SucursalId = sucursalId, DepartamentoId = departamento.Id, AbiertoPorId = s.UsuarioId, AbiertoUtc = Ahora };
        db.ConteosDepartamento.Add(conteo);
        new ServicioControl(db).Registrar(sucursalId, null, s.UsuarioId, AccionControlada.ConteoCompleto, $"Abrió el conteo de «{departamento.Nombre}»", null);
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return await DtoAsync(conteo.Id, s.UsuarioId, revision: false, ct);
    }

    /// <summary>
    /// El conteo con lo que lleva cada producto. Con <paramref name="revision"/>, quien puede cerrarlo ve también lo que
    /// decía el sistema y lo que vale la diferencia; quien cuenta nunca lo ve.
    /// </summary>
    public Task<ConteoDepartamentoDto> ConteoAsync(Guid conteoId, Guid usuarioId, bool revision, CancellationToken ct = default) =>
        DtoAsync(conteoId, usuarioId, revision, ct);

    /// <summary>Lo que contó alguien de un producto: se suma a lo que ya contaron los demás.</summary>
    public async Task<ConteoDepartamentoDto> CapturarAsync(Guid conteoId, SolicitudCaptura s, CancellationToken ct = default)
    {
        // La captura y el cierre no se cruzan: lo que llegue después de cerrar ya no cuenta.
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var conteo = await db.ConteosDepartamento.SingleOrDefaultAsync(c => c.Id == conteoId, ct) ?? throw new InvalidOperationException("El conteo ya no existe.");
        await new ServicioControl(db).ExigirAsync(s.UsuarioId, conteo.SucursalId, AccionControlada.ContarInventario, "No tienes permiso para contar.", ct);
        TrastiendaEnElPiloto.RevisarInventario(lecturaDeEleventa);
        if (!await db.CapturasConteo.AnyAsync(c => c.Id == s.Id, ct))
        {
            var variante = await db.Variantes.Include(v => v.Producto).ThenInclude(p => p!.Marca).SingleOrDefaultAsync(v => v.Id == s.VarianteId, ct)
                ?? throw new InvalidOperationException("El producto no existe.");
            if (variante.Producto!.DepartamentoId != conteo.DepartamentoId)
            {
                var suyo = await db.Departamentos.Where(d => d.Id == variante.Producto.DepartamentoId).Select(d => d.Nombre).SingleAsync(ct);
                throw new InvalidOperationException($"«{RegistroCompras.Nombre(variante)}» es de «{suyo}»: se cuenta con ese departamento.");
            }
            if (!variante.Producto.LlevaInventario)
                throw new InvalidOperationException($"«{RegistroCompras.Nombre(variante)}» no lleva inventario.");
            var cantidad = new Cantidad(s.CantidadMilesimas);
            if (cantidad.Milesimas < 0)
                throw new ArgumentException("Lo contado no puede ser negativo.");
            if (!variante.AdmiteCantidad(cantidad))
                throw new ArgumentException($"«{RegistroCompras.Nombre(variante)}» se cuenta en piezas enteras.");
            var esperado = (await new RegistroInventario(db).ExistenciaAsync(conteo.SucursalId, variante.Id, ct)).Cantidad;
            db.CapturasConteo.Add(conteo.Capturar(s.Id, variante.Id, cantidad, esperado, s.UsuarioId, Ahora));
            await db.SaveChangesAsync(ct);
        }
        await transaccion.CommitAsync(ct);
        return await DtoAsync(conteoId, s.UsuarioId, revision: false, ct);
    }

    /// <summary>Quita una captura equivocada: cada quien las suyas; quien puede cerrar el conteo, cualquiera.</summary>
    public async Task<ConteoDepartamentoDto> QuitarAsync(Guid conteoId, Guid capturaId, Guid usuarioId, CancellationToken ct = default)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var conteo = await db.ConteosDepartamento.SingleOrDefaultAsync(c => c.Id == conteoId, ct) ?? throw new InvalidOperationException("El conteo ya no existe.");
        var captura = await db.CapturasConteo.SingleOrDefaultAsync(c => c.Id == capturaId && c.ConteoId == conteoId, ct)
            ?? throw new InvalidOperationException("Esa captura no es de este conteo.");
        conteo.Quitar(captura, usuarioId, await PuedeAdministrarAsync(usuarioId, conteo.SucursalId, ct), Ahora);
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return await DtoAsync(conteoId, usuarioId, revision: false, ct);
    }

    /// <summary>Lo contado reemplaza la existencia de todo el departamento; lo que nadie contó queda en cero.</summary>
    public async Task<ConteoDepartamentoDto> CerrarAsync(Guid conteoId, Guid usuarioId, CancellationToken ct = default)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var conteo = await db.ConteosDepartamento.Include(c => c.Capturas).SingleOrDefaultAsync(c => c.Id == conteoId, ct)
            ?? throw new InvalidOperationException("El conteo ya no existe.");
        await new ServicioControl(db).ExigirAsync(usuarioId, conteo.SucursalId, AccionControlada.ConteoCompleto, "El conteo completo lo cierra el encargado.", ct);
        TrastiendaEnElPiloto.RevisarInventario(lecturaDeEleventa);
        var departamento = await db.Departamentos.FindAsync([conteo.DepartamentoId], ct);
        var contado = conteo.Contado();
        var variantes = await Variantes(conteo, contado.Keys, delDepartamento: true, ct);

        var inventario = new RegistroInventario(db);
        var ahora = Ahora;
        var renglones = new List<ConteoDepartamentoRenglon>();
        foreach (var variante in variantes.Values)
        {
            var contadoSi = contado.TryGetValue(variante.Id, out var cantidad);
            var esperado = contadoSi ? conteo.EsperadoDe(variante.Id)!.Value : (await inventario.ExistenciaAsync(conteo.SucursalId, variante.Id, ct)).Cantidad;
            if (!contadoSi && esperado.Milesimas == 0)
                continue;
            var diferencia = cantidad - esperado;
            var costo = await inventario.AgregarDiferenciaDeConteoAsync(conteo.SucursalId, variante, diferencia, ahora, nameof(ConteoDepartamento), conteo.Id, ct);
            renglones.Add(new ConteoDepartamentoRenglon
            {
                ConteoId = conteo.Id,
                VarianteId = variante.Id,
                Contado = contadoSi ? cantidad : null,
                Esperado = esperado,
                CostoDiferenciaCentavos = costo,
            });
        }
        conteo.Cerrar(renglones, usuarioId, ahora);
        db.ConteosDepartamentoRenglones.AddRange(renglones);

        var neto = renglones.Sum(r => r.CostoDiferenciaCentavos);
        var conDiferencia = renglones.Count(r => r.Diferencia.Milesimas != 0);
        var enCero = renglones.Count(r => r.Contado is null);
        new ServicioControl(db).Registrar(conteo.SucursalId, null, usuarioId, AccionControlada.ConteoCompleto,
            $"Cerró el conteo de «{departamento?.Nombre}»: {contado.Count} productos contados"
            + (conDiferencia == 0 ? ", todo cuadra" : $", {conDiferencia} con diferencia")
            + (enCero == 0 ? "" : $" ({enCero} que nadie contó quedaron en 0)"),
            neto);
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return await DtoAsync(conteo.Id, usuarioId, revision: true, ct);
    }

    /// <summary>Se descarta lo contado: la existencia no cambia.</summary>
    public async Task<ConteoDepartamentoDto> CancelarAsync(Guid conteoId, Guid usuarioId, CancellationToken ct = default)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var conteo = await db.ConteosDepartamento.SingleOrDefaultAsync(c => c.Id == conteoId, ct) ?? throw new InvalidOperationException("El conteo ya no existe.");
        await new ServicioControl(db).ExigirAsync(usuarioId, conteo.SucursalId, AccionControlada.ConteoCompleto, "El conteo completo lo cancela el encargado.", ct);
        var capturas = await db.CapturasConteo.CountAsync(c => c.ConteoId == conteoId && c.QuitadaUtc == null, ct);
        conteo.Cancelar(usuarioId, Ahora);
        var departamento = await db.Departamentos.FindAsync([conteo.DepartamentoId], ct);
        new ServicioControl(db).Registrar(conteo.SucursalId, null, usuarioId, AccionControlada.ConteoCompleto,
            $"Canceló el conteo de «{departamento?.Nombre}» ({capturas} {(capturas == 1 ? "captura" : "capturas")})", null);
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return await DtoAsync(conteoId, usuarioId, revision: false, ct);
    }

    private IQueryable<Variante> DeInventario() =>
        db.Variantes.AsNoTracking().Where(v => v.Activa && v.Producto!.Activo && v.Producto.Tipo == TipoProducto.Mercancia);

    /// <summary>
    /// Lo que hay que contar del departamento, más lo que ya se capturó aunque después lo hayan cambiado de departamento.
    /// Sin <paramref name="delDepartamento"/>, solo los indicados (lo que quedó en un conteo cerrado).
    /// </summary>
    private async Task<Dictionary<Guid, Variante>> Variantes(ConteoDepartamento conteo, IEnumerable<Guid> indicadas, bool delDepartamento, CancellationToken ct)
    {
        var ids = indicadas.ToList();
        return await db.Variantes.AsNoTracking().Include(v => v.Producto).ThenInclude(p => p!.Marca).Include(v => v.Presentaciones).ThenInclude(p => p.Codigos)
            .Where(v => (delDepartamento && v.Activa && v.Producto!.Activo && v.Producto.Tipo == TipoProducto.Mercancia
                    && v.Producto.DepartamentoId == conteo.DepartamentoId)
                || ids.Contains(v.Id))
            .AsSplitQuery().ToDictionaryAsync(v => v.Id, ct);
    }

    private async Task<bool> PuedeAdministrarAsync(Guid usuarioId, Guid sucursalId, CancellationToken ct) =>
        (await new ServicioControl(db).EvaluarAsync(usuarioId, sucursalId, AccionControlada.ConteoCompleto, ct: ct)).Nivel == NivelPermiso.Permitido;

    private Task<Dictionary<Guid, string>> Usuarios(CancellationToken ct) => db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);

    private async Task<ConteoDepartamentoDto> DtoAsync(Guid conteoId, Guid usuarioId, bool revision, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var conteo = await db.ConteosDepartamento.AsNoTracking().Include(c => c.Capturas).Include(c => c.Renglones)
            .AsSplitQuery().SingleOrDefaultAsync(c => c.Id == conteoId, ct) ?? throw new InvalidOperationException("El conteo ya no existe.");
        var departamento = await db.Departamentos.AsNoTracking().Where(d => d.Id == conteo.DepartamentoId).Select(d => d.Nombre).SingleAsync(ct);
        var usuarios = await Usuarios(ct);
        var cerrado = conteo.Estado == EstadoConteoDepartamento.Cerrado;
        revision = (revision || cerrado) && await PuedeAdministrarAsync(usuarioId, conteo.SucursalId, ct);
        var veCostos = revision && await new RegistroCatalogo(db).VeCostosAsync(usuarioId, ct);

        var contado = cerrado
            ? conteo.Renglones.Where(r => r.Contado is not null).ToDictionary(r => r.VarianteId, r => r.Contado!.Value)
            : conteo.Contado();
        var variantes = cerrado
            ? await Variantes(conteo, conteo.Renglones.Select(r => r.VarianteId), delDepartamento: false, ct)
            : await Variantes(conteo, contado.Keys, delDepartamento: true, ct);

        var inventario = new RegistroInventario(db);
        var renglones = new List<ProductoConteoDto>();
        foreach (var v in variantes.Values)
        {
            decimal? sistema = null;
            long? costo = null;
            var cuenta = contado.TryGetValue(v.Id, out var c) ? c : (Cantidad?)null;
            if (revision && cerrado)
            {
                var final = conteo.Renglones.Single(r => r.VarianteId == v.Id);
                (sistema, costo) = (final.Esperado.EnUnidades, final.CostoDiferenciaCentavos);
            }
            else if (revision)
            {
                // Lo que decía el sistema al contarlo (o ahora, si nadie lo ha contado) y lo que valdría la diferencia al cerrar.
                var esperado = cuenta is null ? (await inventario.ExistenciaAsync(conteo.SucursalId, v.Id, ct)).Cantidad : conteo.EsperadoDe(v.Id)!.Value;
                sistema = esperado.EnUnidades;
                costo = (await inventario.DiferenciaDeConteoAsync(conteo.SucursalId, v, (cuenta ?? Cantidad.Cero) - esperado, Ahora,
                    nameof(ConteoDepartamento), conteo.Id, ct))?.CostoCentavos ?? 0;
            }

            // Para contar: primero las cajas (de la más grande) y al final las sueltas.
            var presentaciones = v.Presentaciones.OrderBy(p => p.EsBase).ThenByDescending(p => p.Factor.Milesimas)
                .Select(p => new PresentacionConteoDto(p.Nombre, p.Factor.Milesimas, p.EsBase, p.Codigos.Select(x => x.Codigo).ToList())).ToList();
            var codigo = presentaciones.OrderByDescending(p => p.EsBase).SelectMany(p => p.Codigos).FirstOrDefault();
            var diferencia = sistema is { } s ? (cuenta?.EnUnidades ?? 0) - s : (decimal?)null;
            renglones.Add(new ProductoConteoDto(v.Id, RegistroCompras.Nombre(v), codigo, v.UnidadBase,
                presentaciones, cuenta?.EnUnidades, sistema, diferencia, veCostos ? costo : null));
        }
        renglones = renglones.OrderBy(r => r.Producto, StringComparer.CurrentCultureIgnoreCase).ToList();

        var capturas = conteo.Capturas.Where(c => c.Vigente).OrderByDescending(c => c.CapturadoUtc)
            .Select(c => new CapturaConteoDto(c.Id, c.VarianteId, c.Cantidad.EnUnidades, c.UsuarioId, usuarios.GetValueOrDefault(c.UsuarioId, ""),
                HoraMexico.ALocal(c.CapturadoUtc)))
            .ToList();
        return new ConteoDepartamentoDto(conteo.Id, conteo.DepartamentoId, departamento, conteo.Estado, usuarios.GetValueOrDefault(conteo.AbiertoPorId, ""),
            HoraMexico.ALocal(conteo.AbiertoUtc), conteo.TerminadoPorId is { } t ? usuarios.GetValueOrDefault(t) : null,
            conteo.TerminadoUtc is { } f ? HoraMexico.ALocal(f) : null,
            renglones.Count, renglones.Count(r => r.Contado is not null), revision,
            veCostos ? renglones.Sum(r => r.CostoDiferenciaCentavos ?? 0) : null, renglones, capturas);
    }
}
