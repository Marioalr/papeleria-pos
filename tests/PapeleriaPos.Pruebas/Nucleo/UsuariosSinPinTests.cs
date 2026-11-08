using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>
/// Quien vino de eleventa llega sin PIN (el piloto alfa, 2026-10-25): tiene su ficha, pero no entra ni autoriza hasta que el
/// dueño se lo pone, y no se le nombra como quien autoriza o quien puede.
/// </summary>
public class UsuariosSinPinTests
{
    private static readonly DateTime Ahora = new(2026, 10, 25, 18, 0, 0, DateTimeKind.Utc);

    private static PermisosDeUsuario Todos() => new(Permisos.Acciones.Select(Permiso.Todo));

    [Fact]
    public void Sin_pin_no_entra_ni_suma_intentos()
    {
        var maria = new Usuario { Nombre = "María López", NombreUsuario = "maria", EleventaId = 3 };

        Assert.False(maria.TienePin);
        for (var i = 0; i < 10; i++)
            Assert.Equal(ResultadoPin.SinPin, maria.VerificarPin("1234", Ahora));
        // Sin intentos ni espera: en cuanto el dueño le pone PIN, entra.
        Assert.Equal(0, maria.IntentosFallidos);
        Assert.Null(maria.BloqueadoHastaUtc);
        maria.EstablecerPin("4821");
        Assert.Equal(ResultadoPin.Correcto, maria.VerificarPin("4821", Ahora));
    }

    [Fact]
    public void Su_ficha_sale_y_dice_que_no_tiene_pin()
    {
        var defecto = new PermisosDeUsuario([]);
        var conPin = new UsuarioConPermisos(Guid.NewGuid(), "luis", "Luis Pérez", Todos());
        var sinPin = new UsuarioConPermisos(Guid.NewGuid(), "maria", "María López", defecto, TienePin: false);

        var fichas = FichasEntrada.Armar([conPin, sinPin], [], defecto, cajeroDelTurno: null);

        Assert.Equal([("Luis Pérez", false), ("María López", true)], fichas.Select(f => (f.Nombre, f.SinPin)));
        Assert.Equal("M", fichas.Single(f => f.SinPin).Tecla);
        Assert.Contains("Configuración › Usuarios y permisos", FichasEntrada.SinPin);
    }

    [Fact]
    public void Quien_no_tiene_pin_no_se_nombra_para_autorizar_ni_para_hacer_algo()
    {
        var cajera = new UsuarioConPermisos(Guid.NewGuid(), "ana", "Ana", new PermisosDeUsuario([new Permiso(AccionControlada.Descuento, NivelPermiso.RequiereAutorizacion)]));
        var luis = new UsuarioConPermisos(Guid.NewGuid(), "luis", "Luis Pérez", Todos());
        var sinPin = new UsuarioConPermisos(Guid.NewGuid(), "maria", "María López", Todos(), TienePin: false);
        List<UsuarioConPermisos> usuarios = [cajera, luis, sinPin];

        var descuento = Permisos.DeLaSesion(cajera.Permisos, usuarios, cajera.Id).Single(p => p.Accion == AccionControlada.Descuento);

        Assert.Equal(["Luis Pérez"], descuento.Autorizan.Select(a => a.Nombre));
        Assert.Equal(["Luis Pérez"], Permisos.QuienesPueden(usuarios, AccionControlada.CorteZ).Select(q => q.Nombre));
        Assert.Equal(["Luis Pérez"], Permisos.EnOrden(usuarios).Where(u => u.Id != cajera.Id).Select(u => u.Nombre));
    }

    [Theory]
    [InlineData("4821", "4821", null)]
    [InlineData("482", "482", "El PIN es de 4 cifras.")]
    [InlineData("48211", "48211", "El PIN es de 4 cifras.")]
    [InlineData("48a1", "48a1", "El PIN es de 4 cifras.")]
    [InlineData(null, null, "El PIN es de 4 cifras.")]
    [InlineData("4821", "4822", "Los dos PIN no son iguales: escríbelo otra vez.")]
    public void El_pin_nuevo_tiene_la_misma_regla_en_la_pantalla_el_servicio_y_la_linea_de_comandos(string? pin, string? otraVez, string? error) =>
        Assert.Equal(error, FichasEntrada.RevisarPinNuevo(pin, otraVez));
}
