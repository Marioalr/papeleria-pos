using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Impresiones.Servicio;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Revisión independiente de cb5f83f: la vigilancia con varias impresoras (arreglado el 2026-11-08).</summary>
public sealed class RevisionVariasImpresorasApiTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    /// <summary>
    /// El módem les cambia la IP a las dos WF-6590 (2026-11-03, la decisión: «Un aviso, una vez por serie; no se cambia nada solo»). El aviso sale,
    /// pero la lectura de la otra impresora se guarda como de esta y su contador cierra el lote de esta: con un contador más chico, el trabajo queda
    /// con 0 caras contadas, su costo en cero y «Impresión incompleta». PageAuditor: «se puede no ver un faltante, pero nunca inventarlo».
    /// </summary>
    [Fact]
    public async Task Si_en_su_IP_contesta_otra_serie_no_inventa_hojas_faltantes_con_el_contador_de_la_otra()
    {
        var (http, caja, cajero, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ruta = ApiDeImpresoras.Ruta(caja);
        var a = new PrinterStatus { InkK = 80, InkC = 80, InkM = 80, InkY = 80, LifeCount = 100_000, Serial = "SERIE-A" };
        var b = new PrinterStatus { InkK = 60, InkC = 60, InkM = 60, InkY = 60, LifeCount = 50_000, Serial = "SERIE-B" };
        servicio.Impresoras["ip-a"] = a;
        servicio.Impresoras["ip-b"] = b;
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto("ip-a", 10, 5))));
        await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ruta, new SolicitudNuevaImpresora(dueno.UsuarioId, "WF-6590 2", PerfilImpresora.WF6590, "ip-b")));
        servicio.Reloj.Avanzar(TimeSpan.FromMinutes(3));
        var trabajo = ApiDeImpresoras.Trabajo(caja, "Salio completo.pdf", 6, servicio.Reloj.AhoraUtc, 1);
        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones", trabajo));

        // Salió completo en la de la serie A; en ese momento el módem les cambió la IP.
        servicio.Impresoras["ip-a"] = b;
        servicio.Impresoras["ip-b"] = a with { LifeCount = a.LifeCount + 6 };
        ImpresoraDto leida = null!;
        foreach (var espera in new[] { 90, 40 })
        {
            servicio.Reloj.Avanzar(TimeSpan.FromSeconds(espera));
            leida = await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsync($"{ruta}/1/leer", null));
        }

        // Con Telegram en pausa, lo dice la Configuración (2026-11-08): qué serie contesta, y que mientras no se lee. Su última lectura es la suya.
        // (La otra prueba de la clase, si corrió antes, dejó en la 1 otra serie conocida: no se compara con cuál.)
        Assert.Contains("En ip-a contesta otra impresora (la serie SERIE-B;", leida.Estado.OtraSerie);
        Assert.NotEqual("SERIE-B", leida.Estado.Ultima!.Serie);
        var enLaLista = (await http.GetFromJsonAsync<List<ImpresoraDto>>(ruta))!.Single(i => i.Numero == 1);
        Assert.Equal(leida.Estado.OtraSerie, enLaLista.Estado.OtraSerie);

        var porCobrar = (await http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>(
            $"api/sucursales/{caja.SucursalId}/impresiones/por-cobrar?usuarioId={dueno.UsuarioId}"))!;
        var suyo = porCobrar.Single(t => t.Id == trabajo.Id);
        Assert.NotEqual(0, suyo.CarasContadas ?? -1);
        Assert.True(suyo.Costo!.TotalCentavos > 0, $"Su costo quedó en {suyo.Costo.TotalCentavos}.");
    }

    /// <summary>
    /// Y sigue: desde la primera lectura de la otra, su base es el contador de la otra. Cada trabajo de esta impresora, mientras la otra no imprima, sale
    /// «0 de N» hasta que alguien corrija la IP. Aquí, un segundo trabajo después del cambio de IP.
    /// </summary>
    [Fact]
    public async Task Mientras_la_IP_siga_cambiada_los_trabajos_que_siguen_tampoco_salen_con_cero_caras()
    {
        var (http, caja, _, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ruta = ApiDeImpresoras.Ruta(caja);
        var a = new PrinterStatus { InkK = 80, InkC = 80, InkM = 80, InkY = 80, LifeCount = 200_000, Serial = "SERIE-C" };
        var b = new PrinterStatus { InkK = 60, InkC = 60, InkM = 60, InkY = 60, LifeCount = 70_000, Serial = "SERIE-D" };
        servicio.Impresoras["ip-c"] = a;
        servicio.Impresoras["ip-d"] = b;
        // Las de la otra prueba (si corrió antes) dejan estas direcciones libres: se cambia la primera a ip-c y se agrega otra.
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto("ip-c", 10, 5))));
        await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ruta, new SolicitudNuevaImpresora(dueno.UsuarioId, $"Otra {Guid.NewGuid():N}"[..20], PerfilImpresora.WF6590, "ip-d")));
        servicio.Impresoras["ip-c"] = b;
        servicio.Impresoras["ip-d"] = a;
        servicio.Reloj.Avanzar(TimeSpan.FromMinutes(6));
        await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsync($"{ruta}/1/leer", null));

        // La IP sigue cambiada. Otro trabajo sale completo de la impresora 1 (la de la serie C, que ahora está en ip-d).
        servicio.Reloj.Avanzar(TimeSpan.FromMinutes(3));
        var trabajo = ApiDeImpresoras.Trabajo(caja, "Salio completo 2.pdf", 4, servicio.Reloj.AhoraUtc, 1);
        await ApiDeImpresoras.Leer<object>(await http.PostAsJsonAsync("api/impresiones", trabajo));
        foreach (var espera in new[] { 90, 40 })
        {
            servicio.Reloj.Avanzar(TimeSpan.FromSeconds(espera));
            await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsync($"{ruta}/1/leer", null));
        }

        var porCobrar = (await http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>(
            $"api/sucursales/{caja.SucursalId}/impresiones/por-cobrar?usuarioId={dueno.UsuarioId}"))!;
        Assert.NotEqual(0, porCobrar.Single(t => t.Id == trabajo.Id).CarasContadas ?? -1);
    }
}

