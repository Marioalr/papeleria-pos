using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Datos.Configuracion;

internal sealed class MovimientoInventarioConfiguracion : IEntityTypeConfiguration<MovimientoInventario>
{
    public void Configure(EntityTypeBuilder<MovimientoInventario> b)
    {
        b.ToTable("MovimientosInventario");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(m => m.SucursalId);
        b.HasOne<Variante>().WithMany().HasForeignKey(m => m.VarianteId);
        b.HasIndex(m => new { m.SucursalId, m.VarianteId, m.FechaUtc });
        b.HasIndex(m => new { m.DocumentoTipo, m.DocumentoId });
    }
}

internal sealed class ExistenciaConfiguracion : IEntityTypeConfiguration<Existencia>
{
    public void Configure(EntityTypeBuilder<Existencia> b)
    {
        b.HasKey(e => new { e.SucursalId, e.VarianteId });
        b.HasOne<Sucursal>().WithMany().HasForeignKey(e => e.SucursalId);
        b.HasOne<Variante>().WithMany().HasForeignKey(e => e.VarianteId);
    }
}

internal sealed class VentaHistoricaConfiguracion : IEntityTypeConfiguration<VentaHistorica>
{
    public void Configure(EntityTypeBuilder<VentaHistorica> b)
    {
        b.ToTable("VentasHistoricas");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(v => v.SucursalId);
        b.HasMany(v => v.Lineas).WithOne().HasForeignKey(l => l.VentaHistoricaId);
        b.HasIndex(v => v.FechaUtc);
    }
}

internal sealed class VentaHistoricaLineaConfiguracion : IEntityTypeConfiguration<VentaHistoricaLinea>
{
    public void Configure(EntityTypeBuilder<VentaHistoricaLinea> b)
    {
        b.ToTable("VentasHistoricasLineas");
        b.HasOne<Variante>().WithMany().HasForeignKey(l => l.VarianteId);
        b.HasIndex(l => l.VarianteId);
    }
}

internal sealed class MapeoImportacionConfiguracion : IEntityTypeConfiguration<MapeoImportacion>
{
    public void Configure(EntityTypeBuilder<MapeoImportacion> b)
    {
        b.ToTable("MapeosImportacion");
        b.HasIndex(m => new { m.Origen, m.Entidad, m.IdOrigen }).IsUnique();
    }
}

internal sealed class CompraConfiguracion : IEntityTypeConfiguration<Compra>
{
    public void Configure(EntityTypeBuilder<Compra> b)
    {
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
        b.HasOne<Proveedor>().WithMany().HasForeignKey(c => c.ProveedorId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.RecibioId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.CosteoId);
        b.HasMany(c => c.Conteo).WithOne().HasForeignKey(c => c.CompraId);
        b.HasMany(c => c.Renglones).WithOne().HasForeignKey(r => r.CompraId);
        b.HasMany(c => c.Documentos).WithOne().HasForeignKey(d => d.CompraId);
        b.HasOne<OrdenCompra>().WithMany().HasForeignKey(c => c.OrdenId);
        b.HasIndex(c => new { c.SucursalId, c.Folio }).IsUnique();
        b.HasIndex(c => new { c.SucursalId, c.Estado });
        b.Ignore(c => c.FolioTexto);
        b.Ignore(c => c.TotalNotaCentavos);
    }
}

internal sealed class CompraConteoConfiguracion : IEntityTypeConfiguration<CompraConteo>
{
    public void Configure(EntityTypeBuilder<CompraConteo> b)
    {
        b.ToTable("ComprasConteo");
        b.HasOne<Variante>().WithMany().HasForeignKey(c => c.VarianteId);
        b.HasOne<Presentacion>().WithMany().HasForeignKey(c => c.PresentacionId);
    }
}

