using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Servicio;

/// <summary>Descuentos (entrega 5, parte 3): antes de cobrar, la revisión del costo.</summary>
public static class ApiDescuentos
{
    public static void MapearApiDescuentos(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").ConErrores().SoloSuCaja();

        // Antes de cobrar: ¿algún descuento deja una línea debajo del costo? Solo dice cuáles y cuánto falta, no el costo.
        api.MapPost("/descuentos/bajo-costo", async (SolicitudBajoCosto s, PosDbContext db, CatalogoEnMemoria catalogo, TimeProvider reloj) =>
        {
            var caja = await db.Cajas.FindAsync(s.CajaId) ?? throw new InvalidOperationException("La caja no existe.");
            // Con los precios que calculó la caja: con su mayoreo de cliente, si lo usó, y con las promociones del día con que las
            // puso (fase 3): el descuento va encima de la promoción.
            var (promociones, _) = await ApiCaja.PromocionesDelCobroAsync(catalogo, caja.SucursalId, s.DiaPromociones, reloj);
            var venta = await ApiCaja.ArmarVentaAsync(db, catalogo, caja, Guid.Empty, Guid.Empty, Guid.NewGuid(), s.Lineas, s.MayoreoDeCliente == true,
                promociones);
            var bajoCosto = await new RegistroCaja(db).BajoCostoAsync(venta);
            return new BajoCostoDto(bajoCosto.Sum(b => b.Costo - b.Linea.ImporteCentavos), bajoCosto.Select(b => b.Linea.Descripcion).ToList());
        });
    }
}
