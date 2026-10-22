using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
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

    /// <summary>Lo que se señala (entrega 13): los exhibidores y las temporadas, sus lugares y lo acomodado en ellos.</summary>
    public DbSet<Exhibidor> Exhibidores => Set<Exhibidor>();

    public DbSet<LugarExhibidor> LugaresExhibidor => Set<LugarExhibidor>();
    public DbSet<ProductoExhibido> ProductosExhibidos => Set<ProductoExhibido>();

    /// <summary>Las fotos de los productos (entrega 13, parte 2): una por variante, ya chica.</summary>
    public DbSet<FotoProducto> FotosProducto => Set<FotoProducto>();

    /// <summary>Cada temporada en cada año (entrega 13, parte 3): cuándo se confirmaron sus precios y cuándo se terminó, con su resumen.</summary>
    public DbSet<TemporadaDelAnio> TemporadasDelAnio => Set<TemporadaDelAnio>();

    public DbSet<RenglonTemporadaDelAnio> TemporadasDelAnioRenglones => Set<RenglonTemporadaDelAnio>();

    /// <summary>Lo que se contó al terminar cada temporada, con lo que decía el sistema al contarlo.</summary>
    public DbSet<ContadoDeTemporada> ContadosDeTemporada => Set<ContadoDeTemporada>();

    /// <summary>Las tarjetas de precio que se imprimieron, con el precio y el código con que salieron.</summary>
    public DbSet<TarjetaDePrecio> TarjetasDePrecio => Set<TarjetaDePrecio>();

    /// <summary>Lo que se pregunta en el verificador (F9): una por consulta.</summary>
    public DbSet<ConsultaDePrecio> ConsultasDePrecio => Set<ConsultaDePrecio>();

    /// <summary>Las promociones con fechas (fase 3): van en la copia de las cajas, con sus fechas y a qué tocan.</summary>
    public DbSet<Promocion> Promociones => Set<Promocion>();

    public DbSet<AlcancePromocion> AlcancesPromocion => Set<AlcancePromocion>();

    /// <summary>Lo quitado de lo que lleva la copia de las cajas, con su versión (entrega 5, parte 2): lo escribe la base al guardar.</summary>
    public DbSet<Borrado> Borrados => Set<Borrado>();

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
        var versiones = PrepararCambios();
        if (!versiones.Alguna)
            return base.SaveChanges(acceptAllChangesOnSuccess);
        // Lo que se lee, antes de tomar el candado de escritura.
        if (versiones.PresentacionesPorLeer is { Count: > 0 } porLeer)
            versiones.Variantes(Presentaciones.AsNoTracking().Where(p => porLeer.Contains(p.Id)).Select(p => new { p.Id, p.VarianteId })
                .AsEnumerable().Select(p => (p.Id, p.VarianteId)));
        using var propia = Database.CurrentTransaction is null ? Database.BeginTransaction() : null;
        SubirVersiones(versiones, nombre =>
        {
            using var comando = ComandoParaSubir(nombre);
            return Convert.ToInt64(comando.ExecuteScalar());
        });
        var cambios = base.SaveChanges(acceptAllChangesOnSuccess);
        propia?.Commit();
        return cambios;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AlertasTelegram.RevisarBitacora(this);
        var versiones = PrepararCambios();
        if (!versiones.Alguna)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (versiones.PresentacionesPorLeer is { Count: > 0 } porLeer)
            versiones.Variantes((await Presentaciones.AsNoTracking().Where(p => porLeer.Contains(p.Id)).Select(p => new { p.Id, p.VarianteId })
                .ToListAsync(cancellationToken)).Select(p => (p.Id, p.VarianteId)));
        await using var propia = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        var subidas = new Dictionary<string, long>();
        foreach (var nombre in versiones.PorSubir)
        {
            await using var comando = ComandoParaSubir(nombre);
            subidas[nombre] = Convert.ToInt64(await comando.ExecuteScalarAsync(cancellationToken));
        }
        SubirVersiones(versiones, nombre => subidas[nombre]);
        var cambios = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (propia is not null)
            await propia.CommitAsync(cancellationToken);
        return cambios;
    }

    /// <summary>
    /// Sube un contador de versión en la base, en la transacción abierta (que tiene el candado de escritura), y regresa el
    /// valor que quedó (<see cref="Secuencia.Subir"/>): dos cambios a la vez dan dos versiones distintas, y ninguna caja ve un
    /// cambio sin su versión. Antes la del catálogo se leía antes de la transacción y dos guardados juntos podían quedar con
    /// la misma; con los cambios de la copia, el segundo no le llegaría a una caja que ya pidió esa versión.
    /// </summary>
    private System.Data.Common.DbCommand ComandoParaSubir(string nombre)
    {
        var comando = Database.GetDbConnection().CreateCommand();
        comando.Transaction = Database.CurrentTransaction!.GetDbTransaction();
        comando.CommandText = Secuencia.Subir(nombre);
        return comando;
    }

    /// <summary>
    /// Lo que cambia los saldos que conocen las cajas: un movimiento de la cuenta, un abono (verificar o cancelar cambia lo
    /// que está por verificar) o el límite y el plazo de un cliente.
    /// </summary>
    private bool HayCambiosDeCredito() =>
        ChangeTracker.Entries<MovimientoCredito>().Any(e => e.State == EntityState.Added)
        || ChangeTracker.Entries<Abono>().Any(e => e.State is EntityState.Added or EntityState.Modified)
        || ChangeTracker.Entries<ClienteRegistrado>().Any(e => e.State == EntityState.Added && e.Entity.LimiteCreditoCentavos > 0
            || e.State == EntityState.Modified && (e.Property(c => c.LimiteCreditoCentavos).IsModified || e.Property(c => c.PlazoCreditoDias).IsModified));

    /// <summary>Qué versiones sube este guardado y qué lleva cada una (entrega 5, parte 2).</summary>
    private sealed class VersionesPorSubir
    {
        /// <summary>Lo que cambió de lo que lleva la copia de las cajas (<see cref="LoQueLlevaLaCopia"/>): sube la del catálogo.</summary>
        public List<EntityEntry> DeLaCopia { get; } = [];

        /// <summary>Lo quitado (o pasado a otro producto) de lo que lleva la copia, con la variante de la que era.</summary>
        public List<(EntityEntry Entrada, Guid? VarianteId, Guid? Presentacion)> Quitados { get; } = [];

        /// <summary>Las existencias a las que les cambió la cantidad: sube la de las existencias.</summary>
        public List<EntityEntry<Existencia>> Existencias { get; } = [];

        public bool Credito { get; init; }

        public bool Alguna => DeLaCopia.Count > 0 || Existencias.Count > 0 || Credito;

        public IEnumerable<string> PorSubir =>
            new[] { (DeLaCopia.Count > 0, Secuencia.Catalogo), (Existencias.Count > 0, Secuencia.Existencias), (Credito, Secuencia.Credito) }
                .Where(x => x.Item1).Select(x => x.Item2);

        /// <summary>Los códigos quitados cuya presentación no está en memoria: de qué variante eran se lee de la base.</summary>
        public List<Guid>? PresentacionesPorLeer => Quitados.Where(q => q.VarianteId is null && q.Presentacion is not null)
            .Select(q => q.Presentacion!.Value).Distinct().ToList() is { Count: > 0 } ids ? ids : null;

        public void Variantes(IEnumerable<(Guid Presentacion, Guid Variante)> leidas)
        {
            var deCada = leidas.ToDictionary(x => x.Presentacion, x => x.Variante);
            for (var i = 0; i < Quitados.Count; i++)
            {
                if (Quitados[i] is { VarianteId: null, Presentacion: { } p } && deCada.TryGetValue(p, out var variante))
                    Quitados[i] = Quitados[i] with { VarianteId = variante };
            }
        }
    }

    /// <summary>
    /// Lo que se revisa y se anota antes de guardar, sin leer la base: lo que solo se agrega no se toca, la hora de cada
    /// cambio versionado, y qué versiones suben. La del catálogo sube solo con lo que lleva la copia de las cajas (antes subía
    /// con cualquier cambio versionado: un PIN mal tecleado o abrir un turno obligaban a cada caja a bajar su copia completa).
    /// </summary>
    private VersionesPorSubir PrepararCambios()
    {
        var ahora = Reloj.GetUtcNow().UtcDateTime;

        ProtegerSoloAgregar<MovimientoInventario>();
        ProtegerSoloAgregar<PrecioHistorial>();
        ProtegerSoloAgregar<Bitacora>();
        ProtegerSoloAgregar<Devolucion>();
        ProtegerSoloAgregar<MovimientoCredito>();
        ProtegerSoloAgregar<Borrado>();

        var versionados = ChangeTracker.Entries<IVersionado>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        var versiones = new VersionesPorSubir { Credito = HayCambiosDeCredito() };
        foreach (var entrada in versionados.Where(LoQueLlevaLaCopia.Cuenta))
        {
            versiones.DeLaCopia.Add(entrada);
            if (LoQueLlevaLaCopia.DejaOtroProducto(entrada))
                versiones.Quitados.Add(DeQueVariante(entrada));
        }
        // La hora de cada cambio, vaya o no en la copia (el sugerido ordena los proveedores con ella).
        foreach (var entrada in versionados.Where(e => e.State is EntityState.Added or EntityState.Modified))
            entrada.Entity.ModificadoUtc = ahora;
        versiones.Existencias.AddRange(ChangeTracker.Entries<Existencia>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified && e.Property(x => x.Cantidad).IsModified));
        return versiones;
    }

    /// <summary>
    /// De qué variante era lo quitado (o lo que pasó a otra): la de antes del cambio. De un código, la de su presentación
    /// de antes; si esa presentación no está en memoria, se lee de la base al guardar.
    /// </summary>
    private (EntityEntry, Guid?, Guid?) DeQueVariante(EntityEntry entrada)
    {
        Guid? Original(string propiedad) => entrada.Property(propiedad).OriginalValue as Guid?;
        switch (entrada.Entity)
        {
            case Variante:
                return (entrada, Original(nameof(Variante.Id)), null);
            case Presentacion:
                return (entrada, Original(nameof(Presentacion.VarianteId)), null);
            case Precio:
                return (entrada, Original(nameof(Precio.VarianteId)), null);
            case CodigoBarras when Original(nameof(CodigoBarras.PresentacionId)) is { } presentacion:
                var enMemoria = ChangeTracker.Entries<Presentacion>().FirstOrDefault(p => p.Entity.Id == presentacion);
                return enMemoria is null ? (entrada, null, presentacion) : (entrada, enMemoria.Property(p => p.VarianteId).OriginalValue, null);
            default:
                return (entrada, null, null);
        }
    }

    /// <summary>
    /// Sube las versiones que tocan y se las pone a lo que cambió (en la transacción abierta): todo lo que se guarda junto
    /// lleva la misma, y los cambios de la copia de una caja son «lo posterior a su versión». También el historial de precios
    /// (entrega 5): con ella sabe el cobro si un precio cambió después de la copia. Y lo quitado, para decírselo a las cajas.
    /// </summary>
    private void SubirVersiones(VersionesPorSubir versiones, Func<string, long> subir)
    {
        var ahora = Reloj.GetUtcNow().UtcDateTime;
        long? catalogo = versiones.DeLaCopia.Count > 0 ? subir(Secuencia.Catalogo) : null;
        RegistrarCambiosDePrecio(ahora, catalogo);
        if (catalogo is { } version)
        {
            foreach (var entrada in versiones.DeLaCopia.Where(e => e.State is EntityState.Added or EntityState.Modified))
                ((IVersionado)entrada.Entity).Version = version;
            foreach (var (entrada, variante, _) in versiones.Quitados)
                Borrados.Add(new Borrado
                {
                    Entidad = entrada.Metadata.ClrType.Name, EntidadId = LlaveGuid(entrada), VarianteId = variante, Version = version, FechaUtc = ahora,
                });
        }
        if (versiones.Existencias.Count > 0)
        {
            var existencias = subir(Secuencia.Existencias);
            foreach (var entrada in versiones.Existencias)
                entrada.Entity.Version = existencias;
        }
        if (versiones.Credito)
            subir(Secuencia.Credito);
    }

    private static Guid? LlaveGuid(EntityEntry entrada) =>
        entrada.Metadata.FindPrimaryKey()?.Properties is [var unica] ? entrada.Property(unica.Name).OriginalValue as Guid? : null;

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
