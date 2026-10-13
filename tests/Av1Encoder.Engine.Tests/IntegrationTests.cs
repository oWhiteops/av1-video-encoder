using System.Diagnostics;
using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

/// <summary>Generates short synthetic (legally unencumbered) clips with FFmpeg's lavfi sources.</summary>
public sealed class Samples : IDisposable
{
    public FfmpegTools Tools { get; }
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "av1enc-tests-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Dictionary<string, string> cache = new();

    public Samples()
    {
        var dir = Environment.GetEnvironmentVariable("AV1ENCODER_FFMPEG_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "Av1Encoder.slnx"))) root = Path.GetDirectoryName(root);
            dir = Path.Combine(root ?? throw new InvalidOperationException("Repository root not found"), "third_party", "ffmpeg", "bin");
        }
        Tools = new FfmpegTools(dir);
        Directory.CreateDirectory(Dir);
    }

    public string Get(string name, params string[] args)
    {
        lock (cache)
        {
            if (cache.TryGetValue(name, out var existing)) return existing;
            var path = Path.Combine(Dir, name);
            Run(Tools.FfmpegPath, ["-hide_banner", "-loglevel", "error", "-y", .. args, path]);
            cache[name] = path;
            return path;
        }
    }

    /// <summary>640x360 @ 30 fps, 4 s, stereo AAC; a high-bitrate MPEG-4 Part 2 source so AV1 has something to save.</summary>
    public string Basic => Get("basic.mp4", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=4", "-f", "lavfi", "-i", "sine=f=440:d=4:sample_rate=48000",
        "-ac", "2", "-c:v", "mpeg4", "-q:v", "2", "-c:a", "aac", "-b:a", "192k", "-shortest");

    public static (int Exit, string StdOut, string StdErr) Run(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && exe.EndsWith("ffmpeg.exe") && args.Contains("-y")) throw new InvalidOperationException("Sample generation failed: " + err.Result);
        return (p.ExitCode, output, err.Result);
    }

    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

[CollectionDefinition("ffmpeg")]
public class FfmpegCollection : ICollectionFixture<Samples>;

[Collection("ffmpeg")]
public class IntegrationTests(Samples s)
{
    static readonly QualitySettings Fast = new(Crf: 35, Speed: 10, HardwareQuality: 30);

    string Out(string name) => Path.Combine(s.Dir, "out", Guid.NewGuid().ToString("N")[..6], name);

