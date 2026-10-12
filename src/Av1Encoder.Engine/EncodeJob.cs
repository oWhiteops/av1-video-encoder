using System.Diagnostics;

namespace Av1Encoder.Engine;

/// <summary>
/// One encode of one file. Stateless apart from the running process, so batch processing is a loop over jobs.
/// Output is written to a temporary file beside the destination and only moved into place after FFmpeg
/// succeeds and the result has been verified with FFprobe.
/// </summary>
public sealed class EncodeJob(FfmpegTools tools, EncodeRequest request, MediaInfo media, Action<string>? log = null)
{
    readonly FfmpegProcess ffmpeg = new();
    readonly Stopwatch active = new();

    /// <summary>
    /// Kill the encode if FFmpeg reports no progress for this long (not counting pauses). Hardware encoders can hang
    /// inside the driver (observed with AMF), and a hang should become a software fallback, not an endless wait.
    /// Null disables it; by default it applies to hardware encoders only, since slow software presets legitimately
    /// spend a long time on look-ahead before the first frame.
    /// </summary>
    public TimeSpan? StallTimeout { get; init; }
    public static readonly TimeSpan HardwareStallTimeout = TimeSpan.FromSeconds(60);

    public EncodeRequest Request { get; } = request;
    public MediaInfo Media { get; } = media;
    public bool IsPaused => ffmpeg.IsPaused;

    public void Pause() { if (ffmpeg.Pause()) { active.Stop(); log?.Invoke("Paused."); } }
    public void Resume() { if (ffmpeg.Resume()) { active.Start(); log?.Invoke("Resumed."); } }

    /// <summary>Bytes the encode may need on the destination drive. A heuristic, not a guarantee.</summary>
    public static long EstimateRequiredBytes(EncodeRequest request, MediaInfo media)
    {
        if (request.Quality.VideoBitrateKbps is int kbps)
            return (long)((kbps + 320) * 1000 / 8.0 * request.OutputDuration(media).TotalSeconds * 1.1);
        // ponytail: AV1 at these CRFs is almost always smaller than typical sources, so source size is a
        // conservative ceiling. Lossless or very low bitrate sources can exceed it; FFmpeg then reports disk full.
        return Math.Max(media.SizeBytes, 16L * 1024 * 1024);
    }

