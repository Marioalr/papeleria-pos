using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// La foto del catálogo de la copia de la caja (entrega 5): un diccionario por cada código, con su versión, que se arma una
/// vez y no cambia; lo que llega después de la caja principal va en una foto nueva, encima.
/// </summary>
public sealed class FotoCatalogoTests
{
    private static readonly PresentacionDto Pieza = new(Ids.Nuevo(), "Pieza", 1000, true);
    private static readonly PresentacionDto Caja12 = new(Ids.Nuevo(), "Caja 12", 12000, false);

    /// <summary>Por pieza a $10.00, desde 12 a $8.00, y la caja de 12 a $90.00.</summary>
    private static readonly ProductoVentaDto Lapicero = new(Ids.Nuevo(), "Lapicero azul", "750100", Impuesto.Iva16, UnidadBase.Pieza, true, 40, Pieza,
        [Pieza, Caja12],
        [
            new PrecioDto(Ids.Nuevo(), null, null, 1000, 1000),
            new PrecioDto(Ids.Nuevo(), null, null, 12000, 800),
            new PrecioDto(Ids.Nuevo(), Caja12.Id, null, 1000, 9000),
        ]);

    private static readonly PresentacionDto PiezaCuaderno = new(Ids.Nuevo(), "Pieza", 1000, true);

    /// <summary>Uno con su propia versión (llegó así en la copia).</summary>
    private static readonly ProductoVentaDto Cuaderno = new(Ids.Nuevo(), "Cuaderno raya", "750200", Impuesto.Iva16, UnidadBase.Pieza, true, 5, PiezaCuaderno,
        [PiezaCuaderno], [new PrecioDto(Ids.Nuevo(), null, null, 1000, 3500)], Version: 7);

    private static PaqueteContingenciaDto Paquete(long version, params ProductoVentaDto[] productos) =>
        new(version, new CajaDto(Ids.Nuevo(), "Caja 1", "A", Ids.Nuevo(), "Matriz"), productos,
        [
            new CodigoLocalDto("750100", Lapicero.VarianteId, Pieza.Id),
            new CodigoLocalDto("7501000000012", Lapicero.VarianteId, Caja12.Id),
            // Otro código de la misma pieza (el de la marca).
            new CodigoLocalDto("LAP-AZ", Lapicero.VarianteId, Pieza.Id),
            new CodigoLocalDto("750200", Cuaderno.VarianteId, PiezaCuaderno.Id),
            // Repetido (la base no lo deja, pero la copia lo admite): se lo lleva el producto más antiguo, venga primero o no.
            new CodigoLocalDto("750200", Lapicero.VarianteId, Pieza.Id),
            // De un producto que no vino en la copia.
            new CodigoLocalDto("750999", Ids.Nuevo(), Ids.Nuevo()),
        ],
        [], [], [], [], null, 0, null);

    [Fact]
    public void Cada_codigo_da_su_producto_con_la_presentacion_de_ese_codigo_y_la_version_de_la_copia()
    {
        var foto = FotoCatalogo.Armar(Paquete(12, Lapicero, Cuaderno));

        Assert.Equal((12L, 2, 5), (foto.Version, foto.Productos, foto.Codigos));
        Assert.Equal((Lapicero.VarianteId, Pieza.Id), Clave(foto.PorCodigo("750100")));
        // La caja de 12, con su código; sin importar los espacios de las orillas ni las mayúsculas.
        Assert.Equal((Lapicero.VarianteId, Caja12.Id), Clave(foto.PorCodigo(" 7501000000012 ")));
        Assert.Equal((Lapicero.VarianteId, Pieza.Id), Clave(foto.PorCodigo("lap-az")));
        Assert.Equal(Clave(DelRepetido), Clave(foto.PorCodigo("750200")));
        Assert.Null(foto.PorCodigo("750999"));
        Assert.Null(foto.PorCodigo("cuaderno"));
        // Los precios son los de la copia, y cada producto dice de qué versión salieron.
        Assert.Equal(Lapicero.Precios, foto.PorCodigo("750100")!.Precios);
        Assert.Equal(12, foto.PorCodigo("7501000000012")!.Version);
        Assert.Equal(7, foto.PorVariante(Cuaderno.VarianteId)!.Version);
        Assert.Equal(Pieza.Id, foto.PorVariante(Lapicero.VarianteId)!.Presentacion.Id);
        Assert.Null(foto.PorVariante(Ids.Nuevo()));
    }

