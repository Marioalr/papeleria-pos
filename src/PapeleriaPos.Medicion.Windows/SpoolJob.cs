// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.
namespace PapeleriaPos.Medicion;

/// <summary>Instantánea de un trabajo en la cola de Windows (JOB_INFO_2).</summary>
public sealed record SpoolJob(
    int JobId,
    string Document,
    string UserName,
    string MachineName,
    string Datatype,
    SpoolJobStatus Status,
    int TotalPages,
    int PagesPrinted,
    long Size,
    DateTime SubmittedUtc,
    byte[]? DevMode)
{
    public bool IsSpooling => Status.HasFlag(SpoolJobStatus.Spooling);
    public bool IsPrinted => Status.HasFlag(SpoolJobStatus.Printed) || Status.HasFlag(SpoolJobStatus.Complete);
    public bool IsDeleting => Status.HasFlag(SpoolJobStatus.Deleting) || Status.HasFlag(SpoolJobStatus.Deleted);
    public bool IsEmf => Datatype.StartsWith("NT EMF", StringComparison.OrdinalIgnoreCase);
}

[Flags]
public enum SpoolJobStatus
{
    None = 0,
    Paused = 0x1,
    Error = 0x2,
    Deleting = 0x4,
    Spooling = 0x8,
    Printing = 0x10,
    Offline = 0x20,
    PaperOut = 0x40,
    Printed = 0x80,
    Deleted = 0x100,
    BlockedDevQueue = 0x200,
    UserIntervention = 0x400,
    Restart = 0x800,
    Complete = 0x1000,
    Retained = 0x2000,
}
