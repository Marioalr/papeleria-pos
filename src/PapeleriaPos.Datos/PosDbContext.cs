using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PapeleriaPos.Impresiones.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Historico;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Inventario;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Datos;

public class PosDbContext(DbContextOptions<PosDbContext> options) : DbContext(options)
{
    public DbSet<Sucursal> Sucursales => Set<Sucursal>();
    public DbSet<Departamento> Departamentos => Set<Departamento>();
    public DbSet<Marca> Marcas => Set<Marca>();
    public DbSet<Producto> Productos => Set<Producto>();
    public DbSet<Variante> Variantes => Set<Variante>();
    public DbSet<Presentacion> Presentaciones => Set<Presentacion>();
    public DbSet<CodigoBarras> CodigosBarras => Set<CodigoBarras>();
    public DbSet<Precio> Precios => Set<Precio>();
    public DbSet<PrecioHistorial> PreciosHistorial => Set<PrecioHistorial>();
    public DbSet<Proveedor> Proveedores => Set<Proveedor>();
    public DbSet<ProveedorVariante> ProveedoresVariantes => Set<ProveedorVariante>();
    public DbSet<MovimientoInventario> MovimientosInventario => Set<MovimientoInventario>();
    public DbSet<Existencia> Existencias => Set<Existencia>();
    public DbSet<VentaHistorica> VentasHistoricas => Set<VentaHistorica>();
    public DbSet<VentaHistoricaLinea> VentasHistoricasLineas => Set<VentaHistoricaLinea>();
    public DbSet<MapeoImportacion> MapeosImportacion => Set<MapeoImportacion>();
    public DbSet<Secuencia> Secuencias => Set<Secuencia>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Caja> Cajas => Set<Caja>();
    public DbSet<Turno> Turnos => Set<Turno>();
    public DbSet<MovimientoCaja> MovimientosCaja => Set<MovimientoCaja>();
    public DbSet<AperturaCajon> AperturasCajon => Set<AperturaCajon>();
    public DbSet<Venta> Ventas => Set<Venta>();
    public DbSet<EnvioAEleventa> EnviosAEleventa => Set<EnvioAEleventa>();
    public DbSet<ClienteRegistrado> ClientesRegistrados => Set<ClienteRegistrado>();
    public DbSet<MovimientoCredito> MovimientosCredito => Set<MovimientoCredito>();
    public DbSet<Abono> Abonos => Set<Abono>();
    public DbSet<RevisionCredito> RevisionesCredito => Set<RevisionCredito>();
    public DbSet<DecisionDepuracion> DecisionesDepuracion => Set<DecisionDepuracion>();
    public DbSet<MayoreoPorRevisar> MayoreoPorRevisar => Set<MayoreoPorRevisar>();
    public DbSet<VentaLinea> VentasLineas => Set<VentaLinea>();
    public DbSet<Cobro> Cobros => Set<Cobro>();
    public DbSet<Corte> Cortes => Set<Corte>();
    public DbSet<CorteConteo> CortesConteo => Set<CorteConteo>();
    public DbSet<ConfiguracionTicket> ConfiguracionesTicket => Set<ConfiguracionTicket>();
    public DbSet<ConfiguracionSucursal> ConfiguracionesSucursal => Set<ConfiguracionSucursal>();
    /// <summary>De antes de la entrega 1: solo la lee la migración a <see cref="PermisosUsuario"/> (<see cref="MigracionPermisos"/>).</summary>
    public DbSet<ReglaPermiso> ReglasPermiso => Set<ReglaPermiso>();
    public DbSet<PermisoUsuario> PermisosUsuario => Set<PermisoUsuario>();
    public DbSet<Autorizacion> Autorizaciones => Set<Autorizacion>();
    public DbSet<Bitacora> Bitacora => Set<Bitacora>();
    public DbSet<Devolucion> Devoluciones => Set<Devolucion>();
    public DbSet<DevolucionLinea> DevolucionesLineas => Set<DevolucionLinea>();
    public DbSet<ChatTelegram> ChatsTelegram => Set<ChatTelegram>();
    public DbSet<CodigoVinculacion> CodigosVinculacion => Set<CodigoVinculacion>();
    public DbSet<SolicitudRemota> SolicitudesRemotas => Set<SolicitudRemota>();
    public DbSet<MensajeTelegram> MensajesTelegram => Set<MensajeTelegram>();
    public DbSet<Compra> Compras => Set<Compra>();
    public DbSet<CompraConteo> ComprasConteo => Set<CompraConteo>();
    public DbSet<CompraRenglon> ComprasRenglones => Set<CompraRenglon>();
    public DbSet<CompraDocumento> ComprasDocumentos => Set<CompraDocumento>();
    public DbSet<LigaProveedor> LigasProveedor => Set<LigaProveedor>();
    public DbSet<OrdenCompra> OrdenesCompra => Set<OrdenCompra>();
    public DbSet<OrdenRenglon> OrdenesRenglones => Set<OrdenRenglon>();
    public DbSet<AperturaPaquete> AperturasPaquete => Set<AperturaPaquete>();
    public DbSet<AjusteInventario> AjustesInventario => Set<AjusteInventario>();
    public DbSet<ConteoCiclico> ConteosCiclicos => Set<ConteoCiclico>();
    public DbSet<ConteoCiclicoRenglon> ConteosCiclicosRenglones => Set<ConteoCiclicoRenglon>();
    public DbSet<ConteoDepartamento> ConteosDepartamento => Set<ConteoDepartamento>();
    public DbSet<CapturaConteo> CapturasConteo => Set<CapturaConteo>();
    public DbSet<ConteoDepartamentoRenglon> ConteosDepartamentoRenglones => Set<ConteoDepartamentoRenglon>();
    public DbSet<RenglonHojaCodigos> HojaCodigos => Set<RenglonHojaCodigos>();
    public DbSet<Busqueda> Busquedas => Set<Busqueda>();
    public DbSet<Sinonimo> Sinonimos => Set<Sinonimo>();