    async Task<(EncodeResult Result, List<EncodeProgress> Progress)> Encode(string input, EncodeRequest? template = null, string outName = "out.mp4")
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, input);
        var request = template is null
            ? new EncodeRequest { InputPath = input, OutputPath = Out(outName), Quality = Fast }
            : template with { InputPath = input };
        var progress = new List<EncodeProgress>();
        var result = await new EncodeJob(s.Tools, request, media).RunAsync(new SyncProgress<EncodeProgress>(progress.Add), CancellationToken.None);
        return (result, progress);
    }

    /// <summary>Full decode with dav1d: the closest automated stand-in for "it plays".</summary>
    void AssertDecodesCleanly(string path)
    {
        var (exit, _, err) = Samples.Run(s.Tools.FfmpegPath, ["-hide_banner", "-v", "error", "-i", path, "-f", "null", "-"]);
        Assert.Equal(0, exit);
        Assert.True(string.IsNullOrWhiteSpace(err), "Decode errors: " + err);
    }

    int CountFrames(string path)
    {
        var (_, stdout, _) = Samples.Run(s.Tools.FfprobePath, ["-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", path]);
        return int.Parse(stdout.Trim());
    }

    static void AssertNoPartialFiles(string dir) =>
        Assert.Empty(Directory.Exists(dir) ? Directory.GetFiles(dir, "*.partial") : []);

    [Fact]
    public async Task OriginalCheckPassesAGoodEncodeAndCatchesADamagedOne()
    {
        var input = s.Get("medium.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=8", "-c:v", "ffv1");
        var media = await MediaProbe.ProbeAsync(s.Tools, input);
        var (result, _) = await Encode(input);
        var request = new EncodeRequest { InputPath = input, OutputPath = result.OutputPath, Quality = Fast };
        double last = 0;
        Assert.Null(await OriginalCheck.VerifyAsync(s.Tools, media, request, result, p => last = p, null, CancellationToken.None));
        Assert.True(last > 0.9, $"decode progress ended at {last:0.00}");

        // Same length, scrambled middle: only the full decode can tell.
        var bytes = File.ReadAllBytes(result.OutputPath);
        for (int i = bytes.Length / 3; i < bytes.Length * 2 / 3; i += 2048) Array.Fill(bytes, (byte)0xFF, i, 64);
        File.WriteAllBytes(result.OutputPath, bytes);
        Assert.Contains("decode", await OriginalCheck.VerifyAsync(s.Tools, media, request, result, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task EncodesAv1WithAudioPreservingGeometryAndFrameRate()
    {
        var (result, progress) = await Encode(s.Basic);
        var o = result.Output;
        Assert.Equal("av1", o.Video!.Codec);
        Assert.Equal((640, 360), (o.Video.Width, o.Video.Height));
        Assert.Equal(30, o.Video.FrameRate, 1);
        Assert.Equal("yuv420p10le", o.Video.PixelFormat);
        Assert.Single(o.Audio);
        Assert.Equal("aac", o.Audio[0].Codec);
        Assert.Equal(2, o.Audio[0].Channels);
        Assert.InRange(o.Duration.TotalSeconds, 3.9, 4.2);
        Assert.Equal(CountFrames(s.Basic), CountFrames(result.OutputPath));
        Assert.True(result.OutputBytes < result.InputBytes / 2, $"Expected a real size reduction, got {result.OutputBytes} from {result.InputBytes}");
        Assert.Equal(new FileInfo(result.OutputPath).Length, result.OutputBytes);
        Assert.False(result.FellBackToSoftware);
        Assert.NotEmpty(progress);
        Assert.InRange(progress.Max(p => p.Fraction), 0.9, 1.0);
        Assert.All(progress, p => Assert.InRange(p.Fraction, 0, 1));
        AssertNoPartialFiles(Path.GetDirectoryName(result.OutputPath)!);
        AssertDecodesCleanly(result.OutputPath);
    }

    [Fact]
    public async Task RemovesAudioWhenAsked()
    {
        var (result, _) = await Encode(s.Basic, new EncodeRequest { InputPath = "", OutputPath = Out("noaudio.mp4"), Quality = Fast, IncludeAudio = false });
        Assert.Empty(result.Output.Audio);
        Assert.Contains(result.Warnings, w => w.Contains("Audio removed"));
    }

    [Fact]
    public async Task ReencodesPcmAudioAndKeepsMultipleTracks()
    {
        var input = s.Get("pcm2.mov", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=3", "-f", "lavfi", "-i", "sine=f=300:d=3",
            "-f", "lavfi", "-i", "sine=f=600:d=3", "-map", "0", "-map", "1", "-map", "2", "-c:v", "mpeg4", "-c:a", "pcm_s16le", "-ac", "2");
        var (result, _) = await Encode(input);
        Assert.Equal(2, result.Output.Audio.Count);
        Assert.All(result.Output.Audio, a => Assert.Equal("aac", a.Codec));
    }

    [Fact]
    public async Task PreservesDisplayedOrientationOfRotatedVideo()
    {
        var plain = s.Get("unrotated.mp4", "-f", "lavfi", "-i", "testsrc2=s=480x270:r=30:d=2", "-c:v", "mpeg4");
        var rotated = Path.Combine(s.Dir, "rotated.mp4");
        Samples.Run(s.Tools.FfmpegPath, ["-hide_banner", "-loglevel", "error", "-y", "-display_rotation", "90", "-i", plain, "-c", "copy", rotated]);
        var source = await MediaProbe.ProbeAsync(s.Tools, rotated);
        Assert.Equal(90, Math.Abs(source.Video!.Rotation));

        var (result, _) = await Encode(rotated);
        Assert.Equal(source.Video.DisplaySize, result.Output.Video!.DisplaySize);
    }

    [Fact]
    public async Task PreservesVariableFrameRateTimestamps()
    {
        // 2 s at 30 fps followed by 2 s at 10 fps, kept variable in MKV.
        var input = s.Get("vfr.mkv", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=30:d=2", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=10:d=2",
            "-filter_complex", "[0:v][1:v]concat=n=2:v=1[v]", "-map", "[v]", "-fps_mode", "vfr", "-c:v", "ffv1");
        int sourceFrames = CountFrames(input);
        Assert.Equal(80, sourceFrames);

        var (result, _) = await Encode(input);
        Assert.Equal(sourceFrames, CountFrames(result.OutputPath));
        Assert.InRange(result.Output.Duration.TotalSeconds, 3.8, 4.2);
    }

    [Fact]
    public async Task ConvertsTextSubtitlesIntoMp4()
    {
        var srt = Path.Combine(s.Dir, "subs.srt");
        File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:01,500\nHello\n\n2\n00:00:01,500 --> 00:00:03,000\nWorld\n");
        var input = s.Get("subs.mkv", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=3", "-i", srt, "-map", "0", "-map", "1", "-c:v", "mpeg4", "-c:s", "srt");
        var (result, _) = await Encode(input);
        Assert.Single(result.Output.Subtitles);
        Assert.Equal("mov_text", result.Output.Subtitles[0].Codec);
    }

    [Fact]
    public async Task PreservesHdrColourSignalling()
    {
        var input = s.Get("hdr.mkv", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=24:d=2,format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc",
            "-c:v", "ffv1", "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc");
        var source = await MediaProbe.ProbeAsync(s.Tools, input);
        Assert.True(source.Video!.IsHdr);
        var (result, _) = await Encode(input);
        Assert.True(result.Output.Video!.IsHdr);
        var (_, stdout, _) = Samples.Run(s.Tools.FfprobePath, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=color_primaries,color_space", "-of", "csv=p=0", result.OutputPath]);
        Assert.Contains("bt2020", stdout);
    }

    [Fact]
    public async Task PreservesAnamorphicAspectRatio()
    {
        var input = s.Get("anamorphic.mkv", "-f", "lavfi", "-i", "testsrc2=s=720x480:r=30:d=2,setsar=32/27", "-c:v", "ffv1");
        var (result, _) = await Encode(input);
        var (_, stdout, _) = Samples.Run(s.Tools.FfprobePath, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,sample_aspect_ratio", "-of", "csv=p=0", result.OutputPath]);
        Assert.Equal("720,480,32:27", stdout.Trim());
    }

    [Fact]
    public async Task HandlesOddDimensions()
    {
        var input = s.Get("odd.mkv", "-f", "lavfi", "-i", "testsrc2=s=321x241:r=24:d=2", "-c:v", "ffv1");
        var (result, _) = await Encode(input);
        Assert.Equal("av1", result.Output.Video!.Codec);
        AssertDecodesCleanly(result.OutputPath);
    }

    [Fact]
    public async Task CancellationLeavesNoOutputOrTempFiles()
    {
        var input = s.Get("long.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=30", "-c:v", "ffv1");
        var media = await MediaProbe.ProbeAsync(s.Tools, input);
        var output = Out("cancelled.mp4");
        var request = new EncodeRequest { InputPath = input, OutputPath = output, Quality = new QualitySettings(30, 4, 30) };
        using var cts = new CancellationTokenSource();
        var job = new EncodeJob(s.Tools, request, media);
        var started = new TaskCompletionSource();
        var run = job.RunAsync(new SyncProgress<EncodeProgress>(p => { if (p.Fraction > 0) started.TrySetResult(); }), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.partial"));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(File.Exists(output));
        AssertNoPartialFiles(Path.GetDirectoryName(output)!);
    }

    [Fact]
    public async Task PauseStopsProgressAndResumeCompletes()
    {
        var input = s.Get("medium.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=8", "-c:v", "ffv1");
        var media = await MediaProbe.ProbeAsync(s.Tools, input);
        var request = new EncodeRequest { InputPath = input, OutputPath = Out("paused.mp4"), Quality = new QualitySettings(35, 8, 30) };
        var job = new EncodeJob(s.Tools, request, media);
        var latest = 0.0;
        var started = new TaskCompletionSource();
        var run = job.RunAsync(new SyncProgress<EncodeProgress>(p => { latest = p.Fraction; if (p.Fraction > 0) started.TrySetResult(); }), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60));

        job.Pause();
        Assert.True(job.IsPaused);
        await Task.Delay(700); // let any in-flight progress line drain
        var atPause = latest;
        await Task.Delay(2000);
        Assert.Equal(atPause, latest);

        job.Resume();
        Assert.False(job.IsPaused);
        var result = await run.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.Equal("av1", result.Output.Video!.Codec);
        Assert.True(result.EncodeTime < TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task StalledEncoderIsStoppedAndCleanedUp()
    {
        // Slow preset on 720p: SVT-AV1 spends seconds in look-ahead before the first frame, which a
        // 300 ms stall limit treats as a hang. This exercises the same path a frozen GPU driver takes.
        var input = s.Get("long.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=30", "-c:v", "ffv1");
        var media = await MediaProbe.ProbeAsync(s.Tools, input);
        var output = Out("stalled.mp4");
        var job = new EncodeJob(s.Tools, new EncodeRequest { InputPath = input, OutputPath = output, Quality = new QualitySettings(30, 2, 30) }, media)
        {
            StallTimeout = TimeSpan.FromMilliseconds(300),
        };
        var ex = await Assert.ThrowsAsync<EncodeException>(() => job.RunAsync(null, CancellationToken.None));
        Assert.Contains("stopped responding", ex.Message);
        Assert.False(File.Exists(output));
        AssertNoPartialFiles(Path.GetDirectoryName(output)!);
    }

    [Fact]
    public async Task LeftoverTempFilesFromAKilledRunAreRemovedButLiveOnesKept()
    {
        var output = Out("leftover.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var stale = output + ".0123456789abcdef0123456789abcdef.partial";
        var live = output + ".fedcba9876543210fedcba9876543210.partial";
        File.WriteAllText(stale, "junk");
        File.WriteAllText(live, "busy");
        using (new FileStream(live, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Write))
        {
            await Encode(s.Basic, new EncodeRequest { InputPath = "", OutputPath = output, Quality = Fast });
        }
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(live));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task InvalidFileIsRejectedClearly()
    {
        var bogus = Path.Combine(s.Dir, "bogus.mp4");
        File.WriteAllBytes(bogus, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray());
        var ex = await Assert.ThrowsAsync<EncodeException>(() => MediaProbe.ProbeAsync(s.Tools, bogus));
        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public async Task LockedInputIsRejectedClearly()
    {
        var copy = Path.Combine(s.Dir, "locked.mp4");
        File.Copy(s.Basic, copy, true);
        using var hold = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var ex = await Assert.ThrowsAsync<EncodeException>(() => MediaProbe.ProbeAsync(s.Tools, copy));
        Assert.Contains("in use", ex.Message);
    }

    [Fact]
    public async Task NeverOverwritesWithoutPermission()
    {
        var output = Out("exists.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "keep me");
        var ex = await Assert.ThrowsAsync<EncodeException>(() =>
            Encode(s.Basic, new EncodeRequest { InputPath = "", OutputPath = output, Quality = Fast }));
        Assert.Contains("already exists", ex.Message);
        Assert.Equal("keep me", File.ReadAllText(output));

        var (result, _) = await Encode(s.Basic, new EncodeRequest { InputPath = "", OutputPath = output, Quality = Fast, OverwriteExisting = true });
        Assert.Equal("av1", result.Output.Video!.Codec);
    }

    [Fact]
    public async Task RefusesToOverwriteTheInput()
    {
        var ex = await Assert.ThrowsAsync<EncodeException>(() =>
            Encode(s.Basic, new EncodeRequest { InputPath = "", OutputPath = s.Basic, Quality = Fast, OverwriteExisting = true }));
        Assert.Contains("same as the input", ex.Message);
    }

    [Fact]
    public async Task FailedEncodeCleansUpAndReportsFfmpegError()
    {
        var output = Out("fail.mp4");
        var ex = await Assert.ThrowsAsync<EncodeException>(() =>
            Encode(s.Basic, new EncodeRequest { InputPath = "", OutputPath = output, Quality = Fast with { Crf = 99 } }));
        Assert.NotNull(ex.Details);
        Assert.False(File.Exists(output));
        AssertNoPartialFiles(Path.GetDirectoryName(output)!);
    }

    [Fact]
    public async Task TruncatedSourceIsNotPassedOffAsComplete()
    {
        // MKV writes its duration up front, so a cut-off copy still claims the full length.
        var full = s.Get("full.mkv", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=10", "-c:v", "mpeg4", "-q:v", "3");
        var cut = Path.Combine(s.Dir, "cut.mkv");
        var bytes = File.ReadAllBytes(full);
        File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);
        var output = Out("cut.mp4");
        var ex = await Assert.ThrowsAsync<EncodeException>(() => Encode(cut, new EncodeRequest { InputPath = "", OutputPath = output, Quality = Fast }));
        Assert.Contains("shorter than the source", ex.Message);
        Assert.False(File.Exists(output));
        AssertNoPartialFiles(Path.GetDirectoryName(output)!);
    }

    [Fact]
    public async Task UnavailableHardwareEncoderFallsBackToSoftware()
    {
        var availability = await HardwareDetector.DetectAsync(s.Tools);
        var missing = availability.FirstOrDefault(a => !a.Available);
        if (missing is null) return; // every hardware encoder works on this machine; nothing to fall back from
        var (result, progress) = await Encode(s.Basic, new EncodeRequest { InputPath = "", OutputPath = Out("fallback.mp4"), Quality = Fast, Encoder = missing.Encoder });
        // The GPU gets one retry first (AMF's start-up hang is intermittent).
        Assert.True(progress.FindIndex(p => p.Stage.Contains("retrying")) is var retry && retry >= 0
            && retry < progress.FindIndex(p => p.Encoder == VideoEncoder.SvtAv1));
        Assert.True(result.FellBackToSoftware);
        Assert.Equal(VideoEncoder.SvtAv1, result.EncoderUsed);
        Assert.Equal("av1", result.Output.Video!.Codec);
    }

    [Fact]
    public async Task FallbackCanBeDisabled()
    {
        var availability = await HardwareDetector.DetectAsync(s.Tools);
        var missing = availability.FirstOrDefault(a => !a.Available);
        if (missing is null) return;
        var output = Out("nofallback.mp4");
        var ex = await Assert.ThrowsAsync<EncodeException>(() => Encode(s.Basic,
            new EncodeRequest { InputPath = "", OutputPath = output, Quality = Fast, Encoder = missing.Encoder, AllowSoftwareFallback = false }));
        Assert.Contains("hardware", ex.Message);
        AssertNoPartialFiles(Path.GetDirectoryName(output)!);
    }

    [Fact]
    public async Task AvailableHardwareEncodersProduceAv1()
    {
        var availability = await HardwareDetector.DetectAsync(s.Tools);
        foreach (var hw in availability.Where(a => a.Available))
        {
            var (result, _) = await Encode(s.Basic, new EncodeRequest { InputPath = "", OutputPath = Out($"{hw.Encoder}.mp4"), Quality = Fast, Encoder = hw.Encoder });
            Assert.False(result.FellBackToSoftware, $"{hw.Encoder} fell back to software");
            Assert.Equal("av1", result.Output.Video!.Codec);
            Assert.Equal((640, 360), (result.Output.Video.Width, result.Output.Video.Height));
            AssertDecodesCleanly(result.OutputPath);
        }
    }

    [Fact]
    public async Task AmfEncodesTenBitHdrWithoutFallingBack()
    {
        var availability = await HardwareDetector.DetectAsync(s.Tools);
        if (!availability.Any(a => a.Encoder == VideoEncoder.Amf && a.Available)) return; // no AMD AV1 GPU here
        var input = s.Get("hdr10bit.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=24:d=2,format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc",
            "-c:v", "ffv1", "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc");
        var media = await MediaProbe.ProbeAsync(s.Tools, input);
        Assert.Equal(10, media.Video!.BitDepth);
        // AMF initialization fails intermittently on some drivers; a real 10-bit problem fails both tries.
        EncodeResult? result = null;
        for (int attempt = 0; attempt < 2 && result is not { FellBackToSoftware: false }; attempt++)
            (result, _) = await Encode(input, new EncodeRequest { InputPath = "", OutputPath = Out($"amf10-{attempt}.mp4"), Quality = Fast, Encoder = VideoEncoder.Amf });
        Assert.False(result!.FellBackToSoftware, "AMF should encode 10-bit input without falling back to software");
        Assert.Equal("av1", result.Output.Video!.Codec);
        Assert.Equal(10, result.Output.Video.BitDepth);
        AssertDecodesCleanly(result.OutputPath);
    }

    [Fact]
    public async Task HardwareSampleMeasuresSizeAndSpeed()
    {
        var availability = await HardwareDetector.DetectAsync(s.Tools);
        var hw = availability.FirstOrDefault(a => a.Available);
        if (hw is null) return;
        var input = s.Get("long.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=30", "-c:v", "ffv1");
        var media = await MediaProbe.ProbeAsync(s.Tools, input);
        // A start between frames at 30 fps once made the start-up probe ask for less than one frame; AMF then hung.
        var request = new EncodeRequest { InputPath = input, OutputPath = "x.mp4", Quality = QualitySettings.For(QualityPreset.Balanced), Encoder = hw.Encoder,
            TrimStart = TimeSpan.FromSeconds(3.0177) };
        int before = Directory.GetFiles(Path.GetTempPath(), "av1-gpu-sample-*").Length;
        HardwareSample? sample = null;
        for (int attempt = 0; attempt < 2 && sample is null; attempt++)
            try { sample = await SizeTargeting.SampleHardwareAsync(s.Tools, media, request); } catch (EncodeException) when (attempt == 0) { }
        Assert.True(sample!.Matches(request));
        Assert.True(sample.VideoKbps > 0 && sample.Fps > 0);
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "av1-gpu-sample-*").Length);
    }

    [Fact]
    public async Task SizeTargetedEncodeStaysUnderLimit()
    {
        var input = s.Get("target.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=6", "-f", "lavfi", "-i", "sine=d=6", "-c:v", "ffv1", "-c:a", "pcm_s16le");
        var media = await MediaProbe.ProbeAsync(s.Tools, input);
        const long limit = 500_000;
        var request = new EncodeRequest { InputPath = input, OutputPath = Out("target.mp4"), Quality = new QualitySettings(35, 10, 30) };
        var result = await SizeTargeting.EncodeToSizeAsync(s.Tools, request, media, limit);
        Assert.True(result.OutputBytes <= limit, $"{result.OutputBytes} > {limit}");
        Assert.True(result.OutputBytes > limit / 4, "Bitrate targeting should use a reasonable share of the budget");
        Assert.Equal("av1", result.Output.Video!.Codec);
    }
}

/// <summary>Synchronous IProgress so tests observe reports in order without a synchronization context.</summary>
sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
