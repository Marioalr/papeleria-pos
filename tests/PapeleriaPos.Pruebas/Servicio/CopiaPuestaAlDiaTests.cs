using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using Xunit.Abstractions;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// La copia de una caja puesta al día con lo que cambió (entrega 5, partes 2 y 3) busca y escanea igual que una recién bajada
/// y que la caja principal: lo que ve el cajero, renglón por renglón y en su orden, y el producto de cada código. Con su
/// propio servicio: cambian marcas, códigos y productos.
/// </summary>
public sealed class CopiaPuestaAlDiaTests(ITestOutputHelper salida)
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static string Renglon(ResultadoBusquedaDto r) =>
        $"{r.VarianteId} · {r.Descripcion} · {r.Codigo} · {r.PrecioCentavos} · {r.Existencia} · {r.Vendidos} · {r.Parecido} · {r.PorSinonimo} · "
        + string.Join(" ", (r.Partes ?? []).Select(p => $"{p.Texto}/{p.Eje}/{p.Clave}"));

    private static string Comparable(PaqueteContingenciaDto p)
    {
        var json = JsonSerializerOptions.Web;
        var productos = p.Productos.OrderBy(x => x.VarianteId).Select(x => JsonSerializer.Serialize(x with { Version = null }, json));
        var codigos = p.Codigos.Select(x => $"{x.Codigo}|{x.VarianteId}|{x.PresentacionId}").Order(StringComparer.Ordinal);
        var resto = p with { Productos = [], Codigos = [], Marcas = p.Marcas?.Order(StringComparer.Ordinal).ToList() };
        return string.Join("\n", productos.Concat(codigos).Append(JsonSerializer.Serialize(resto, json)));
    }

    /// <summary>Lo que cambió desde esa copia, como lo pide la caja.</summary>
    internal static async Task<CambiosCopiaDto> CambiosAsync(HttpClient http, Guid cajaId, PaqueteContingenciaDto desde) =>
        await Leer<CambiosCopiaDto>(await http.GetAsync($"api/cajas/{cajaId}/cambios?desde={desde.Version}&existencias={desde.VersionExistencias}"
            + $"&base={desde.Base}&formato={CopiaAlDia.Formato}&vendidos={desde.VersionVendidos}&elegidos={desde.VersionElegidos}"
            + $"&promociones={desde.HuellaPromociones}"));

    /// <summary>Dos códigos que solo cambian en mayúsculas, uno en cada producto (el de B, primero en la base).</summary>
    private static async Task CodigosParecidosAsync(ServicioDePrueba servicio, ProductoVentaDto a, ProductoVentaDto b, string deB, string deA)
    {
        await using var db = servicio.Base();
        var presentacionB = await db.Presentaciones.SingleAsync(p => p.VarianteId == b.VarianteId && p.EsBase);
        db.CodigosBarras.Add(new CodigoBarras { Codigo = deB, PresentacionId = presentacionB.Id });
        await db.SaveChangesAsync();
        var presentacionA = await db.Presentaciones.SingleAsync(p => p.VarianteId == a.VarianteId && p.EsBase);
        db.CodigosBarras.Add(new CodigoBarras { Codigo = deA, PresentacionId = presentacionA.Id });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Códigos que solo cambian en mayúsculas («ZZ-CASO-01» en B, el primero en la base, y «zz-caso-01» en A): el escaneo se lo
    /// lleva el que es igual tal cual y, si ninguno lo es, el producto más antiguo, en la caja principal, en la copia recién
    /// bajada y en la puesta al día. Antes ganaba el primero de la lista: cuando B cambiaba, sus códigos se iban al final de la
    /// copia puesta al día y «ZZ-CASO-01» daba A en la caja y B en la caja principal.
    /// </summary>
    [Fact]
    public async Task Los_codigos_que_solo_cambian_en_mayusculas_se_escanean_igual_despues_de_un_cambio()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var http = propio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        await propio.CatalogoAlDiaAsync();
        var primera = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));
        var porId = primera.Productos.Where(p => p.Presentacion.EsBase).OrderBy(p => p.VarianteId).ToList();
        var (a, b) = (porId[20], porId[21]);
        await CodigosParecidosAsync(propio, a, b, "ZZ-CASO-01", "zz-caso-01");
        await propio.CatalogoAlDiaAsync();
        var antes = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));

        // B cambia (su precio): en la copia puesta al día, sus códigos van al final.
        await using (var db = propio.Base())
        {
            (await db.Precios.FirstAsync(x => x.VarianteId == b.VarianteId)).PrecioCentavos += 100;
            await db.SaveChangesAsync();
        }
        await propio.CatalogoAlDiaAsync();
        var alDia = FotoCatalogo.Armar(CopiaAlDia.Aplicar(antes, await CambiosAsync(http, caja.Id, antes)));
        var completa = FotoCatalogo.Armar(await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete")));

        var masAntiguo = a.VarianteId.CompareTo(b.VarianteId) < 0 ? a : b;
        foreach (var (escaneado, esperado) in new[] { ("ZZ-CASO-01", b), ("zz-caso-01", a), ("Zz-Caso-01", masAntiguo), (" ZZ-CASO-01 ", b) })
        {
            var enLaPrincipal = await Leer<ProductoVentaDto>(await http.GetAsync(
                $"api/productos/codigo/{Uri.EscapeDataString(escaneado.Trim())}?sucursalId={caja.SucursalId}"));
            Assert.Equal(esperado.VarianteId, enLaPrincipal.VarianteId);
            Assert.Equal(esperado.VarianteId, completa.PorCodigo(escaneado)!.VarianteId);
            Assert.Equal(esperado.VarianteId, alDia.PorCodigo(escaneado)!.VarianteId);
        }
    }

    /// <summary>
    /// De todo un poco (un nombre, un código que se pasa de producto, una marca que se queda sin productos y otra que se renombra,
    /// precios, un alta, un sinónimo nuevo y otro que se quita, una venta, lo elegido en esta caja y en otra, y lo más vendido
    /// calculado otra vez): la copia de antes con los cambios encima busca y escanea igual que la recién bajada, y busca igual
    /// que la caja principal.
    /// </summary>
    [Fact]
    public async Task La_copia_puesta_al_dia_busca_y_escanea_como_la_completa_y_la_caja_principal()
    {
        await using var propio = await ServicioDePrueba.CrearAsync();
        var http = propio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.First();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var catalogo = propio.Fabrica.Services.GetRequiredService<PapeleriaPos.Servicio.CatalogoEnMemoria>();
        await propio.CatalogoAlDiaAsync();

        // Antes de la copia de antes: un sinónimo que después se quita, y dos códigos que solo cambian en mayúsculas.
        var sinonimos = await Leer<List<SinonimoDto>>(await http.PostAsJsonAsync("api/catalogo/sinonimos",
            new SolicitudSinonimo(dueno.UsuarioId, caja.SucursalId, "lapicero", "pluma", false)));
        var primera = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));
        var porId = primera.Productos.Where(p => p.Presentacion.EsBase).OrderBy(p => p.VarianteId).ToList();
        var (prodA, prodB) = (porId[20], porId[21]);
        await CodigosParecidosAsync(propio, prodA, prodB, "ZZ-CASO-01", "zz-caso-01");
        await propio.CatalogoAlDiaAsync();
        var antes = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));

        var usados = new HashSet<Guid> { prodA.VarianteId, prodB.VarianteId };
        ProductoVentaDto Otro(Func<ProductoVentaDto, bool> filtro)
        {
            var p = antes.Productos.Where(x => !usados.Contains(x.VarianteId) && filtro(x)).OrderBy(x => x.VarianteId).First();
            usados.Add(p.VarianteId);
            return p;
        }

        string marcaRenombrada, marcaVaciada;
        await using (var db = propio.Base())
        {
            // Un nombre.
            var renombrado = Otro(p => p.Presentacion.EsBase);
            (await db.Variantes.Include(v => v.Producto).SingleAsync(v => v.Id == renombrado.VarianteId)).Producto!.Nombre = "Zanfona";
            // Un código que se pasa de un producto (que no cambia en nada más) a otro.
            var conDos = antes.Codigos.GroupBy(x => x.VarianteId).Where(g => g.Count() >= 2 && !usados.Contains(g.Key)).Select(g => g.Key).First();
            usados.Add(conDos);
            var movido = antes.Codigos.Where(x => x.VarianteId == conDos).OrderBy(x => x.Codigo, StringComparer.Ordinal).First();
            var destino = Otro(p => p.Presentacion.EsBase && p.Unidad == UnidadBase.Pieza);
            (await db.CodigosBarras.SingleAsync(x => x.Codigo == movido.Codigo)).PresentacionId = destino.Presentacion.Id;
            // Una marca que se queda sin productos: los suyos se dan de baja (la que menos tiene).
            var marcas = await db.Marcas.Where(m => db.Productos.Any(p => p.MarcaId == m.Id && p.Activo)).ToListAsync();
            var vaciar = marcas.OrderBy(m => db.Productos.Count(p => p.MarcaId == m.Id && p.Activo)).ThenBy(m => m.Nombre).First();
            marcaVaciada = vaciar.Nombre;
            foreach (var p in await db.Productos.Where(p => p.MarcaId == vaciar.Id && p.Activo).ToListAsync())
                p.Activo = false;
            // Una marca que se renombra.
            var renombrar = marcas.Where(m => m.Id != vaciar.Id).OrderBy(m => m.Nombre).First();
            renombrar.Nombre += " Mx";
            marcaRenombrada = renombrar.Nombre;
            // B, el del código que estaba primero en la base, cambia: sus códigos van al final de la copia puesta al día.
            (await db.Precios.FirstAsync(x => x.VarianteId == prodB.VarianteId)).PrecioCentavos += 100;
            await db.SaveChangesAsync();
        }
        // Un precio, un alta, un sinónimo nuevo y el de antes quitado.
        var conPrecio = Otro(p => p.Precios.Count == 1 && p.Precios[0].PresentacionId is null && p.Presentacion.EsBase);
        Assert.True((await http.PostAsJsonAsync("api/catalogo/precio-menudeo",
            new SolicitudPrecioMenudeo(dueno.UsuarioId, caja.SucursalId, conPrecio.VarianteId, conPrecio.Precios[0].PrecioCentavos + 250))).IsSuccessStatusCode);
        var departamento = (await http.GetFromJsonAsync<OpcionesCatalogoDto>("api/catalogo/opciones"))!.Departamentos[0].Id;
        var nuevo = await Leer<ProductoVentaDto>(await http.PostAsJsonAsync("api/catalogo/alta-rapida",
            new SolicitudAltaRapida(cajero.UsuarioId, caja.SucursalId, "9900000777001", "Zanfona de alta", 1990, departamento)));
        await Leer<List<SinonimoDto>>(await http.PostAsJsonAsync("api/catalogo/sinonimos",
            new SolicitudSinonimo(dueno.UsuarioId, caja.SucursalId, "ocarina", "zanfona", false)));
        var quitar = sinonimos.Single(s => s.Palabra == "LAPICERO");
        await Leer<List<SinonimoDto>>(await http.PostAsJsonAsync($"api/catalogo/sinonimos/{quitar.Id}/quitar",
            new SolicitudQuitarSinonimo(dueno.UsuarioId, caja.SucursalId)));
        // Una venta.
        var vendido = Otro(p => p.LlevaInventario && p.Presentacion.EsBase && p.Precios.Any(x => x.PresentacionId is null));
        var abierto = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = abierto.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(abierto);
        await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(vendido.VarianteId, vendido.Presentacion.Id, 2000)], [new CobroSolicitado(FormaPago.Efectivo, 10_000_000)])));
        // Lo elegido: en esta caja y en otra.
        Assert.True((await http.PostAsJsonAsync("api/busquedas", new SolicitudBusqueda(caja.Id, "zanfona", 3, nuevo.VarianteId))).IsSuccessStatusCode);
        catalogo.Aprender(Guid.NewGuid(), "pluma", vendido.VarianteId);
        // Y lo más vendido, calculado otra vez (al día siguiente).
        catalogo.Invalidar();
        await propio.CatalogoAlDiaAsync();

        var cambios = await CambiosAsync(http, caja.Id, antes);
        Assert.Null(cambios.PaqueteCompleto);
        var aplicado = CopiaAlDia.Aplicar(antes, cambios);
        var despues = await Leer<PaqueteContingenciaDto>(await http.GetAsync($"api/cajas/{caja.Id}/paquete"));

        var problemas = new List<string>();
        if (Comparable(despues) != Comparable(aplicado))
            problemas.Add("La copia puesta al día no es igual a la completa.");
        var fotoCompleta = FotoCatalogo.Armar(despues);
        var fotoAlDia = FotoCatalogo.Armar(aplicado);
        var textos = new List<string> { "zanfona", "ocarina", "lapicero", "pluma", "pluma gel", marcaRenombrada, marcaVaciada, "zz-caso", "ZZ-CASO-01", "cua pro",
            "folder", "lap az", "sobre manial" };
        textos.AddRange(despues.Productos.Where((_, i) => i % 25 == 0).Select(p => string.Join(" ", p.Descripcion.Split(' ').Take(2))));
        foreach (var texto in textos.Distinct())
        {
            var completa = fotoCompleta.Buscar(texto, 200).Select(Renglon).ToList();
            var alDia = fotoAlDia.Buscar(texto, 200).Select(Renglon).ToList();
            var enLaPrincipal = (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>(
                $"api/productos/buscar?q={Uri.EscapeDataString(texto)}&sucursalId={caja.SucursalId}&cajaId={caja.Id}&maximo=200"))!.Select(Renglon).ToList();
            if (!completa.SequenceEqual(alDia))
                problemas.Add($"«{texto}»: la completa y la puesta al día buscan distinto.");
            if (!enLaPrincipal.SequenceEqual(alDia))
                problemas.Add($"«{texto}»: la caja principal y la puesta al día buscan distinto.");
        }
        foreach (var codigo in despues.Codigos.Select(c => c.Codigo).Distinct(StringComparer.Ordinal))
        {
            var (completa, alDia) = (fotoCompleta.PorCodigo(codigo), fotoAlDia.PorCodigo(codigo));
            if (completa?.VarianteId != alDia?.VarianteId || completa?.Presentacion.Id != alDia?.Presentacion.Id)
                problemas.Add($"«{codigo}»: la completa escanea {completa?.Descripcion} y la puesta al día {alDia?.Descripcion}.");
        }
        var enLaPrincipalZz = await Leer<ProductoVentaDto>(await http.GetAsync($"api/productos/codigo/ZZ-CASO-01?sucursalId={caja.SucursalId}"));
        if (enLaPrincipalZz.VarianteId != fotoAlDia.PorCodigo("ZZ-CASO-01")?.VarianteId)
            problemas.Add("«ZZ-CASO-01»: la caja principal y la puesta al día escanean distinto.");
        foreach (var problema in problemas)
            salida.WriteLine(problema);
        Assert.Empty(problemas);
        Assert.True(textos.Count > 50);
    }
}
