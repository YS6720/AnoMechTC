using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnoMech.Multiplayer;

/// <summary>
/// Owns one cloudflared process and the Windows job object that contains it. The process is
/// created with its job assignment in the initial CreateProcessW call; it is never started first
/// and assigned later.
/// </summary>
public sealed class HostedTunnelProcess : IDisposable, IAsyncDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint CreateSuspended = 0x00000004;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const long ProcThreadAttributeJobList = 0x0002000D;
    private const long ProcThreadAttributeHandleList = 0x00020002;
    private const uint StillActive = 259;
    private const uint Infinite = 0xFFFFFFFF;
    private const int ErrorInsufficientBuffer = 122;

    private readonly object sync = new();
    private IntPtr jobHandle;
    private IntPtr processHandle;
    private bool disposed;

    private HostedTunnelProcess(IntPtr jobHandle, IntPtr processHandle)
    {
        this.jobHandle = jobHandle;
        this.processHandle = processHandle;
    }

    public bool IsRunning
    {
        get
        {
            lock (sync)
            {
                if (disposed || processHandle == IntPtr.Zero) return false;
                return GetExitCodeProcess(processHandle, out var code) && code == StillActive;
            }
        }
    }

    public static HostedTunnelProcess Start(string executable, string configPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var executablePath = RequireExistingFile(executable);
        var tunnelConfigPath = RequireExistingFile(configPath);
        return StartWindows(executablePath, tunnelConfigPath);
    }

    public static Task<HostedTunnelProcess> StartAsync(
        string executable, string configPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Start(executable, configPath));
    }

    public void Dispose()
    {
        IntPtr process;
        IntPtr job;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            process = processHandle;
            job = jobHandle;
            processHandle = IntPtr.Zero;
            jobHandle = IntPtr.Zero;
        }

        if (process != IntPtr.Zero)
        {
            try { TerminateProcess(process, 1); } catch { }
            try { WaitForSingleObject(process, 5000); } catch { }
        }

        // Closing the job is the final containment fallback if termination or waiting raced.
        if (job != IntPtr.Zero)
        {
            try { CloseHandle(job); } catch { }
            try { if (process != IntPtr.Zero) WaitForSingleObject(process, 5000); } catch { }
        }
        if (process != IntPtr.Zero)
        {
            try { CloseHandle(process); } catch { }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static HostedTunnelProcess StartWindows(string executablePath, string configPath)
    {
        IntPtr job = IntPtr.Zero;
        IntPtr process = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        IntPtr standardInput = IntPtr.Zero;
        IntPtr standardOutput = IntPtr.Zero;
        IntPtr standardError = IntPtr.Zero;
        var attributesInitialized = false;
        GCHandle jobsPinned = default;
        var jobsPinnedAllocated = false;
        GCHandle standardHandlesPinned = default;
        var standardHandlesPinnedAllocated = false;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            EnsureHandle(job);
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            if (!SetInformationJobObject(job, ExtendedLimitInformationClass,
                    ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                throw StartFailure();

            var attributeSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attributeSize);
            if (attributeSize == IntPtr.Zero ||
                Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                throw StartFailure();
            attributes = Marshal.AllocHGlobal(attributeSize);
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref attributeSize))
                throw StartFailure();
            attributesInitialized = true;

            var jobList = new[] { job };
            jobsPinned = GCHandle.Alloc(jobList, GCHandleType.Pinned);
            jobsPinnedAllocated = true;
            if (!UpdateProcThreadAttribute(
                    attributes,
                    0,
                    (IntPtr)ProcThreadAttributeJobList,
                    jobsPinned.AddrOfPinnedObject(),
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                throw StartFailure();

            standardInput = CreateNullHandle(GenericRead);
            standardOutput = CreateNullHandle(GenericWrite);
            standardError = CreateNullHandle(GenericWrite);
            var inheritedHandles = new[] { standardInput, standardOutput, standardError };
            standardHandlesPinned = GCHandle.Alloc(inheritedHandles, GCHandleType.Pinned);
            standardHandlesPinnedAllocated = true;
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)ProcThreadAttributeHandleList,
                    standardHandlesPinned.AddrOfPinnedObject(), (IntPtr)(IntPtr.Size * inheritedHandles.Length),
                    IntPtr.Zero, IntPtr.Zero))
                throw StartFailure();
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    dwFlags = StartfUseStdHandles,
                    hStdInput = standardInput,
                    hStdOutput = standardOutput,
                    hStdError = standardError,
                },
                lpAttributeList = attributes,
            };
            var processInfo = new ProcessInformation();
            environment = BuildEnvironmentBlock();
            var commandLine = new StringBuilder(BuildCommandLine(executablePath, configPath));
            var creationFlags = CreateSuspended | ExtendedStartupInfoPresent |
                CreateUnicodeEnvironment | CreateNoWindow;
            if (!CreateProcessW(
                    executablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    creationFlags,
                    environment,
                    IntPtr.Zero,
                    ref startup,
                    out processInfo))
                throw StartFailure();
            process = processInfo.hProcess;
            thread = processInfo.hThread;
            if (process == IntPtr.Zero || thread == IntPtr.Zero)
                throw StartFailure();
            if (!IsProcessInJob(process, job, out var inJob) || !inJob)
                throw StartFailure();
            if (ResumeThread(thread) == Infinite)
                throw StartFailure();

            var ownedJob = job;
            var ownedProcess = process;
            job = IntPtr.Zero;
            process = IntPtr.Zero;
            return new HostedTunnelProcess(ownedJob, ownedProcess);
        }
        catch
        {
            if (process != IntPtr.Zero)
            {
                try { TerminateProcess(process, 1); } catch { }
                try { WaitForSingleObject(process, 5000); } catch { }
            }
            throw new InvalidOperationException("Hosted tunnel could not be started.");
        }
        finally
        {
            if (attributesInitialized)
            {
                try { DeleteProcThreadAttributeList(attributes); } catch { }
            }
            if (attributes != IntPtr.Zero)
            {
                try { Marshal.FreeHGlobal(attributes); } catch { }
            }
            if (jobsPinnedAllocated)
            {
                try { jobsPinned.Free(); } catch { }
            }
            if (standardHandlesPinnedAllocated)
                standardHandlesPinned.Free();
            if (environment != IntPtr.Zero)
            {
                try { Marshal.FreeHGlobal(environment); } catch { }
            }
            CloseHandleQuietly(standardInput);
            CloseHandleQuietly(standardOutput);
            CloseHandleQuietly(standardError);
            CloseHandleQuietly(thread);
            CloseHandleQuietly(process);
            CloseHandleQuietly(job);
        }
    }

    private static string RequireExistingFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || ContainsControl(path))
            throw new ArgumentException("A hosted tunnel path is invalid.");
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new ArgumentException("A hosted tunnel path is invalid.");
            return fullPath;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch
        {
            throw new ArgumentException("A hosted tunnel path is invalid.");
        }
    }

    private static bool ContainsControl(string value)
    {
        foreach (var character in value)
            if (char.IsControl(character)) return true;
        return false;
    }

    private static string BuildCommandLine(string executablePath, string configPath)
        => $"{QuoteWindowsArgument(executablePath)} tunnel --config {QuoteWindowsArgument(configPath)}"
            + " --no-autoupdate --loglevel error --transport-loglevel error --metrics 127.0.0.1:0 run";

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length == 0) return "\"\"";
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes);
            builder.Append(character);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static IntPtr BuildEnvironmentBlock()
    {
        var names = new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA" };
        var values = new List<string>(names.Length);
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is null || ContainsControl(value)) continue;
            values.Add(name + "=" + value);
        }
        values.Sort(StringComparer.OrdinalIgnoreCase);
        var block = string.Join('\0', values) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    private static IntPtr CreateNullHandle(uint access)
    {
        var security = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = true,
        };
        var handle = CreateFileW("NUL", access, FileShareRead | FileShareWrite,
            ref security, OpenExisting, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            throw StartFailure();
        return handle;
    }

    private static void EnsureHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) throw StartFailure();
    }

    private static InvalidOperationException StartFailure()
        => new("Hosted tunnel could not be started.");

    private static void CloseHandleQuietly(IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
        try { CloseHandle(handle); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job, uint informationClass, ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value,
        IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, IntPtr currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string name, uint desiredAccess, uint shareMode, ref SecurityAttributes securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
