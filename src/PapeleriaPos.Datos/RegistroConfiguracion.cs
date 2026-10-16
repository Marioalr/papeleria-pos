using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Configuracion;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;

namespace PapeleriaPos.Datos;

/// <summary>
/// La configuración de la tienda (la Configuración nueva, entrega 7, docs/propuesta-interfaz.md «Configuración»): lo de
/// Tienda, Venta y cobro y Efectivo y corte, y los rápidos. La lee cualquier caja; la cambia quien entró a la caja con
/// «Configuración de la tienda», sin volver a escribir su PIN (como «Usuarios y permisos»). Cada cambio se guarda al
/// momento y queda en la bitácora con lo que cambió.
/// </summary>
public sealed class RegistroConfiguracion(PosDbContext db)
{
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>La de la sucursal o, si nunca se ha guardado, la de siempre (3 fichas, sin fondo sugerido ni límite).</summary>
    public async Task<ConfiguracionSucursalDto> LeerAsync(Guid sucursalId, CancellationToken ct = default) =>
        Dto(await db.ConfiguracionesSucursal.AsNoTracking().SingleOrDefaultAsync(c => c.SucursalId == sucursalId, ct)
            ?? new ConfiguracionSucursal { SucursalId = sucursalId });

    public static ConfiguracionSucursalDto Dto(ConfiguracionSucursal c) =>
        new(c.NombreTienda, c.Direccion, c.Telefono, c.HoraApertura, c.HoraCierre, c.LimiteEfectivoCentavos, c.FondoSugeridoCentavos,
            c.UmbralCorteCentavos, c.FichasEnEspera);

    /// <summary>
    /// Deja la configuración como dice la solicitud; con <see cref="SolicitudConfiguracionSucursal.Antes"/>, solo lo que
    /// cambió quien guarda, y lo demás como está. Lo que no cambió no se toca ni va a la bitácora; si nada cambió, no se
    /// guarda nada (las cajas no vuelven a bajar su copia). Regresa toda la configuración como quedó.
    /// </summary>
    public async Task<ConfiguracionSucursalDto> GuardarAsync(Guid cajaId, SolicitudConfiguracionSucursal s, CancellationToken ct = default)
    {
        var caja = await CajaAsync(cajaId, ct);
        var control = new ServicioControl(db);
        await control.ExigirDeLaSesionAsync(s.UsuarioId, caja.SucursalId, caja.Id, AccionControlada.ConfigurarTienda, ct);
        var pedida = s.Configuracion ?? throw new ArgumentException("Falta la configuración.");
        var c = await ParaCambiarAsync(caja.SucursalId, ct);
        var nueva = Validar(s.Antes is { } antes ? Mezclar(Dto(c), antes, pedida) : pedida);

        var cambios = new List<string>();
        void Cambio<T>(string nombre, T antes, T despues, string texto, Action poner)
        {
            if (EqualityComparer<T>.Default.Equals(antes, despues))
                return;
            cambios.Add($"{nombre} {texto}");
            poner();
        }
        Cambio("Nombre de la tienda", c.NombreTienda, nueva.NombreTienda, nueva.NombreTienda is { } n ? $"«{n}»" : "vacío (el de la sucursal)",
            () => c.NombreTienda = nueva.NombreTienda);
        Cambio("Dirección", c.Direccion, nueva.Direccion, nueva.Direccion is { } d ? $"«{d}»" : "vacía", () => c.Direccion = nueva.Direccion);
        Cambio("Teléfono", c.Telefono, nueva.Telefono, nueva.Telefono is { } t ? $"«{t}»" : "vacío", () => c.Telefono = nueva.Telefono);
        Cambio("Horario", (c.HoraApertura, c.HoraCierre), (nueva.HoraApertura, nueva.HoraCierre),
            $"de {nueva.HoraApertura.ToString("HH:mm", Mexico)} a {nueva.HoraCierre.ToString("HH:mm", Mexico)}",
            () => (c.HoraApertura, c.HoraCierre) = (nueva.HoraApertura, nueva.HoraCierre));
        Cambio("Tickets en espera a la vista", c.FichasEnEspera, nueva.FichasEnEspera, nueva.FichasEnEspera.ToString(Mexico),
            () => c.FichasEnEspera = nueva.FichasEnEspera);
        Cambio("Límite de efectivo en caja", c.LimiteEfectivoCentavos, nueva.LimiteEfectivoCentavos,
            nueva.LimiteEfectivoCentavos is { } l ? Pesos(l) : "sin límite", () => c.LimiteEfectivoCentavos = nueva.LimiteEfectivoCentavos);
        Cambio("Fondo sugerido al abrir turno", c.FondoSugeridoCentavos, nueva.FondoSugeridoCentavos,
            nueva.FondoSugeridoCentavos is { } f ? Pesos(f) : "sin fondo sugerido", () => c.FondoSugeridoCentavos = nueva.FondoSugeridoCentavos);
        Cambio("Avisar si un corte no cuadra por más de", c.UmbralCorteCentavos, nueva.UmbralCorteCentavos, Pesos(nueva.UmbralCorteCentavos),
            () => c.UmbralCorteCentavos = nueva.UmbralCorteCentavos);

        if (cambios.Count > 0)
        {
            control.Registrar(caja.SucursalId, caja.Id, s.UsuarioId, AccionControlada.ConfigurarTienda, $"Configuración de la tienda: {string.Join("; ", cambios)}");
            await db.SaveChangesAsync(ct);
        }
        return Dto(c);
    }

