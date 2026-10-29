using Microsoft.Extensions.Configuration;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que es un secreto por su nombre (revisión del PR #145, 2026-10-28): el registro del servicio, la configuración que va en el
/// .zip del piloto y juntar-datos-piloto.ps1 lo tachan con la misma regla, sin una lista de ajustes. Lo que se agregue después (el
/// código de conexión de Control Print, en ControlPrint__Codigo) ya se tacha.
/// </summary>
public class LoQueEsSecretoTests
{
    [Fact]
    public void Lo_que_es_un_secreto_por_su_nombre()
    {
        foreach (var secreto in new[]
                 {
                     "Clave", "clave", "Eleventa__Clave", "Eleventa:Clave", "Password", "UserPassword", "userpassword", "ISC_PASSWORD", "Token",
                     "ApiToken", "APIToken", "Telegram:Token", "Pin", "PinHash", "pinNuevo", "pin_actual", "LlaveProtegida", "llaveDelServicio",
                     "Secreto", "Contraseña", "Codigo", "ControlPrint__Codigo", "ControlPrint:Codigo", "SmtpPass", "pwd",
                 })
            Assert.True(LoQueEsSecreto.PorSuNombre(secreto), secreto);
        foreach (var normal in new[]
                 {
                     "Servidor", "Base", "Pintar", "Spinner", "Opinion", "Impresora", "Caja", "Cajero", "Gbak", "Usuario", "CodigoBarras",
                     "codigoDeBarras", "Puerto", "Escribir", "OTRA_VARIABLE",
                 })
            Assert.False(LoQueEsSecreto.PorSuNombre(normal), normal);
    }

    [Fact]
    public void De_la_configuracion_se_tachan_los_valores_de_lo_que_por_su_nombre_es_un_secreto()
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Eleventa:Clave"] = "clave-de-eleventa-9876",
            // Como llega de la variable de entorno del servicio ControlPrint__Codigo (el PR que sigue).
            ["ControlPrint:Codigo"] = "CP-7K2M-9QXA-4410",
            ["Telegram:Token"] = "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw",
            ["Eleventa:Servidor"] = "192.168.0.10",
            // Ni los cortos (se confundirían con cualquier número) ni un sí o un no.
            ["Otra:Pin"] = "123",
            ["Usuarios:PedirPin"] = "true",
            ["Eleventa:Clave2"] = null,
        }).Build();

        Assert.Equal(["123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", "CP-7K2M-9QXA-4410", "clave-de-eleventa-9876"],
            LoQueEsSecreto.ValoresDe(configuracion).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void En_un_texto_se_tacha_el_valor_de_lo_que_por_su_nombre_es_un_secreto()
    {
        static string Tachar(string texto) => LoQueEsSecreto.TacharEnUnTexto(texto, "***");

        Assert.Equal("data source=caja-principal;port=3050;user id=SYSDBA;userpassword=***;charset=WIN1252",
            Tachar("data source=caja-principal;port=3050;user id=SYSDBA;userpassword=s3cr3t-otra;charset=WIN1252"));
        Assert.Equal("user password=***;charset=WIN1252", Tachar("user password=s3cr3t;charset=WIN1252"));
        Assert.Equal("""{"usuarioId":"9b6c8a4e","pinNuevo":***,"pin_actual":***}""",
            Tachar("""{"usuarioId":"9b6c8a4e","pinNuevo":"4821","pin_actual":"5932"}"""));
        Assert.Equal("""{"pin": ***}""", Tachar("""{"pin": 4821}"""));
        Assert.Equal("ControlPrint__Codigo=*** y Eleventa:Clave=***", Tachar("ControlPrint__Codigo=CP-7K2M-9QXA-4410 y Eleventa:Clave=clave-9876"));
        // Dentro del valor de otro también se busca.
        Assert.Equal("opciones: pin=***", Tachar("opciones: pin=1234"));
        // El código de un producto, una hora, una dirección o una ruta se quedan.
        foreach (var normal in new[]
                 {
                     """{"codigo":"7501234567890","cantidad":2}""", "Respaldo de eleventa del día · empieza: gbak de 10.0.0.10:3050/C:\\eleventa\\PDVDATA.FDB",
                     "a las 14:03:12", "http://localhost:5080/api/eleventa", "Sin secretos: la clave de Firebird no se ve",
                 })
            Assert.Equal(normal, Tachar(normal));
        // Un texto armado para tardar no detiene a nadie: se revisa a tiempo o no se escribe nada de él.
        var largo = string.Concat(Enumerable.Repeat("ab:", 30_000)) + "pin=1234";
        Assert.Contains(Tachar(largo), new[] { largo[..^4] + "***", LoQueEsSecreto.SinRevisar });
        // En el valor de un ajuste que es una cadena de conexión, hasta el «;», con espacios.
        Assert.Equal("DataSource=192.168.0.10;User=SYSDBA;UserPassword=***;Charset=WIN1252",
            LoQueEsSecreto.TacharEnUnaCadena("DataSource=192.168.0.10;User=SYSDBA;UserPassword=con espacio;Charset=WIN1252", "***"));
    }
}
