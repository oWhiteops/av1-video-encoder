using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Av1Encoder.Engine;

public sealed record FfmpegProgress(TimeSpan? OutTime, long? TotalSizeBytes, double? Speed, double? Fps, bool IsEnd);

/// <summary>Parses FFmpeg's machine-readable "-progress" output (key=value lines, each block ending in progress=...).</summary>
public sealed class ProgressParser
{
    readonly Dictionary<string, string> block = new();

    /// <summary>Feeds one line; returns a snapshot when a block completes.</summary>
    public FfmpegProgress? Feed(string line)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0) return null;
        string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
        if (key != "progress") { block[key] = value; return null; }

        long? outUs = Long("out_time_us") ?? Long("out_time_ms"); // out_time_ms is actually microseconds in FFmpeg
        var snap = new FfmpegProgress(
            outUs is >= 0 ? TimeSpan.FromTicks(outUs.Value * 10) : null,
            Long("total_size"),
            Dbl(block.GetValueOrDefault("speed")?.TrimEnd('x')),
            Dbl(block.GetValueOrDefault("fps")),
            value == "end");
        block.Clear();
        return snap;
    }

    long? Long(string key) => long.TryParse(block.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    static double? Dbl(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

public sealed record FfmpegExit(int ExitCode, IReadOnlyList<string> StderrTail);

/// <summary>Runs one FFmpeg process with progress, stderr capture, cancellation, and pause/resume.</summary>
public sealed class FfmpegProcess
{
    Process? process;
    bool paused;

    public static readonly int StderrTailLines = 40;

    public async Task<FfmpegExit> RunAsync(string exe, IReadOnlyList<string> args, Action<FfmpegProgress>? onProgress,
        Action<string>? onStderr, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception ex) { throw new EncodeException("FFmpeg could not be started.", ex.Message); }
        process = p;
        Native.AssignToKillOnCloseJob(p);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } // keep the desktop responsive

        var tail = new Queue<string>();
        var stderrTask = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
            {
                lock (tail) { tail.Enqueue(line); if (tail.Count > StderrTailLines) tail.Dequeue(); }
                onStderr?.Invoke(line);
            }
        }, CancellationToken.None);
        var stdoutTask = Task.Run(async () =>
        {
            var parser = new ProgressParser();
            while (await p.StandardOutput.ReadLineAsync() is { } line)
                if (parser.Feed(line) is { } snap) onProgress?.Invoke(snap);
        }, CancellationToken.None);

        using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
        {
            await p.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stderrTask, stdoutTask);
        }
        process = null;
        ct.ThrowIfCancellationRequested();
        lock (tail) return new FfmpegExit(p.ExitCode, tail.ToArray());
    }

    public bool IsPaused => paused;

    /// <summary>Suspends the FFmpeg process. Returns false if nothing is running.</summary>
    public bool Pause()
    {
        if (process is not { HasExited: false } p || paused) return false;
        paused = Native.NtSuspendProcess(p.Handle) == 0;
        return paused;
    }

    public bool Resume()
    {
        if (process is not { HasExited: false } p || !paused) return false;
        paused = Native.NtResumeProcess(p.Handle) != 0;
        return !paused;
    }

    static class Native
    {
        [DllImport("ntdll.dll")] internal static extern int NtSuspendProcess(IntPtr processHandle);
        [DllImport("ntdll.dll")] internal static extern int NtResumeProcess(IntPtr processHandle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInfo info, int size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimitInfo
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        // One job for the app's lifetime. Windows closes the handle when the app exits (even on a crash),
        // and KILL_ON_JOB_CLOSE then terminates any FFmpeg still running, so no orphaned encodes.
        static readonly Lazy<IntPtr> Job = new(() =>
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            var info = new ExtendedLimitInfo { LimitFlags = 0x2000 /* JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE */ };
            SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref info, Marshal.SizeOf<ExtendedLimitInfo>());
            return job;
        });

        internal static void AssignToKillOnCloseJob(Process p)
        {
            try { AssignProcessToJobObject(Job.Value, p.Handle); } catch { } // best effort; cancellation still kills directly
        }
    }
}
