using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;

namespace PapeleriaPos.Pruebas.Datos;

/// <summary>
/// El modelo de la base (lo que dicen las clases y su mapeo) es el mismo que dejó la última migración. Si alguien cambia
/// una tabla y olvida su migración, esto falla aquí y no en la base de la tienda, que se quedaría sin el cambio. Mover
/// clases de proyecto o de espacio de nombres no cambia el esquema: esto también lo comprueba.
/// </summary>
public class MigracionesTests
{
    [Fact]
    public void El_modelo_no_tiene_cambios_sin_su_migracion()
    {
        using var db = new PosDbContext(BaseDeDatos.Opciones("Data Source=:memory:"));

        Assert.False(db.Database.HasPendingModelChanges(),
            "El modelo cambió y falta su migración: dotnet ef migrations add <Nombre> -p src/PapeleriaPos.Datos -s src/PapeleriaPos.Datos -o Migraciones");
    }
}
