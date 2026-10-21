using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Datos;

/// <summary>
/// Lo que se señala (entrega 13, docs/propuesta-interfaz.md, «Lo que se señala: exhibidos y temporada»): los exhibidores y
/// las temporadas, con sus lugares y lo acomodado en cada lugar. Los cambia quien entró a la caja con «Productos, precios y
/// etiquetas», sin volver a escribir su PIN (aquí no se pide autorización: puede o no puede), en Configuración › Mostrador y
/// temporadas (los nombres, las fechas y los lugares) y en Ctrl+M › Acomodar (los productos). Lo de los exhibidores queda en
/// la bitácora; mover un producto de lugar, no. Nada del catálogo se toca: un exhibidor solo dice dónde está cada producto.
/// <para>
/// En el piloto con eleventa también se cambian aquí: los productos son de eleventa, pero dónde están en la tienda solo lo
/// sabe el POS. Viajan a las cajas con la copia (cambiarlos sube la versión del catálogo): Ctrl+M se pinta sin la red.
/// </para>
/// </summary>
public sealed class RegistroMostrador(PosDbContext db)
{
    private const AccionControlada Permiso = AccionControlada.EditarCatalogo;

    private const string SinPermiso = "El mostrador y las temporadas los cambia quien tiene «Productos, precios y etiquetas».";

    /// <summary>Los activos, con la versión del catálogo con que se leyeron (la caja usa lo más nuevo entre esto y su copia).</summary>
    public async Task<MostradorDto> LeerAsync(CancellationToken ct = default)
    {
        // La versión antes que la lista: si algo cambia entre las dos, la lista es más nueva que su versión, nunca más vieja.
        var version = await db.Secuencias.AsNoTracking().Where(s => s.Nombre == Secuencia.Catalogo).Select(s => s.Valor).FirstOrDefaultAsync(ct);
        return new MostradorDto(version, await ListarAsync(db, ct));
    }

    /// <summary>
    /// Los activos, como van en la copia de las cajas: primero los exhibidores y luego las temporadas, cada uno en su orden; sus
    /// lugares de arriba abajo y en cada uno sus productos de izquierda a derecha. Siempre en el mismo orden: la copia puesta al
    /// día y la completa quedan iguales.
    /// </summary>
    public static async Task<List<ExhibidorDto>> ListarAsync(PosDbContext db, CancellationToken ct = default)
    {
        var exhibidores = await db.Exhibidores.AsNoTracking().Where(e => e.Activo).ToListAsync(ct);
        var ids = exhibidores.Select(e => e.Id).ToList();
        var lugares = ids.Count == 0 ? [] : await db.LugaresExhibidor.AsNoTracking().Where(l => ids.Contains(l.ExhibidorId)).ToListAsync(ct);
        var deLosLugares = lugares.Select(l => l.Id).ToList();
        var productos = deLosLugares.Count == 0 ? [] : await db.ProductosExhibidos.AsNoTracking().Where(p => deLosLugares.Contains(p.LugarId)).ToListAsync(ct);
        var porLugar = productos.ToLookup(p => p.LugarId);
        var porExhibidor = lugares.ToLookup(l => l.ExhibidorId);
        return exhibidores
            .OrderBy(e => e.Tipo).ThenBy(e => e.Orden).ThenBy(e => e.Nombre, StringComparer.Ordinal).ThenBy(e => e.Id)
            .Select(e => new ExhibidorDto(e.Id, e.Nombre, e.Tipo, e.Desde, e.Hasta, porExhibidor[e.Id]
                .OrderBy(l => l.Orden).ThenBy(l => l.Id)
                .Select(l => new LugarExhibidorDto(l.Id, l.Nombre, porLugar[l.Id].OrderBy(p => p.Posicion).ThenBy(p => p.Id).Select(p => p.VarianteId).ToList()))
                .ToList()))
            .ToList();
    }

    /// <summary>Un exhibidor o una temporada nuevo, al final de los de su tipo y sin lugares (se le agregan aquí mismo).</summary>
    public async Task<MostradorDto> NuevoAsync(SolicitudNuevoExhibidor s, CancellationToken ct = default)
    {
        var control = await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        if (!Enum.IsDefined(s.Tipo))
            throw new ArgumentException("Elige si es un exhibidor o una temporada.");
        var nombre = Nombre(s.Nombre, Exhibidor.LargoNombre, De(s.Tipo));
        await RevisarQueNoSeRepitaAsync(nombre, null, ct);
        var orden = (await db.Exhibidores.Where(e => e.Tipo == s.Tipo).MaxAsync(e => (int?)e.Orden, ct) ?? 0) + 1;
        db.Exhibidores.Add(new Exhibidor { Nombre = nombre, Tipo = s.Tipo, Orden = orden });
        control.Registrar(s.SucursalId, null, s.UsuarioId, Permiso, $"Alta de {De(s.Tipo)} «{nombre}» (Ctrl+M)");
        await db.SaveChangesAsync(ct);
        return await LeerAsync(ct);
    }

