using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;

namespace PapeleriaPos.Datos;

/// <summary>
/// Las fotos de los productos (entrega 13, parte 2; docs/propuesta-interfaz.md, «Lo que se señala», punto 7): una por
/// variante, la que sale en Ctrl+M, en la lista de la búsqueda y en el verificador. Las sube o las quita quien entró con
/// «Productos, precios y etiquetas» (desde el celular, en la página del conteo, o desde la PC, en Productos), sin volver a
/// escribir su PIN; queda en la bitácora. Llegan ya chicas (las achica el servicio antes de guardarlas): la original nunca se
/// guarda. Viven en la misma base: el respaldo diario y el de cada corte Z las llevan.
/// <para>
/// En el piloto con eleventa también se suben aquí: los productos son de eleventa, pero sus fotos solo las tiene el POS.
/// </para>
/// <para>
/// Desde la app del celular (docs/celular-como-caja.md, paso 12; 2026-11-26): un celular dado de alta las pone y las quita con su
/// llave, como cobra y manda conteos (el alta ya pidió el PIN del dueño). Si la app manda quién entró, ese usuario necesita el
/// permiso y queda a su nombre; si no, queda a nombre del celular (<see cref="CelularDeLaFoto"/>).
/// </para>
/// </summary>
public sealed class RegistroFotos(PosDbContext db)
{
    private const AccionControlada Permiso = AccionControlada.EditarCatalogo;

    private const string SinPermiso = "Las fotos de los productos las pone y las quita quien tiene «Productos, precios y etiquetas».";

    /// <summary>
    /// De qué producto es cada foto y su huella, en el orden de las variantes (el de .NET, el mismo en cada caja): como van en
    /// la copia de las cajas. Sin las fotos: esas se bajan aparte.
    /// </summary>
    public static async Task<List<FotoDeProductoDto>> ReferenciasAsync(PosDbContext db, CancellationToken ct = default) =>
        (await db.FotosProducto.AsNoTracking().Select(f => new { f.VarianteId, f.Huella }).ToListAsync(ct))
        .OrderBy(f => f.VarianteId)
        .Select(f => new FotoDeProductoDto(f.VarianteId, f.Huella))
        .ToList();

    /// <summary>Todas (de qué producto y su huella), con la versión del catálogo con que se leyeron: para la caja sin copia.</summary>
    public async Task<FotosDto> LeerAsync(CancellationToken ct = default)
    {
        // La versión antes que la lista: si algo cambia entre las dos, la lista es más nueva que su versión, nunca más vieja.
        var version = await db.Secuencias.AsNoTracking().Where(s => s.Nombre == Secuencia.Catalogo).Select(s => s.Valor).FirstOrDefaultAsync(ct);
        return new FotosDto(version, await ReferenciasAsync(db, ct));
    }

    /// <summary>La foto de un producto, como la bajan las cajas, con su huella; vacío si no tiene.</summary>
    public async Task<(byte[] Datos, string Huella)?> FotoAsync(Guid varianteId, CancellationToken ct = default) =>
        await db.FotosProducto.AsNoTracking().Where(f => f.VarianteId == varianteId).Select(f => new { f.Datos, f.Huella }).SingleOrDefaultAsync(ct)
            is { } foto
            ? (foto.Datos, foto.Huella)
            : null;

    /// <summary>
    /// Antes de leer la foto que llegó (es lo que más cuesta): que quien la sube pueda y que el producto exista. Así nadie sin
    /// el permiso pone a trabajar al servicio con una imagen.
    /// </summary>
    public async Task RevisarAsync(Guid varianteId, Guid usuarioId, Guid sucursalId, CancellationToken ct = default, CelularDeLaFoto? celular = null)
    {
        await QuienAsync(new ServicioControl(db), usuarioId, sucursalId, celular, ct);
        await NombreAsync(varianteId, ct);
    }

    /// <summary>
    /// Quién la pone o la quita y en qué sucursal queda anotado. Desde una caja o la página del conteo: quien entró, con el permiso.
    /// Desde un celular dado de alta: su sucursal es la del celular (lo que diga la app no cuenta); quien entró en él, con el
    /// permiso, o nadie (basta su llave).
    /// </summary>
    private static async Task<(Guid SucursalId, Guid? UsuarioId)> QuienAsync(ServicioControl control, Guid usuarioId, Guid sucursalId, CelularDeLaFoto? celular,
        CancellationToken ct)
    {
        var sucursal = celular?.SucursalId ?? sucursalId;
        if (celular is not null && usuarioId == Guid.Empty)
            return (sucursal, null);
        await control.ExigirAsync(usuarioId, sucursal, Permiso, SinPermiso, ct);
        return (sucursal, usuarioId);
    }

