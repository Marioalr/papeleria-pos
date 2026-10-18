using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Datos.Configuracion;

// Empezar y terminar la temporada, las tarjetas de precio y lo que se pregunta en el verificador (entrega 13, parte 3). Nada
// de esto va en la copia de las cajas: lo leen y lo cambian Productos (F3) con la caja principal; la venta solo manda cada
// consulta del verificador.

internal sealed class TemporadaDelAnioConfiguracion : IEntityTypeConfiguration<TemporadaDelAnio>
{
    public void Configure(EntityTypeBuilder<TemporadaDelAnio> b)
    {
        b.ToTable("TemporadasDelAnio");
        b.HasOne<Exhibidor>().WithMany().HasForeignKey(t => t.ExhibidorId);
        b.HasOne<Sucursal>().WithMany().HasForeignKey(t => t.SucursalId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(t => t.PreciosConfirmadosPorId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(t => t.TerminadaPorId);
        b.HasMany(t => t.Renglones).WithOne().HasForeignKey(r => r.TemporadaId);
        // Una por temporada, sucursal y año (su primer día): confirmar y terminar caen en la misma.
        b.HasIndex(t => new { t.ExhibidorId, t.SucursalId, t.Inicio }).IsUnique();
    }
}

internal sealed class RenglonTemporadaDelAnioConfiguracion : IEntityTypeConfiguration<RenglonTemporadaDelAnio>
{
    public void Configure(EntityTypeBuilder<RenglonTemporadaDelAnio> b)
    {
        b.ToTable("TemporadasDelAnioRenglones");
        b.HasOne<Variante>().WithMany().HasForeignKey(r => r.VarianteId);
        b.HasIndex(r => new { r.TemporadaId, r.VarianteId }).IsUnique();
    }
}

internal sealed class ContadoDeTemporadaConfiguracion : IEntityTypeConfiguration<ContadoDeTemporada>
{
    public void Configure(EntityTypeBuilder<ContadoDeTemporada> b)
    {
        b.ToTable("TemporadasDelAnioContados");
        b.HasOne<TemporadaDelAnio>().WithMany().HasForeignKey(c => c.TemporadaId);
        b.HasOne<Variante>().WithMany().HasForeignKey(c => c.VarianteId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.UsuarioId);
        b.Ignore(c => c.Diferencia);
        // Uno por producto en cada temporada del año: contarlo otra vez lo reemplaza.
        b.HasIndex(c => new { c.TemporadaId, c.VarianteId }).IsUnique();
    }
}

internal sealed class TarjetaDePrecioConfiguracion : IEntityTypeConfiguration<TarjetaDePrecio>
{
    public void Configure(EntityTypeBuilder<TarjetaDePrecio> b)
    {
        b.ToTable("TarjetasDePrecio");
        b.Property(t => t.Codigo).HasMaxLength(TarjetaDePrecio.LargoCodigo);
        b.HasOne<Sucursal>().WithMany().HasForeignKey(t => t.SucursalId);
        b.HasOne<Variante>().WithMany().HasForeignKey(t => t.VarianteId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(t => t.ImpresaPorId);
        // Una tarjeta por producto en cada sucursal: imprimirla otra vez la pone al día.
        b.HasIndex(t => new { t.SucursalId, t.VarianteId }).IsUnique();
    }
}

internal sealed class ConsultaDePrecioConfiguracion : IEntityTypeConfiguration<ConsultaDePrecio>
{
    public void Configure(EntityTypeBuilder<ConsultaDePrecio> b)
    {
        b.ToTable("ConsultasDePrecio");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
        b.HasOne<Caja>().WithMany().HasForeignKey(c => c.CajaId);
        b.HasOne<Variante>().WithMany().HasForeignKey(c => c.VarianteId);
        // Lo más preguntado: las de una sucursal en los últimos días.
        b.HasIndex(c => new { c.SucursalId, c.FechaUtc });
    }
}