    /// <summary>Su nombre o, de una temporada, sus fechas (las dos o ninguna): lo que no viene se queda como está.</summary>
    public async Task<MostradorDto> CambiarAsync(Guid id, SolicitudCambiarExhibidor s, CancellationToken ct = default)
    {
        var control = await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var exhibidor = await ActivoAsync(id, ct);
        var cambios = new List<string>();
        if (s.Nombre is { } texto && Nombre(texto, Exhibidor.LargoNombre, De(exhibidor.Tipo)) is var nombre && nombre != exhibidor.Nombre)
        {
            await RevisarQueNoSeRepitaAsync(nombre, id, ct);
            cambios.Add($"el nombre de «{exhibidor.Nombre}» a «{nombre}»");
            exhibidor.Nombre = nombre;
        }
        if (s.Fechas is { } fechas)
        {
            var antes = Temporadas.Fechas(exhibidor.Desde, exhibidor.Hasta);
            exhibidor.PonerFechas(fechas.Desde, fechas.Hasta);
            if (Temporadas.Fechas(exhibidor.Desde, exhibidor.Hasta) is var despues && despues != antes)
                cambios.Add($"las fechas de «{exhibidor.Nombre}»: {despues ?? "sin fechas"}");
        }
        if (cambios.Count > 0)
        {
            control.Registrar(s.SucursalId, null, s.UsuarioId, Permiso, $"Cambió {string.Join(" y ", cambios)} (Ctrl+M)");
            await db.SaveChangesAsync(ct);
        }
        return await LeerAsync(ct);
    }

    /// <summary>Quitarlo lo desactiva: ya no sale en Ctrl+M ni en la Configuración, y lo acomodado en él no se borra.</summary>
    public async Task<MostradorDto> QuitarAsync(Guid id, SolicitudMostrador s, CancellationToken ct = default)
    {
        var control = await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var exhibidor = await db.Exhibidores.SingleOrDefaultAsync(e => e.Id == id, ct) ?? throw new ArgumentException("Ese exhibidor ya no existe.");
        if (exhibidor.Activo)
        {
            exhibidor.Activo = false;
            control.Registrar(s.SucursalId, null, s.UsuarioId, Permiso, $"Quitó {De(exhibidor.Tipo)} «{exhibidor.Nombre}» (Ctrl+M)");
            await db.SaveChangesAsync(ct);
        }
        return await LeerAsync(ct);
    }

    // ── Los lugares ────────────────────────────────────────────────────────

    /// <summary>Un lugar nuevo, abajo de los que tiene.</summary>
    public async Task<MostradorDto> AgregarLugarAsync(Guid exhibidorId, SolicitudLugar s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var exhibidor = await ActivoAsync(exhibidorId, ct);
        var nombre = Nombre(s.Nombre, LugarExhibidor.LargoNombre, "el lugar");
        var lugares = await db.LugaresExhibidor.Where(l => l.ExhibidorId == exhibidorId).ToListAsync(ct);
        RevisarLugarQueNoSeRepita(exhibidor, lugares, nombre, null);
        db.LugaresExhibidor.Add(new LugarExhibidor { ExhibidorId = exhibidorId, Nombre = nombre, Orden = (lugares.Max(l => (int?)l.Orden) ?? 0) + 1 });
        await db.SaveChangesAsync(ct);
        return await LeerAsync(ct);
    }

    public async Task<MostradorDto> CambiarLugarAsync(Guid lugarId, SolicitudLugar s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var (exhibidor, lugar, lugares) = await LugarAsync(lugarId, ct);
        var nombre = Nombre(s.Nombre, LugarExhibidor.LargoNombre, "el lugar");
        if (nombre != lugar.Nombre)
        {
            RevisarLugarQueNoSeRepita(exhibidor, lugares, nombre, lugarId);
            lugar.Nombre = nombre;
            await db.SaveChangesAsync(ct);
        }
        return await LeerAsync(ct);
    }

