namespace PapeleriaPos.Nucleo.Contratos;

// ── Los departamentos (Productos › Departamentos) ────────────────────────────
// Antes solo llegaban de eleventa (la importación y la lectura del piloto) y en el POS no había dónde manejarlos. Como en
// eleventa («Departamentos: nuevo, cambiar el nombre, eliminar»), salvo que aquí no se borran: se desactivan, como todo el
// catálogo.

/// <summary>Un departamento, con cuántos productos activos tiene.</summary>
public sealed record DepartamentoDto(Guid Id, string Nombre, bool Activo, int ProductosActivos);

/// <summary>Todos los departamentos: primero los activos, por nombre, y después los desactivados.</summary>
/// <param name="EnEleventa">
/// Mientras dure el piloto con eleventa (con su lectura prendida), por qué aquí solo se consultan: se dan de alta y se
/// cambian en eleventa y llegan con la lectura. Vacío: se manejan aquí.
/// </param>
public sealed record DepartamentosDto(IReadOnlyList<DepartamentoDto> Departamentos, string? EnEleventa = null);

/// <summary>Dar de alta un departamento o cambiarle el nombre: quien entró a la caja, con «Productos, precios y etiquetas».</summary>
public sealed record SolicitudDepartamento(Guid UsuarioId, Guid SucursalId, string Nombre);

/// <summary>Desactivar un departamento: ya no se ofrece para los productos.</summary>
/// <param name="PasarA">
/// Si todavía tiene productos activos, el departamento (activo) al que pasan en el mismo paso. Sin él, un departamento con
/// productos activos no se desactiva.
/// </param>
public sealed record SolicitudDesactivarDepartamento(Guid UsuarioId, Guid SucursalId, Guid? PasarA = null);

public sealed record SolicitudReactivarDepartamento(Guid UsuarioId, Guid SucursalId);