    /// <summary>Reloj para <see cref="IVersionado.ModificadoUtc"/> y el historial de precios; las pruebas lo fijan.</summary>
    public TimeProvider Reloj { get; init; } = TimeProvider.System;

    /// <summary>
    /// Cada conexión que abre, con <c>synchronous=FULL</c> y <c>busy_timeout</c> (<see cref="AjustesContraApagones"/>),
    /// la cree quien la cree: el servicio, las herramientas o las pruebas.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.AddInterceptors(AjustesAlAbrir.Instancia);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Cantidad>().HaveConversion<CantidadConverter>();
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcConverter>();
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Las tablas del módulo de impresión las mapea el módulo; sus llaves foráneas hacia el POS van en
        // Configuracion/ImpresionesConfiguracion.cs. No tienen DbSet aquí: el POS las usa por ImpresionesDelPos.
        ModeloImpresiones.Configurar(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PosDbContext).Assembly);

        // Nada se borra en cascada: el catálogo se desactiva y el kardex nunca se toca.
        foreach (var fk in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AlertasTelegram.RevisarBitacora(this);
        PrepararCambios(ObtenerSecuencia());
        if (!HayCambiosDeCredito())
            return base.SaveChanges(acceptAllChangesOnSuccess);
        using var propia = Database.CurrentTransaction is null ? Database.BeginTransaction() : null;
        Database.ExecuteSqlRaw(SubirVersionCredito);
        var cambios = base.SaveChanges(acceptAllChangesOnSuccess);
        propia?.Commit();
        return cambios;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AlertasTelegram.RevisarBitacora(this);
        PrepararCambios(await ObtenerSecuenciaAsync(cancellationToken));
        if (!HayCambiosDeCredito())
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await using var propia = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        await Database.ExecuteSqlRawAsync(SubirVersionCredito, cancellationToken);
        var cambios = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (propia is not null)
            await propia.CommitAsync(cancellationToken);
        return cambios;
    }

    /// <summary>
    /// La versión del crédito sube en la base, en la misma transacción que el cambio (que tiene el candado de escritura):
    /// dos cambios a la vez dan dos versiones, y ninguna caja ve el cambio sin la versión nueva.
    /// </summary>
    private const string SubirVersionCredito =
        $"INSERT INTO \"Secuencias\" (\"Nombre\", \"Valor\") VALUES ('{Secuencia.Credito}', 1) ON CONFLICT (\"Nombre\") DO UPDATE SET \"Valor\" = \"Valor\" + 1";

    /// <summary>
    /// Lo que cambia los saldos que conocen las cajas: un movimiento de la cuenta, un abono (verificar o cancelar cambia lo
    /// que está por verificar) o el límite y el plazo de un cliente.
    /// </summary>
    private bool HayCambiosDeCredito() =>
        ChangeTracker.Entries<MovimientoCredito>().Any(e => e.State == EntityState.Added)
        || ChangeTracker.Entries<Abono>().Any(e => e.State is EntityState.Added or EntityState.Modified)
        || ChangeTracker.Entries<ClienteRegistrado>().Any(e => e.State == EntityState.Added && e.Entity.LimiteCreditoCentavos > 0
            || e.State == EntityState.Modified && (e.Property(c => c.LimiteCreditoCentavos).IsModified || e.Property(c => c.PlazoCreditoDias).IsModified));

    /// <summary>También lo borrado (una escala de mayoreo que se quitó): las cajas vuelven a bajar el catálogo.</summary>
    private bool HayCambiosDeCatalogo() =>
        ChangeTracker.Entries<IVersionado>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    private Secuencia? ObtenerSecuencia() =>
        HayCambiosDeCatalogo() ? Secuencias.Find(Secuencia.Catalogo) ?? NuevaSecuencia() : null;

    private async Task<Secuencia?> ObtenerSecuenciaAsync(CancellationToken ct) =>
        HayCambiosDeCatalogo() ? await Secuencias.FindAsync([Secuencia.Catalogo], ct) ?? NuevaSecuencia() : null;

    private Secuencia NuevaSecuencia() => Secuencias.Add(new Secuencia { Nombre = Secuencia.Catalogo }).Entity;

    private void PrepararCambios(Secuencia? secuenciaCatalogo)
    {
        var ahora = Reloj.GetUtcNow().UtcDateTime;

        ProtegerSoloAgregar<MovimientoInventario>();
        ProtegerSoloAgregar<PrecioHistorial>();
        ProtegerSoloAgregar<Bitacora>();
        ProtegerSoloAgregar<Devolucion>();
        ProtegerSoloAgregar<MovimientoCredito>();
        // Todo lo que se guarda junto lleva la misma versión: la sincronización baja "lo posterior a la versión N". También el
        // historial de precios (entrega 5), aunque no se baje: con ella sabe el cobro si un precio cambió después de la copia.
        long? version = secuenciaCatalogo is null ? null : ++secuenciaCatalogo.Valor;
        RegistrarCambiosDePrecio(ahora, version);

        if (version is not { } nueva)
            return;
        foreach (var entry in ChangeTracker.Entries<IVersionado>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Version = nueva;
            entry.Entity.ModificadoUtc = ahora;
        }
    }

    private void ProtegerSoloAgregar<T>() where T : class
    {
        if (ChangeTracker.Entries<T>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException($"Los registros de {typeof(T).Name} solo se agregan: no se editan ni se borran.");
    }

    private void RegistrarCambiosDePrecio(DateTime ahora, long? version)
    {
        foreach (var entry in ChangeTracker.Entries<Precio>().ToList())
        {
            (long? anterior, long? nuevo) = entry.State switch
            {
                EntityState.Added => (null, entry.Entity.PrecioCentavos),
                EntityState.Deleted => (Original(entry), null),
                EntityState.Modified when entry.Property(p => p.PrecioCentavos).IsModified
                    && Original(entry) != entry.Entity.PrecioCentavos => (Original(entry), entry.Entity.PrecioCentavos),
                _ => ((long?)null, (long?)null),
            };
            if (anterior is null && nuevo is null)
                continue;

            PreciosHistorial.Add(new PrecioHistorial
            {
                PrecioId = entry.Entity.Id,
                VarianteId = entry.Entity.VarianteId,
                PresentacionId = entry.Entity.PresentacionId,
                SucursalId = entry.Entity.SucursalId,
                CantidadMinima = entry.Entity.CantidadMinima,
                PrecioAnteriorCentavos = anterior,
                PrecioNuevoCentavos = nuevo,
                FechaUtc = ahora,
                VersionCatalogo = version,
            });
        }

        static long Original(EntityEntry<Precio> entry) => entry.Property(p => p.PrecioCentavos).OriginalValue;
    }
}
