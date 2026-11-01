using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones.Contratos;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Revisión independiente de «Servicios» (servicios/curp, 7c8e143; 2026-10-31), traída tal cual. Antes, un trabajo se tomaba como la copia
/// incluida de un trámite (EnTramite, sin precio, fuera de F2) solo por cómo se llamaba: cualquier título «T-» y cuatro números, solo o con
/// «.pdf», sin revisar que esta caja hubiera dado ese folio. Un archivo de un cliente que se llamara así (una factura, un ticket o un formato
/// «T-2026.pdf») salía gratis y no aparecía en F2. Ahora solo lo marca la caja que lo mandó desde su panel (TrabajosDeTramites).
/// </summary>
public sealed class RevisionServiciosModuloTests : IAsyncLifetime
{
    private BaseDelModulo _base = null!;

    private DateTime Ahora => _base.Pos.Hora.Ahora.UtcDateTime;

    public async Task InitializeAsync() => _base = await BaseDelModulo.CrearAsync();

    public async Task DisposeAsync() => await _base.DisposeAsync();

    private static TrabajoMedidoDto Trabajo(CajaDelPos caja, string documento, DateTime impresoUtc) =>
        new(Guid.NewGuid(), caja.Id, null, "CAJA2", "EPSON WF-6590 Series", 12, documento, "caja", impresoUtc, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            [new CoberturaPaginaDto(0, 0, 0, 0.08, 0.08, 93.5, false)], true, null, Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]),
            impresoUtc.AddSeconds(-15));

    [Theory]
    // El PDF de un cliente impreso desde Edge o Acrobat: el trabajo se llama como el archivo.
    [InlineData("T-2026.pdf")]
    [InlineData("t-10458.PDF")]
    // Un documento cuyo título es solo eso (Word, una página web): la expresión acepta el folio sin nada después.
    [InlineData("T-1234")]
    public async Task Un_archivo_de_un_cliente_que_se_llama_como_un_folio_se_cobra_en_F2(string documento)
    {
        var caja = _base.Pos.NuevaCaja("Caja 2");
        var delCliente = Trabajo(caja, documento, Ahora);
        await using (var db = _base.Contexto())
            await _base.Registro(db).RegistrarAsync(delCliente);

        await using var otro = _base.Contexto();
        var trabajo = await otro.Set<TrabajoImpresion>().SingleAsync(t => t.Id == delCliente.Id);
        Assert.True(trabajo.Cobro == EstadoCobroImpresion.PorCobrar,
            $"«{documento}» (de un cliente; ningún trámite de esta caja tiene ese folio) quedó {trabajo.Cobro}, a {trabajo.PrecioCentavos} centavos: "
            + "no sale en F2 y no se cobra.");
        Assert.True(trabajo.PrecioCentavos > 0);
        Assert.Single(await _base.Registro(otro).PorCobrarAsync(PosDePrueba.Sucursal));
    }
}
