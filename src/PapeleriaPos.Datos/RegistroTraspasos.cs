using System.Text;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos;

/// <summary>
/// Inventario › «Traspasos»: mandar mercancía de una sucursal a otra con un archivo, <b>provisional</b> mientras no exista el
/// servidor central (dueño, 2026-11-26; lo decidido, en docs/traspasos.md). Cada sucursal tiene su base: aquí solo se guarda el
/// lado de esta.
/// <list type="bullet">
/// <item><b>La que lo tramita</b> arma la lista (un borrador que se guarda completo cada vez) y la exporta: ahí sale su mercancía
/// (un movimiento «Traspaso (salida)» por producto, a su costo promedio), queda el archivo con su huella y ya no se cambia. El
/// archivo se puede volver a guardar las veces que haga falta: es el mismo.</item>
/// <item><b>La que recibe</b> importa el archivo (con su huella intacta, una sola vez y nunca en la sucursal de donde salió) y
/// los productos se empatan por su código de la pieza; lo que no está en su catálogo queda «sin empatar». Nada entra todavía.
/// Quien revisa escribe lo que llegó. Si es lo que dice el archivo, lo recibe quien tiene «Traspasos entre sucursales». Si
/// cambia algo (otra cantidad, algo que no venía, otro producto), lo recibe así solo quien tiene «Traspasos: modificar lo que
/// llegó»; quien no, lo deja marcado para revisión. Al recibir entra la mercancía («Traspaso (entrada)», al costo de esta
/// sucursal: el archivo no lleva costos).</item>
/// <item>Todo queda en la bitácora: exportar, importar, marcar para revisión y recibir (con lo que cambió).</item>
/// <item>En el piloto con eleventa (<paramref name="lecturaDeEleventa"/>) el inventario se lleva allá: el traspaso es solo el
/// documento, no mueve existencias aquí, y lo dice (<see cref="EnElPiloto"/>).</item>
/// <item>No se deshace: lo equivocado se corrige con un ajuste, como «Agregar inventario».</item>
/// </list>
/// </summary>
public sealed class RegistroTraspasos(PosDbContext db, bool lecturaDeEleventa = false)
{
    /// <summary>Cuántos trae la lista, como mucho.</summary>
    public const int MaximoDeLaLista = 60;

    public const string EnElPiloto =
        "Mientras dure el piloto con eleventa, el inventario se lleva en eleventa: aquí el traspaso es solo el documento y no mueve las existencias. "
        + "Lo que sale o lo que entra se captura en eleventa.";

    private DateTime Ahora => db.Reloj.GetUtcNow().UtcDateTime;

    private string? Piloto => lecturaDeEleventa ? EnElPiloto : null;

    // ── Consultar ────────────────────────────────────────────────────────────

    /// <summary>Los últimos de la sucursal, los dos sentidos, del más reciente al más viejo; sin los borradores descartados.</summary>
    public async Task<TraspasosDto> ListaAsync(Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        await ExigirAsync(usuarioId, sucursalId, ct);
        var traspasos = await db.Traspasos.AsNoTracking().Include(t => t.Renglones)
            .Where(t => t.SucursalId == sucursalId && t.Estado != EstadoTraspaso.Descartado)
            .OrderByDescending(t => t.CreadoUtc).ThenByDescending(t => t.Id).Take(MaximoDeLaLista).ToListAsync(ct);
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        return new TraspasosDto(traspasos.Select(t =>
        {
            var (cuando, quien) = t.CerradoUtc is { } cerrado ? (cerrado, t.CerroId) : t.MarcadoUtc is { } marcado ? (marcado, t.MarcoId) : (t.CreadoUtc, t.UsuarioId);
            return new TraspasoResumenDto(t.Id, t.Sentido, t.Estado, t.OtraSucursal, t.Clave, HoraMexico.ALocal(cuando),
                usuarios.GetValueOrDefault(quien ?? t.UsuarioId, "?"), t.Renglones.Count,
                t.Sentido == SentidoTraspaso.Entrada ? t.Renglones.Count(r => r.Cambio) : 0);
        }).ToList(), await PuedeModificarAsync(usuarioId, sucursalId, ct), Piloto);
    }

    public async Task<TraspasoDto> VerAsync(Guid id, Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        await ExigirAsync(usuarioId, sucursalId, ct);
        return await DtoAsync(await DeLaSucursalAsync(id, sucursalId, tracking: false, ct), usuarioId, ct);
    }

    // ── La sucursal que lo tramita ──────────────────────────────────────────

