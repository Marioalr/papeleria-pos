using System.Globalization;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones;

/// <summary>
/// Qué hace el agente con un trabajo que ya salió de la impresora cuando lo vio por primera vez (docs/plan.md, «Recomendaciones»;
/// 2026-11-09, por revisar). Con «Conservar documentos impresos», si la caja estuvo cerrada, Windows guardó en la cola lo que se imprimió
/// mientras tanto, de horas o días antes; al abrir la caja, el agente lo encuentra ya impreso. Antes lo mandaba con la hora en que lo vio,
/// y salía en F2 como impresión de hoy por cobrar (y en el desglose de hoy, y contra el contador de hoy).
/// <list type="bullet">
/// <item>Lo de hasta <see cref="Vigencia"/> se manda con la hora en que entró a la cola (<c>SubmittedUtc</c>): es lo más cercano que se
/// sabe de cuándo salió. Así queda en su día, en su lote de F2 («dom 08/11 19:30», no «Hoy») y en la lectura del contador que lo
/// contó.</item>
/// <item>Lo de antes no se manda: ya no se cobra a nadie, y su documento es de un cliente. Se cuenta como visto, para que el agente lo
/// quite de la cola igual que lo mandado; si no, se quedaría ahí para siempre.</item>
/// </list>
/// Lo que el agente vio en la cola antes de que saliera (lo normal, con la caja abierta) se manda como siempre, con la hora en que lo vio
/// salir: esa es la de verdad, aunque haya esperado horas en la cola (la impresora apagada o sin papel).
/// </summary>
public static class TrabajoDeLaCola
{
    /// <summary>Lo que salió hace más de esto (contado desde que entró a la cola) no se sube: un día, como lo que se recuerda de los trámites.</summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromDays(1);

    /// <summary>Lo que el reloj de la PC y el de la cola pueden no coincidir: una hora de envío un poco adelantada no es un error.</summary>
    public static readonly TimeSpan Holgura = TimeSpan.FromMinutes(1);

    /// <summary>Una hora de envío de antes de esto no es de verdad (la cola la dio vacía o dañada): se toma la hora en que se vio.</summary>
    public static readonly DateTime FechaMinima = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Si se sube y con qué hora de impresión (<see cref="Contratos.TrabajoMedidoDto.ImpresoUtc"/>).
    /// </summary>
    /// <param name="enviadoUtc">Cuando entró a la cola (<c>SpoolJob.SubmittedUtc</c>; si Windows la da inválida, la cola pone la hora en que la leyó).</param>
    /// <param name="ahoraUtc">Cuando el agente lo ve.</param>
    /// <param name="salioSinVerlo">
    /// Ya había salido (impreso o con error) la primera vez que el agente lo vio: salió mientras el agente no corría. Si el agente lo vio
    /// antes en la cola, sale con <paramref name="ahoraUtc"/>, como siempre.
    /// </param>
    public static DecisionDeLaCola Decidir(DateTime enviadoUtc, DateTime ahoraUtc, bool salioSinVerlo)
    {
        if (!salioSinVerlo)
            return new DecisionDeLaCola(true, ahoraUtc);
        // Una hora que no sirve (de antes del 2020, o del futuro: el reloj de la PC se movió) no dice cuándo salió: como siempre.
        if (enviadoUtc < FechaMinima || enviadoUtc > ahoraUtc + Holgura)
            return new DecisionDeLaCola(true, ahoraUtc);
        if (ahoraUtc - enviadoUtc > Vigencia)
            return new DecisionDeLaCola(false, enviadoUtc);
        return new DecisionDeLaCola(true, enviadoUtc < ahoraUtc ? enviadoUtc : ahoraUtc);
    }

    /// <summary>
    /// La línea del registro de la caja por un trabajo que no se subió. Sin el nombre del documento: es de un cliente (o el folio de un
    /// trámite), y el registro sale de la tienda en la copia del piloto. Con su número en la cola y la hora basta para encontrarlo.
    /// </summary>
    public static string NoSeSube(string cola, int jobId, DateTime enviadoUtc, int paginas, bool seQuita) =>
        string.Create(CultureInfo.InvariantCulture,
            $"El trabajo {jobId} de «{cola}» ({paginas} pág.) entró a la cola el {HoraMexico.ALocal(enviadoUtc):dd/MM/yyyy HH:mm}, hace más de un " +
            $"día: no se sube a la caja principal ni sale en F2{(seQuita ? "; se quita de la cola" : "")}.");
}

/// <summary>Lo que decide <see cref="TrabajoDeLaCola.Decidir"/>.</summary>
/// <param name="Subir">Si se manda a la caja principal.</param>
/// <param name="ImpresoUtc">La hora de impresión con que se manda.</param>
public readonly record struct DecisionDeLaCola(bool Subir, DateTime ImpresoUtc);