    /// <summary>Free space on the destination drive, or null if it cannot be determined (e.g. some network shares).</summary>
    public static long? AvailableBytes(string outputPath)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outputPath))!).AvailableFreeSpace; }
        catch { return null; }
    }

    public async Task<EncodeResult> RunAsync(IProgress<EncodeProgress>? progress, CancellationToken ct)
    {
        Preflight();
        var warnings = FfmpegArguments.Warnings(Media, Request);
        foreach (var w in warnings) log?.Invoke("Note: " + w);
        active.Restart();
        var encoder = Request.Encoder;
        bool fellBack = false;
        MediaInfo output;
        try
        {
            output = await EncodeOnceAsync(encoder, progress, ct);
        }
        catch (EncodeException ex) when (ex is not OutputTooLargeException && encoder != VideoEncoder.SvtAv1 && Request.AllowSoftwareFallback && !ct.IsCancellationRequested)
        {
            log?.Invoke($"Hardware encoder {encoder} failed ({ex.Message}). Falling back to SVT-AV1 software encoding.");
            warnings.Add($"The {encoder} hardware encoder failed, so the video was encoded in software (SVT-AV1) instead.");
            progress?.Report(new EncodeProgress(0, active.Elapsed, null, null, null, null, "Retrying with software encoder"));
            encoder = VideoEncoder.SvtAv1;
            fellBack = true;
            output = await EncodeOnceAsync(encoder, progress, ct);
        }
        active.Stop();
        log?.Invoke($"Finished in {active.Elapsed:hh\\:mm\\:ss}: {Media.SizeBytes:N0} -> {output.SizeBytes:N0} bytes.");
        return new EncodeResult(Request.OutputPath, Media.SizeBytes, output.SizeBytes, active.Elapsed, encoder, fellBack, warnings, output);
    }

    void Preflight()
    {
        if (Media.Video is null) throw new EncodeException("This file does not contain a video stream.");
        var input = Path.GetFullPath(Request.InputPath);
        var outputPath = Path.GetFullPath(Request.OutputPath);
        if (string.Equals(input, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new EncodeException("The output file cannot be the same as the input file.");
        if (!File.Exists(input)) throw new EncodeException("The input file no longer exists.");
        MediaProbe.EnsureReadable(input);
        if (File.Exists(outputPath) && !Request.OverwriteExisting)
            throw new EncodeException($"\"{Path.GetFileName(outputPath)}\" already exists. Choose another name or confirm overwriting.");
        try { Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!); }
        catch (Exception ex) { throw new EncodeException("The output folder could not be created.", ex.Message); }

        // Leftovers from a run that was killed outright (power loss, Task Manager). A temp file still being
        // written by a live encode is held open by FFmpeg, so the delete fails and it is left alone.
        var dir = Path.GetDirectoryName(outputPath)!;
        var name = Path.GetFileName(outputPath);
        foreach (var stale in Directory.EnumerateFiles(dir, name + ".*.partial").Concat(Directory.EnumerateFiles(dir, name + ".*.passlog*")))
        {
            try { File.Delete(stale); log?.Invoke($"Removed leftover temporary file {Path.GetFileName(stale)}."); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    async Task<MediaInfo> EncodeOnceAsync(VideoEncoder encoder, IProgress<EncodeProgress>? progress, CancellationToken ct)
    {
        string temp = TempPathFor(Request.OutputPath);
        var args = FfmpegArguments.Build(Media, Request, encoder, temp);
        log?.Invoke("Running: " + FfmpegArguments.ToDisplayString("ffmpeg", args));

        double total = Request.OutputDuration(Media).TotalSeconds;
        string stage = encoder == VideoEncoder.SvtAv1 ? "Encoding (SVT-AV1)" : $"Encoding ({encoder} hardware)";
        var stallTimeout = StallTimeout ?? (encoder == VideoEncoder.SvtAv1 ? null : HardwareStallTimeout);
        var sinceAdvance = Stopwatch.StartNew();
        (TimeSpan?, long?) lastMark = default;
        void OnProgress(FfmpegProgress p)
        {
            if ((p.OutTime, p.TotalSizeBytes) != lastMark) { lastMark = (p.OutTime, p.TotalSizeBytes); sinceAdvance.Restart(); }
            double f = total > 0 && p.OutTime is { } t ? Math.Clamp(t.TotalSeconds / total, 0, 1) : 0;
            var elapsed = active.Elapsed;
            TimeSpan? remaining = f >= 0.01 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - f) / f) : null;
            long? estimate = f >= 0.05 && p.TotalSizeBytes > 0 ? (long)(p.TotalSizeBytes.Value / f) : null;
            progress?.Report(new EncodeProgress(f, elapsed, remaining, p.Speed, p.Fps, estimate, p.IsEnd ? "Finalizing" : stage));
        }

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool stalled = false;
        var watchdog = stallTimeout is { } limit ? Task.Run(async () =>
        {
            while (!stall.IsCancellationRequested)
            {
                await Task.Delay(500, CancellationToken.None);
                if (ffmpeg.IsPaused) sinceAdvance.Restart();
                else if (sinceAdvance.Elapsed > limit) { stalled = true; stall.Cancel(); }
            }
        }, CancellationToken.None) : Task.CompletedTask;

        try
        {
            FfmpegExit exit;
            try { exit = await ffmpeg.RunAsync(tools.FfmpegPath, args, OnProgress, line => log?.Invoke("ffmpeg: " + line), stall.Token); }
            catch (OperationCanceledException) when (stalled && !ct.IsCancellationRequested)
            {
                throw new EncodeException($"The {(encoder == VideoEncoder.SvtAv1 ? "SVT-AV1" : encoder + " hardware")} encoder stopped responding and was stopped.");
            }
            if (exit.ExitCode != 0)
                throw new EncodeException(Explain(exit.StderrTail, encoder), string.Join(Environment.NewLine, exit.StderrTail));

            // Size is checked on the temp file so an oversized result never reaches the destination.
            if (Request.MaxOutputBytes is long max && new FileInfo(temp).Length is var size && size > max)
                throw new OutputTooLargeException(size, max);
            var output = await VerifyAsync(temp, ct);
            Finalize(temp);
            return output with { Path = Request.OutputPath };
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            stall.Cancel();
            await watchdog;
        }
    }

    async Task<MediaInfo> VerifyAsync(string temp, CancellationToken ct)
    {
        MediaInfo output;
        try { output = await MediaProbe.ProbeAsync(tools, temp, ct); }
        catch (EncodeException ex) { throw new EncodeException("The encoded file could not be verified and was discarded.", ex.Details); }
        if (output.Video?.Codec != "av1") throw new EncodeException("The encoded file does not contain AV1 video and was discarded.");
        int expectedAudio = FfmpegArguments.KeptAudio(Media, Request).Count();
        if (expectedAudio > 0 && output.Audio.Count != expectedAudio)
            throw new EncodeException("The encoded file is missing audio tracks and was discarded.");
        var expected = Request.OutputDuration(Media);
        if (expected.TotalSeconds > 2 && output.Duration.TotalSeconds < expected.TotalSeconds * 0.9)
            throw new EncodeException($"The encoded file is shorter than the source ({output.Duration:hh\\:mm\\:ss} vs {expected:hh\\:mm\\:ss}) and was discarded.");
        return output;
    }

    void Finalize(string temp)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(temp, Request.OutputPath, Request.OverwriteExisting); return; }
            catch (IOException) when (!Request.OverwriteExisting && File.Exists(Request.OutputPath))
            {
                throw new EncodeException($"\"{Path.GetFileName(Request.OutputPath)}\" was created by another program during encoding, so it was not overwritten.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(300 * attempt); // destination briefly locked (antivirus, preview handler)
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new EncodeException($"The encode finished but could not be saved as \"{Path.GetFileName(Request.OutputPath)}\" because the file is in use or access was denied.", ex.Message);
            }
        }
    }

    internal static string TempPathFor(string output) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $"{Path.GetFileName(output)}.{Guid.NewGuid():N}.partial");

    static void TryDelete(string path)
    {
        for (int i = 0; i < 10 && File.Exists(path); i++)
        {
            try { File.Delete(path); }
            catch { Thread.Sleep(200); } // FFmpeg may still hold the handle for a moment after being killed
        }
    }

    /// <summary>Turns FFmpeg's last error lines into a message a user can act on.</summary>
    internal static string Explain(IReadOnlyList<string> stderr, VideoEncoder encoder)
    {
        string all = string.Join("\n", stderr);
        bool Has(string s) => all.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("No space left on device") || Has("There is not enough space")) return "The destination drive ran out of space.";
        if (Has("Permission denied")) return "Access was denied while reading the input or writing the output. Check that the file is not open elsewhere and that the folder is writable.";
        if (encoder != VideoEncoder.SvtAv1 && (Has("Error while opening encoder") || Has("Cannot load") || Has("Init() failed") || Has("MFX session") || Has("No capable devices")))
            return $"The {encoder} hardware AV1 encoder could not be initialized. The GPU or driver may not support AV1 encoding for this video.";
        if (Has("Invalid data found when processing input") || Has("moov atom not found")) return "The input file appears to be corrupt or incomplete.";
        if (Has("Error while opening encoder")) return "The AV1 encoder rejected this video's format (for example its resolution or pixel format).";
        var last = stderr.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
        return "Encoding failed." + (last is null ? "" : " FFmpeg reported: " + last.Trim());
    }
}
