using System.Text;
using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

/// <summary>
/// Checks the size/time estimates against real encodes: for each clip in ESTIMATE_DIR, a 30 s excerpt is analysed, then
/// encoded with every Compress preset and a Discord plan. Slow; nothing else should run meanwhile (it times encodes).
/// Writes artifacts/estimate-validation.md with actual / point-estimate ratios.
/// </summary>
[Collection("ffmpeg")]
public class EstimateValidationTests(Samples s)
{
    static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".ts", ".mts"];

    /// <summary>Same comparison for the first available GPU encoder (ESTIMATE_GPU=1). Writes artifacts/estimate-validation-gpu.md.</summary>
    [Fact]
    public async Task CompareGpuEstimatesWithRealEncodes()
    {
        var dir = Environment.GetEnvironmentVariable("ESTIMATE_DIR");
        if (string.IsNullOrEmpty(dir) || Environment.GetEnvironmentVariable("ESTIMATE_GPU") != "1") return;
        var gpu = (await HardwareDetector.DetectAsync(s.Tools)).First(a => a.Available).Encoder;
        var work = Path.Combine(s.Dir, "gpu-estimates");
        Directory.CreateDirectory(work);
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Av1Encoder.slnx"))) root = Path.GetDirectoryName(root)!;
        var md = new StringBuilder("| Clip | Preset | Size range | Actual size | Size ratio | Time range | Actual time | Time ratio |\n| --- | --- | --- | --- | --- | --- | --- | --- |\n");
        var sizeRatios = new List<double>();
        var timeRatios = new List<double>();
        void Save() => File.WriteAllText(Path.Combine(root, "artifacts", "estimate-validation-gpu.md"),
            $"# GPU estimate validation ({gpu})\n\nRatios are actual / point estimate. Generated {DateTime.Now:yyyy-MM-dd HH:mm}.\n\n{md}\n" +
            $"Size ratio: min {sizeRatios.DefaultIfEmpty().Min():0.00}, max {sizeRatios.DefaultIfEmpty().Max():0.00}, n={sizeRatios.Count}\n\n" +
            $"Time ratio: min {timeRatios.DefaultIfEmpty().Min():0.00}, max {timeRatios.DefaultIfEmpty().Max():0.00}, n={timeRatios.Count}\n");

        // AMF sometimes fails to initialize or hangs; one retry, and no software fallback (it would time the wrong encoder).
        static async Task<T> Retry<T>(Func<Task<T>> run) { try { return await run(); } catch (EncodeException) { return await run(); } }

        foreach (var clip in Directory.EnumerateFiles(dir).Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Order())
        {
            var name = Path.GetFileName(clip);
            try
            {
                var media = await MediaProbe.ProbeAsync(s.Tools, clip);
                var len = media.Duration < TimeSpan.FromSeconds(30) ? media.Duration : TimeSpan.FromSeconds(30);
                var start = (media.Duration - len) / 2;
                foreach (var preset in Enum.GetValues<QualityPreset>())
                {
                    var request = new EncodeRequest
                    {
                        InputPath = clip, OutputPath = Path.Combine(work, $"{Guid.NewGuid():N}.mp4"), Quality = QualitySettings.For(preset),
                        Encoder = gpu, AllowSoftwareFallback = false,
                        TrimStart = start > TimeSpan.Zero ? start : null, TrimEnd = start + len < media.Duration ? start + len : null,
                    };
                    var sample = await Retry(() => SizeTargeting.SampleHardwareAsync(s.Tools, media, request));
                    var estimate = EncodeEstimator.EstimateHardware(media, request, sample);
                    var result = await Retry(() => new EncodeJob(s.Tools, request, media).RunAsync(null, CancellationToken.None));
                    double sizePoint = estimate.MinBytes / EncodeEstimator.HardwareSizeLow, timePoint = estimate.MinTime!.Value.TotalSeconds / EncodeEstimator.HardwareTimeLow;
                    double sizeRatio = result.OutputBytes / sizePoint, timeRatio = result.EncodeTime.TotalSeconds / timePoint;
                    sizeRatios.Add(sizeRatio);
                    timeRatios.Add(timeRatio);
                    md.AppendLine($"| {name} | {preset} | {estimate.MinBytes / 1048576.0:0.0}-{estimate.MaxBytes / 1048576.0:0.0} MB | {result.OutputBytes / 1048576.0:0.0} MB | {sizeRatio:0.00} | " +
                        $"{estimate.MinTime.Value.TotalSeconds:0.0}-{estimate.MaxTime!.Value.TotalSeconds:0.0} s | {result.EncodeTime.TotalSeconds:0.0} s | {timeRatio:0.00} |");
                    File.Delete(result.OutputPath);
                }
            }
            catch (Exception ex) { md.AppendLine($"| {name} | failed: {ex.Message.ReplaceLineEndings(" ")} | | | | | | |"); }
            Save();
        }
    }

    [Fact]
    public async Task CompareEstimatesWithRealEncodes()
    {
        var dir = Environment.GetEnvironmentVariable("ESTIMATE_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        var work = Path.Combine(s.Dir, "estimates");
        Directory.CreateDirectory(work);
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Av1Encoder.slnx"))) root = Path.GetDirectoryName(root)!;
        var md = new StringBuilder("| Clip | Case | Size range | Actual size | Size ratio | Time range | Actual time | Time ratio |\n| --- | --- | --- | --- | --- | --- | --- | --- |\n");
        var sizeRatios = new List<double>();
        var timeRatios = new List<double>();
        void Save() => File.WriteAllText(Path.Combine(root, "artifacts", "estimate-validation.md"),
            $"# Estimate validation\n\nRatios are actual / point estimate (range midpoint before widening). Generated {DateTime.Now:yyyy-MM-dd HH:mm}.\n\n{md}\n" +
            $"Size ratio: min {sizeRatios.DefaultIfEmpty().Min():0.00}, max {sizeRatios.DefaultIfEmpty().Max():0.00}, n={sizeRatios.Count}\n\n" +
            $"Time ratio: min {timeRatios.DefaultIfEmpty().Min():0.00}, max {timeRatios.DefaultIfEmpty().Max():0.00}, n={timeRatios.Count}\n");

        foreach (var clip in Directory.EnumerateFiles(dir).Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Order())
        {
            var name = Path.GetFileName(clip);
            try
            {
                var media = await MediaProbe.ProbeAsync(s.Tools, clip);
                var len = media.Duration < TimeSpan.FromSeconds(30) ? media.Duration : TimeSpan.FromSeconds(30);
                var start = (media.Duration - len) / 2;
                TimeSpan? trimStart = start > TimeSpan.Zero ? start : null;
                TimeSpan? trimEnd = start + len < media.Duration ? start + len : null;
                var complexity = await SizeTargeting.AnalyzeAsync(s.Tools, media, trimStart, trimEnd);

                var cases = Enum.GetValues<QualityPreset>().Select(p => (Name: p.ToString(), Request: new EncodeRequest
                {
                    InputPath = clip, OutputPath = Path.Combine(work, $"{Guid.NewGuid():N}.mp4"), Quality = QualitySettings.For(p),
                    TrimStart = trimStart, TrimEnd = trimEnd,
                })).ToList();
                var planInput = new PlanInput(media, 19_922_944, DiscordStrategy.Balanced) { TrimStart = trimStart, TrimEnd = trimEnd, Complexity = complexity };
                var plan = SizePlanner.Plan(planInput);
                cases.Add(("Discord Balanced", plan.Apply(new EncodeRequest { InputPath = clip, OutputPath = Path.Combine(work, $"{Guid.NewGuid():N}.mp4"), Quality = QualitySettings.For(QualityPreset.Balanced) }, planInput)
                    with { Quality = new QualitySettings(32, plan.Settings.Speed, 26, VideoBitrateKbps: plan.VideoKbps) }));

                foreach (var (caseName, request) in cases)
                {
                    var estimate = EncodeEstimator.Estimate(media, request, complexity);
                    var result = await new EncodeJob(s.Tools, request, media).RunAsync(null, CancellationToken.None);
                    double sizePoint = estimate.MinBytes / (request.Quality.VideoBitrateKbps is null ? EncodeEstimator.SizeLow : 0.6);
                    double timePoint = estimate.MinTime!.Value.TotalSeconds / EncodeEstimator.TimeLow;
                    double sizeRatio = result.OutputBytes / sizePoint, timeRatio = result.EncodeTime.TotalSeconds / timePoint;
                    if (request.Quality.VideoBitrateKbps is null) sizeRatios.Add(sizeRatio);
                    timeRatios.Add(timeRatio);
                    md.AppendLine($"| {name} | {caseName} | {estimate.MinBytes / 1048576.0:0.0}-{estimate.MaxBytes / 1048576.0:0.0} MB | {result.OutputBytes / 1048576.0:0.0} MB | {sizeRatio:0.00} | " +
                        $"{estimate.MinTime.Value.TotalSeconds:0}-{estimate.MaxTime!.Value.TotalSeconds:0} s | {result.EncodeTime.TotalSeconds:0} s | {timeRatio:0.00} |");
                    File.Delete(result.OutputPath);
                }
            }
            catch (Exception ex) { md.AppendLine($"| {name} | failed: {ex.Message.ReplaceLineEndings(" ")} | | | | | | |"); }
            Save();
        }
    }
}