internal sealed class CompraRenglonConfiguracion : IEntityTypeConfiguration<CompraRenglon>
{
    private static readonly JsonSerializerOptions JsonOpciones = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<CompraRenglon> b)
    {
        b.ToTable("ComprasRenglones");
        b.HasOne<Variante>().WithMany().HasForeignKey(r => r.VarianteId);
        b.HasOne<Presentacion>().WithMany().HasForeignKey(r => r.PresentacionId);
        b.Ignore(r => r.Ligado);
        // El borrador de un producto nuevo vive poco (hasta que se da de alta): va como JSON en el renglón.
        b.Property(r => r.Nuevo).HasConversion(
            n => n == null ? null : JsonSerializer.Serialize(n, JsonOpciones),
            j => j == null ? null : JsonSerializer.Deserialize<ProductoNuevo>(j, JsonOpciones));
    }
}

internal sealed class CompraDocumentoConfiguracion : IEntityTypeConfiguration<CompraDocumento>
{
    public void Configure(EntityTypeBuilder<CompraDocumento> b)
    {
        b.ToTable("ComprasDocumentos");
        b.HasOne<Usuario>().WithMany().HasForeignKey(d => d.UsuarioId);
        b.HasIndex(d => d.Uuid);
    }
}

internal sealed class LigaProveedorConfiguracion : IEntityTypeConfiguration<LigaProveedor>
{
    public void Configure(EntityTypeBuilder<LigaProveedor> b)
    {
        b.ToTable("LigasProveedor");
        b.HasOne<Proveedor>().WithMany().HasForeignKey(l => l.ProveedorId);
        b.HasOne<Presentacion>().WithMany().HasForeignKey(l => l.PresentacionId);
        b.HasIndex(l => new { l.ProveedorId, l.Clave }).IsUnique();
    }
}

