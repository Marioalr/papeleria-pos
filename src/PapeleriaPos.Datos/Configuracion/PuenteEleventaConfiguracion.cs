using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos.Configuracion;

// Las tablas del puente con eleventa (solo del piloto en la caja 2): se quitan al dejar eleventa, con la bandeja. No tienen
// DbSet en PosDbContext: el puente (PapeleriaPos.Eleventa) las usa con db.Set<…>().

internal sealed class TurnoEnEleventaConfiguracion : IEntityTypeConfiguration<TurnoEnEleventa>
{
    public void Configure(EntityTypeBuilder<TurnoEnEleventa> b)
    {
        b.ToTable("TurnosEnEleventa");
        b.HasKey(t => t.TurnoId);
        b.HasOne<Turno>().WithOne().HasForeignKey<TurnoEnEleventa>(t => t.TurnoId);
        // La lectura de eleventa busca los que siguen sin su corte.
        b.HasIndex(t => t.TomadoUtc);
        b.HasIndex(t => t.TurnoEleventa);
    }
}

internal sealed class FolioRepetidoEnEleventaConfiguracion : IEntityTypeConfiguration<FolioRepetidoEnEleventa>
{
    public void Configure(EntityTypeBuilder<FolioRepetidoEnEleventa> b)
    {
        b.ToTable("FoliosRepetidosEnEleventa");
        b.HasOne<EnvioAEleventa>().WithMany().HasForeignKey(f => f.EnvioId);
        // El mismo otro ticket una sola vez por venta; la devolución busca por el folio escrito.
        b.HasIndex(f => new { f.EnvioId, f.TicketEleventa }).IsUnique();
        b.HasIndex(f => f.Folio);
        b.Property(f => f.Caja).HasMaxLength(50);
    }
}

internal sealed class DevolucionDeEleventaConfiguracion : IEntityTypeConfiguration<DevolucionDeEleventa>
{
    public void Configure(EntityTypeBuilder<DevolucionDeEleventa> b)
    {
        b.ToTable("DevolucionesDeEleventa");
        b.HasOne<Turno>().WithMany().HasForeignKey(d => d.TurnoId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(d => d.UsuarioId);
        b.HasOne<MovimientoCaja>().WithOne().HasForeignKey<DevolucionDeEleventa>(d => d.MovimientoId);
        // Lo devuelto de un ticket de eleventa, para no devolver dos veces lo que todavía no llega allá.
        b.HasIndex(d => d.TicketEleventa);
        b.Property(d => d.CajaNombre).HasMaxLength(50);
        b.Property(d => d.Motivo).HasMaxLength(500);
        // El ticket como se leyó y lo devuelto van como JSON: se guardan y se leen juntos, y no cambian.
        Json(b.Property(d => d.Ticket));
        Json(b.Property(d => d.Articulos));
    }

    private static void Json<T>(PropertyBuilder<List<T>> propiedad) =>
        propiedad.HasConversion(
            l => JsonSerializer.Serialize(l, Opciones),
            j => JsonSerializer.Deserialize<List<T>>(j, Opciones) ?? new List<T>(),
            new ValueComparer<List<T>>((x, y) => x!.SequenceEqual(y!), l => l.Aggregate(0, (h, x) => HashCode.Combine(h, x)), l => l.ToList()));

    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web);
}

internal sealed class DevolucionDesdeEleventaConfiguracion : IEntityTypeConfiguration<DevolucionDesdeEleventa>
{
    public void Configure(EntityTypeBuilder<DevolucionDesdeEleventa> b)
    {
        b.ToTable("DevolucionesDesdeEleventa");
        b.HasOne<Venta>().WithMany().HasForeignKey(d => d.VentaId);
        b.HasOne<Devolucion>().WithOne().HasForeignKey<DevolucionDesdeEleventa>(d => d.DevolucionId);
        // Cada devolución de eleventa se pasa una sola vez.
        b.HasIndex(d => new { d.DevolucionEleventa, d.DevueltaUtc }).IsUnique();
        b.HasIndex(d => d.TicketEleventa);
        b.Property(d => d.Cajero).HasMaxLength(50);
        b.Property(d => d.Caja).HasMaxLength(50);
        b.Property(d => d.PorRevisar).HasMaxLength(500);
    }
}

internal sealed class DetencionDelPuenteConfiguracion : IEntityTypeConfiguration<DetencionDelPuente>
{
    public void Configure(EntityTypeBuilder<DetencionDelPuente> b)
    {
        b.ToTable("DetencionesDelPuente");
        b.HasOne<Usuario>().WithMany().HasForeignKey(d => d.ReanudoId);
        // La bandeja y la lectura preguntan, a cada vuelta, si hay una sin reanudar.
        b.HasIndex(d => d.ReanudadoUtc);
        b.Property(d => d.Pruebas).HasMaxLength(2000);
        b.Property(d => d.Nota).HasMaxLength(500);
    }
}
