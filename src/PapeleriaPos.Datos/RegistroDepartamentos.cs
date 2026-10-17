using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Datos;

/// <summary>
/// Productos (F3) › Departamentos: darlos de alta, cambiarles el nombre, desactivarlos y reactivarlos, como en eleventa
/// («Departamentos: nuevo, cambiar el nombre, eliminar»), salvo que aquí nada se borra: el catálogo se desactiva. Lo hace
/// quien entró a la caja con «Productos, precios y etiquetas», sin volver a escribir su PIN, y cada cambio queda en la
/// bitácora. Un nombre no se repite, sin importar mayúsculas, acentos ni espacios de más (tampoco el de uno desactivado:
/// ese se reactiva). Como el resto del catálogo, llevan su versión (<see cref="Nucleo.IVersionado"/>): la pone la base al
/// guardar.
/// <para>
/// Un departamento no se desactiva con productos activos adentro: en el mismo paso pasan a otro departamento activo, el
/// que elige quien lo desactiva. Si se quedaran ahí, ya no saldrían al filtrar la lista por departamento, en el conteo
/// completo, en las etiquetas por departamento ni en el margen mínimo de Configuración › Inventario, y la ficha los
/// guardaría en un departamento que ya no se ofrece. Los productos inactivos se quedan donde estaban, con su historia.
/// </para>
/// <para>
/// Mientras dure el piloto con eleventa (<paramref name="lecturaDeEleventa"/>: su lectura prendida), aquí solo se
/// consultan: se dan de alta y se cambian en eleventa (decisión del dueño, 2026-10-10: las altas y los precios, en
/// eleventa) y llegan con la lectura, que les pone el nombre y si están activos como allá; un cambio hecho aquí lo
/// regresaría la siguiente lectura. Solo para convivir con eleventa: se quita al dejarlo.
/// </para>
/// </summary>
public sealed class RegistroDepartamentos(PosDbContext db, bool lecturaDeEleventa = false)
{
    /// <summary>Por qué en el piloto aquí solo se consultan: lo dice la página y, si se intenta un cambio, el servicio.</summary>
    public const string SeCambianEnEleventa =
        "Mientras dure el piloto con eleventa, los departamentos se dan de alta y se cambian en eleventa: llegan aquí con su lectura, cada pocos minutos.";

    /// <summary>Todos, con cuántos productos activos tiene cada uno: primero los activos, por nombre, y luego los desactivados.</summary>
    public async Task<DepartamentosDto> ListarAsync(CancellationToken ct = default)
    {
        var departamentos = await db.Departamentos.AsNoTracking().ToListAsync(ct);
        var activos = await db.Productos.AsNoTracking().Where(p => p.Activo).GroupBy(p => p.DepartamentoId)
            .Select(g => new { DepartamentoId = g.Key, Cuantos = g.Count() }).ToDictionaryAsync(x => x.DepartamentoId, x => x.Cuantos, ct);
        return new DepartamentosDto(
            departamentos.OrderByDescending(d => d.Activo).ThenBy(d => d.Nombre, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => new DepartamentoDto(d.Id, d.Nombre, d.Activo, activos.GetValueOrDefault(d.Id))).ToList(),
            lecturaDeEleventa ? SeCambianEnEleventa : null);
    }