    /// <summary>
    /// Los rápidos, en su orden (Alt+1 a Alt+6), como deben quedar: hasta 6, cada uno con su nombre (el del botón), un
    /// producto o servicio del catálogo y cuántos vende. Sin ninguno, la fila de la venta queda sin rápidos.
    /// </summary>
    public async Task<List<Rapido>> GuardarRapidosAsync(Guid cajaId, SolicitudRapidos s, CancellationToken ct = default)
    {
        var caja = await CajaAsync(cajaId, ct);
        var control = new ServicioControl(db);
        await control.ExigirDeLaSesionAsync(s.UsuarioId, caja.SucursalId, caja.Id, AccionControlada.ConfigurarTienda, ct);
        var rapidos = await ValidarAsync(s.Rapidos ?? [], ct);
        var c = await ParaCambiarAsync(caja.SucursalId, ct);
        if (c.Rapidos is null || !c.Rapidos.SequenceEqual(rapidos))
        {
            // Sin ninguno se guarda vacío (no «sin configurar»): preparar-caja no vuelve a poner los de la prueba.
            c.Rapidos = rapidos;
            var texto = rapidos.Count == 0
                ? "ninguno"
                : string.Join("; ", rapidos.Select((r, i) => $"Alt+{i + 1} {r.Nombre}{(r.Cantidad == 1 ? "" : $" ×{r.Cantidad.ToString("0.###", Mexico)}")}"));
            control.Registrar(caja.SucursalId, caja.Id, s.UsuarioId, AccionControlada.ConfigurarTienda, $"Rápidos: {texto}");
            await db.SaveChangesAsync(ct);
        }
        return rapidos;
    }

    /// <summary>
    /// Lo que es distinto entre <paramref name="antes"/> y <paramref name="pedida"/> (lo que cambió quien guarda), sobre como
    /// está <paramref name="actual"/>: dos que cambian cosas distintas al mismo tiempo no se deshacen una a la otra.
    /// </summary>
    private static ConfiguracionSucursalDto Mezclar(ConfiguracionSucursalDto actual, ConfiguracionSucursalDto antes, ConfiguracionSucursalDto pedida)
    {
        T Toma<T>(T deAntes, T pedido, T esta) => EqualityComparer<T>.Default.Equals(deAntes, pedido) ? esta : pedido;
        return new ConfiguracionSucursalDto(
            Toma(antes.NombreTienda, pedida.NombreTienda, actual.NombreTienda),
            Toma(antes.Direccion, pedida.Direccion, actual.Direccion),
            Toma(antes.Telefono, pedida.Telefono, actual.Telefono),
            Toma(antes.HoraApertura, pedida.HoraApertura, actual.HoraApertura),
            Toma(antes.HoraCierre, pedida.HoraCierre, actual.HoraCierre),
            Toma(antes.LimiteEfectivoCentavos, pedida.LimiteEfectivoCentavos, actual.LimiteEfectivoCentavos),
            Toma(antes.FondoSugeridoCentavos, pedida.FondoSugeridoCentavos, actual.FondoSugeridoCentavos),
            Toma(antes.UmbralCorteCentavos, pedida.UmbralCorteCentavos, actual.UmbralCorteCentavos),
            Toma(antes.FichasEnEspera, pedida.FichasEnEspera, actual.FichasEnEspera));
    }

