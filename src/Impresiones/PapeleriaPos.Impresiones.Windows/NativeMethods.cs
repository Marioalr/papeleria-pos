// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PapeleriaPos.Impresiones.Windows;

internal static class NativeMethods
{
    public const int PRINTER_ACCESS_USE = 0x00000008;
    public const int PRINTER_CHANGE_JOB = 0x0000FF00;
    public const int JOB_CONTROL_DELETE = 5;
    public const int PRINTER_ATTRIBUTE_KEEPPRINTEDJOBS = 0x00000100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PRINTER_DEFAULTS
    {
        public IntPtr pDatatype;
        public IntPtr pDevMode;
        public int DesiredAccess;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOB_INFO_2
    {
        public int JobId;
        public IntPtr pPrinterName, pMachineName, pUserName, pDocument, pNotifyName, pDatatype,
            pPrintProcessor, pParameters, pDriverName, pDevMode, pStatus, pSecurityDescriptor;
        public int Status, Priority, Position, StartTime, UntilTime, TotalPages, Size;
        public SYSTEMTIME Submitted;
        public int Time, PagesPrinted;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTER_INFO_2
    {
        public IntPtr pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation,
            pDevMode, pSepFile, pPrintProcessor, pDatatype, pParameters, pSecurityDescriptor;
        public int Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenPrinterW")]
    public static extern bool OpenPrinter(string pPrinterName, out SafePrinterHandle phPrinter, ref PRINTER_DEFAULTS pDefault);

    /// <summary>Sin PRINTER_DEFAULTS: necesario para abrir "Cola,Job N" (el acceso lo decide el spooler).</summary>
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenPrinterW")]
    public static extern bool OpenPrinter(string pPrinterName, out SafePrinterHandle phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumJobsW")]
    public static extern bool EnumJobs(SafePrinterHandle hPrinter, int firstJob, int noJobs, int level,
        IntPtr pJob, int cbBuf, out int pcbNeeded, out int pcReturned);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetPrinterW")]
    public static extern bool GetPrinter(SafePrinterHandle hPrinter, int level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    public const int PRINTER_ALL_ACCESS = 0x000F000C;

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetPrinterW")]
    public static extern bool SetPrinter(SafePrinterHandle hPrinter, int level, IntPtr pPrinter, int command);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetJobW")]
    public static extern bool SetJob(SafePrinterHandle hPrinter, int jobId, int level, IntPtr pJob, int command);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ReadPrinter(SafePrinterHandle hPrinter, byte[] pBuf, int cbBuf, out int pNoBytesRead);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern IntPtr FindFirstPrinterChangeNotification(SafePrinterHandle hPrinter, int fdwFilter,
        int fdwOptions, IntPtr pPrinterNotifyOptions);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool FindNextPrinterChangeNotification(IntPtr hChange, out int pdwChange,
        IntPtr pPrinterNotifyOptions, IntPtr ppPrinterNotifyInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool FindClosePrinterChangeNotification(IntPtr hChange);
}

internal sealed class SafePrinterHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePrinterHandle() : base(true) { }
    protected override bool ReleaseHandle() => NativeMethods.ClosePrinter(handle);
}