    /// <summary>Lo sube (−1) o lo baja (+1) entre los de su exhibidor; en la orilla se queda.</summary>
    public async Task<MostradorDto> MoverLugarAsync(Guid lugarId, SolicitudMoverLugar s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var (_, lugar, lugares) = await LugarAsync(lugarId, ct);
        var i = lugares.IndexOf(lugar);
        var j = i + Math.Sign(s.Paso);
        if (j >= 0 && j < lugares.Count && j != i)
        {
            (lugares[i], lugares[j]) = (lugares[j], lugares[i]);
            Renumerar(lugares);
            await db.SaveChangesAsync(ct);
        }
        return await LeerAsync(ct);
    }

    /// <summary>
    /// Lo quita. Lo acomodado en él no se pierde: pasa al final del lugar de arriba (o al de abajo, si era el primero). Si es
    /// el único lugar y tiene productos, no se quita: no tendrían dónde quedar.
    /// </summary>
    public async Task<MostradorDto> QuitarLugarAsync(Guid lugarId, SolicitudMostrador s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        var (exhibidor, lugar, lugares) = await LugarAsync(lugarId, ct);
        var suyos = await db.ProductosExhibidos.Where(p => p.LugarId == lugarId).OrderBy(p => p.Posicion).ThenBy(p => p.Id).ToListAsync(ct);
        var i = lugares.IndexOf(lugar);
        if (suyos.Count > 0 && lugares.Count == 1)
            throw new ArgumentException($"«{lugar.Nombre}» es el único lugar de «{exhibidor.Nombre}» y tiene productos: sácalos primero (Ctrl+M › Acomodar).");
        await using (var transaccion = await db.Database.BeginTransactionAsync(ct))
        {
            if (suyos.Count > 0)
            {
                var otro = lugares[i == 0 ? 1 : i - 1];
                var enElOtro = await db.ProductosExhibidos.Where(p => p.LugarId == otro.Id).ToListAsync(ct);
                var posicion = enElOtro.Max(p => (int?)p.Posicion) ?? 0;
                foreach (var producto in suyos)
                {
                    if (enElOtro.Any(p => p.VarianteId == producto.VarianteId))
                        db.ProductosExhibidos.Remove(producto);
                    else
                        (producto.LugarId, producto.Posicion) = (otro.Id, ++posicion);
                }
                // Primero lo que pasa al otro lugar; luego, ya sin productos, el lugar se quita. Las dos cosas o ninguna.
                await db.SaveChangesAsync(ct);
            }
            lugares.RemoveAt(i);
            db.LugaresExhibidor.Remove(lugar);
            Renumerar(lugares);
            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
        }
        return await LeerAsync(ct);
    }

    // ── Lo acomodado (Ctrl+M › Acomodar) ───────────────────────────────────

    /// <summary>
    /// Cómo quedan los productos de esos lugares, en orden: los que no vienen salen de ese lugar (no del catálogo). Lo de
    /// varios lugares va junto (pasar un producto de una repisa a otra cambia las dos).
    /// </summary>
    public async Task<MostradorDto> AcomodarAsync(SolicitudAcomodo s, CancellationToken ct = default)
    {
        await ExigirAsync(s.UsuarioId, s.SucursalId, ct);
        if (s.Lugares.Count == 0)
            return await LeerAsync(ct);
        if (s.Lugares.GroupBy(l => l.LugarId).Any(g => g.Count() > 1))
            throw new ArgumentException("Un lugar viene dos veces.");
        if (s.Lugares.Any(l => l.Productos.Distinct().Count() != l.Productos.Count))
            throw new ArgumentException("Un producto va una sola vez en cada lugar.");
        var ids = s.Lugares.Select(l => l.LugarId).ToList();
        var lugares = await db.LugaresExhibidor.Include(l => l.Exhibidor).Where(l => ids.Contains(l.Id)).ToListAsync(ct);
        if (lugares.Count != ids.Count || lugares.Any(l => l.Exhibidor is not { Activo: true }))
            throw new ArgumentException("Ese lugar ya no está: cierra y vuelve a abrir el mostrador.");
        var variantes = s.Lugares.SelectMany(l => l.Productos).Distinct().ToList();
        if (await db.Variantes.CountAsync(v => variantes.Contains(v.Id), ct) != variantes.Count)
            throw new ArgumentException("Uno de esos productos ya no está en el catálogo.");

        var actuales = await db.ProductosExhibidos.Where(p => ids.Contains(p.LugarId)).ToListAsync(ct);
        foreach (var lugar in s.Lugares)
        {
            var suyos = actuales.Where(p => p.LugarId == lugar.LugarId).ToDictionary(p => p.VarianteId);
            for (var k = 0; k < lugar.Productos.Count; k++)
            {
                if (suyos.Remove(lugar.Productos[k], out var ya))
                {
                    if (ya.Posicion != k + 1)
                        ya.Posicion = k + 1;
                }
                else
                    db.ProductosExhibidos.Add(new ProductoExhibido { LugarId = lugar.LugarId, VarianteId = lugar.Productos[k], Posicion = k + 1 });
            }
            db.ProductosExhibidos.RemoveRange(suyos.Values);
        }
        await db.SaveChangesAsync(ct);
        return await LeerAsync(ct);
    }

