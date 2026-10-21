using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Inventario;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El servicio con la hora de la tienda fija el 17 de octubre de 2040 a mediodía (entrega 13, parte 3): corre Todos Santos y la
/// que sigue de Navidad es la del 20 de noviembre de 2040. Así las temporadas no dependen del día en que corren las pruebas. Los
/// datos de cada prueba se guardan con su propia hora (<see cref="Base"/>): el historial de precios y el kardex quedan en las
/// fechas que se dicen. Cada prueba da de alta lo suyo (su departamento, sus productos y su temporada): no se mezclan.
/// </summary>
public sealed class ServicioDeTemporadas : IAsyncLifetime
{
    public static readonly DateTimeOffset Hoy = new(2040, 10, 17, 18, 0, 0, TimeSpan.Zero);

    public static readonly DiaDelAnio Del20DeNoviembre = new(11, 20), Al1DeEnero = new(1, 1);
    public static readonly DiaDelAnio Del16DeSeptiembre = new(9, 16), Al5DeNoviembre = new(11, 5);

    private readonly ServicioDePrueba _base = new();
    public RelojFijo Reloj { get; } = new(Hoy);
    public WebApplicationFactory<Program> Fabrica { get; private set; } = null!;

    /// <summary>Un día a mediodía en la tienda.</summary>
    public static DateTimeOffset El(int anio, int mes, int dia) => new(anio, mes, dia, 18, 0, 0, TimeSpan.Zero);

    /// <summary>La base del servicio, para preparar o revisar; lo que se guarda con ella queda con la hora <paramref name="cuando"/>.</summary>
    public PosDbContext Base(DateTimeOffset? cuando = null) =>
        new(BaseDeDatos.Opciones($"Data Source={_base.Ruta};Pooling=False")) { Reloj = new RelojFijo(cuando ?? Hoy) };

    /// <summary>El mismo servicio en el piloto con eleventa (su lectura prendida, sin servidor: no lee nada).</summary>
    public WebApplicationFactory<Program> Piloto() => Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Activo", "true"));

    /// <summary>Que el catálogo en memoria del servicio ya tenga lo que se guardó directo en la base (el verificador lo busca ahí).</summary>
    public async Task CatalogoAlDiaAsync()
    {
        await using var db = Base();
        await Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.CatalogoEnMemoria>().AlDiaAsync(db);
    }

