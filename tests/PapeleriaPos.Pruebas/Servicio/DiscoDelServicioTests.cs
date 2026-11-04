using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que el POS ocupa en el disco de la PC, para que no se llene sin que nadie se entere: el aviso en la franja antes de que falte, y las
/// miniaturas de las impresiones ya cobradas, que se quedaban para siempre en la base y en cada respaldo.
/// </summary>
public sealed class DiscoDelServicioTests
{
    private sealed class DiscoCon(long? libres) : IEspacioEnDisco
    {
        public long? Libre(string carpeta) => libres;
    }

    private static async Task<string?> AvisoConAsync(long? libres)
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(s => s.Replace(ServiceDescriptor.Singleton<IEspacioEnDisco>(new DiscoCon(libres))));
        return (await servicio.Fabrica.CreateClient().GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.AvisoBase;
    }

    [Fact]
    public async Task Con_poco_disco_la_franja_de_la_caja_avisa_y_con_espacio_o_sin_saberlo_no()
    {
        Assert.Equal("Queda poco disco en esta PC (1.5 GB libres): avisa al dueño antes de que dejen de salir los respaldos.", await AvisoConAsync(1610612736));
        Assert.Null(await AvisoConAsync(EspacioEnDisco.PocoDisco));
        Assert.Null(await AvisoConAsync(null));
    }

    [Fact]
    public async Task La_copia_del_dia_quita_antes_las_miniaturas_de_lo_cobrado_hace_mas_de_una_semana()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var hace = (int dias) => DateTime.UtcNow.AddDays(-dias);
        Guid vieja, reciente, porCobrar;
        await using (var db = servicio.Base())
        {
            var caja = await db.Cajas.FirstAsync();
            TrabajoImpresion Trabajo(EstadoCobroImpresion cobro, DateTime? cuando)
            {
                var t = new TrabajoImpresion
                {
                    SucursalId = caja.SucursalId, CajaId = caja.Id, Pc = "CAJA-2", Cola = "Cola", Documento = "Tarea.pdf", Tamano = "Carta", TipoPapel = "Bond", Calidad = "Normal", DetalleJson = "{}",
                    Miniatura = [1, 2, 3], Cobro = cobro, CobroUtc = cuando,
                };
                db.Set<TrabajoImpresion>().Add(t);
                return t;
            }
            vieja = Trabajo(EstadoCobroImpresion.NoVendida, hace(ServicioRespaldos.DiasDeMiniaturas + 1)).Id;
            reciente = Trabajo(EstadoCobroImpresion.NoVendida, hace(ServicioRespaldos.DiasDeMiniaturas - 1)).Id;
            // Falta cobrarla, por vieja que sea: F2 la muestra.
            porCobrar = Trabajo(EstadoCobroImpresion.PorCobrar, null).Id;
            await db.SaveChangesAsync();
        }
        var carpeta = Directory.CreateTempSubdirectory("respaldos-miniaturas-");
        try
        {
            new ServicioRespaldos($"Data Source={servicio.Ruta};Pooling=False", carpeta.FullName, 30, NullLogger<ServicioRespaldos>.Instance).DiarioSiFalta();

            await using var lectura = servicio.Base();
            var miniaturas = await lectura.Set<TrabajoImpresion>().AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Miniatura);
            Assert.Null(miniaturas[vieja]);
            Assert.NotNull(miniaturas[reciente]);
            Assert.NotNull(miniaturas[porCobrar]);
            Assert.Single(carpeta.GetFiles("pos-*.db"));
        }
        finally
        {
            ArchivosDePrueba.BorrarCarpeta(carpeta.FullName);
        }
    }
}