    /// <summary>De dónde llegó, como se dice en la bitácora: «(desde el celular «Celular de Luis»)», «(desde la PC)» o nada.</summary>
    private static string Desde(string? desde, CelularDeLaFoto? celular) =>
        celular is not null ? $" (desde el celular «{celular.Nombre}»)" : string.IsNullOrWhiteSpace(desde) ? "" : $" (desde {desde.Trim()})";

    /// <summary>
    /// La foto ya chica (<paramref name="jpeg"/>) de ese producto: nueva, o en lugar de la que tenía. Si es la misma, no cambia
    /// nada (ni la versión: las cajas no bajan nada). Regresa cómo quedó, con la foto, para enseñarla enseguida.
    /// </summary>
    public async Task<FotoGuardadaDto> GuardarAsync(Guid varianteId, SolicitudFoto s, byte[] jpeg, int ancho, int alto, CancellationToken ct = default,
        CelularDeLaFoto? celular = null)
    {
        var control = new ServicioControl(db);
        var (sucursalId, usuarioId) = await QuienAsync(control, s.UsuarioId, s.SucursalId, celular, ct);
        var nombre = await NombreAsync(varianteId, ct);
        var huella = FotoProducto.HuellaDe(jpeg);
        var foto = await db.FotosProducto.SingleOrDefaultAsync(f => f.VarianteId == varianteId, ct);
        if (foto?.Huella != huella)
        {
            if (foto is null)
                db.FotosProducto.Add(new FotoProducto { VarianteId = varianteId, Datos = jpeg, Huella = huella, Ancho = ancho, Alto = alto });
            else
                (foto.Datos, foto.Huella, foto.Ancho, foto.Alto) = (jpeg, huella, ancho, alto);
            control.Registrar(sucursalId, celular?.Id, usuarioId, Permiso,
                $"{(foto is null ? "Puso" : "Cambió")} la foto de «{nombre}»{Desde(s.Desde, celular)}");
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Dos a la vez con el mismo producto nuevo: la otra ya quedó.
                throw new FotoRechazada($"Alguien más acaba de guardar la foto de «{nombre}»: vuelve a intentarlo.", "a-la-vez");
            }
        }
        return new FotoGuardadaDto(varianteId, huella, ancho, alto, jpeg.Length, Convert.ToBase64String(jpeg));
    }

    /// <summary>Quita la foto de un producto: en las cajas vuelven a salir sus iniciales. Si no tenía, no pasa nada.</summary>
    public async Task QuitarAsync(Guid varianteId, SolicitudQuitarFoto s, CancellationToken ct = default, CelularDeLaFoto? celular = null)
    {
        var control = new ServicioControl(db);
        var (sucursalId, usuarioId) = await QuienAsync(control, s.UsuarioId, s.SucursalId, celular, ct);
        if (await db.FotosProducto.SingleOrDefaultAsync(f => f.VarianteId == varianteId, ct) is not { } foto)
            return;
        db.FotosProducto.Remove(foto);
        control.Registrar(sucursalId, celular?.Id, usuarioId, Permiso, $"Quitó la foto de «{await NombreAsync(varianteId, ct)}»{Desde(null, celular)}");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>El nombre del producto, como se dice en la bitácora; si ya no está, por qué no se puede.</summary>
    private async Task<string> NombreAsync(Guid varianteId, CancellationToken ct)
    {
        var variante = await db.Variantes.AsNoTracking().Where(v => v.Id == varianteId)
            .Select(v => new { Producto = v.Producto!.Nombre, v.Descripcion })
            .SingleOrDefaultAsync(ct) ?? throw new FotoRechazada("Ese producto ya no está en el catálogo.", "no-esta");
        return string.IsNullOrWhiteSpace(variante.Descripcion) ? variante.Producto : $"{variante.Producto} {variante.Descripcion}";
    }
}

/// <summary>
/// El celular dado de alta (una caja que nunca ha abierto un turno) que pone o quita una foto con su llave: queda como la caja
/// del renglón de la bitácora, con su nombre en el texto.
/// </summary>
public sealed record CelularDeLaFoto(Guid Id, string Nombre, Guid SucursalId);

/// <summary>Una foto que no se guarda, con su código para quien la mandó (<c>no-esta</c>, <c>a-la-vez</c>).</summary>
public sealed class FotoRechazada(string mensaje, string codigo) : InvalidOperationException(mensaje)
{
    public string Codigo { get; } = codigo;
}