    /// <summary>
    /// La búsqueda por nombre de la copia (entrega 5, parte 3): con lo más vendido y lo elegido antes, como el servicio; un
    /// código repetido se encuentra en los dos productos (al escanear, en uno: <see cref="CodigosDeEscaneo"/>). La copia de un
    /// servicio de antes no los trae y ordenaría distinto: ahí se le pregunta al servicio.
    /// </summary>
    [Fact]
    public void La_copia_busca_por_nombre_si_trae_lo_mas_vendido()
    {
        Assert.False(FotoCatalogo.Armar(Paquete(12, Lapicero, Cuaderno)).SeBusca);

        var foto = FotoCatalogo.Armar(Paquete(12, Lapicero, Cuaderno) with { Vendidos = [new VecesVendidoDto(Cuaderno.VarianteId, 3)], Elegidos = [] });

        Assert.True(foto.SeBusca);
        Assert.Equal(["Cuaderno raya"], foto.Buscar("raya").Select(r => r.Descripcion));
        // El código exacto primero, y el otro producto que también lo tiene.
        Assert.Equal(["Cuaderno raya", "Lapicero azul"], foto.Buscar("750200").Select(r => r.Descripcion));
        Assert.Equal((3, 3500L), (foto.Buscar("cuaderno")[0].Vendidos, foto.Buscar("cuaderno")[0].PrecioCentavos));
    }

