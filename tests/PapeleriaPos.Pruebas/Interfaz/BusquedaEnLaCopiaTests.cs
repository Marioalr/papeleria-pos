using System.Diagnostics;
using System.Net.Http.Json;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Pruebas.Servicio;
using Xunit.Abstractions;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>
/// Buscar por nombre contra la copia de la caja (entrega 5, parte 3, docs/propuesta-interfaz.md, «Rendimiento»): la caja
/// arma la misma búsqueda que el servicio con su copia, y con los datos de la base de prueba da lo mismo, en el mismo
/// orden, sin la red. Lo que se revisa no depende de lo que tarda la PC; lo que tarda solo se anota.
/// </summary>
public sealed class BusquedaEnLaCopiaTests(ServicioDePrueba servicio, ITestOutputHelper salida) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-busqueda-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private sealed record Caja(ClienteCaja Cliente, Contingencia Contingencia, Interruptor Red, CajaDto Datos, HttpClient Http);

    private async Task<Caja> AbrirAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var red = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var aLaPrincipal = new HttpClient(red);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), aLaPrincipal, conexion);
        var cliente = new ClienteCaja(aLaPrincipal, conexion, contingencia);
        await contingencia.LatidoAsync();
        Assert.True(cliente.Foto is { SeBusca: true });
        return new Caja(cliente, contingencia, red, caja, http);
    }

    private static async Task<List<ResultadoBusquedaDto>> DelServicioAsync(Caja c, string texto, int maximo, bool conLaCaja = true) =>
        (await c.Http.GetFromJsonAsync<List<ResultadoBusquedaDto>>(
            $"api/productos/buscar?q={Uri.EscapeDataString(texto)}&sucursalId={c.Datos.SucursalId}{(conLaCaja ? $"&cajaId={c.Datos.Id}" : "")}&maximo={maximo}"))!;

    /// <summary>Todo lo que dice un resultado, para comparar (el nombre en pedazos, pedazo por pedazo).</summary>
    private static string Renglon(ResultadoBusquedaDto r) =>
        $"{r.VarianteId} · {r.Descripcion} · {r.Codigo} · {r.PrecioCentavos} · {r.Existencia} · {r.Vendidos} · {r.Parecido} · {r.PorSinonimo} · "
        + string.Join(" ", (r.Partes ?? []).Select(p => $"{p.Texto}/{p.Eje}/{p.Clave}"));

    /// <summary>
    /// Lo que escribiría la cajera, sacado del catálogo de la copia: palabras enteras, principios de palabra, dos pedazos en
    /// cualquier orden, lo que lee la caja del nombre (colores, medidas), errores de dedo y principios de código.
    /// </summary>
    private static List<string> Textos(PaqueteContingenciaDto paquete)
    {
        var palabras = paquete.Productos.SelectMany(p => p.Descripcion.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(w => w.Trim('(', ')', '.', ',')).Where(w => w.Length >= 3 && w.Any(char.IsLetter))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var textos = new List<string>();
        for (var i = 0; i < palabras.Count; i += Math.Max(1, palabras.Count / 40))
        {
            var w = palabras[i];
            textos.Add(w.ToLowerInvariant());
            textos.Add(w[..3].ToLowerInvariant());
            // Un error de dedo: dos letras volteadas.
            if (w.Length >= 5)
                textos.Add($"{w[..2]}{w[3]}{w[2]}{w[4..]}".ToLowerInvariant());
        }
        foreach (var p in paquete.Productos.Where((_, i) => i % Math.Max(1, paquete.Productos.Count / 30) == 0))
        {
            var ws = p.Descripcion.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3 && w.Any(char.IsLetter)).ToList();
            if (ws.Count >= 2)
            {
                textos.Add($"{ws[0][..3]} {ws[1][..3]}");
                textos.Add($"{ws[1][..3]} {ws[0][..3]}");
                textos.Add($"{ws[0]} {ws[^1]}");
            }
            if (p.Codigo is { Length: >= 4 } codigo)
                textos.Add(codigo[..4]);
        }
        textos.AddRange(["pluma gel", "pl gel", "cua pro", "folder", "lustre plata", "negro", "surtido", "1mm", "sobre manial", "cuadreno",
            "pluma pumto fino", "Pluma  PUNTO fino", "lápiz", "z"]);
        return textos.Distinct().ToList();
    }

    private async Task<int> CompararAsync(Caja c, IEnumerable<string> textos, int maximo)
    {
        var comparados = 0;
        foreach (var texto in textos)
        {
            var delServicio = (await DelServicioAsync(c, texto, maximo)).Select(Renglon).ToList();
            var deLaCopia = c.Cliente.BuscarEnLaCopia(texto, maximo)!.Select(Renglon).ToList();
            Assert.True(delServicio.SequenceEqual(deLaCopia),
                $"«{texto}» no da lo mismo:\nservicio:\n{string.Join("\n", delServicio.Take(8))}\ncopia:\n{string.Join("\n", deLaCopia.Take(8))}");
            comparados += delServicio.Count > 0 ? 1 : 0;
        }
        return comparados;
    }

    [Fact]
    public async Task La_copia_busca_lo_mismo_que_el_servicio_en_el_mismo_orden()
    {
        var c = await AbrirAsync();
        var paquete = c.Contingencia.Paquete!;
        var textos = Textos(paquete);
        // Lo vendido en 90 días ordena cuando el texto no distingue: la base de prueba trae ventas, y la copia las lleva.
        Assert.NotEmpty(paquete.Vendidos!);

        var conResultados = await CompararAsync(c, textos, maximo: 200);
        await CompararAsync(c, textos.Take(60), maximo: 30);

        salida.WriteLine($"{textos.Count} textos, {conResultados} con resultados, contra {paquete.Productos.Count:N0} productos.");
        Assert.True(conResultados > textos.Count / 2, $"Solo {conResultados} de {textos.Count} encontraron algo.");
        // También los parecidos (un error de dedo) y los que se encuentran por lo que lee la caja del nombre.
        Assert.All(c.Cliente.BuscarEnLaCopia("sobre manial", 200)!, r => Assert.True(r.Parecido));
        Assert.NotEmpty(c.Cliente.BuscarEnLaCopia("sobre manial", 200)!);
        Assert.All(c.Cliente.BuscarEnLaCopia("lustre plata", 200)!, r => Assert.Contains("Plateado", r.Descripcion));
    }

    /// <summary>
    /// Lo que esta caja eligió con un texto sale primero, igual en el servicio que en la copia: el que se eligió antes viene en
    /// la copia, y el que se elige ahora se recuerda en la caja sin esperar a que baje otra.
    /// </summary>
    [Fact]
    public async Task Lo_elegido_antes_sale_primero_tambien_en_la_copia()
    {
        var c = await AbrirAsync();
        var antes = await DelServicioAsync(c, "pluma punto fino", 200);
        var elegido = antes[^1];
        Assert.NotEqual(antes[0].VarianteId, elegido.VarianteId);

        // Se elige en la caja: lo guarda el servicio y lo recuerda la caja, sin bajar otra copia.
        await c.Cliente.RegistrarBusquedaAsync(new SolicitudBusqueda(c.Datos.Id, " Pluma  PUNTO fino", antes.Count, elegido.VarianteId));
        Assert.Equal(elegido.VarianteId, (await DelServicioAsync(c, "pluma punto fino", 200))[0].VarianteId);
        Assert.Equal(elegido.VarianteId, c.Cliente.BuscarEnLaCopia("pluma punto fino", 200)![0].VarianteId);
        await CompararAsync(c, ["pluma punto fino", "PLUMA PUNTO FINO"], maximo: 200);

        // Otra caja (una caja nueva con la misma copia) lo encuentra en la copia que baja.
        await c.Contingencia.ActualizarPaqueteAsync();
        Assert.Contains(c.Contingencia.Paquete!.Elegidos!, e => e.Texto == "PLUMA PUNTO FINO" && e.VarianteId == elegido.VarianteId);
        Assert.Equal(elegido.VarianteId, c.Contingencia.Foto!.Buscar("pluma punto fino", 200)[0].VarianteId);
        // Sin decir la caja (compras, etiquetas…) el servicio no lo pone primero, y la copia tampoco.
        var sinCaja = await DelServicioAsync(c, "pluma punto fino", 200, conLaCaja: false);
        Assert.Equal(sinCaja.Select(Renglon), c.Contingencia.Buscar("pluma punto fino", 200).Select(Renglon));
    }

    /// <summary>
    /// Los sinónimos de la tienda viajan con la copia y la búsqueda de la caja los usa igual: lo que salió por un sinónimo va
    /// después de lo que se llama así y lo dice.
    /// </summary>
    [Fact]
    public async Task Los_sinonimos_de_la_tienda_dan_lo_mismo_en_la_copia()
    {
        var c = await AbrirAsync();
        var encargado = (await (await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var agregar = await c.Http.PostAsJsonAsync("api/catalogo/sinonimos", new SolicitudSinonimo(encargado.UsuarioId, c.Datos.SucursalId, "rotulador", "plumon", AlReves: true));
        Assert.True(agregar.IsSuccessStatusCode, await agregar.Content.ReadAsStringAsync());
        // Cambió el catálogo: el siguiente latido baja la copia con el sinónimo.
        await c.Contingencia.LatidoAsync();

        var plumones = c.Cliente.BuscarEnLaCopia("rotulador", 200)!;
        Assert.NotEmpty(plumones);
        Assert.All(plumones, r => Assert.Equal("rotulador", r.PorSinonimo));
        await CompararAsync(c, ["rotulador", "rotul", "plumon", "plumon rotulador", "rotulador negro"], maximo: 200);

        // Sin conexión, igual.
        var enLinea = (await DelServicioAsync(c, "rotulador", 200, conLaCaja: false)).Select(Renglon).ToList();
        c.Red.Caido = true;
        await c.Contingencia.LatidoAsync();
        Assert.False(c.Contingencia.EnLinea);
        Assert.Equal(enLinea, (await c.Cliente.BuscarAsync("rotulador", c.Datos.SucursalId, maximo: 200)).Select(Renglon));
        Assert.Equal(plumones.Select(Renglon), c.Cliente.BuscarEnLaCopia("rotulador", 200)!.Select(Renglon));
    }

    /// <summary>
    /// Escribir un nombre en la venta busca en la copia, sin la red (aunque la red tarde 3 s en cada petición): la lista sale
    /// al momento, y Enter agrega el primero también de la copia. Que no haya peticiones no depende de lo que tarda la PC.
    /// </summary>
    [Fact]
    public async Task Escribir_en_la_venta_busca_en_la_copia_sin_la_red()
    {
        await using var v = await VentaConCopia.AbrirAsync(servicio, _baseLocal);
        v.Red.Retraso = TimeSpan.FromSeconds(3);
        var pedidas = v.Red.Cuantas("/api/productos/");

        await v.Pantalla.Dispatcher.InvokeAsync(() => v.Venta.Escribiendo("pluma gel"));
        Assert.Contains("Productos · ", await v.Pantalla.TextoAsync());
        // Enter sin elegir agrega el primer renglón (en una familia, su más vendido).
        var resultados = v.Cliente.BuscarEnLaCopia("pluma gel", 200)!;
        var primero = Familias.Agrupar(resultados, r => r.Partes ?? [], r => r.Vendidos).Renglones[0].Elegido;

        await v.EscanearAsync("pluma gel");

        Assert.Equal(pedidas, v.Red.Cuantas("/api/productos/"));
        Assert.StartsWith($"1 {primero.Descripcion}", Assert.Single(await VentaConCopia.RenglonesAsync(v.Pantalla)));
        Assert.Empty(v.Pantalla.Errores);
    }

    /// <summary>
    /// Lo que tarda buscar en la copia, del texto a la lista (sin la red ni el dibujo), con la base de prueba y con 6,000
    /// productos (la base de prueba cuatro veces, como la tienda): se anota, no se revisa (depende de la PC). La meta es menos
    /// de 100 ms de la tecla a la lista pintada.
    /// </summary>
    [Fact]
    public async Task Lo_que_tarda_buscar_en_la_copia_se_anota()
    {
        var c = await AbrirAsync();
        var paquete = c.Contingencia.Paquete!;
        Medir(paquete);
        Medir(Cuatro(paquete));
    }

    private void Medir(PaqueteContingenciaDto paquete)
    {
        var armar = Stopwatch.StartNew();
        var foto = FotoCatalogo.Armar(paquete);
        armar.Stop();
        string[] textos = ["pl", "plu", "pluma", "pluma g", "pluma gel", "cua pro", "folder", "sobre manial", "cuadreno", "lap az"];
        foreach (var texto in textos)
            foto.Buscar(texto, 200);
        salida.WriteLine($"Copia con {foto.Productos:N0} productos: armar la foto con su búsqueda, {armar.Elapsed.TotalMilliseconds:N0} ms.");
        foreach (var texto in textos)
        {
            var reloj = Stopwatch.StartNew();
            var encontrados = 0;
            for (var i = 0; i < 20; i++)
                encontrados = foto.Buscar(texto, 200).Count;
            salida.WriteLine($"  «{texto}»: {reloj.Elapsed.TotalMilliseconds / 20:N2} ms, {encontrados} resultados.");
        }
        Assert.True(foto.SeBusca);
    }

    /// <summary>La copia cuatro veces, cada vez con otros Id y otro número al final del nombre.</summary>
    private static PaqueteContingenciaDto Cuatro(PaqueteContingenciaDto paquete)
    {
        var productos = new List<ProductoVentaDto>();
        var codigos = new List<CodigoLocalDto>();
        for (var vez = 0; vez < 4; vez++)
        {
            var nuevos = paquete.Productos.ToDictionary(p => p.VarianteId, _ => Ids.Nuevo());
            productos.AddRange(paquete.Productos.Select(p => p with { VarianteId = nuevos[p.VarianteId], Descripcion = vez == 0 ? p.Descripcion : $"{p.Descripcion} {vez}" }));
            codigos.AddRange(paquete.Codigos.Where(x => nuevos.ContainsKey(x.VarianteId))
                .Select(x => new CodigoLocalDto(vez == 0 ? x.Codigo : $"{x.Codigo}-{vez}", nuevos[x.VarianteId], x.PresentacionId)));
        }
        return paquete with { Productos = productos, Codigos = codigos };
    }
}