/// <summary>Una muerta tarda lo que tarda de verdad PrinterStatusReader sin respuesta: 2 s de SNMP, 2 s del estado Epson y 8 s de su página.</summary>
internal sealed class LectorConUnaMuerta(string muerta, TimeSpan loQueTarda) : ILectorImpresora
{
    public TaskCompletionSource VivaLeida { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<PrinterStatus> LeerAsync(string host, CancellationToken ct)
    {
        if (host == muerta)
        {
            await Task.Delay(loQueTarda, ct);
            throw new TimeoutException($"SNMP sin respuesta de {host}");
        }
        VivaLeida.TrySetResult();
        return new PrinterStatus { InkK = 80, InkC = 80, InkM = 80, InkY = 80, LifeCount = 1000 };
    }
}

public sealed class RevisionVigilanciaConUnaMuertaTests(ServicioConVariasImpresoras servicio) : IClassFixture<ServicioConVariasImpresoras>
{
    /// <summary>
    /// «Una impresora muerta no debe atrasar a las demás»: ExecuteAsync las lee una tras otra (VigilanciaImpresora.cs:126-131). Con la 1 muerta, la
    /// 2 espera los ~12 s de la 1 en cada vuelta en que le toca; con dos muertas, el doble. Aquí la muerta tarda 8 s y la viva debe leerse antes.
    /// </summary>
    [Fact]
    public async Task Una_muerta_no_atrasa_a_las_demas()
    {
        var (http, caja, _, dueno) = await ApiDeImpresoras.AbrirAsync(servicio.Fabrica);
        var ruta = ApiDeImpresoras.Ruta(caja);
        await ApiDeImpresoras.Leer<EstadoImpresoraDto>(await http.PutAsJsonAsync($"api/sucursales/{caja.SucursalId}/impresora",
            new SolicitudConfiguracionImpresora(dueno.UsuarioId, new ConfiguracionImpresoraDto("muerta", 10, 5))));
        await ApiDeImpresoras.Leer<ImpresoraDto>(await http.PostAsJsonAsync(ruta, new SolicitudNuevaImpresora(dueno.UsuarioId, "WF-6590 2", PerfilImpresora.WF6590, "viva")));

        var lector = new LectorConUnaMuerta("muerta", TimeSpan.FromSeconds(8));
        using var vigilancia = new VigilanciaImpresora(servicio.Fabrica.Services.GetRequiredService<IServiceScopeFactory>(), lector, TimeProvider.System,
            NullLogger<VigilanciaImpresora>.Instance);
        await vigilancia.StartAsync(CancellationToken.None);
        try
        {
            var primero = await Task.WhenAny(lector.VivaLeida.Task, Task.Delay(TimeSpan.FromSeconds(4)));
            Assert.True(primero == lector.VivaLeida.Task, "La viva no se leyó en 4 s: espera a la muerta.");
        }
        finally
        {
            await vigilancia.StopAsync(CancellationToken.None);
        }
    }
}

public sealed class RevisionDespertarTests
{
    /// <summary>
    /// Antes un solo candado (la sucursal) cuidaba a Pronto; desde el candado por impresora, dos impresoras pueden llegar juntas: «CurrentCount == 0» y
    /// luego «Release» sobre un SemaphoreSlim(0, 1) no es atómico (VigilanciaImpresora.cs:223-224): SemaphoreFullException. Se llama a Pronto como lo
    /// harían dos POST /impresiones de dos impresoras, y la vigilancia consumiendo.
    /// </summary>
    [Fact]
    public async Task Dos_impresoras_a_la_vez_no_truenan_al_despertar_a_la_vigilancia()
    {
        var tipo = typeof(VigilanciaImpresora);
        var vigilancia = new VigilanciaImpresora(null!, new ImpresorasDePrueba(), TimeProvider.System, NullLogger<VigilanciaImpresora>.Instance);
        var pronto = tipo.GetMethod("Pronto", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var despertar = (SemaphoreSlim)tipo.GetField("_despertar", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vigilancia)!;
        var estadoTipo = tipo.GetNestedType("Estado", System.Reflection.BindingFlags.NonPublic)!;
        var fin = DateTime.UtcNow.AddSeconds(10);
        Exception? error = null;

        Task Hilo() => Task.Run(() =>
        {
            var estado = Activator.CreateInstance(estadoTipo);
            while (DateTime.UtcNow < fin && error is null)
            {
                try
                {
                    pronto.Invoke(vigilancia, [estado, TimeSpan.FromSeconds(90)]);
                }
                catch (System.Reflection.TargetInvocationException ex)
                {
                    error = ex.InnerException;
                }
            }
        });
        var consumir = Task.Run(() =>
        {
            while (DateTime.UtcNow < fin && error is null)
                despertar.Wait(0);
        });

        await Task.WhenAll(Hilo(), Hilo(), consumir);

        Assert.Null(error);
    }
}