internal sealed class OrdenCompraConfiguracion : IEntityTypeConfiguration<OrdenCompra>
{
    public void Configure(EntityTypeBuilder<OrdenCompra> b)
    {
        b.ToTable("OrdenesCompra");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(o => o.SucursalId);
        b.HasOne<Proveedor>().WithMany().HasForeignKey(o => o.ProveedorId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(o => o.UsuarioId);
        b.HasMany(o => o.Renglones).WithOne().HasForeignKey(r => r.OrdenId);
        b.HasIndex(o => new { o.SucursalId, o.Folio }).IsUnique();
        b.HasIndex(o => new { o.SucursalId, o.Estado });
        b.Ignore(o => o.FolioTexto);
    }
}

internal sealed class OrdenRenglonConfiguracion : IEntityTypeConfiguration<OrdenRenglon>
{
    public void Configure(EntityTypeBuilder<OrdenRenglon> b)
    {
        b.ToTable("OrdenesRenglones");
        b.HasOne<Variante>().WithMany().HasForeignKey(r => r.VarianteId);
        b.HasOne<Presentacion>().WithMany().HasForeignKey(r => r.PresentacionId);
    }
}

internal sealed class AperturaPaqueteConfiguracion : IEntityTypeConfiguration<AperturaPaquete>
{
    public void Configure(EntityTypeBuilder<AperturaPaquete> b)
    {
        b.ToTable("AperturasPaquete");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(a => a.SucursalId);
        b.HasOne<Variante>().WithMany().HasForeignKey(a => a.OrigenId);
        b.HasOne<Variante>().WithMany().HasForeignKey(a => a.DestinoId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(a => a.UsuarioId);
        b.HasIndex(a => new { a.OrigenId, a.FechaUtc });
    }
}

internal sealed class AjusteInventarioConfiguracion : IEntityTypeConfiguration<AjusteInventario>
{
    public void Configure(EntityTypeBuilder<AjusteInventario> b)
    {
        b.ToTable("AjustesInventario");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(a => a.SucursalId);
        b.HasOne<Variante>().WithMany().HasForeignKey(a => a.VarianteId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(a => a.UsuarioId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(a => a.AutorizoId);
        b.HasIndex(a => new { a.SucursalId, a.FechaUtc });
    }
}

internal sealed class ConteoCiclicoConfiguracion : IEntityTypeConfiguration<ConteoCiclico>
{
    public void Configure(EntityTypeBuilder<ConteoCiclico> b)
    {
        b.ToTable("ConteosCiclicos");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
        b.HasMany(c => c.Renglones).WithOne().HasForeignKey(r => r.ConteoId);
        // Uno por sucursal y día: si dos cajas lo piden a la vez, vale el primero.
        b.HasIndex(c => new { c.SucursalId, c.Fecha }).IsUnique();
    }
}

internal sealed class ConteoCiclicoRenglonConfiguracion : IEntityTypeConfiguration<ConteoCiclicoRenglon>
{
    public void Configure(EntityTypeBuilder<ConteoCiclicoRenglon> b)
    {
        b.ToTable("ConteosCiclicosRenglones");
        b.HasOne<Variante>().WithMany().HasForeignKey(r => r.VarianteId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(r => r.ContadoPorId);
        b.Ignore(r => r.Diferencia);
    }
}

internal sealed class ConteoDepartamentoConfiguracion : IEntityTypeConfiguration<ConteoDepartamento>
{
    public void Configure(EntityTypeBuilder<ConteoDepartamento> b)
    {
        b.ToTable("ConteosDepartamento");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(c => c.SucursalId);
        b.HasOne<Departamento>().WithMany().HasForeignKey(c => c.DepartamentoId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.AbiertoPorId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.TerminadoPorId);
        b.HasMany(c => c.Capturas).WithOne().HasForeignKey(c => c.ConteoId);
        b.HasMany(c => c.Renglones).WithOne().HasForeignKey(r => r.ConteoId);
        // Un solo conteo abierto por departamento en la sucursal.
        b.HasIndex(c => new { c.SucursalId, c.DepartamentoId }).IsUnique().HasFilter($"\"Estado\" = '{EstadoConteoDepartamento.Abierto}'");
    }
}

internal sealed class CapturaConteoConfiguracion : IEntityTypeConfiguration<CapturaConteo>
{
    public void Configure(EntityTypeBuilder<CapturaConteo> b)
    {
        b.ToTable("ConteosDepartamentoCapturas");
        b.HasOne<Variante>().WithMany().HasForeignKey(c => c.VarianteId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.UsuarioId);
        b.HasOne<Usuario>().WithMany().HasForeignKey(c => c.QuitadaPorId);
        b.Ignore(c => c.Vigente);
    }
}

internal sealed class ConteoDepartamentoRenglonConfiguracion : IEntityTypeConfiguration<ConteoDepartamentoRenglon>
{
    public void Configure(EntityTypeBuilder<ConteoDepartamentoRenglon> b)
    {
        b.ToTable("ConteosDepartamentoRenglones");
        b.HasOne<Variante>().WithMany().HasForeignKey(r => r.VarianteId);
        b.Ignore(r => r.Diferencia);
    }
}

internal sealed class RenglonHojaCodigosConfiguracion : IEntityTypeConfiguration<RenglonHojaCodigos>
{
    public void Configure(EntityTypeBuilder<RenglonHojaCodigos> b)
    {
        b.ToTable("HojaCodigos");
        b.HasOne<Sucursal>().WithMany().HasForeignKey(r => r.SucursalId);
        b.HasOne<Variante>().WithMany().HasForeignKey(r => r.VarianteId);
        b.HasIndex(r => new { r.SucursalId, r.VarianteId }).IsUnique();
    }
}

internal sealed class BusquedaConfiguracion : IEntityTypeConfiguration<Busqueda>
{
    public void Configure(EntityTypeBuilder<Busqueda> b)
    {
        b.ToTable("Busquedas");
        b.HasOne<Caja>().WithMany().HasForeignKey(x => x.CajaId);
        b.HasOne<Variante>().WithMany().HasForeignKey(x => x.VarianteId);
        // «Lo elegido antes» se busca por caja y texto; lo que no se encontró, por fecha.
        b.HasIndex(x => new { x.CajaId, x.Texto });
        b.HasIndex(x => x.FechaUtc);
    }
}

internal sealed class SecuenciaConfiguracion : IEntityTypeConfiguration<Secuencia>
{
    public void Configure(EntityTypeBuilder<Secuencia> b) => b.HasKey(s => s.Nombre);
}
