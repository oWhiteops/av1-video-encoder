using System.Text;
using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

/// <summary>
/// Size accuracy of every Discord strategy on easy, noisy and variable-complexity clips. Slow (several minutes), so it
/// runs only with RUN_ACCURACY=1. Writes artifacts/accuracy-matrix.md for docs/DESIGN.md.
/// </summary>
[Collection("ffmpeg")]
public class AccuracyMatrixTests(Samples s)
{
    [Fact]
    public async Task EveryStrategyStaysUnderTheLimit()
    {
        if (Environment.GetEnvironmentVariable("RUN_ACCURACY") != "1") return;

        const string audio = "sine=f=440:d=20";
        var sources = new (string Name, string Path)[]
        {
            ("easy 720p60", s.Get("acc-easy.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=60:d=20", "-f", "lavfi", "-i", audio, "-c:v", "ffv1", "-c:a", "pcm_s16le")),
            ("noisy 720p60", s.Get("acc-noisy.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=60:d=20,noise=alls=12:allf=t", "-f", "lavfi", "-i", audio, "-c:v", "ffv1", "-c:a", "pcm_s16le")),
            ("calm then noisy 720p60", s.Get("acc-variable.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=60:d=10", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=60:d=10,noise=alls=20:allf=t",
                "-f", "lavfi", "-i", audio, "-filter_complex", "[0:v][1:v]concat=n=2:v=1[v]", "-map", "[v]", "-map", "2:a", "-c:v", "ffv1", "-c:a", "pcm_s16le")),
        };
        long[] limits = [1_500_000, 3_000_000, 6_000_000];

        var md = new StringBuilder("| Source | Strategy | Limit (bytes) | Output (bytes) | Of limit | Attempts | Time (s) |\n| --- | --- | --- | --- | --- | --- | --- |\n");
        var failures = new List<string>();
        foreach (var (name, path) in sources)
        {
            var media = await MediaProbe.ProbeAsync(s.Tools, path);
            foreach (var strategy in Enum.GetValues<DiscordStrategy>())
                foreach (var limit in limits)
                {
                    var st = StrategySettings.For(strategy);
                    var output = Path.Combine(s.Dir, "acc", Guid.NewGuid().ToString("N")[..6], "out.mp4");
                    var request = new EncodeRequest
                    {
                        InputPath = path, OutputPath = output, Quality = new QualitySettings(32, st.Speed, 26),
                        TwoPass = st.TwoPass, AudioBitrateKbps = st.AudioKbps,
                    };
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var r = await SizeTargeting.EncodeToSizeAsync(s.Tools, request, media, limit, margin: st.Margin);
                        md.AppendLine($"| {name} | {strategy} | {limit:N0} | {r.OutputBytes:N0} | {100.0 * r.OutputBytes / limit:0.0}% | {r.Attempts} | {watch.Elapsed.TotalSeconds:0} |");
                        if (r.OutputBytes > limit) failures.Add($"{name} {strategy} {limit}");
                    }
                    catch (EncodeException ex)
                    {
                        md.AppendLine($"| {name} | {strategy} | {limit:N0} | failed: {ex.Message} | | | {watch.Elapsed.TotalSeconds:0} |");
                        failures.Add($"{name} {strategy} {limit}: {ex.Message}");
                    }
                }
        }
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Av1Encoder.slnx"))) root = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        File.WriteAllText(Path.Combine(root, "artifacts", "accuracy-matrix.md"), md.ToString());
        Assert.Empty(failures);
    }
}
