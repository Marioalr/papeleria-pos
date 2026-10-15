// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.
using System.ComponentModel;
using System.Runtime.InteropServices;
using static PapeleriaPos.Impresiones.Windows.NativeMethods;

namespace PapeleriaPos.Impresiones.Windows;

/// <summary>Acceso a una cola de impresión local mediante winspool: trabajos, datos de spool y borrado.</summary>
public sealed class PrintQueueClient : IDisposable
{
    readonly SafePrinterHandle handle;

    public string QueueName { get; }

    public PrintQueueClient(string queueName)
    {
        QueueName = queueName;
        handle = Open(queueName);
    }

    internal SafePrinterHandle Handle => handle;

    internal static SafePrinterHandle Open(string name)
    {
        var defaults = new PRINTER_DEFAULTS { DesiredAccess = PRINTER_ACCESS_USE };
        if (!OpenPrinter(name, out var h, ref defaults)) throw Error($"No se pudo abrir la cola '{name}'");
        return h;
    }

    static SafePrinterHandle OpenJob(string queue, int jobId)
    {
        string name = $"{queue},Job {jobId}";
        if (!OpenPrinter(name, out var h, IntPtr.Zero)) throw Error($"No se pudo abrir el trabajo '{name}'");
        return h;
    }

    static Win32Exception Error(string context)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{context}: {new Win32Exception(code).Message} ({code})");
    }

    public (string DriverName, string PortName, bool KeepPrintedJobs) GetPrinterInfo()
    {
        GetPrinter(handle, 2, IntPtr.Zero, 0, out int needed);
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetPrinter(handle, 2, buf, needed, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetPrinter");
            var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buf);
            return (Str(info.pDriverName), Str(info.pPortName),
                (info.Attributes & PRINTER_ATTRIBUTE_KEEPPRINTEDJOBS) != 0);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Activa o desactiva "Conservar documentos impresos" (requiere permisos de administrador).</summary>
    public static void SetKeepPrintedJobs(string queueName, bool keep)
    {
        var defaults = new PRINTER_DEFAULTS { DesiredAccess = PRINTER_ALL_ACCESS };
        if (!OpenPrinter(queueName, out var h, ref defaults)) throw Error($"No se pudo abrir '{queueName}' como administrador");
        using (h)
        {
            GetPrinter(h, 2, IntPtr.Zero, 0, out int needed);
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetPrinter(h, 2, buf, needed, out _)) throw Error("GetPrinter");
                var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buf);
                info.Attributes = keep ? info.Attributes | PRINTER_ATTRIBUTE_KEEPPRINTEDJOBS
                                       : info.Attributes & ~PRINTER_ATTRIBUTE_KEEPPRINTEDJOBS;
                info.pSecurityDescriptor = IntPtr.Zero; // no modificar permisos
                Marshal.StructureToPtr(info, buf, false);
                if (!SetPrinter(h, 2, buf, 0)) throw Error("SetPrinter");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    public IReadOnlyList<SpoolJob> GetJobs()
    {
        // El número de trabajos puede cambiar entre la consulta de tamaño y la lectura; se reintenta.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            EnumJobs(handle, 0, int.MaxValue, 2, IntPtr.Zero, 0, out int needed, out _);
            if (needed == 0) return [];
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (!EnumJobs(handle, 0, int.MaxValue, 2, buf, needed, out _, out int count))
                {
                    if (Marshal.GetLastWin32Error() == 122 /* ERROR_INSUFFICIENT_BUFFER */) continue;
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumJobs");
                }
                var jobs = new List<SpoolJob>(count);
                int size = Marshal.SizeOf<JOB_INFO_2>();
                for (int i = 0; i < count; i++)
                    jobs.Add(ToJob(Marshal.PtrToStructure<JOB_INFO_2>(buf + i * size)));
                return jobs;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return [];
    }

    /// <summary>Abre los datos de spool del trabajo (EMF o RAW) como flujo de solo lectura.</summary>
    public Stream OpenJobData(int jobId) => new JobDataStream(OpenJob(QueueName, jobId));

    /// <summary>Elimina el trabajo de la cola (el dueño del trabajo tiene permiso por defecto).</summary>
    public bool DeleteJob(int jobId) => SetJob(handle, jobId, 0, IntPtr.Zero, JOB_CONTROL_DELETE);

    public void Dispose() => handle.Dispose();

    static SpoolJob ToJob(JOB_INFO_2 j)
    {
        var st = j.Submitted;
        DateTime submitted;
        try { submitted = new DateTime(st.Year, st.Month, st.Day, st.Hour, st.Minute, st.Second, st.Milliseconds, DateTimeKind.Utc); }
        catch (ArgumentOutOfRangeException) { submitted = DateTime.UtcNow; }
        return new SpoolJob(j.JobId, Str(j.pDocument), Str(j.pUserName), Str(j.pMachineName).TrimStart('\\'),
            Str(j.pDatatype), (SpoolJobStatus)j.Status, j.TotalPages, j.PagesPrinted, (uint)j.Size, submitted,
            CopyDevMode(j.pDevMode));
    }

    static string Str(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p) ?? "";

    static byte[]? CopyDevMode(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        int size = (ushort)Marshal.ReadInt16(p, 68) + (ushort)Marshal.ReadInt16(p, 70); // dmSize + dmDriverExtra
        var raw = new byte[size];
        Marshal.Copy(p, raw, 0, size);
        return raw;
    }

    sealed class JobDataStream(SafePrinterHandle job) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            byte[] target = offset == 0 ? buffer : new byte[count];
            if (!ReadPrinter(job, target, count, out int read))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadPrinter");
            if (offset != 0) Buffer.BlockCopy(target, 0, buffer, offset, read);
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) job.Dispose();
            base.Dispose(disposing);
        }
    }
}
