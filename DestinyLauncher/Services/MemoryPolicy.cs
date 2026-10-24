using System.Runtime.InteropServices;

namespace DestinyLauncher.Services;

/// <summary>One budget for defaults, settings, persisted configurations and launch validation.</summary>
public sealed class MemoryPolicy
{
    private const long GiB = 1024L * 1024 * 1024;
    public static MemoryPolicy System { get; } = new(ReadSystemBytes());
    public long TotalBytes { get; }
    public int MaximumGb => (int)Math.Min(1024, TotalBytes / (double)GiB * 0.8);
    public int DefaultGb => Math.Clamp((int)(TotalBytes / (double)GiB * 0.4), 1, Math.Max(1, MaximumGb));
    public MemoryPolicy(long totalBytes)
    {
        if (totalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));
        TotalBytes = totalBytes;
    }
    public int Normalize(int requested) => Math.Clamp(requested, 1, Math.Max(1, MaximumGb));
    public string Warning => $"Máximo {MaximumGb} GB: no puedes asignar más del 80 % de la RAM del equipo.";
    public void Validate(int requested)
    {
        if (requested < 1 || requested > MaximumGb) throw new InvalidOperationException(Warning);
    }
    private static long ReadSystemBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status) && status.TotalPhysical > 0) return checked((long)status.TotalPhysical);
        }
        else if (OperatingSystem.IsLinux())
        {
            var total = File.ReadLines("/proc/meminfo").First(line => line.StartsWith("MemTotal:", StringComparison.Ordinal));
            var bytes = checked(long.Parse(total.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1]) * 1024);
            // A container's cgroup limit can be lower than the host's physical RAM.
            foreach (var limitFile in new[] { "/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes" })
                if (File.Exists(limitFile) && long.TryParse(File.ReadAllText(limitFile).Trim(), out var limit) && limit > 0)
                    bytes = Math.Min(bytes, limit);
            return bytes;
        }
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? available : 2 * GiB;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
