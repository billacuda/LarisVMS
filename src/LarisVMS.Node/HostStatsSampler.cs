using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node;

/// <summary>
/// The node machine's own load, sampled once per heartbeat for the dashboard: CPU percent busy across
/// all cores and network bytes/s across active adapters (both as deltas since the previous sample, so
/// the first call reports them as null), plus physical memory used/total. Windows only — elsewhere
/// <see cref="Sample"/> returns null and the dashboard shows no host figures for the node.
/// </summary>
public sealed class HostStatsSampler
{
    private CpuTimes? _lastCpu;
    private (long Received, long Sent, long Timestamp)? _lastNet;

    public NodeHostStats? Sample()
    {
        if (!OperatingSystem.IsWindows()) return null;

        double? cpuPercent = null;
        if (TryGetCpuTimes() is { } cpu)
        {
            if (_lastCpu is { } previous) cpuPercent = CpuPercentBetween(previous, cpu);
            _lastCpu = cpu;
        }

        long? memoryUsed = null, memoryTotal = null;
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            memoryTotal = (long)memory.TotalPhys;
            memoryUsed = (long)(memory.TotalPhys - memory.AvailPhys);
        }

        long? receivePerSec = null, sendPerSec = null;
        if (TryGetNetworkBytes() is { } net)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastNet is { } previous)
            {
                var seconds = Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalSeconds;
                receivePerSec = RatePerSecond(previous.Received, net.Received, seconds);
                sendPerSec = RatePerSecond(previous.Sent, net.Sent, seconds);
            }
            _lastNet = (net.Received, net.Sent, now);
        }

        return new NodeHostStats(cpuPercent, memoryUsed, memoryTotal, receivePerSec, sendPerSec);
    }

    /// <summary>Idle/kernel/user time in 100 ns units, as GetSystemTimes reports them — kernel time
    /// includes idle time.</summary>
    internal readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    internal static double? CpuPercentBetween(CpuTimes previous, CpuTimes current)
    {
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User) return null;
        var idle = current.Idle - previous.Idle;
        var total = (current.Kernel - previous.Kernel) + (current.User - previous.User);
        if (total == 0) return null;
        var busy = total > idle ? total - idle : 0;
        return Math.Round(100.0 * busy / total, 1);
    }

    /// <summary>Null on a counter that went backwards (an adapter reset or removed between samples) or
    /// an interval too short to mean anything.</summary>
    internal static long? RatePerSecond(long previous, long current, double seconds)
    {
        if (seconds < 0.5 || current < previous) return null;
        return (long)Math.Round((current - previous) / seconds);
    }

    private static CpuTimes? TryGetCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        return new CpuTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
    }

    private static (long Received, long Sent)? TryGetNetworkBytes()
    {
        try
        {
            long received = 0, sent = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
            return (received, sent);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
