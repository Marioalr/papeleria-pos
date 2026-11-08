using PapeleriaPos.Eleventa;

namespace PapeleriaPos.Pruebas.Eleventa;

/// <summary>
/// Los usuarios de eleventa, leídos de un Firebird de prueba (el piloto alfa, 2026-10-25): solo su número, su usuario, su
/// nombre y si siguen activos. En la base de prueba la clave (<c>CLAVE</c>) y los permisos (<c>PERMISOS</c>) truenan si alguien
/// los lee (<see cref="EleventaDePrueba"/>): la importación, la lectura de cada pocos minutos y la foto de observar-eleventa
/// pasan sin leerlos.
/// </summary>
public class UsuariosEnFirebirdTests
{
    private static async Task<EleventaDePrueba> TiendaAsync()
    {
        var e = await EleventaDePrueba.CrearAsync();
        // Como en la tienda: ACTIVO '1', '0' o vacío, y ELIMINADO_EN en los borrados.
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO) VALUES (3, 'María López', 'cajera2', '1')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO) VALUES (7, 'Luis Pérez', 'luis', '1')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO) VALUES (5, 'Temporal', 'temporal', '0')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO, ELIMINADO_EN) VALUES (6, 'Se Fue', 'sefue', NULL, @cuando)",
            ("cuando", new DateTime(2025, 6, 1)));
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, USUARIO) VALUES (8, 'rosa')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        await e.ProductoAsync(10, "PLU-FINA", "Pluma punto fino", depto: 5, costo: 4, precio: 9, existencia: 50);
        return e;
    }

    [FactConFirebird]
    public async Task La_lectura_trae_a_los_usuarios_sin_leer_su_clave_ni_sus_permisos()
    {
        await using var e = await TiendaAsync();
        // La base de prueba sí truena si se lee la clave: lo que se prueba aquí es que nadie la lee.
        await Assert.ThrowsAnyAsync<Exception>(() => e.FilasAsync("SELECT CLAVE FROM USUARIOS"));

        var datos = await LectorEleventa.LeerCatalogoAsync(e.Conexion);

        Assert.Equal(
            [new FilaUsuario(3, "cajera2", "María López", true, false), new FilaUsuario(5, "temporal", "Temporal", false, false),
                new FilaUsuario(6, "sefue", "Se Fue", true, true), new FilaUsuario(7, "luis", "Luis Pérez", true, false),
                new FilaUsuario(8, "rosa", "", true, false)],
            datos.Usuarios!.OrderBy(u => u.Id));
        Assert.Equal([3, 7, 8], datos.Usuarios!.Where(u => u.Vigente).Select(u => u.Id).Order());
    }

    [FactConFirebird]
    public async Task La_importacion_lee_a_los_usuarios_sin_su_clave()
    {
        await using var e = await TiendaAsync();

        var datos = await LectorEleventa.LeerAsync(e.Conexion);

        Assert.Equal(5, datos.Usuarios!.Count);
        Assert.Single(datos.Productos);
    }

    /// <summary>
    /// observar-eleventa guarda lo que cambió en archivos de texto: antes leía la tabla completa (<c>SELECT *</c>), y un usuario
    /// nuevo o una clave cambiada salían ahí. Ahora la foto lleva a los usuarios sin su clave ni sus permisos.
    /// </summary>
    [FactConFirebird]
    public async Task La_foto_de_observar_eleventa_no_lee_la_clave_ni_los_permisos()
    {
        await using var e = await TiendaAsync();
        var antes = await FotoEleventa.TomarAsync(e.Conexion);
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, NOMBRE_COMPLETO, USUARIO, ACTIVO) VALUES (9, 'Carla Díaz', 'carla', '1')");

        var despues = await FotoEleventa.TomarAsync(e.Conexion);

        var usuarios = despues.Tablas["USUARIOS"];
        Assert.DoesNotContain("CLAVE", usuarios.Columnas);
        Assert.DoesNotContain("PERMISOS", usuarios.Columnas);
        Assert.Contains("NOMBRE_COMPLETO", usuarios.Columnas);
        Assert.Equal(6, usuarios.Renglones.Count);
        var cambios = ComparacionEleventa.Comparar(antes, despues);
        Assert.Contains("+ ID=9: NOMBRE_COMPLETO=Carla Díaz, USUARIO=carla, ACTIVO=1", cambios);
        Assert.DoesNotContain("CLAVE", cambios);
    }
}
