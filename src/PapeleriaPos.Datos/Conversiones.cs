using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Datos;

/// <summary>Las cantidades se guardan como entero en milésimas.</summary>
internal sealed class CantidadConverter() : ValueConverter<Cantidad, long>(c => c.Milesimas, m => new Cantidad(m));

/// <summary>
/// SQLite no guarda la zona de las fechas: todas se guardan en UTC y al leerlas se marcan como UTC, para que nunca
/// se confundan con la hora local.
/// </summary>
internal sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
    f => f.Kind == DateTimeKind.Local ? f.ToUniversalTime() : DateTime.SpecifyKind(f, DateTimeKind.Utc),
    f => DateTime.SpecifyKind(f, DateTimeKind.Utc));
