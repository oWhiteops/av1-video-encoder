using System.Diagnostics;
using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

/// <summary>
/// Hardware encoders this PC lacks can't be run, but their arguments can still be checked against the bundled FFmpeg's
/// own option list (<c>ffmpeg -h encoder=...</c>): every option must exist and every named value must be one it lists.
/// Catches typos and options removed in an FFmpeg update. Encodes nothing.
/// </summary>
[Collection("ffmpeg")]
public class HardwareArgumentTests(Samples s)
{
    static MediaInfo Media(int bitDepth) => new(@"C:\in\clip.mp4", "mov", "QuickTime", 100_000_000, TimeSpan.FromSeconds(60), null,
        new VideoStreamInfo(0, "h264", 1920, 1080, 30, false, 0, bitDepth > 8 ? "yuv420p10le" : "yuv420p", bitDepth, null),
        [new AudioStreamInfo(1, "aac", 2, "stereo", 48000, 128_000, null)], [], 0, 0, 0);

    string Help(string args)
    {
        var p = Process.Start(new ProcessStartInfo(s.Tools.FfmpegPath, "-hide_banner " + args) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text;
    }

    [Theory]
    [InlineData(VideoEncoder.Nvenc, "av1_nvenc")]
    [InlineData(VideoEncoder.Qsv, "av1_qsv")]
    [InlineData(VideoEncoder.Amf, "av1_amf")]
    public void EveryOptionIsKnownToTheBundledFfmpeg(VideoEncoder encoder, string codec)
    {
        string encoderHelp = Help("-h encoder=" + codec), generic = Help("-h full");
        Assert.Contains("Encoder " + codec, encoderHelp);
        foreach (var bitDepth in new[] { 8, 10 })
        foreach (var quality in new[] { QualitySettings.For(QualityPreset.Balanced), new QualitySettings(32, 6, 26, VideoBitrateKbps: 3000) })
        {
            var request = new EncodeRequest { InputPath = @"C:\in\clip.mp4", OutputPath = @"C:\out\x.mp4", Quality = quality, Encoder = encoder };
            var args = FfmpegArguments.Build(Media(bitDepth), request, encoder, @"C:\out\x.partial");
            int start = args.IndexOf("-c:v"), end = args.FindIndex(start, a => a is "-map" or "-movflags");
            Assert.Equal(codec, args[start + 1]);
            for (int i = start + 2; i < end; i += 2)
            {
                string option = args[i].Split(':')[0], value = args[i + 1];
                bool own = encoderHelp.Contains($"\n  {option} ");
                Assert.True(own || generic.Contains($"\n{option} ") || generic.Contains($"\n{option}[") || generic.Contains($"\n  {option} "), $"{codec}: unknown option {option}");
                // Named values (p6, hq, vbr, cqp…) must be listed under the encoder's option; numbers are range-checked by FFmpeg.
                if (own && !double.TryParse(value, out _) && !value.EndsWith('k'))
                    Assert.True(encoderHelp.Contains($"\n     {value} "), $"{codec}: {option} has no value {value}");
            }
        }
    }
}