    /// <summary>Uno nuevo, activo y con el margen mínimo general (el suyo se pone en Configuración › Inventario).</summary>
    public async Task<DepartamentosDto> DarDeAltaAsync(SolicitudDepartamento s, CancellationToken ct = default)
    {
        var control = await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var nombre = Nombre(s.Nombre);
        await RevisarQueNoSeRepitaAsync(nombre, null, ct);
        db.Departamentos.Add(new Departamento { Nombre = nombre });
        control.Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.EditarCatalogo, $"Alta del departamento «{nombre}»");
        await db.SaveChangesAsync(ct);
        return await ListarAsync(ct);
    }

    public async Task<DepartamentosDto> CambiarNombreAsync(Guid id, SolicitudDepartamento s, CancellationToken ct = default)
    {
        var control = await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var departamento = await DepartamentoAsync(id, ct);
        var nombre = Nombre(s.Nombre);
        if (nombre != departamento.Nombre)
        {
            await RevisarQueNoSeRepitaAsync(nombre, departamento.Id, ct);
            control.Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.EditarCatalogo,
                $"Cambió el nombre del departamento «{departamento.Nombre}» a «{nombre}»");
            departamento.Nombre = nombre;
            await db.SaveChangesAsync(ct);
        }
        return await ListarAsync(ct);
    }

    /// <summary>
    /// Ya no se ofrece para los productos. Si tiene productos activos, pasan antes a <see cref="SolicitudDesactivarDepartamento.PasarA"/>;
    /// sin él, no se desactiva. Tampoco mientras se cuenta completo (su conteo abierto ya no se vería).
    /// </summary>
    public async Task<DepartamentosDto> DesactivarAsync(Guid id, SolicitudDesactivarDepartamento s, CancellationToken ct = default)
    {
        var control = await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var departamento = await DepartamentoAsync(id, ct);
        if (!departamento.Activo)
            return await ListarAsync(ct);
        if (await db.ConteosDepartamento.AnyAsync(c => c.DepartamentoId == id && c.Estado == EstadoConteoDepartamento.Abierto, ct))
            throw new InvalidOperationException(
                $"«{departamento.Nombre}» se está contando (Inventario › Conteo completo): cierra o cancela ese conteo antes de desactivarlo.");

        var productos = await db.Productos.Where(p => p.DepartamentoId == id && p.Activo).ToListAsync(ct);
        var detalle = $"Desactivó el departamento «{departamento.Nombre}»";
        if (productos.Count > 0)
        {
            var uno = productos.Count == 1;
            if (s.PasarA is not { } pasarA)
                throw new InvalidOperationException(uno
                    ? $"«{departamento.Nombre}» tiene 1 producto activo: elige a qué departamento pasarlo para desactivarlo."
                    : $"«{departamento.Nombre}» tiene {productos.Count} productos activos: elige a qué departamento pasarlos para desactivarlo.");
            if (pasarA == id || await db.Departamentos.FindAsync([pasarA], ct) is not { Activo: true } destino)
                throw new ArgumentException("Elige otro departamento, uno activo, para pasar sus productos.");
            foreach (var producto in productos)
                producto.DepartamentoId = destino.Id;
            detalle += uno ? $"; su producto activo pasó a «{destino.Nombre}»" : $"; sus {productos.Count} productos activos pasaron a «{destino.Nombre}»";
        }
        departamento.Activo = false;
        control.Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.EditarCatalogo, detalle);
        await db.SaveChangesAsync(ct);
        return await ListarAsync(ct);
    }

    /// <summary>Se vuelve a ofrecer, con su nombre y su margen mínimo de antes.</summary>
    public async Task<DepartamentosDto> ReactivarAsync(Guid id, SolicitudReactivarDepartamento s, CancellationToken ct = default)
    {
        var control = await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var departamento = await DepartamentoAsync(id, ct);
        if (departamento.Activo)
            return await ListarAsync(ct);
        // Los que llegaron de eleventa pueden repetir un nombre: que no queden dos activos que se llamen igual.
        if ((await OtrosAsync(id, ct)).FirstOrDefault(d => d.Activo && Departamento.MismoNombre(d.Nombre, departamento.Nombre)) is { } igual)
            throw new InvalidOperationException($"Ya hay otro departamento activo «{igual.Nombre}»: cámbiale el nombre a uno de los dos para reactivar este.");
        departamento.Activo = true;
        control.Registrar(s.SucursalId, null, s.UsuarioId, AccionControlada.EditarCatalogo, $"Reactivó el departamento «{departamento.Nombre}»");
        await db.SaveChangesAsync(ct);
        return await ListarAsync(ct);
    }

    /// <summary>
    /// Quien entró a la caja, con «Productos, precios y etiquetas» en «Puede» (si no, el error dice quién lo tiene y el
    /// intento queda en la bitácora); y fuera del piloto con eleventa.
    /// </summary>
    private async Task<ServicioControl> ExigirAsync(Guid usuarioId, Guid sucursalId, CancellationToken ct)
    {
        var control = new ServicioControl(db);
        await control.ExigirDeLaSesionAsync(usuarioId, sucursalId, null, AccionControlada.EditarCatalogo, ct);
        if (lecturaDeEleventa)
            throw new InvalidOperationException(SeCambianEnEleventa);
        return control;
    }

    private async Task<Departamento> DepartamentoAsync(Guid id, CancellationToken ct) =>
        await db.Departamentos.FindAsync([id], ct) ?? throw new InvalidOperationException("Ese departamento ya no existe.");

    /// <summary>Todos menos ese (son pocos: se comparan aquí, sin acentos).</summary>
    private async Task<List<Departamento>> OtrosAsync(Guid? id, CancellationToken ct) =>
        (await db.Departamentos.AsNoTracking().ToListAsync(ct)).Where(d => d.Id != id).ToList();

    /// <summary>Sin espacios de más; ni vacío ni más largo que <see cref="Departamento.LargoNombre"/>.</summary>
    private static string Nombre(string? nombre)
    {
        var limpio = Departamento.NombreLimpio(nombre);
        if (limpio.Length == 0)
            throw new ArgumentException("Escribe el nombre del departamento.");
        if (limpio.Length > Departamento.LargoNombre)
            throw new ArgumentException($"El nombre del departamento va de hasta {Departamento.LargoNombre} letras.");
        return limpio;
    }

    /// <summary>Que no haya otro que se llame igual, activo o no: uno desactivado se reactiva, no se repite.</summary>
    private async Task RevisarQueNoSeRepitaAsync(string nombre, Guid? id, CancellationToken ct)
    {
        if ((await OtrosAsync(id, ct)).FirstOrDefault(d => Departamento.MismoNombre(d.Nombre, nombre)) is not { } igual)
            return;
        throw new InvalidOperationException(igual.Activo
            ? $"Ya hay un departamento «{igual.Nombre}»."
            : $"Ya hay un departamento «{igual.Nombre}», desactivado: reactívalo en lugar de repetirlo.");
    }
}
