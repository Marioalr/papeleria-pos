using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos.Configuracion;

/// <summary>Las listas contadas con el celular y mandadas a una caja (docs/celular-como-caja.md, «Mandar un conteo a una caja»).</summary>
internal sealed class ConteoDelCelularConfiguracion : IEntityTypeConfiguration<ConteoDelCelular>
{
    public void Configure(EntityTypeBuilder<ConteoDelCelular> b)
    {
        b.ToTable("ConteosDelCelular");
        b.Property(c => c.Id).ValueGeneratedNever();
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
        b.HasOne<Caja>().WithMany().HasForeignKey(c => c.CelularId);
        b.HasOne<Caja>().WithMany().HasForeignKey(c => c.CajaId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.ContoId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.ResolvioId);
        b.HasMany(c => c.Renglones).WithOne().HasForeignKey(r => r.ConteoId);
        b.Property(c => c.Motivo).HasMaxLength(500);
        b.Ignore(c => c.Pendiente);
        // Lo que espera en cada caja, y lo de cada celular.
        b.HasIndex(c => new { c.CajaId, c.Estado });
        b.HasIndex(c => new { c.CelularId, c.RecibidoUtc });
    }
}

internal sealed class ConteoDelCelularRenglonConfiguracion : IEntityTypeConfiguration<ConteoDelCelularRenglon>
{
    public void Configure(EntityTypeBuilder<ConteoDelCelularRenglon> b)
    {
        b.ToTable("ConteosDelCelularRenglones");
        b.HasOne<Variante>().WithMany().HasForeignKey(r => r.VarianteId);
        b.Property(r => r.Descripcion).HasMaxLength(300);
        b.Property(r => r.Detalle).HasMaxLength(300);
        b.Ignore(r => r.Aplicado);
        // Lo que ya se aplicó de un producto (lo más nuevo gana).
        b.HasIndex(r => r.VarianteId);
    }
}
