using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TvEmulator.Services;

public static class ProcessBudget
{
    public const int CpuRateScale = 10_000;

    /// <summary>
    /// <paramref name="cpuPercent"/> is a share of one logical thread.
    /// The job wants that share as parts of 10 000 of the whole machine, rounded.
    /// 8% of one thread on 12 threads is 67.
    /// </summary>
    public static int CpuRate(int cpuPercent, int logicalProcessors)
    {
        var processors = logicalProcessors < 1 ? 1 : logicalProcessors;
        var percent = Math.Clamp(cpuPercent, 1, 100);
        var rate = (int)Math.Round(percent * 100.0 / processors, MidpointRounding.AwayFromZero);
        if (rate < 1)
            return 1;
        if (rate > CpuRateScale)
            return CpuRateScale;
        return rate;
    }

    public static EngineJob Create(int? cpuPercent, int? memoryMb)
    {
        var cpu = cpuPercent is >= 1 and <= 100 ? cpuPercent : null;
        var memory = memoryMb is >= 1 ? memoryMb : null;
        if (cpu is null && memory is null)
            throw new ArgumentException("Brak limitu do założenia.");

        var handle = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (handle == IntPtr.Zero)
            throw Fail("Nie udało się założyć limitu procesu");

        try
        {
            Apply(handle, cpu, memory);
            return new EngineJob(handle);
        }
        catch
        {
            CloseHandle(handle);
            throw;
        }
    }

    public static Process Start(
        string exe,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        EngineJob job)
    {
        var command = new StringBuilder(Quote(exe));
        foreach (var argument in arguments)
        {
            command.Append(' ');
            command.Append(Quote(argument));
        }

        var environmentBlock = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var jobList = IntPtr.Zero;
        var attributeReady = false;
        try
        {
            environmentBlock = EnvironmentBlock(environment);
            attributeList = AttributeList(job.Handle, out jobList);
            attributeReady = true;

            var startup = new StartupInfoEx();
            startup.StartupInfo.Cb = Marshal.SizeOf<StartupInfoEx>();
            startup.AttributeList = attributeList;
            var directory = Path.GetDirectoryName(exe);
            var created = CreateProcess(
                exe,
                command,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                environmentBlock,
                string.IsNullOrEmpty(directory) ? null : directory,
                ref startup,
                out var information);
            if (!created)
                throw Fail("Nie udało się uruchomić silnika w limicie");

            CloseHandle(information.Thread);
            CloseHandle(information.Process);
            try
            {
                return Process.GetProcessById(information.ProcessId);
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("Silnik zakończył się od razu po starcie.");
            }
        }
        finally
        {
            if (attributeReady)
                DeleteProcThreadAttributeList(attributeList);
            if (attributeList != IntPtr.Zero)
                Marshal.FreeHGlobal(attributeList);
            if (jobList != IntPtr.Zero)
                Marshal.FreeHGlobal(jobList);
            if (environmentBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlock);
        }
    }

    internal static string Quote(string value)
    {
        if (value.Length == 0)
            return "\"\"";
        if (value.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            return value;

        var builder = new StringBuilder();
        builder.Append('"');
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', slashes * 2 + 1);
                builder.Append('"');
                slashes = 0;
                continue;
            }

            if (slashes > 0)
            {
                builder.Append('\\', slashes);
                slashes = 0;
            }

            builder.Append(character);
        }

        if (slashes > 0)
            builder.Append('\\', slashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void Apply(IntPtr job, int? cpuPercent, int? memoryMb)
    {
        // No breakaway flag: renderer children stay in this job. On Windows 8+ a sandbox
        // job nests under it, so the cap still covers them. The video plane is a different process.
        var limits = new ExtendedLimitInformation
        {
            Basic = new BasicLimitInformation
            {
                LimitFlags = KillOnJobClose | (memoryMb is null ? 0u : JobMemory)
            }
        };
        if (memoryMb is int megabytes)
            limits.JobMemoryLimit = new UIntPtr((ulong)megabytes * 1024UL * 1024UL);

        var limitPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimitInformation>());
        try
        {
            Marshal.StructureToPtr(limits, limitPointer, false);
            if (!SetInformationJobObject(job, JobExtendedLimit, limitPointer, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                throw Fail("Nie udało się nałożyć limitu na proces");
        }
        finally
        {
            Marshal.FreeHGlobal(limitPointer);
        }

        if (cpuPercent is not int percent)
            return;

        var rate = new CpuRateInformation
        {
            ControlFlags = CpuRateEnable | CpuRateHardCap,
            CpuRate = (uint)CpuRate(percent, Environment.ProcessorCount)
        };
        var ratePointer = Marshal.AllocHGlobal(Marshal.SizeOf<CpuRateInformation>());
        try
        {
            Marshal.StructureToPtr(rate, ratePointer, false);
            if (!SetInformationJobObject(job, JobCpuRate, ratePointer, (uint)Marshal.SizeOf<CpuRateInformation>()))
                throw Fail("Nie udało się ograniczyć procesora");
        }
        finally
        {
            Marshal.FreeHGlobal(ratePointer);
        }
    }

    private static IntPtr AttributeList(IntPtr job, out IntPtr jobList)
    {
        jobList = IntPtr.Zero;
        nint bytes = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
        if (bytes <= 0)
            throw Fail("Nie udało się przygotować limitu procesu");

        var list = Marshal.AllocHGlobal(bytes);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref bytes))
                throw Fail("Nie udało się przygotować limitu procesu");
            initialized = true;
            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobList, job);
            // PROC_THREAD_ATTRIBUTE_JOB_LIST: the process is inside the job before its first instruction.
            if (!UpdateProcThreadAttribute(list, 0, new IntPtr(0x0002000D), jobList, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw Fail("Nie udało się przypiąć limitu do procesu");
            return list;
        }
        catch
        {
            if (jobList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(jobList);
                jobList = IntPtr.Zero;
            }

            if (initialized)
                DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            throw;
        }
    }

    private static IntPtr EnvironmentBlock(IReadOnlyDictionary<string, string> extra)
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (string.IsNullOrEmpty(key))
                continue;
            map[key] = entry.Value?.ToString() ?? "";
        }

        foreach (var pair in extra)
            map[pair.Key] = pair.Value;

        var builder = new StringBuilder();
        foreach (var pair in map)
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        builder.Append('\0');
        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    private static InvalidOperationException Fail(string message) =>
        new(message + " (" + Marshal.GetLastWin32Error() + ").");

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint KillOnJobClose = 0x00002000;
    private const uint JobMemory = 0x00000200;
    private const uint CpuRateEnable = 0x1;
    private const uint CpuRateHardCap = 0x4;
    private const int JobExtendedLimit = 9;
    private const int JobCpuRate = 15;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct CpuRateInformation
    {
        [FieldOffset(0)] public uint ControlFlags;
        [FieldOffset(4)] public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Length;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr list,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previous,
        IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);
}

public sealed class EngineJob : IDisposable
{
    private IntPtr _handle;

    internal EngineJob(IntPtr handle) => _handle = handle;

    public IntPtr Handle => _handle;

    public void Dispose()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero)
            CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
