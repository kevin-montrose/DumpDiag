using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DumpDiag.Impl
{
    /// <summary>
    /// Helper for making sure that associated processes terminate when 
    /// the parent process does.
    /// </summary>
    internal sealed partial class Job : IDisposable
    {
        private static class PInvoke
        {
            [SupportedOSPlatform("windows")]
            internal static class Windows
            {
                internal enum JOBOBJECTINFOCLASS
                {
                    AssociateCompletionPortInformation = 7,
                    BasicLimitInformation = 2,
                    BasicUIRestrictions = 4,
                    EndOfJobTimeInformation = 6,
                    ExtendedLimitInformation = 9,
                    SecurityLimitInformation = 5,
                    GroupInformation = 11
                }

                [StructLayout(LayoutKind.Sequential)]
                internal struct SECURITY_ATTRIBUTES
                {
                    public int nLength;
                    public IntPtr lpSecurityDescriptor;
                    public int bInheritHandle;
                }

                [StructLayout(LayoutKind.Sequential)]
                internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    public Int64 PerProcessUserTimeLimit;
                    public Int64 PerJobUserTimeLimit;
                    public JOBOBJECTLIMIT LimitFlags;
                    public UIntPtr MinimumWorkingSetSize;
                    public UIntPtr MaximumWorkingSetSize;
                    public UInt32 ActiveProcessLimit;
                    public Int64 Affinity;
                    public UInt32 PriorityClass;
                    public UInt32 SchedulingClass;
                }

                [StructLayout(LayoutKind.Sequential)]
                internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                    public IO_COUNTERS IoInfo;
                    public UIntPtr ProcessMemoryLimit;
                    public UIntPtr JobMemoryLimit;
                    public UIntPtr PeakProcessMemoryUsed;
                    public UIntPtr PeakJobMemoryUsed;
                }

                [StructLayout(LayoutKind.Sequential)]
                internal struct IO_COUNTERS
                {
                    public UInt64 ReadOperationCount;
                    public UInt64 WriteOperationCount;
                    public UInt64 OtherOperationCount;
                    public UInt64 ReadTransferCount;
                    public UInt64 WriteTransferCount;
                    public UInt64 OtherTransferCount;
                }

                [Flags]
                internal enum JOBOBJECTLIMIT : uint
                {
                    // Basic Limits
                    Workingset = 0x00000001,
                    ProcessTime = 0x00000002,
                    JobTime = 0x00000004,
                    ActiveProcess = 0x00000008,
                    Affinity = 0x00000010,
                    PriorityClass = 0x00000020,
                    PreserveJobTime = 0x00000040,
                    SchedulingClass = 0x00000080,

                    // Extended Limits
                    ProcessMemory = 0x00000100,
                    JobMemory = 0x00000200,
                    DieOnUnhandledException = 0x00000400,
                    BreakawayOk = 0x00000800,
                    SilentBreakawayOk = 0x00001000,
                    KillOnJobClose = 0x00002000,
                    SubsetAffinity = 0x00004000,

                    // Notification Limits
                    JobReadBytes = 0x00010000,
                    JobWriteBytes = 0x00020000,
                    RateControl = 0x00040000,
                }


                [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
                internal static extern IntPtr CreateJobObject([In] ref SECURITY_ATTRIBUTES lpJobAttributes, string lpName);

                [DllImport("kernel32.dll")]
                internal static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

                [DllImport("kernel32.dll", SetLastError = true)]
                internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

                [DllImport("kernel32.dll", SetLastError = true)]
                internal static extern bool CloseHandle(IntPtr hObject);
            }
        }

        private sealed class JobHandle : SafeHandle
        {
            public override bool IsInvalid => handle == IntPtr.MinValue;

            public JobHandle() : base(IntPtr.MinValue, false)
            {
            }

            public JobHandle(IntPtr handle) : base(handle, true)
            {
            }

            protected override bool ReleaseHandle()
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        return PInvoke.Windows.CloseHandle(handle);
                    }
                    else
                    {
                        throw new NotImplementedException("Not implemented for this platform");
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        internal static readonly Job Instance = Create();

        private readonly JobHandle handle;

        private Job(JobHandle handle)
        {
            this.handle = handle;
        }

        public void Dispose()
        {
            handle.Dispose();
        }

        internal void AssociateProcess(Process process)
        {
            if (OperatingSystem.IsWindows())
            {
                AssociateWindowsProcess(process);
                return;
            }
            else
            {
                throw new NotImplementedException("Not implemented for this platform");
            }
        }

        [SupportedOSPlatform("windows")]
        private void AssociateWindowsProcess(Process proc)
        {
            PInvoke.Windows.AssignProcessToJobObject(handle.DangerousGetHandle(), proc.Handle);
        }

        private static Job Create()
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateWindowsSingleton();
            }
            else
            {
                throw new NotImplementedException("Not implemented for this platform");
            }
        }

        [SupportedOSPlatform("windows")]
        private static Job CreateWindowsSingleton()
        {
            var attrs = new PInvoke.Windows.SECURITY_ATTRIBUTES();
            var handle = new JobHandle(PInvoke.Windows.CreateJobObject(ref attrs, null));

            var info = new PInvoke.Windows.JOBOBJECT_BASIC_LIMIT_INFORMATION();
            info.LimitFlags = PInvoke.Windows.JOBOBJECTLIMIT.KillOnJobClose;

            var extendedInfo = new PInvoke.Windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            extendedInfo.BasicLimitInformation = info;

            var length = Marshal.SizeOf(typeof(PInvoke.Windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            var extendedInfoPtr = Marshal.AllocHGlobal(length);
            Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);
            if (!PInvoke.Windows.SetInformationJobObject(handle.DangerousGetHandle(), PInvoke.Windows.JOBOBJECTINFOCLASS.ExtendedLimitInformation, extendedInfoPtr, (uint)length))
            {
                throw new InvalidOperationException($"Failed to set information job object: {Marshal.GetLastWin32Error()}");
            }

            return new Job(handle);
        }
    }
}
