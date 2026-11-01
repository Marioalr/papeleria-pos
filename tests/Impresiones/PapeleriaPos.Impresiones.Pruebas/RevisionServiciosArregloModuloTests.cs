using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Pruebas;

/// <summary>
/// Segunda revisión de «Servicios» (servicios/curp, ad0a76c: lo que se arregló de la primera; 2026-10-31), traída al repositorio tal cual,
/// con su arreglo (lo anotado se recuerda un día y se guarda en la base de la caja). Lo que debería pasar; en ad0a76c fallaba.
/// </summary>
public sealed class RevisionServiciosArregloModuloTests
{
    /// <summary>
    /// <see cref="TrabajosDeTramites"/> reconoce un trabajo si entró a la cola entre 1 minuto antes y 5 después de mandarlo (TrabajoDeTramite.cs:76),
    /// pero <see cref="TrabajosDeTramites.Enviado"/> borra lo anotado hace más de una hora, por su edad (:58), aunque su trabajo siga en la cola.
    /// El agente mide un trabajo cuando Windows ya lo imprimió (AgenteImpresion.cs:162-168): si la impresora estaba apagada, sin papel o
    /// atascada más de una hora y mientras tanto salió otra CURP, la copia incluida del primer trámite se manda como una impresión de un
    /// cliente: con su miniatura (la CURP de la persona, AgenteImpresion.cs:211-212) y por cobrar en F2 (RegistroImpresiones.cs:91-107),
    /// aunque ya se cobró con la CURP. Lo mismo si la caja se cierra o se reinicia antes de que se imprima: lo anotado vive en memoria.
    /// </summary>
    [Fact]
    public void La_copia_de_un_tramite_que_sale_tarde_de_la_impresora_se_sigue_reconociendo()
    {
        var reloj = new RelojFijo(new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero));
        var trabajos = new TrabajosDeTramites(reloj);
        trabajos.Enviado("T-0042 · CURP", new TrabajoDeTramite("T-0042", CopiasExtra: false));
        // Entró a la cola de Windows a los 5 segundos: a tiempo. La impresora no lo imprime todavía (apagada, sin papel).
        var entroALaCola = reloj.GetUtcNow().UtcDateTime.AddSeconds(5);

        // Una hora y un minuto después sale otra CURP (en otra impresora, o ya con la impresora de vuelta)…
        reloj.Ahora = reloj.Ahora.AddMinutes(61);
        trabajos.Enviado("T-0043 · CURP", new TrabajoDeTramite("T-0043", CopiasExtra: false));

        // …y el agente mide ahora la copia de T-0042, que por fin salió.
        var tramite = trabajos.De("T-0042 · CURP", entroALaCola);
        Assert.True(tramite == new TrabajoDeTramite("T-0042", false),
            "La copia incluida de T-0042 entró a la cola a los 5 s de mandarse, pero ya no se reconoce: va como impresión de un cliente, con su "
            + "miniatura (la CURP de la persona) y por cobrar en F2, aunque se cobró con la CURP.");
    }
}
