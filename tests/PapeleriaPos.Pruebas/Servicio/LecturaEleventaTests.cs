using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>La lectura de eleventa del piloto: apagada por omisión, y dice qué le falta si se prende a medias.</summary>
public class LecturaEleventaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    [Fact]
    public async Task Sin_configurar_esta_apagada()
    {
        var estado = await servicio.Fabrica.CreateClient().GetFromJsonAsync<EstadoEleventaDto>("api/eleventa");

        Assert.False(estado!.Activa);
        Assert.Null(estado.Error);
        Assert.Equal(0, estado.Lecturas);
    }

    [Fact]
    public async Task Activa_sin_la_clave_dice_que_falta_y_no_lee()
    {
        await using var fabrica = servicio.Fabrica.WithWebHostBuilder(b => b
            .UseSetting("Eleventa:Activo", "true").UseSetting("Eleventa:Servidor", "caja-principal").UseSetting("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"));
        var http = fabrica.CreateClient();

        var estado = await http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa");

        Assert.False(estado!.Activa);
        Assert.Contains("la clave", estado.Error);
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsync("api/eleventa/leer", null)).StatusCode);
    }

    [Fact]
    public void La_conexion_no_muestra_la_clave()
    {
        var opciones = new OpcionesEleventa { Activo = true, Servidor = "caja-principal", Base = @"C:\eleventa\PDVDATA.FDB", Clave = "secreta" };

        Assert.DoesNotContain("secreta", opciones.Conexion()!.ToString());
        Assert.Null(new OpcionesEleventa { Servidor = "caja-principal", Base = "x", Clave = "y" }.Conexion());
    }
}
