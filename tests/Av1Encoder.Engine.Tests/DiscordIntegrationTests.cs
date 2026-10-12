using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

/// <summary>Size-limited (Video to Discord) encoding against the real bundled FFmpeg.</summary>
[Collection("ffmpeg")]
public class DiscordIntegrationTests(Samples s)
{
    static readonly QualitySettings Fast = new(Crf: 35, Speed: 10, HardwareQuality: 30);

    string Out(string name) => Path.Combine(s.Dir, "out", Guid.NewGuid().ToString("N")[..6], name);

    static string[] TempFiles(string dir) =>
        Directory.GetFiles(dir, "*.partial").Concat(Directory.GetFiles(dir, "*.passlog*")).ToArray();

    [Fact]
    public async Task OversizedOutputIsRejectedBeforeReachingTheDestination()
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, s.Basic);
        var output = Out("too-big.mp4");
        var request = new EncodeRequest { InputPath = s.Basic, OutputPath = output, Quality = Fast, MaxOutputBytes = 10_000 };
        var ex = await Assert.ThrowsAsync<OutputTooLargeException>(() => new EncodeJob(s.Tools, request, media).RunAsync(null, CancellationToken.None));
        Assert.True(ex.ActualBytes > 10_000);
        Assert.False(File.Exists(output));
        Assert.Empty(TempFiles(Path.GetDirectoryName(output)!));
    }

    [Fact]
    public async Task ExistingFileSurvivesAnOversizedAttemptEvenWithOverwriteConfirmed()
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, s.Basic);
        var output = Out("existing.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "keep me");
        var request = new EncodeRequest { InputPath = s.Basic, OutputPath = output, Quality = Fast, MaxOutputBytes = 10_000, OverwriteExisting = true };
        await Assert.ThrowsAsync<OutputTooLargeException>(() => new EncodeJob(s.Tools, request, media).RunAsync(null, CancellationToken.None));
        Assert.Equal("keep me", File.ReadAllText(output));
    }

    [Fact]
    public async Task LeftoverPassLogsFromAKilledRunAreRemoved()
    {
        var output = Out("sweep.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var stale = output + ".0123456789abcdef0123456789abcdef.passlog-0.log";
        File.WriteAllText(stale, "junk");
        var media = await MediaProbe.ProbeAsync(s.Tools, s.Basic);
        await new EncodeJob(s.Tools, new EncodeRequest { InputPath = s.Basic, OutputPath = output, Quality = Fast }, media).RunAsync(null, CancellationToken.None);
        Assert.False(File.Exists(stale));
    }
}
