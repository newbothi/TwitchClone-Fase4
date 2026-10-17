using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation
{
    // En Windows, si la app se cierra de golpe (Detener depuración en Visual Studio,
    // un crash, matar el proceso), los ffmpeg hijos quedarían vivos transmitiendo solos
    // y el canal seguiría EN VIVO. Para evitarlo se meten en un "Job Object" de Windows
    // con KILL_ON_JOB_CLOSE: cuando muere la app, Windows cierra el job y mata a sus hijos.
    //
    // En Linux/macOS no hace nada (allí el cierre normal ya los detiene en StopAsync).
    internal static class ChildProcessTracker
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private static readonly Lazy<IntPtr> Job = new(CreateJob);

        public static void Track(Process process)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                var job = Job.Value;
                if (job != IntPtr.Zero) AssignProcessToJobObject(job, process.Handle);
            }
            catch
            {
                // Si no se puede (permisos, proceso ya terminado), se sigue igual:
                // el cierre normal de la app los detiene de todas formas.
            }
        }

        private static IntPtr CreateJob()
        {
            if (!OperatingSystem.IsWindows()) return IntPtr.Zero;

            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JobObjectLimitKillOnJobClose }
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            // El handle se deja abierto a propósito: se cierra solo cuando termina la app.
            return job;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
