using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Turnos;
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