    // ── Lo de cada cambio ───────────────────────────────────────────────────

    private async Task<ServicioControl> ExigirAsync(Guid usuarioId, Guid sucursalId, CancellationToken ct)
    {
        var control = new ServicioControl(db);
        await control.ExigirAsync(usuarioId, sucursalId, Permiso, SinPermiso, ct);
        return control;
    }

    private static string De(TipoExhibidor tipo) => tipo == TipoExhibidor.Temporada ? "la temporada" : "el exhibidor";

    /// <summary>El nombre sin espacios de más; si no trae nada o no cabe, por qué.</summary>
    private static string Nombre(string? texto, int largo, string deQue)
    {
        var nombre = Exhibidor.NombreLimpio(texto);
        if (nombre.Length == 0)
            throw new ArgumentException($"Escribe el nombre de {deQue}.");
        if (nombre.Length > largo)
            throw new ArgumentException($"El nombre de {deQue} va hasta {largo} letras.");
        return nombre;
    }

    /// <summary>Dos activos no se llaman igual (sin importar mayúsculas, acentos ni espacios de más): el chip y Ctrl+M los distinguen por su nombre.</summary>
    private async Task RevisarQueNoSeRepitaAsync(string nombre, Guid? salvo, CancellationToken ct)
    {
        var normal = Texto.Normalizar(nombre);
        var activos = await db.Exhibidores.AsNoTracking().Where(e => e.Activo && e.Id != salvo).Select(e => new { e.Nombre, e.Tipo }).ToListAsync(ct);
        if (activos.FirstOrDefault(e => Texto.Normalizar(e.Nombre) == normal) is { } igual)
            throw new ArgumentException($"Ya hay {(igual.Tipo == TipoExhibidor.Temporada ? "una temporada" : "un exhibidor")} que se llama «{igual.Nombre}».");
    }

    private static void RevisarLugarQueNoSeRepita(Exhibidor exhibidor, List<LugarExhibidor> lugares, string nombre, Guid? salvo)
    {
        var normal = Texto.Normalizar(nombre);
        if (lugares.FirstOrDefault(l => l.Id != salvo && Texto.Normalizar(l.Nombre) == normal) is { } igual)
            throw new ArgumentException($"«{exhibidor.Nombre}» ya tiene un lugar que se llama «{igual.Nombre}».");
    }

    private async Task<Exhibidor> ActivoAsync(Guid id, CancellationToken ct) =>
        await db.Exhibidores.SingleOrDefaultAsync(e => e.Id == id && e.Activo, ct) ?? throw new ArgumentException("Ese exhibidor ya no está: vuelve a abrir la Configuración.");

    /// <summary>El lugar, su exhibidor (activo) y todos los lugares de él, en su orden.</summary>
    private async Task<(Exhibidor Exhibidor, LugarExhibidor Lugar, List<LugarExhibidor> Lugares)> LugarAsync(Guid lugarId, CancellationToken ct)
    {
        var lugar = await db.LugaresExhibidor.SingleOrDefaultAsync(l => l.Id == lugarId, ct) ?? throw new ArgumentException("Ese lugar ya no está: vuelve a abrir la Configuración.");
        var exhibidor = await ActivoAsync(lugar.ExhibidorId, ct);
        var lugares = (await db.LugaresExhibidor.Where(l => l.ExhibidorId == exhibidor.Id).ToListAsync(ct)).OrderBy(l => l.Orden).ThenBy(l => l.Id).ToList();
        return (exhibidor, lugares.Single(l => l.Id == lugarId), lugares);
    }

    /// <summary>Del 1 en adelante, en el orden de la lista; solo cambia los que no están ya así.</summary>
    private static void Renumerar(List<LugarExhibidor> lugares)
    {
        for (var k = 0; k < lugares.Count; k++)
        {
            if (lugares[k].Orden != k + 1)
                lugares[k].Orden = k + 1;
        }
    }
}
