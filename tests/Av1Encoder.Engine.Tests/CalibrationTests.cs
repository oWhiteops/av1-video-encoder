using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

/// <summary>
/// Stage 6 calibration and validation on real footage. Runs only with CALIBRATION_DIR set to a folder of clips; slow
/// (many full encodes plus VMAF per clip). Writes artifacts/calibration.md with raw numbers to review: it measures, it
/// does not judge, because the pass criteria are agreed after looking at the data.
/// </summary>
[Collection("ffmpeg")]
public class CalibrationTests(Samples s)
{
    static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".ts", ".mts"];
    const long Free = 19_922_944;

    [Fact]
    public async Task MeasureCalibrationAndValidation()
    {
        var dir = Environment.GetEnvironmentVariable("CALIBRATION_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        var work = Path.Combine(s.Dir, "calibration");
        Directory.CreateDirectory(work);

        var cal = new StringBuilder("## Calibration (60 s excerpt, preset 8, video only)\n\n| Clip | Source | CRF 32 kbps | CRF 26 / 38 / 44 size vs 32 | Pixel exponent (720p, 480p) | 30 fps factor | Sample / full |\n| --- | --- | --- | --- | --- | --- | --- |\n");
        var val = new StringBuilder("## Validation (whole clip, Discord encode, Balanced)\n\n| Clip | Target | Predicted r | Predicted label | Size | Mean VMAF | 5th pct VMAF | Worst 2 s VMAF | Analysis s |\n| --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Av1Encoder.slnx"))) root = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        void Save() => File.WriteAllText(Path.Combine(root, "artifacts", "calibration.md"),
            $"# Calibration results\n\nFFmpeg/SVT-AV1 as pinned by BundledFfmpegIsTheCalibratedBuild. Generated {DateTime.Now:yyyy-MM-dd HH:mm}.\n\n{cal}\n{val}");

        foreach (var clip in Directory.EnumerateFiles(dir).Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Order())
        {
            try { await MeasureClip(clip); }
            catch (Exception ex) { cal.AppendLine($"| {Path.GetFileName(clip)} | failed: {ex.Message.ReplaceLineEndings(" ")} | | | | | |"); }
            Save(); // after every clip, so a late failure keeps the results so far
        }

        async Task MeasureClip(string clip)
        {
            var name = Path.GetFileName(clip);
            var media = await MediaProbe.ProbeAsync(s.Tools, clip);
            var v = media.Video!;
            var (w, h) = v.DisplaySize;

            // Calibration on an excerpt from the middle so a long clip doesn't take hours.
            var excerptStart = media.Duration > TimeSpan.FromSeconds(60) ? (media.Duration - TimeSpan.FromSeconds(60)) / 2 : TimeSpan.Zero;
            var excerptLen = media.Duration < TimeSpan.FromSeconds(60) ? media.Duration : TimeSpan.FromSeconds(60);
            double Kbps(int crf, int? side = null, double? fps = null) => CrfKbps(clip, media, excerptStart, excerptLen, crf, side, fps, work);
            double k32 = Kbps(32);
            string ratios = string.Join(" / ", new[] { 26, 38, 44 }.Select(c => (Kbps(c) / k32).ToString("0.00", CultureInfo.InvariantCulture)));
            int shortSide = Math.Min(w, h);
            string Exponent(int side) => side >= shortSide ? "–"
                : (Math.Log(Kbps(32, side) / k32) / Math.Log(Math.Pow((double)side / shortSide, 2))).ToString("0.00", CultureInfo.InvariantCulture);
            string fpsFactor = v.FrameRate > 31 ? (Kbps(32, fps: 30) / k32).ToString("0.00", CultureInfo.InvariantCulture) : "–";
            var excerptEstimate = await SizeTargeting.AnalyzeAsync(s.Tools, media, excerptStart, excerptStart + excerptLen);
            cal.AppendLine($"| {name} | {w}x{h} {v.FrameRate:0.##} fps | {k32:0} | {ratios} | {Exponent(720)}, {Exponent(480)} | {fpsFactor} | {excerptEstimate.Crf32Kbps / k32:0.00} |");

            // Validation: whole clip, three targets chosen so the predicted score spans weak to comfortable.
            var watch = Stopwatch.StartNew();
            var complexity = await SizeTargeting.AnalyzeAsync(s.Tools, media, null, null);
            double analysisSeconds = watch.Elapsed.TotalSeconds;
            foreach (var wantedScore in new[] { 0.3, 0.6, 1.0 })
            {
                double videoKbps = wantedScore * complexity.Crf32Kbps * SizePlanner.PresetFactor(6);
                long target = (long)((videoKbps + 96) * 1000 / 8.0 * media.Duration.TotalSeconds / 0.96);
                var input = new PlanInput(media, target, DiscordStrategy.Balanced) { Complexity = complexity, ShortSide = 0, FrameRate = 0 };
                var plan = SizePlanner.Plan(input);
                var output = Path.Combine(work, $"{Path.GetFileNameWithoutExtension(name)}-{wantedScore:0.0}.mp4");
                if (File.Exists(output)) File.Delete(output);
                var request = plan.Apply(new EncodeRequest { InputPath = clip, OutputPath = output, Quality = QualitySettings.For(QualityPreset.Balanced) }, input);
                string row = $"| {name} | {target / 1048576.0:0.0} MB | {plan.Score:0.00} | {plan.Label}";
                try
                {
                    var result = await SizeTargeting.EncodeToSizeAsync(s.Tools, request, media, target, margin: plan.Settings.Margin);
                    var (mean, p5, worst) = Vmaf(result.OutputPath, clip, w, h, work);
                    val.AppendLine($"{row} | {result.OutputBytes / 1048576.0:0.00} MB | {mean:0.0} | {p5:0.0} | {worst:0.0} | {analysisSeconds:0} |");
                }
                catch (Exception ex)
                {
                    val.AppendLine($"{row} | failed: {ex.Message.ReplaceLineEndings(" ")} | | | | {analysisSeconds:0} |");
                }
                finally
                {
                    if (File.Exists(output)) File.Delete(output);
                }
            }
        }
    }

    /// <summary>Video-only CRF encode of an excerpt at preset 8 (the analysis preset); returns its kbps.</summary>
    double CrfKbps(string clip, MediaInfo media, TimeSpan start, TimeSpan length, int crf, int? side, double? fps, string work)
    {
        var request = new EncodeRequest { InputPath = clip, OutputPath = "", Quality = new(crf, 8, 26), MaxShortSide = side, MaxFrameRate = fps };
        var filters = FfmpegArguments.VideoFilters(media.Video!, request);
        var output = Path.Combine(work, $"crf-{Guid.NewGuid():N}.mkv");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-ss", Secs(start), "-i", clip, "-t", Secs(length), "-map", $"0:{media.Video!.Index}", "-an", "-sn", "-dn" };
        if (filters.Count > 0) args.AddRange(["-vf", string.Join(",", filters)]);
        args.AddRange(["-c:v", "libsvtav1", "-preset", "8", "-crf", $"{crf}", "-pix_fmt", "yuv420p10le", output]);
        var (exit, _, err) = Samples.Run(s.Tools.FfmpegPath, args);
        if (exit != 0) throw new InvalidOperationException(err);
        double kbps = new FileInfo(output).Length * 8 / 1000.0 / length.TotalSeconds;
        File.Delete(output);
        return kbps;
    }

    /// <summary>Per-frame VMAF of an output against its source, at source size; returns mean, 5th percentile, worst 2 s window.</summary>
    (double Mean, double P5, double Worst) Vmaf(string distorted, string reference, int w, int h, string work)
    {
        var outInfo = MediaProbe.ProbeAsync(s.Tools, distorted).GetAwaiter().GetResult().Video!;
        var refInfo = MediaProbe.ProbeAsync(s.Tools, reference).GetAwaiter().GetResult().Video!;
        string refFps = outInfo.FrameRate < refInfo.FrameRate - 0.5 ? $"fps={outInfo.FrameRate.ToString("0.###", CultureInfo.InvariantCulture)}," : "";
        var log = $"vmaf-{Guid.NewGuid():N}.json";
        var psi = new ProcessStartInfo(s.Tools.FfmpegPath) { WorkingDirectory = work, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", distorted, "-i", reference, "-lavfi",
            $"[0:v]scale={w}:{h}:flags=bicubic,format=yuv420p10le,setpts=PTS-STARTPTS[d];[1:v]{refFps}format=yuv420p10le,setpts=PTS-STARTPTS[r];" +
            $"[d][r]libvmaf=log_fmt=json:log_path={log}:n_threads={Environment.ProcessorCount}", "-f", "null", "-" })
            psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!)
        {
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("VMAF failed: " + err);
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(work, log)));
        var scores = doc.RootElement.GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("metrics").GetProperty("vmaf").GetDouble()).ToArray();
        File.Delete(Path.Combine(work, log));
        int window = Math.Max(1, (int)Math.Round(outInfo.FrameRate * 2));
        var sums = new double[scores.Length + 1];
        for (int i = 0; i < scores.Length; i++) sums[i + 1] = sums[i] + scores[i];
        double worst = scores.Length <= window ? scores.Average()
            : Enumerable.Range(0, scores.Length - window + 1).Min(i => (sums[i + window] - sums[i]) / window);
        return (scores.Average(), scores.Order().ElementAt((int)(scores.Length * 0.05)), worst);
    }

    static string Secs(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
