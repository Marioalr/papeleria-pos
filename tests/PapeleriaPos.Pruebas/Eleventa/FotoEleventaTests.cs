using PapeleriaPos.Eleventa;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// La foto de observar-eleventa, que guarda lo que cambió en archivos de texto (revisión del PR #144): ninguna columna que parezca
/// secreta se lee, en ninguna tabla, ni su valor ni su huella.
/// </summary>
public class FotoEleventaTests
{
    [Theory]
    [InlineData("CLAVE", true)]
    [InlineData("CLAVE_LLAVE_PRIVADA", true)]
    [InlineData("CONTENIDO_LLAVEPRIVADA", true)]
    [InlineData("PASSWORD_SMTP", true)]
    [InlineData("Contrasena", true)]
    [InlineData("API_KEY", true)]
    [InlineData("TOKEN_NUBE", true)]
    [InlineData("ARCHIVO_PFX", true)]
    [InlineData("NOMBRE_COMPLETO", false)]
    [InlineData("CONTENIDO_CERTIFICADO", false)]
    [InlineData("TOTAL", false)]
    public void Lo_que_parece_secreto(string columna, bool secreta) => Assert.Equal(secreta, FotoEleventa.PareceSecreta(columna));

    /// <summary>
    /// Revisión del PR #144 (BAJA; hoy la tabla está vacía en la tienda). La foto ya no leía USUARIOS.CLAVE, pero seguía leyendo con
    /// «*» las demás tablas, y en la base de la tienda hay otra clave en claro: FACTURACION_CERTIFICADOS.CLAVE_LLAVE_PRIVADA
    /// (VARCHAR(255), la contraseña de la llave privada del certificado de facturación; CONTENIDO_LLAVEPRIVADA es un BLOB binario y
    /// solo salía su huella). Si el dueño configura la facturación en eleventa mientras se observa, la contraseña quedaba escrita en
    /// la comparación.
    /// </summary>
    [FactConFirebird]
    public async Task La_foto_de_observar_eleventa_no_escribe_la_clave_de_la_llave_privada_de_facturacion()
    {
        await using var e = await EleventaDePrueba.CrearAsync();
        // Como en la base de la tienda (2026-10-24): los mismos tipos (TLLAVE, TCADENALARGA, TFECHAHORA, TARCHIVO = BLOB binario).
        await e.EjecutarAsync("""
            CREATE TABLE FACTURACION_CERTIFICADOS (ID TLLAVE NOT NULL, EMISOR_ID TLLAVE NOT NULL, NUMERO_SERIE TCADENALARGA NOT NULL,
                VIGENCIA_INICIO TFECHAHORA NOT NULL, VIGENCIA_FIN TFECHAHORA NOT NULL, CLAVE_LLAVE_PRIVADA TCADENALARGA NOT NULL,
                CONTENIDO_CERTIFICADO BLOB SUB_TYPE 0, CONTENIDO_LLAVEPRIVADA BLOB SUB_TYPE 0, CONSTRAINT PK_FACTURACION_CERTIFICADOS PRIMARY KEY (ID))
            """);
        var antes = await FotoEleventa.TomarAsync(e.Conexion);
        await e.EjecutarAsync("""
            INSERT INTO FACTURACION_CERTIFICADOS (ID, EMISOR_ID, NUMERO_SERIE, VIGENCIA_INICIO, VIGENCIA_FIN, CLAVE_LLAVE_PRIVADA, CONTENIDO_LLAVEPRIVADA)
            VALUES (1, 1, '00001000000500000001', @desde, @hasta, 'ClaveSecretaDelCSD2026', @llave)
            """, ("desde", new DateTime(2026, 1, 1)), ("hasta", new DateTime(2030, 1, 1)), ("llave", new byte[] { 1, 2, 3, 4 }));

        var despues = await FotoEleventa.TomarAsync(e.Conexion);

        var comparacion = ComparacionEleventa.Comparar(antes, despues);
        Assert.Contains("FACTURACION_CERTIFICADOS", comparacion);
        Assert.Contains("NUMERO_SERIE=00001000000500000001", comparacion);
        Assert.DoesNotContain("ClaveSecretaDelCSD2026", comparacion);
        // Ni la llave privada: ni su huella.
        Assert.DoesNotContain("LLAVE", comparacion);
        Assert.DoesNotContain("blob 4 B", comparacion);
        Assert.Equal(["ID", "EMISOR_ID", "NUMERO_SERIE", "VIGENCIA_INICIO", "VIGENCIA_FIN", "CONTENIDO_CERTIFICADO"],
            despues.Tablas["FACTURACION_CERTIFICADOS"].Columnas);
    }
}
