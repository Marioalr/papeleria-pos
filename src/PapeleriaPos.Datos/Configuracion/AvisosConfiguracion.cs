using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Avisos;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Datos.Configuracion;

// Los avisos de la caja (el «!» de arriba, 2026-11-28). No van en la copia de las cajas: las cajas los piden a la caja principal.
internal sealed class AvisoConfiguracion : IEntityTypeConfiguration<Aviso>
{
    public void Configure(EntityTypeBuilder<Aviso> b)
    {
        b.ToTable("Avisos");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(a => a.SucursalId);
        b.HasOne<Caja>().WithMany().HasForeignKey(a => a.CajaId);
        b.Property(a => a.Modulo).HasMaxLength(40);
        b.Property(a => a.Clave).HasMaxLength(200);
        // Uno vigente por módulo y clave en la sucursal: ponerlo otra vez lo cambia.
        b.HasIndex(a => new { a.SucursalId, a.Modulo, a.Clave }).IsUnique().HasFilter("\"RetiradoUtc\" IS NULL")
            .HasDatabaseName("IX_Avisos_Vigente");
        b.HasIndex(a => new { a.SucursalId, a.RetiradoUtc });
    }
}