    /// <summary>
    /// Guarda el borrador con la lista completa, como está (reemplaza la que hubiera). Con el mismo Id, el mismo borrador. Lo
    /// repetido se junta en un renglón.
    /// </summary>
    public async Task<TraspasoDto> GuardarAsync(SolicitudGuardarTraspaso s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var destino = (s.Destino ?? "").Trim();
        if (destino.Length > Traspaso.SucursalMaxima)
            throw new ArgumentException($"El nombre de la sucursal a la que va es muy largo: hasta {Traspaso.SucursalMaxima} letras.");
        var nota = Nota(s.Nota);
        var pedidos = s.Renglones.GroupBy(r => r.VarianteId).Select(g => (VarianteId: g.Key, Cantidad: new Cantidad(g.Sum(r => r.CantidadMilesimas)))).ToList();
        if (pedidos.Count > Traspaso.RenglonesMaximos)
            throw new ArgumentException($"Un traspaso lleva hasta {Traspaso.RenglonesMaximos} productos: parte la lista en dos traspasos.");
        var variantes = await VariantesAsync(pedidos.Select(p => p.VarianteId), ct);
        foreach (var (varianteId, cantidad) in pedidos)
            RevisarCantidad(variantes[varianteId], cantidad, "se mandan");

        var traspaso = await db.Traspasos.Include(t => t.Renglones).SingleOrDefaultAsync(t => t.Id == s.Id, ct);
        if (traspaso is null)
        {
            traspaso = new Traspaso
            {
                Id = s.Id, SucursalId = s.SucursalId, Sentido = SentidoTraspaso.Salida, Estado = EstadoTraspaso.Borrador, UsuarioId = s.UsuarioId, CreadoUtc = Ahora,
            };
            db.Traspasos.Add(traspaso);
        }
        else if (traspaso.SucursalId != s.SucursalId || traspaso.Sentido != SentidoTraspaso.Salida)
            throw new SolicitudDistinta("Ese traspaso no es un borrador de esta sucursal: no se guardó nada.");
        else if (traspaso.Estado != EstadoTraspaso.Borrador)
            throw new InvalidOperationException(traspaso.Estado == EstadoTraspaso.Enviado
                ? "Ese traspaso ya se exportó y su mercancía ya salió: ya no se cambia. Lo que falte va en otro traspaso."
                : "Ese traspaso se descartó: empieza uno nuevo.");
        (traspaso.OtraSucursal, traspaso.Nota) = (destino, nota);
        db.TraspasosRenglones.RemoveRange(traspaso.Renglones);
        traspaso.Renglones = pedidos.Select((p, i) => PorEnviar(traspaso.Id, i, variantes[p.VarianteId], p.Cantidad)).ToList();
        db.TraspasosRenglones.AddRange(traspaso.Renglones);
        await db.SaveChangesAsync(ct);
        return await DtoAsync(traspaso, s.UsuarioId, ct);
    }

