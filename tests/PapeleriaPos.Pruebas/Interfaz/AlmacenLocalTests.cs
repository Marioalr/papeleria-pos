using Microsoft.Data.Sqlite;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>La base local de una caja ya instalada se pone al día sin perder lo que tenía.</summary>
public sealed class AlmacenLocalTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"almacen-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _ruta, _ruta + "-wal", _ruta + "-shm" })
            File.Delete(archivo);
    }

    [Fact]
    public void Una_base_de_antes_del_credito_gana_su_cuenta_propia_y_conserva_el_diario()
    {
        var turno = Ids.Nuevo();
        using (var conexion = new SqliteConnection($"Data Source={_ruta}"))
        {
            conexion.Open();
            using var comando = conexion.CreateCommand();
            comando.CommandText = $"""
                CREATE TABLE Ajustes (Clave TEXT PRIMARY KEY, Valor TEXT NOT NULL);
                CREATE TABLE Bandeja (Orden INTEGER PRIMARY KEY AUTOINCREMENT, Id TEXT NOT NULL UNIQUE, Ruta TEXT NOT NULL, Json TEXT NOT NULL,
                    CreadoUtc TEXT NOT NULL, SubidoUtc TEXT, Error TEXT);
                CREATE TABLE Diario (Id TEXT PRIMARY KEY, TurnoId TEXT NOT NULL, Ventas INTEGER NOT NULL, Efectivo INTEGER NOT NULL, Tarjeta INTEGER NOT NULL,
                    Transferencia INTEGER NOT NULL, Entradas INTEGER NOT NULL, Salidas INTEGER NOT NULL, Devoluciones INTEGER NOT NULL,
                    Cancelaciones INTEGER NOT NULL);
                INSERT INTO Diario VALUES ('{Ids.Nuevo()}', '{turno}', 1, 5000, 0, 0, 0, 0, 0, 0);
                """;
            comando.ExecuteNonQuery();
        }

        var almacen = new AlmacenLocal(_ruta);
        var cliente = Ids.Nuevo();
        using (var lote = almacen.Lote())
            lote.Credito(Ids.Nuevo(), cliente, 2500, DateTime.UtcNow, new DateOnly(2026, 11, 7)).Confirmar();

        Assert.Equal((1, 5000L, 0L), (almacen.Diario(turno).Ventas, almacen.Diario(turno).Efectivo, almacen.Diario(turno).Credito));
        var fila = Assert.Single(almacen.CuentaPropia(cliente));
        Assert.Equal((2500L, new DateOnly(2026, 11, 7), false), (fila.Local.MontoCentavos, fila.Local.Vence, fila.Local.PorVerificar));
    }

    [Fact]
    public void Lo_anotado_hace_rato_que_nunca_paso_por_la_bandeja_se_quita_y_lo_demas_se_queda()
    {
        var almacen = new AlmacenLocal(_ruta);
        var cliente = Ids.Nuevo();
        var (huerfano, enBandeja, subido, reciente) = (Ids.Nuevo(), Ids.Nuevo(), Ids.Nuevo(), Ids.Nuevo());
        var antes = DateTime.UtcNow.AddMinutes(-5);
        using (var lote = almacen.Lote())
            lote.Credito(huerfano, cliente, 1000, antes, null).Credito(enBandeja, cliente, 2000, antes, null).Credito(subido, cliente, 4000, antes, null)
                .Credito(reciente, cliente, 3000, DateTime.UtcNow, null)
                .Encolar(enBandeja, "api/bandeja/ventas", new { Id = enBandeja }).Encolar(subido, "api/bandeja/abonos", new { Id = subido })
                .Confirmar();
        // Lo que ya subió (el servicio lo tiene, aunque la foto de sus saldos fuera de antes) tampoco es huérfano.
        almacen.MarcarSubido(almacen.PorSubir(conErrores: true).Single(e => e.Id == subido).Orden, DateTime.UtcNow);

        using (var lote = almacen.Lote())
            lote.QuitarCreditoHuerfano([huerfano, enBandeja, subido, reciente], DateTime.UtcNow.AddMinutes(-2)).Confirmar();

        Assert.Equal(new[] { enBandeja, subido, reciente }.Order(), almacen.CuentaPropia(cliente).Select(f => f.Id).Order());
    }

    [Fact]
    public void Lo_confirmado_deja_de_contarse_y_no_revive_si_se_vuelve_a_anotar()
    {
        var almacen = new AlmacenLocal(_ruta);
        var cliente = Ids.Nuevo();
        var id = Ids.Nuevo();
        using (var lote = almacen.Lote())
            lote.Credito(id, cliente, 5000, DateTime.UtcNow, null).Confirmar();

        using (var lote = almacen.Lote())
            lote.ConfirmarCredito([id], DateTime.UtcNow.AddDays(-1)).Confirmar();
        using (var lote = almacen.Lote())
            lote.Credito(id, cliente, 5000, DateTime.UtcNow, null).Confirmar();

        Assert.Empty(almacen.CuentaPropia(cliente));
        Assert.True(almacen.CreditoConfirmado(id));
    }
}