    public async Task InitializeAsync()
    {
        await _base.InitializeAsync();
        Fabrica = _base.Fabrica.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Reloj);
        }));
    }

    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        await _base.DisposeAsync();
    }

    // ── Lo de cada prueba ───────────────────────────────────────────────────

    /// <summary>Un departamento propio con margen mínimo de 30 %.</summary>
    public async Task<Guid> DepartamentoAsync()
    {
        await using var db = Base();
        var departamento = new Departamento { Nombre = $"Temporada {Guid.NewGuid():N}"[..30], MargenMinimoCentesimas = 3000 };
        db.Departamentos.Add(departamento);
        await db.SaveChangesAsync();
        return departamento.Id;
    }

    /// <summary>
    /// Un producto de una pieza con su precio de menudeo, dado de alta <paramref name="cuando"/> (así queda en su historial). Sin
    /// <paramref name="codigo"/>, no lleva código; con escalas, su mayoreo. Regresa su variante.
    /// </summary>
    public async Task<Guid> ProductoAsync(Guid departamento, string nombre, long precio, DateTimeOffset cuando, string? codigo = null,
        (long DesdePiezas, long Precio)[]? escalas = null)
    {
        await using var db = Base(cuando);
        var producto = new Producto { Nombre = nombre, DepartamentoId = departamento };
        var variante = new Variante { ProductoId = producto.Id };
        var pieza = new Presentacion { VarianteId = variante.Id, Nombre = "Pieza", EsBase = true };
        if (codigo is not null)
            pieza.Codigos.Add(new CodigoBarras { Codigo = codigo, PresentacionId = pieza.Id });
        variante.Presentaciones.Add(pieza);
        producto.Variantes.Add(variante);
        db.Productos.Add(producto);
        db.Precios.Add(new Precio { VarianteId = variante.Id, PrecioCentavos = precio });
        foreach (var (desde, deMayoreo) in escalas ?? [])
            db.Precios.Add(new Precio { VarianteId = variante.Id, CantidadMinima = Cantidad.Piezas(desde), PrecioCentavos = deMayoreo });
        await db.SaveChangesAsync();
        return variante.Id;
    }

    /// <summary>Un código de prueba que no choca con otro: tres letras y un pedazo de un Guid.</summary>
    public static string Codigo(string inicio) => $"{inicio}{Guid.NewGuid():N}"[..13];

    /// <summary>Le cambia el menudeo <paramref name="cuando"/>: queda en su historial con esa fecha.</summary>
    public async Task CambiarPrecioAsync(Guid variante, long precio, DateTimeOffset cuando)
    {
        await using var db = Base(cuando);
        var menudeo = (await db.Precios.Where(p => p.VarianteId == variante && p.PresentacionId == null).ToListAsync()).MinBy(p => p.CantidadMinima)!;
        menudeo.PrecioCentavos = precio;
        await db.SaveChangesAsync();
    }

    public async Task<long> MenudeoAsync(Guid variante)
    {
        await using var db = Base();
        return (await db.Precios.Where(p => p.VarianteId == variante && p.PresentacionId == null).ToListAsync()).MinBy(p => p.CantidadMinima)!.PrecioCentavos;
    }

    /// <summary>
    /// Un movimiento del kardex <paramref name="cuando"/>, con su existencia al día. La cantidad va positiva; un conteo, con
    /// signo (negativo si bajó).
    /// </summary>
    public async Task MovimientoAsync(Guid sucursal, Guid varianteId, TipoMovimiento tipo, int cantidad, DateTimeOffset cuando, string? documento = null)
    {
        await using var db = Base(cuando);
        var variante = await db.Variantes.SingleAsync(v => v.Id == varianteId);
        var inventario = new RegistroInventario(db);
        var fecha = cuando.UtcDateTime;
        var entra = tipo is TipoMovimiento.InventarioInicial or TipoMovimiento.Compra or TipoMovimiento.Devolucion || (tipo == TipoMovimiento.Conteo && cantidad > 0);
        var piezas = Cantidad.Piezas(Math.Abs(cantidad));
        var movimiento = entra
            ? Kardex.Entrada(sucursal, variante, tipo, piezas, piezas.Importe(1500), fecha, documentoTipo: documento)
            : Kardex.Salida(sucursal, variante, tipo, piezas, await inventario.ExistenciaAsync(sucursal, varianteId), 1500, fecha,
                motivo: tipo == TipoMovimiento.Merma ? "prueba" : null, documentoTipo: documento);
        await inventario.RegistrarAsync(movimiento);
    }

    /// <summary>
    /// Una temporada nueva con sus fechas y su «Mesa de la entrada» con esos productos, en ese orden, dada de alta por
    /// <paramref name="usuario"/> (con «Productos, precios y etiquetas»). Regresa su Id.
    /// </summary>
    public static async Task<Guid> TemporadaAsync(HttpClient http, Guid usuario, Guid sucursal, DiaDelAnio desde, DiaDelAnio hasta, params Guid[] productos)
    {
        var nombre = $"Temporada {Guid.NewGuid():N}"[..20];
        var alta = await Leer<MostradorDto>(await http.PostAsJsonAsync("api/mostrador", new SolicitudNuevoExhibidor(usuario, sucursal, nombre, TipoExhibidor.Temporada)));
        var id = alta.Exhibidores.Single(e => e.Nombre == nombre).Id;
        await Leer<MostradorDto>(await http.PutAsJsonAsync($"api/mostrador/{id}",
            new SolicitudCambiarExhibidor(usuario, sucursal, Fechas: new FechasTemporadaDto(desde, hasta))));
        var conLugar = await Leer<MostradorDto>(await http.PostAsJsonAsync($"api/mostrador/{id}/lugares", new SolicitudLugar(usuario, sucursal, "Mesa de la entrada")));
        await Leer<MostradorDto>(await http.PutAsJsonAsync("api/mostrador/acomodo", new SolicitudAcomodo(usuario, sucursal,
            [new LugarConProductosDto(conLugar.Exhibidores.Single(e => e.Id == id).Lugares.Single().Id, productos)])));
        return id;
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }
}