    /// <summary>
    /// Exporta el borrador: sale la mercancía (un «Traspaso (salida)» por producto, a su costo promedio; en el piloto con eleventa,
    /// no), queda el archivo con su huella y la bitácora, todo junto. Otra vez (se perdió la respuesta), regresa el que ya se exportó.
    /// </summary>
    public async Task<TraspasoDto> ExportarAsync(Guid id, SolicitudDeTraspaso s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var traspaso = await DeLaSucursalAsync(id, s.SucursalId, tracking: true, ct);
            if (traspaso.Sentido != SentidoTraspaso.Salida)
                throw new InvalidOperationException("Ese traspaso llegó de otra sucursal: no se exporta desde aquí.");
            if (traspaso.Estado == EstadoTraspaso.Enviado)
                return await DtoAsync(traspaso, s.UsuarioId, ct);
            if (traspaso.Estado != EstadoTraspaso.Borrador)
                throw new InvalidOperationException("Ese traspaso se descartó: empieza uno nuevo.");
            if (traspaso.OtraSucursal.Length == 0)
                throw new ArgumentException("Escribe a qué sucursal va el traspaso.");
            if (traspaso.Renglones.Count == 0)
                throw new ArgumentException("La lista está vacía: escanea o busca lo que se manda.");

            var ahora = Ahora;
            var variantes = await VariantesAsync(traspaso.Renglones.Select(r => r.VarianteId!.Value), ct);
            var ultimos = lecturaDeEleventa ? new Dictionary<Guid, long>() : await UltimosCostos.DeAsync(db, variantes.Keys, ct);
            var inventario = new RegistroInventario(db);
            long costo = 0;
            var renglones = new List<RenglonDeArchivo>();
            foreach (var renglon in traspaso.Renglones.OrderBy(r => r.Orden))
            {
                var variante = variantes[renglon.VarianteId!.Value];
                RevisarCantidad(variante, renglon.Enviada, "se mandan");
                // Como está el producto al exportar: así lo dice el archivo.
                (renglon.Codigo, renglon.Nombre, renglon.Unidad) = (RegistroEtiquetas.Codigo(variante), Recortado(RegistroCompras.Nombre(variante), 300), variante.UnidadBase);
                renglones.Add(new RenglonDeArchivo(Codigos(variante), renglon.Nombre, renglon.Unidad, renglon.Enviada.Milesimas));
                if (lecturaDeEleventa)
                    continue;
                var existencia = await inventario.ExistenciaAsync(s.SucursalId, variante.Id, ct);
                var salida = Kardex.Salida(s.SucursalId, variante, TipoMovimiento.TraspasoSalida, renglon.Enviada, existencia, ultimos.GetValueOrDefault(variante.Id),
                    ahora, documentoTipo: nameof(Traspaso), documentoId: traspaso.Id);
                costo -= salida.CostoCentavos;
                await inventario.AgregarAsync(salida, ct);
            }
            var sucursal = await db.Sucursales.AsNoTracking().Where(x => x.Id == s.SucursalId).Select(x => x.Nombre).SingleAsync(ct);
            var quien = await db.Usuarios.AsNoTracking().Where(u => u.Id == s.UsuarioId).Select(u => u.Nombre).SingleAsync(ct);
            var contenido = new ContenidoDeTraspaso(traspaso.Id, sucursal, traspaso.OtraSucursal, ahora, quien, traspaso.Nota, renglones);
            (traspaso.Archivo, traspaso.Huella) = (ArchivoDeTraspaso.Escribir(contenido), ArchivoDeTraspaso.Huella(contenido));
            (traspaso.Estado, traspaso.CerroId, traspaso.CerradoUtc, traspaso.MovioExistencias) = (EstadoTraspaso.Enviado, s.UsuarioId, ahora, !lecturaDeEleventa);
            new ServicioControl(db).Registrar(s.SucursalId, s.CajaId, s.UsuarioId, AccionControlada.Traspaso,
                $"Exportó el traspaso {traspaso.Clave} a «{traspaso.OtraSucursal}»: {Cuantos(traspaso.Renglones.Count)}"
                + (lecturaDeEleventa ? " (piloto con eleventa: sin mover existencias)" : ""), lecturaDeEleventa ? null : costo);
            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
            return await DtoAsync(traspaso, s.UsuarioId, ct);
        }
        catch
        {
            // Lo que se quedó a medias en el contexto no se guarda después por otro camino.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>El archivo de un traspaso ya exportado, tal como salió: para guardarlo otra vez (se perdió la memoria).</summary>
    public async Task<(string Nombre, byte[] Datos)> ArchivoAsync(Guid id, Guid sucursalId, Guid usuarioId, CancellationToken ct = default)
    {
        await ExigirAsync(usuarioId, sucursalId, ct);
        var traspaso = await DeLaSucursalAsync(id, sucursalId, tracking: false, ct);
        if (traspaso is not { Sentido: SentidoTraspaso.Salida, Estado: EstadoTraspaso.Enviado, Archivo: { } archivo })
            throw new InvalidOperationException("Ese traspaso todavía no se exporta: no tiene archivo.");
        var datos = Encoding.UTF8.GetBytes(archivo);
        var (contenido, _) = ArchivoDeTraspaso.Leer(datos);
        return (ArchivoDeTraspaso.Nombre(contenido, DateOnly.FromDateTime(HoraMexico.ALocal(contenido.Fecha))), datos);
    }

    /// <summary>Descarta un borrador: no salió nada, y ya no aparece en la lista.</summary>
    public async Task DescartarAsync(Guid id, SolicitudDeTraspaso s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var traspaso = await DeLaSucursalAsync(id, s.SucursalId, tracking: true, ct);
        if (traspaso.Estado == EstadoTraspaso.Descartado)
            return;
        if (traspaso.Estado != EstadoTraspaso.Borrador)
            throw new InvalidOperationException("Solo se descarta un borrador: lo exportado o lo que llegó ya no se quita. Lo equivocado se corrige con un ajuste.");
        traspaso.Estado = EstadoTraspaso.Descartado;
        await db.SaveChangesAsync(ct);
    }

    // ── La sucursal que recibe ──────────────────────────────────────────────

    /// <summary>
    /// Importa el archivo que llegó: lo revisa (que sea un traspaso y que su huella esté intacta), empata cada renglón con el
    /// producto de aquí por su código de la pieza y lo deja «por recibir». No entra nada todavía. El mismo archivo otra vez
    /// regresa el que ya estaba; uno que salió de esta sucursal no se importa.
    /// </summary>
    public async Task<TraspasoDto> ImportarAsync(SolicitudImportarTraspaso s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        if (string.IsNullOrEmpty(s.ContenidoBase64) || s.ContenidoBase64.Length > (ArchivoDeTraspaso.BytesMaximos + 2) / 3 * 4)
            throw new ArchivoDeTraspasoInvalido(ArchivoDeTraspaso.NoEs);
        byte[] datos;
        try
        {
            datos = Convert.FromBase64String(s.ContenidoBase64);
        }
        catch (FormatException)
        {
            throw new ArchivoDeTraspasoInvalido("El archivo no llegó completo a la caja principal: vuelve a elegirlo.");
        }
        var (contenido, huella) = ArchivoDeTraspaso.Leer(datos);

        if (await db.Traspasos.AsNoTracking().Include(t => t.Renglones).SingleOrDefaultAsync(t => t.Id == contenido.Id, ct) is { } ya)
        {
            if (ya.Sentido == SentidoTraspaso.Salida)
                throw new InvalidOperationException(
                    $"Este traspaso ({ArchivoDeTraspaso.Clave(huella)}) salió de esta sucursal: se importa en la sucursal a la que va («{contenido.Destino}»).");
            if (ya.SucursalId != s.SucursalId)
                throw new InvalidOperationException("Ese traspaso ya se importó en otra sucursal de esta base.");
            if (!string.Equals(ya.Huella, huella, StringComparison.OrdinalIgnoreCase))
                throw new ArchivoDeTraspasoInvalido(
                    $"Ya se importó un traspaso con ese mismo número, pero no dice lo mismo que este archivo (aquel es {ya.Clave}; este, {ArchivoDeTraspaso.Clave(huella)}): "
                    + "no se importó. Pregunta en la sucursal que lo mandó cuál es el bueno.");
            return await DtoAsync(ya, s.UsuarioId, ct) with { YaEstaba = true };
        }

        var empatados = await EmpatarAsync(contenido.Renglones, ct);
        var traspaso = new Traspaso
        {
            Id = contenido.Id, SucursalId = s.SucursalId, Sentido = SentidoTraspaso.Entrada, Estado = EstadoTraspaso.PorRecibir,
            OtraSucursal = Recortado(contenido.Origen.Trim(), Traspaso.SucursalMaxima), Nota = Nota(Recortado(contenido.Nota ?? "", Traspaso.NotaMaxima)),
            Huella = huella, Archivo = Encoding.UTF8.GetString(datos), UsuarioId = s.UsuarioId, CreadoUtc = Ahora,
        };
        traspaso.Renglones = contenido.Renglones.Select((r, i) => new TraspasoRenglon
        {
            TraspasoId = traspaso.Id, Orden = i, VarianteId = empatados[i], VarianteDelArchivoId = empatados[i],
            Codigo = r.Codigos.Count > 0 ? Recortado(r.Codigos[0], 100) : null, Nombre = Recortado(r.Nombre, 300), Unidad = r.Unidad,
            Enviada = new Cantidad(r.Milesimas), Recibida = new Cantidad(r.Milesimas),
        }).ToList();
        db.Traspasos.Add(traspaso);
        var sinEmpatar = empatados.Count(e => e is null);
        new ServicioControl(db).Registrar(s.SucursalId, s.CajaId, s.UsuarioId, AccionControlada.Traspaso,
            $"Importó el traspaso {traspaso.Clave} de «{traspaso.OtraSucursal}» (lo exportó {contenido.Quien}; archivo «{Recortado(s.Archivo, 120)}»): "
            + Cuantos(traspaso.Renglones.Count) + (sinEmpatar > 0 ? $", {sinEmpatar} sin empatar" : ""));
        await db.SaveChangesAsync(ct);
        return await DtoAsync(traspaso, s.UsuarioId, ct);
    }

    /// <summary>
    /// Lo que quien revisa dice de lo que llegó, y qué se hace con eso (<see cref="PasoDeRecepcion"/>): guardarlo, dejarlo marcado
    /// para revisión, o recibirlo (entra la mercancía). Recibirlo distinto de lo que dice el archivo es de quien tiene «Traspasos:
    /// modificar lo que llegó»; lo demás, de quien tiene «Traspasos entre sucursales». Recibirlo otra vez regresa el ya recibido.
    /// </summary>
    public async Task<TraspasoDto> RecibirAsync(Guid id, SolicitudRecepcion s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var traspaso = await DeLaSucursalAsync(id, s.SucursalId, tracking: true, ct);
            if (traspaso.Sentido != SentidoTraspaso.Entrada)
                throw new InvalidOperationException("Ese traspaso salió de esta sucursal: se recibe en la otra.");
            if (traspaso.Estado == EstadoTraspaso.Recibido)
                return s.Paso == PasoDeRecepcion.Recibir
                    ? await DtoAsync(traspaso, s.UsuarioId, ct)
                    : throw new InvalidOperationException("Ese traspaso ya se recibió: ya no se cambia. Lo equivocado se corrige con un ajuste.");

            var variantes = await AnotarAsync(traspaso, s.Renglones, ct);
            var cambios = traspaso.Renglones.Where(r => r.Cambio).OrderBy(r => r.Orden).ToList();
            var sinEmpatar = traspaso.Renglones.Where(r => r.VarianteId is null && r.Recibida.EsPositiva).OrderBy(r => r.Orden).ToList();
            var control = new ServicioControl(db);
            var clave = $"{traspaso.Clave} de «{traspaso.OtraSucursal}»";
            var ahora = Ahora;
            switch (s.Paso)
            {
                case PasoDeRecepcion.MandarARevision:
                    if (cambios.Count == 0 && sinEmpatar.Count == 0)
                        throw new ArgumentException("No marcaste ningún cambio: si llegó lo que dice el traspaso, recíbelo.");
                    (traspaso.Estado, traspaso.MarcoId, traspaso.MarcadoUtc) = (EstadoTraspaso.EnRevision, s.UsuarioId, ahora);
                    control.Registrar(s.SucursalId, s.CajaId, s.UsuarioId, AccionControlada.Traspaso,
                        $"Marcó para revisión el traspaso {clave}: {Resumen(cambios, sinEmpatar, variantes)}");
                    break;

                case PasoDeRecepcion.Recibir:
                    var puedeModificar = await PuedeModificarAsync(s.UsuarioId, s.SucursalId, ct);
                    if ((cambios.Count > 0 || sinEmpatar.Count > 0) && !puedeModificar)
                        throw new PermisoRequerido(AccionControlada.ModificarTraspaso, NivelPermiso.NoPermitido,
                            "Lo que marcaste no es lo que dice el traspaso: déjalo marcado para revisión. Recibirlo con cambios lo hace quien tiene el permiso "
                            + $"«{Permisos.Nombre(AccionControlada.ModificarTraspaso)}»"
                            + (await control.QuienesPuedenAsync(s.SucursalId, AccionControlada.ModificarTraspaso, ct) is { Count: > 0 } quienes
                                ? $": {Permisos.Nombres(quienes)}." : "."));
                    if (sinEmpatar.Count > 0)
                        throw new ArgumentException(
                            $"«{sinEmpatar[0].Nombre}»{(sinEmpatar.Count > 1 ? $" y {sinEmpatar.Count - 1} más" : "")} no está en el catálogo de esta sucursal: "
                            + "empátalo con su producto de aquí, o pon 0 en lo que llegó y, ya dado de alta en Productos, agrégalo con «Agregar inventario».");
                    long costo = 0;
                    if (!lecturaDeEleventa)
                    {
                        var inventario = new RegistroInventario(db);
                        var ultimos = await UltimosCostos.DeAsync(db, variantes.Keys, ct);
                        foreach (var renglon in traspaso.Renglones.Where(r => r.Recibida.EsPositiva).OrderBy(r => r.Orden))
                        {
                            var variante = variantes[renglon.VarianteId!.Value];
                            var existencia = await inventario.ExistenciaAsync(s.SucursalId, variante.Id, ct);
                            var entrada = Kardex.Entrada(s.SucursalId, variante, TipoMovimiento.TraspasoEntrada, renglon.Recibida,
                                CostoDeEntrada(existencia, renglon.Recibida, ultimos.GetValueOrDefault(variante.Id)), ahora,
                                documentoTipo: nameof(Traspaso), documentoId: traspaso.Id);
                            costo += entrada.CostoCentavos;
                            await inventario.AgregarAsync(entrada, ct);
                        }
                    }
                    (traspaso.Estado, traspaso.CerroId, traspaso.CerradoUtc, traspaso.MovioExistencias) = (EstadoTraspaso.Recibido, s.UsuarioId, ahora, !lecturaDeEleventa);
                    control.Registrar(s.SucursalId, s.CajaId, s.UsuarioId, cambios.Count > 0 ? AccionControlada.ModificarTraspaso : AccionControlada.Traspaso,
                        $"Recibió el traspaso {clave}: "
                        + (cambios.Count == 0 ? $"{Cuantos(traspaso.Renglones.Count)}, como venía" : $"con cambios. {Resumen(cambios, [], variantes)}")
                        + (lecturaDeEleventa ? " (piloto con eleventa: sin mover existencias)" : ""), lecturaDeEleventa ? null : costo);
                    break;
            }
            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
            return await DtoAsync(traspaso, s.UsuarioId, ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    // ── Apoyo ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pone en el traspaso lo que dice quien revisa: lo que llegó de cada renglón, con qué producto va y su nota; agrega lo que
    /// llegó y no venía, y quita lo agregado que quedó en cero. Los renglones del archivo que no se mandan se quedan como están.
    /// Regresa las variantes de los renglones empatados.
    /// </summary>
    private async Task<Dictionary<Guid, Variante>> AnotarAsync(Traspaso traspaso, IReadOnlyList<RenglonRecibido> recibidos, CancellationToken ct)
    {
        if (recibidos.Count(r => r.Id is null) + traspaso.Renglones.Count > Traspaso.RenglonesMaximos)
            throw new ArgumentException($"Un traspaso lleva hasta {Traspaso.RenglonesMaximos} renglones.");
        var porId = traspaso.Renglones.ToDictionary(r => r.Id);
        var orden = traspaso.Renglones.Count == 0 ? 0 : traspaso.Renglones.Max(r => r.Orden) + 1;
        foreach (var recibido in recibidos)
        {
            var cantidad = new Cantidad(recibido.RecibidaMilesimas);
            if (cantidad.Milesimas < 0)
                throw new ArgumentException("Lo que llegó no puede ser negativo: si no llegó, pon 0.");
            if (recibido.Id is { } renglonId)
            {
                if (!porId.TryGetValue(renglonId, out var renglon))
                    throw new SolicitudDistinta("Uno de los renglones ya no es de este traspaso: ábrelo otra vez.");
                if (renglon.Agregado && (!cantidad.EsPositiva || recibido.VarianteId is null))
                {
                    traspaso.Renglones.Remove(renglon);
                    db.TraspasosRenglones.Remove(renglon);
                    continue;
                }
                (renglon.VarianteId, renglon.Recibida, renglon.Nota) = (recibido.VarianteId, cantidad, Nota(recibido.Nota));
            }
            else if (recibido.VarianteId is { } varianteId && cantidad.EsPositiva)
            {
                var nuevo = new TraspasoRenglon
                {
                    TraspasoId = traspaso.Id, Orden = orden++, VarianteId = varianteId, Recibida = cantidad, Nota = Nota(recibido.Nota), Agregado = true,
                };
                traspaso.Renglones.Add(nuevo);
                db.TraspasosRenglones.Add(nuevo);
            }
        }
        var variantes = await VariantesAsync(traspaso.Renglones.Where(r => r.VarianteId is not null).Select(r => r.VarianteId!.Value), ct);
        foreach (var renglon in traspaso.Renglones.Where(r => r.VarianteId is not null))
        {
            var variante = variantes[renglon.VarianteId!.Value];
            if (renglon.Agregado)
                (renglon.Codigo, renglon.Nombre, renglon.Unidad) = (RegistroEtiquetas.Codigo(variante), Recortado(RegistroCompras.Nombre(variante), 300), variante.UnidadBase);
            if (renglon.Recibida.EsPositiva)
                RevisarCantidad(variante, renglon.Recibida, "llegaron");
        }
        return variantes;
    }

    /// <summary>
    /// El producto de aquí de cada renglón del archivo, por sus códigos de la pieza; vacío («sin empatar») si ninguno está en este
    /// catálogo, si aquí ese código es de un paquete (no de la pieza), si aquí se cuenta en otra unidad, si sus códigos caen en
    /// productos distintos o si aquí es un servicio. Quien revisa ve los dos nombres, el del archivo y el de aquí.
    /// </summary>
    private async Task<List<Guid?>> EmpatarAsync(IReadOnlyList<RenglonDeArchivo> renglones, CancellationToken ct)
    {
        var codigos = renglones.SelectMany(r => r.Codigos).Distinct().ToList();
        var deAqui = (await db.CodigosBarras.AsNoTracking().Where(c => codigos.Contains(c.Codigo))
                .Select(c => new { c.Codigo, c.Presentacion!.VarianteId, c.Presentacion.EsBase, c.Presentacion.Variante!.UnidadBase, c.Presentacion.Variante.Producto!.Tipo })
                .ToListAsync(ct))
            .ToDictionary(c => c.Codigo);
        return renglones.Select(r =>
        {
            var encontrados = r.Codigos.Where(deAqui.ContainsKey).Select(c => deAqui[c]).ToList();
            return encontrados.Count > 0 && encontrados.All(e => e.EsBase && e.UnidadBase == r.Unidad && e.Tipo == TipoProducto.Mercancia)
                && encontrados.Select(e => e.VarianteId).Distinct().Count() == 1
                    ? encontrados[0].VarianteId
                    : (Guid?)null;
        }).ToList();
    }

    /// <summary>Los códigos de la pieza, el de su etiqueta primero: con ellos se empata en la otra sucursal.</summary>
    private static List<string> Codigos(Variante v)
    {
        var principal = RegistroEtiquetas.Codigo(v);
        var todos = v.Presentaciones.Where(p => p.EsBase).SelectMany(p => p.Codigos).Select(c => c.Codigo).Where(c => c != principal).Order(StringComparer.Ordinal);
        return principal is null ? [.. todos] : [principal, .. todos];
    }

    private static TraspasoRenglon PorEnviar(Guid traspasoId, int orden, Variante v, Cantidad cantidad) => new()
    {
        TraspasoId = traspasoId, Orden = orden, VarianteId = v.Id, VarianteDelArchivoId = v.Id, Codigo = RegistroEtiquetas.Codigo(v),
        Nombre = Recortado(RegistroCompras.Nombre(v), 300), Unidad = v.UnidadBase, Enviada = cantidad, Recibida = cantidad,
    };

    /// <summary>Más de cero, hasta el tope y en piezas enteras si se cuenta por pieza.</summary>
    private static void RevisarCantidad(Variante variante, Cantidad cantidad, string que)
    {
        var nombre = RegistroCompras.Nombre(variante);
        if (!cantidad.EsPositiva)
            throw new ArgumentException($"Escribe cuántos {que} de «{nombre}»: más de cero.");
        if (cantidad > Cantidad.Piezas(EntradaInventario.CantidadMaxima))
            throw new ArgumentException($"De «{nombre}» van hasta {EntradaInventario.CantidadMaxima:N0} de una vez: revisa cuántos {que}.");
        if (!variante.AdmiteCantidad(cantidad))
            throw new ArgumentException($"«{nombre}» se cuenta en piezas enteras.");
    }

    /// <summary>Lo que entra vale el costo promedio de aquí (sin existencia, su último costo; sin ninguno, $0): el archivo no lleva costos.</summary>
    private static long CostoDeEntrada(Existencia existencia, Cantidad cantidad, long ultimoCosto) =>
        existencia.CostoPromedio is { } promedio && promedio > 0
            ? (long)Math.Round(promedio * cantidad.Milesimas / Cantidad.Escala, MidpointRounding.AwayFromZero)
            : cantidad.Importe(ultimoCosto);

    /// <summary>Lo que cambió, para la bitácora: los primeros y cuántos más.</summary>
    private static string Resumen(List<TraspasoRenglon> cambios, List<TraspasoRenglon> sinEmpatar, Dictionary<Guid, Variante> variantes)
    {
        const int Primeros = 6;
        string DeAqui(TraspasoRenglon r) => r.VarianteId is { } id && variantes.TryGetValue(id, out var v) ? RegistroCompras.Nombre(v) : r.Nombre;
        var partes = cambios.Select(r => r.Agregado
                ? $"+{r.Recibida} «{DeAqui(r)}» (no venía)"
                : (r.Recibida != r.Enviada ? $"«{r.Nombre}» {r.Enviada} → {r.Recibida}" : $"«{r.Nombre}»")
                  + (r.VarianteId != r.VarianteDelArchivoId ? r.VarianteId is null ? " (sin empatar)" : $" (va como «{DeAqui(r)}»)" : "")
                  + (r.Nota is { } nota ? $" [{nota}]" : ""))
            .Concat(sinEmpatar.Where(r => !r.Cambio).Select(r => $"«{r.Nombre}» sin empatar")).ToList();
        return string.Join("; ", partes.Take(Primeros)) + (partes.Count > Primeros ? $"; y {partes.Count - Primeros} más" : "");
    }

    private static string Cuantos(int renglones) => renglones == 1 ? "1 producto" : $"{renglones} productos";

    private static string? Nota(string? nota)
    {
        nota = nota?.Trim();
        if (nota is { Length: > Traspaso.NotaMaxima })
            throw new ArgumentException($"La nota es muy larga: hasta {Traspaso.NotaMaxima} letras.");
        return string.IsNullOrEmpty(nota) ? null : nota;
    }

    private static string Recortado(string texto, int largo) => texto.Length <= largo ? texto : texto[..largo];

    /// <summary>Quien entra tiene «Traspasos entre sucursales» en «Puede». Si no, el error dice quién lo tiene.</summary>
    private async Task ExigirAsync(Guid usuarioId, Guid sucursalId, CancellationToken ct)
    {
        var control = new ServicioControl(db);
        if ((await control.EvaluarAsync(usuarioId, sucursalId, AccionControlada.Traspaso, ct: ct)).Nivel == NivelPermiso.Permitido)
            return;
        throw new PermisoRequerido(AccionControlada.Traspaso, NivelPermiso.NoPermitido,
            Permisos.QuienLoHace(AccionControlada.Traspaso, await control.QuienesPuedenAsync(sucursalId, AccionControlada.Traspaso, ct)));
    }

    private async Task<bool> PuedeModificarAsync(Guid usuarioId, Guid sucursalId, CancellationToken ct) =>
        (await new ServicioControl(db).EvaluarAsync(usuarioId, sucursalId, AccionControlada.ModificarTraspaso, ct: ct)).Nivel == NivelPermiso.Permitido;

    private async Task<Traspaso> DeLaSucursalAsync(Guid id, Guid sucursalId, bool tracking, CancellationToken ct)
    {
        var consulta = db.Traspasos.Include(t => t.Renglones).Where(t => t.Id == id && t.SucursalId == sucursalId);
        return await (tracking ? consulta : consulta.AsNoTracking()).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Ese traspaso ya no existe en esta sucursal.");
    }

    /// <summary>Las variantes con su producto, su marca y sus códigos; todas existen y llevan inventario, o el error lo dice.</summary>
    private async Task<Dictionary<Guid, Variante>> VariantesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var pedidas = ids.Distinct().ToList();
        var variantes = await new RegistroEtiquetas(db).VariantesAsync(pedidas, tracking: false, ct);
        if (pedidas.Any(id => !variantes.ContainsKey(id)))
            throw new InvalidOperationException("Uno de los productos ya no existe: quítalo de la lista.");
        if (variantes.Values.FirstOrDefault(v => !v.Producto!.LlevaInventario) is { } servicio)
            throw new InvalidOperationException($"«{RegistroCompras.Nombre(servicio)}» es un servicio: no lleva inventario.");
        return variantes;
    }

    private async Task<TraspasoDto> DtoAsync(Traspaso t, Guid usuarioId, CancellationToken ct)
    {
        var ids = t.Renglones.Where(r => r.VarianteId is not null).Select(r => r.VarianteId!.Value).Distinct().ToList();
        var variantes = await new RegistroEtiquetas(db).VariantesAsync(ids, tracking: false, ct);
        var existencias = await db.Existencias.AsNoTracking().Where(e => e.SucursalId == t.SucursalId && ids.Contains(e.VarianteId))
            .ToDictionaryAsync(e => e.VarianteId, e => e.Cantidad, ct);
        var usuarios = await db.Usuarios.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        string? Quien(Guid? id) => id is { } x ? usuarios.GetValueOrDefault(x, "?") : null;
        var renglones = t.Renglones.OrderBy(r => r.Orden).Select(r =>
        {
            var variante = r.VarianteId is { } id ? variantes.GetValueOrDefault(id) : null;
            return new TraspasoRenglonDto(r.Id, variante?.Id, variante is null ? null : RegistroCompras.Nombre(variante), r.Nombre,
                r.Codigo, r.Unidad, r.Enviada.EnUnidades, r.Recibida.EnUnidades, r.Nota, r.Agregado, t.Sentido == SentidoTraspaso.Entrada && r.Cambio,
                variante is null ? null : existencias.GetValueOrDefault(variante.Id).EnUnidades);
        }).ToList();
        return new TraspasoDto(t.Id, t.Sentido, t.Estado, t.OtraSucursal, t.Nota, t.Clave, HoraMexico.ALocal(t.CreadoUtc), Quien(t.UsuarioId)!,
            t.CerradoUtc is { } cerrado ? HoraMexico.ALocal(cerrado) : null, Quien(t.CerroId), t.MarcadoUtc is { } marcado ? HoraMexico.ALocal(marcado) : null,
            Quien(t.MarcoId), t.MovioExistencias, await PuedeModificarAsync(usuarioId, t.SucursalId, ct), Piloto, renglones);
    }
}