    private static ConfiguracionSucursalDto Validar(ConfiguracionSucursalDto c)
    {
        if (c.HoraCierre <= c.HoraApertura)
            throw new ArgumentException("La hora de cierre va después de la de apertura.");
        if (c.FichasEnEspera is < 0 or > ConfiguracionSucursal.FichasEnEsperaMaximo)
            throw new ArgumentException($"Se ven de 0 a {ConfiguracionSucursal.FichasEnEsperaMaximo} fichas; los demás tickets en espera van en «Más».");
        if (c.LimiteEfectivoCentavos is <= 0)
            throw new ArgumentException("El límite de efectivo debe ser mayor a cero (o vacío para no tener límite).");
        if (c.FondoSugeridoCentavos is < 0)
            throw new ArgumentException("El fondo sugerido no puede ser negativo.");
        if (c.UmbralCorteCentavos < 0)
            throw new ArgumentException("El aviso de un corte que no cuadra no puede ser negativo.");
        return c with
        {
            NombreTienda = Limpio(c.NombreTienda, ConfiguracionSucursal.LargoNombreTienda, "El nombre de la tienda"),
            Direccion = Limpio(c.Direccion, ConfiguracionSucursal.LargoDireccion, "La dirección"),
            Telefono = Limpio(c.Telefono, ConfiguracionSucursal.LargoTelefono, "El teléfono"),
        };
    }

    /// <summary>Sin espacios de más; vacío, null.</summary>
    private static string? Limpio(string? texto, int largo, string que)
    {
        var limpio = string.Join(' ', (texto ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (limpio.Length == 0)
            return null;
        return limpio.Length <= largo ? limpio : throw new ArgumentException($"{que} va hasta {largo} caracteres.");
    }

    private async Task<List<Rapido>> ValidarAsync(IReadOnlyList<Rapido> pedidos, CancellationToken ct)
    {
        if (pedidos.Count > Rapido.Maximo)
            throw new ArgumentException($"Van hasta {Rapido.Maximo} rápidos: Alt+1 a Alt+{Rapido.Maximo}.");
        var ids = pedidos.Select(r => r.VarianteId).Distinct().ToList();
        var vigentes = await db.Variantes.AsNoTracking().Where(v => ids.Contains(v.Id) && v.Activa && v.Producto!.Activo).Select(v => v.Id).ToListAsync(ct);
        var rapidos = new List<Rapido>();
        foreach (var pedido in pedidos)
        {
            var nombre = Limpio(pedido.Nombre, int.MaxValue, "") ?? throw new ArgumentException("Cada rápido lleva su nombre: es lo que dice su botón.");
            if (nombre.Length > Rapido.LargoNombre)
                throw new ArgumentException($"«{nombre}» no cabe en el botón: el nombre de un rápido va hasta {Rapido.LargoNombre} caracteres.");
            if (rapidos.Any(r => Texto.Normalizar(r.Nombre) == Texto.Normalizar(nombre)))
                throw new ArgumentException($"Ya hay un rápido que se llama «{nombre}»: ponle otro nombre.");
            if (pedido.Cantidad is <= 0 or > Rapido.CantidadMaxima || decimal.Round(pedido.Cantidad, 3) != pedido.Cantidad)
                throw new ArgumentException($"La cantidad de «{nombre}» va de 0.001 a {Rapido.CantidadMaxima.ToString("#,##0", Mexico)}.");
            if (!vigentes.Contains(pedido.VarianteId))
                throw new ArgumentException($"El producto de «{nombre}» ya no está en el catálogo: elige otro.");
            rapidos.Add(new Rapido(nombre, pedido.VarianteId, pedido.Cantidad));
        }
        return rapidos;
    }

    private async Task<Caja> CajaAsync(Guid cajaId, CancellationToken ct) =>
        await db.Cajas.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cajaId, ct) ?? throw new InvalidOperationException("La caja no existe.");

    private async Task<ConfiguracionSucursal> ParaCambiarAsync(Guid sucursalId, CancellationToken ct)
    {
        if (await db.ConfiguracionesSucursal.SingleOrDefaultAsync(c => c.SucursalId == sucursalId, ct) is { } guardada)
            return guardada;
        var nueva = new ConfiguracionSucursal { SucursalId = sucursalId };
        db.ConfiguracionesSucursal.Add(nueva);
        return nueva;
    }

    private static string Pesos(long centavos) => (centavos / 100m).ToString("$#,##0.00", Mexico);
}