    /// <summary>Lo de antes (Contingencia.PorCodigo, hasta la entrega 5), para revisar que la foto resuelve igual.</summary>
    internal static ProductoVentaDto? ComoAntes(PaqueteContingenciaDto paquete, string codigo)
    {
        var porVariante = paquete.Productos.ToDictionary(x => x.VarianteId);
        var porCodigo = new Dictionary<string, CodigoLocalDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in paquete.Codigos)
            porCodigo.TryAdd(c.Codigo, c);
        return porCodigo.TryGetValue(codigo.Trim(), out var cod) && porVariante.TryGetValue(cod.VarianteId, out var p)
            ? p with { Presentacion = p.Presentaciones.FirstOrDefault(x => x.Id == cod.PresentacionId) ?? p.Presentacion }
            : null;
    }

    internal static (Guid VarianteId, Guid PresentacionId)? Clave(ProductoVentaDto? p) => p is null ? null : (p.VarianteId, p.Presentacion.Id);

    /// <summary>
    /// Como lo hacía la contingencia, salvo un código repetido: antes ganaba el primero de la lista, y ahora el producto más
    /// antiguo, como en el servicio, para que no dependa del orden de la copia (la puesta al día pone lo que cambió al final).
    /// </summary>
    [Fact]
    public void Resuelve_cada_codigo_igual_que_la_contingencia_de_antes()
    {
        var paquete = Paquete(3, Lapicero, Cuaderno);
        var foto = FotoCatalogo.Armar(paquete);

        foreach (var codigo in paquete.Codigos.Select(c => c.Codigo).Where(c => c != "750200").Append("LAP-az ").Append("nada").Append(""))
        {
            var antes = ComoAntes(paquete, codigo);
            var ahora = foto.PorCodigo(codigo);
            Assert.Equal(Clave(antes), Clave(ahora));
            Assert.Equal(antes?.Precios, ahora?.Precios);
        }
        Assert.Equal(Clave(DelRepetido), Clave(foto.PorCodigo("750200")));
        // En otro orden, lo mismo.
        var alReves = FotoCatalogo.Armar(paquete with { Codigos = paquete.Codigos.Reverse().ToList() });
        foreach (var codigo in paquete.Codigos.Select(c => c.Codigo))
            Assert.Equal(Clave(foto.PorCodigo(codigo)), Clave(alReves.PorCodigo(codigo)));
    }

    /// <summary>De quién es «750200», que está en los dos: del más antiguo (su Id va por fecha), con la presentación de su código.</summary>
    private static ProductoVentaDto DelRepetido =>
        Lapicero.VarianteId.CompareTo(Cuaderno.VarianteId) < 0 ? Lapicero with { Presentacion = Pieza } : Cuaderno with { Presentacion = PiezaCuaderno };

    [Fact]
    public void Lo_que_llega_de_la_principal_va_en_una_foto_nueva_encima_y_la_de_antes_no_cambia()
    {
        var foto = FotoCatalogo.Armar(Paquete(12, Lapicero, Cuaderno));
        var piezaNueva = new PresentacionDto(Ids.Nuevo(), "Pieza", 1000, true);
        var nuevo = new ProductoVentaDto(Ids.Nuevo(), "Libreta recién dada de alta", "750300", Impuesto.Iva16, UnidadBase.Pieza, true, null, piezaNueva,
            [piezaNueva], [new PrecioDto(Ids.Nuevo(), null, null, 1000, 2200)], Version: 14);
        // Los precios de ahora del lapicero (los dijo el cobro), con la presentación base.
        var lapiceroAlDia = Lapicero with { Precios = [new PrecioDto(Ids.Nuevo(), null, null, 1000, 1200)], Version = 14 };

        var conNuevo = foto.Con(nuevo, " 750300 ");
        var conTodo = conNuevo.Con(lapiceroAlDia);

        Assert.Null(foto.PorCodigo("750300"));
        Assert.Equal(nuevo, conNuevo.PorCodigo("750300"));
        Assert.Equal(nuevo, conTodo.PorVariante(nuevo.VarianteId));
        Assert.Equal((1, 2), (conNuevo.Nuevos, conTodo.Nuevos));
        // Todos los códigos del lapicero, con los precios de ahora y la presentación de cada código.
        Assert.Equal(1200, conTodo.PorCodigo("750100")!.Precios[0].PrecioCentavos);
        Assert.Equal((Lapicero.VarianteId, Caja12.Id), Clave(conTodo.PorCodigo("7501000000012")));
        Assert.Equal(lapiceroAlDia.Precios, conTodo.PorCodigo("7501000000012")!.Precios);
        Assert.Equal(lapiceroAlDia with { Presentacion = Pieza }, conTodo.PorVariante(Lapicero.VarianteId));
        // Las de antes siguen como estaban: quien las tiene no ve un cambio a medias.
        Assert.Equal(1000, foto.PorCodigo("750100")!.Precios[0].PrecioCentavos);
        Assert.Equal(1000, conNuevo.PorCodigo("LAP-AZ")!.Precios[0].PrecioCentavos);
        Assert.Equal(12, conTodo.Version);
    }

    [Fact]
    public void La_foto_de_una_copia_nueva_se_queda_con_lo_que_es_mas_nuevo_que_ella()
    {
        var piezaNueva = new PresentacionDto(Ids.Nuevo(), "Pieza", 1000, true);
        var nuevo = new ProductoVentaDto(Ids.Nuevo(), "Libreta", "750300", Impuesto.Iva16, UnidadBase.Pieza, true, null, piezaNueva, [piezaNueva],
            [new PrecioDto(Ids.Nuevo(), null, null, 1000, 2200)], Version: 16);
        var anterior = FotoCatalogo.Armar(Paquete(12, Lapicero, Cuaderno))
            .Con(nuevo, "750300")
            .Con(Lapicero with { Precios = [new PrecioDto(Ids.Nuevo(), null, null, 1000, 1200)], Version = 14 });

        // La copia nueva (versión 15) ya trae los precios del lapicero; la libreta todavía no.
        var nueva = FotoCatalogo.Armar(Paquete(15, Lapicero with { Precios = [new PrecioDto(Ids.Nuevo(), null, null, 1000, 1300)] }, Cuaderno))
            .ConLoNuevoDe(anterior);

        Assert.Equal(15, nueva.Version);
        Assert.Equal(1, nueva.Nuevos);
        Assert.Equal(nuevo, nueva.PorCodigo("750300"));
        Assert.Equal(1300, nueva.PorCodigo("750100")!.Precios[0].PrecioCentavos);
        Assert.Equal(15, nueva.PorCodigo("750100")!.Version);
        Assert.Same(nueva, nueva.ConLoNuevoDe(null));
    }

    /// <summary>
    /// Una copia más vieja que la anterior quiere decir que la base de la caja principal cambió entera (la prueba empezó de
    /// cero): lo que estaba encima era de la otra base y no se queda, aunque su versión sea más nueva que la copia.
    /// </summary>
    [Fact]
    public void Una_copia_mas_vieja_que_la_anterior_no_se_queda_con_lo_de_encima()
    {
        var piezaNueva = new PresentacionDto(Ids.Nuevo(), "Pieza", 1000, true);
        var nuevo = new ProductoVentaDto(Ids.Nuevo(), "Libreta", "750300", Impuesto.Iva16, UnidadBase.Pieza, true, null, piezaNueva, [piezaNueva],
            [new PrecioDto(Ids.Nuevo(), null, null, 1000, 2200)], Version: 16);
        var anterior = FotoCatalogo.Armar(Paquete(15, Lapicero, Cuaderno))
            .Con(nuevo, "750300")
            .Con(Lapicero with { Precios = [new PrecioDto(Ids.Nuevo(), null, null, 1000, 1200)], Version = 16 });

        var deCero = FotoCatalogo.Armar(Paquete(9, Lapicero, Cuaderno));
        var nueva = deCero.ConLoNuevoDe(anterior);

        Assert.Same(deCero, nueva);
        Assert.Equal(0, nueva.Nuevos);
        Assert.Null(nueva.PorCodigo("750300"));
        Assert.Equal(1000, nueva.PorCodigo("750100")!.Precios[0].PrecioCentavos);
    }
}
