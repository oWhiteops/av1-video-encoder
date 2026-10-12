using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

public class ProgressParserTests
{
    [Fact]
    public void EmitsSnapshotPerBlock()
    {
        var p = new ProgressParser();
        string[] lines = ["frame=120", "fps=48.5", "total_size=1048576", "out_time_us=4000000", "out_time=00:00:04.000000", "speed=1.93x", "progress=continue"];
        FfmpegProgress? snap = null;
        foreach (var l in lines) snap = p.Feed(l) ?? snap;
        Assert.NotNull(snap);
        Assert.Equal(TimeSpan.FromSeconds(4), snap.OutTime);
        Assert.Equal(1048576, snap.TotalSizeBytes);
        Assert.Equal(1.93, snap.Speed);
        Assert.Equal(48.5, snap.Fps);
        Assert.False(snap.IsEnd);
    }

    [Fact]
    public void HandlesNotAvailableValuesAndEnd()
    {
        var p = new ProgressParser();
        Assert.Null(p.Feed("out_time_us=N/A"));
        Assert.Null(p.Feed("speed=N/A"));
        var snap = p.Feed("progress=end");
        Assert.NotNull(snap);
        Assert.Null(snap.OutTime);
        Assert.Null(snap.Speed);
        Assert.True(snap.IsEnd);
    }

    [Fact]
    public void IgnoresGarbageAndResetsBetweenBlocks()
    {
        var p = new ProgressParser();
        Assert.Null(p.Feed(""));
        Assert.Null(p.Feed("not a key value line"));
        p.Feed("total_size=10");
        p.Feed("progress=continue");
        var second = p.Feed("progress=continue");
        Assert.Null(second!.TotalSizeBytes);
    }
}

public class MediaProbeParseTests
{
    const string Json = """
    {
      "streams": [
        { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "pix_fmt": "yuv420p",
          "avg_frame_rate": "30000/1001", "r_frame_rate": "30000/1001",
          "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": -90 } ] },
        { "index": 1, "codec_type": "audio", "codec_name": "aac", "channels": 2, "channel_layout": "stereo", "sample_rate": "48000", "bit_rate": "128000", "tags": { "language": "eng" } },
        { "index": 2, "codec_type": "audio", "codec_name": "pcm_s16le", "channels": 6, "sample_rate": "48000" },
        { "index": 3, "codec_type": "subtitle", "codec_name": "subrip" },
        { "index": 4, "codec_type": "subtitle", "codec_name": "hdmv_pgs_subtitle" },
        { "index": 5, "codec_type": "video", "codec_name": "mjpeg", "disposition": { "attached_pic": 1 } },
        { "index": 6, "codec_type": "data", "codec_name": "bin_data" },
        { "index": 7, "codec_type": "attachment", "codec_name": "ttf" }
      ],
      "format": { "format_name": "matroska,webm", "format_long_name": "Matroska / WebM", "duration": "12.500000", "size": "5000000", "bit_rate": "3200000" }
    }
    """;

    [Fact]
    public void ParsesAllStreamKinds()
    {
        var m = MediaProbe.Parse(@"C:\x.mkv", Json);
        Assert.Equal(TimeSpan.FromSeconds(12.5), m.Duration);
        Assert.Equal(5000000, m.SizeBytes);
        Assert.NotNull(m.Video);
        Assert.Equal(0, m.Video.Index);
        Assert.Equal(-90, m.Video.Rotation);
        Assert.Equal((1080, 1920), m.Video.DisplaySize);
        Assert.Equal(29.97, m.Video.FrameRate, 2);
        Assert.False(m.Video.IsVariableFrameRate);
        Assert.Equal(2, m.Audio.Count);
        Assert.Equal("eng", m.Audio[0].Language);
        Assert.Equal(6, m.Audio[1].Channels);
        Assert.Equal(2, m.Subtitles.Count);
        Assert.True(m.Subtitles[0].IsText);
        Assert.False(m.Subtitles[1].IsText);
        Assert.Equal(1, m.ExtraVideoStreams);
        Assert.Equal(1, m.DataStreams);
        Assert.Equal(1, m.AttachmentStreams);
    }

    [Theory]
    [InlineData("yuv420p", null, 8)]
    [InlineData("yuv420p10le", null, 10)]
    [InlineData("yuv444p12le", null, 12)]
    [InlineData("p010le", null, 10)]
    [InlineData("nv12", null, 8)]
    [InlineData("gbrp", 10, 10)]
    public void DetectsBitDepth(string pixFmt, int? raw, int expected) => Assert.Equal(expected, MediaProbe.BitDepth(pixFmt, raw));

    [Theory]
    [InlineData("30000/1001", 29.97)]
    [InlineData("25/1", 25)]
    [InlineData("0/0", 0)]
    [InlineData(null, 0)]
    public void ParsesRates(string? text, double expected) => Assert.Equal(expected, MediaProbe.Rate(text), 2);
}

public class FfmpegArgumentsTests
{
    static MediaInfo Media(string audioCodec = "aac", params SubtitleStreamInfo[] subs) => new(
        @"C:\in\clip.mov", "mov", "QuickTime", 10_000_000, TimeSpan.FromSeconds(10), 8_000_000,
        new VideoStreamInfo(0, "h264", 1920, 1080, 30, false, 0, "yuv420p", 8, null),
        [new AudioStreamInfo(1, audioCodec, 2, "stereo", 48000, 192000, null)], subs, 0, 0, 0);

    static EncodeRequest Request(string output = @"C:\out\clip.mp4", QualityPreset preset = QualityPreset.Balanced) => new()
    {
        InputPath = @"C:\in\clip.mov", OutputPath = output, Quality = QualitySettings.For(preset),
    };

    static string After(List<string> args, string flag) => args[args.IndexOf(flag) + 1];

    [Fact]
    public void DefaultIsSvtAv1CrfTenBitMp4()
    {
        var args = FfmpegArguments.Build(Media(), Request(), VideoEncoder.SvtAv1, @"C:\out\tmp.partial");
        Assert.Equal("libsvtav1", After(args, "-c:v"));
        Assert.Equal(QualitySettings.For(QualityPreset.Balanced).Crf.ToString(), After(args, "-crf"));
        Assert.Equal("yuv420p10le", After(args, "-pix_fmt"));
        Assert.Equal("mp4", After(args, "-f"));
        Assert.Equal("pipe:1", After(args, "-progress"));
        Assert.Equal("vfr", After(args, "-fps_mode"));
        Assert.Contains("+faststart", args);
        Assert.Equal(@"C:\in\clip.mov", After(args, "-i"));
        Assert.Equal(@"C:\out\tmp.partial", args[^1]);
        Assert.DoesNotContain("-b:v", args);
    }

    [Fact]
    public void PathsWithSpacesAndQuotesStaySingleArguments()
    {
        var media = Media() with { Path = @"C:\My Videos\it's ""fun"" & more.mov" };
        var args = FfmpegArguments.Build(media, Request(), VideoEncoder.SvtAv1, @"C:\out dir\x.partial");
        Assert.Equal(media.Path, After(args, "-i"));
        Assert.Equal(@"C:\out dir\x.partial", args[^1]);
    }

    [Fact]
    public void CopiesAacButReencodesPcm()
    {
        var copy = FfmpegArguments.Build(Media("aac"), Request(), VideoEncoder.SvtAv1, "o");
        Assert.Equal("copy", After(copy, "-c:a:0"));
        var pcm = FfmpegArguments.Build(Media("pcm_s24le"), Request(), VideoEncoder.SvtAv1, "o");
        Assert.Equal("aac", After(pcm, "-c:a:0"));
        Assert.Equal("160k", After(pcm, "-b:a:0"));
    }

    [Fact]
    public void RemovingAudioMapsNoAudio()
    {
        var args = FfmpegArguments.Build(Media(), Request() with { IncludeAudio = false }, VideoEncoder.SvtAv1, "o");
        Assert.DoesNotContain("0:1", args);
        Assert.Contains(FfmpegArguments.Warnings(Media(), Request() with { IncludeAudio = false }), w => w.Contains("Audio removed"));
    }

    [Fact]
    public void TextSubtitlesBecomeMovTextInMp4AndBitmapOnesAreReported()
    {
        var media = Media("aac", new SubtitleStreamInfo(2, "subrip", "eng"), new SubtitleStreamInfo(3, "hdmv_pgs_subtitle", "eng"));
        var args = FfmpegArguments.Build(media, Request(), VideoEncoder.SvtAv1, "o");
        Assert.Contains("0:2", args);
        Assert.DoesNotContain("0:3", args);
        Assert.Equal("mov_text", After(args, "-c:s:0"));
        Assert.Contains(FfmpegArguments.Warnings(media, Request()), w => w.Contains("image-based subtitle"));
        Assert.Equal(OutputContainer.Mkv, FfmpegArguments.PreferredContainer(media));
    }

    [Fact]
    public void MkvKeepsAllSubtitlesAsCopies()
    {
        var media = Media("aac", new SubtitleStreamInfo(2, "subrip", null), new SubtitleStreamInfo(3, "hdmv_pgs_subtitle", null));
        var req = Request(@"C:\out\clip.mkv");
        var args = FfmpegArguments.Build(media, req, VideoEncoder.SvtAv1, "o");
        Assert.Contains("0:2", args);
        Assert.Contains("0:3", args);
        Assert.Equal("matroska", After(args, "-f"));
        Assert.DoesNotContain(FfmpegArguments.Warnings(media, req), w => w.Contains("subtitle"));
    }

    [Fact]
    public void PlainSourcePrefersMp4()
    {
        Assert.Equal(OutputContainer.Mp4, FfmpegArguments.PreferredContainer(Media("aac", new SubtitleStreamInfo(2, "subrip", null))));
    }

    [Fact]
    public void BitrateModeUsesTargetBitrateInsteadOfCrf()
    {
        var req = Request() with { Quality = QualitySettings.For(QualityPreset.Balanced) with { VideoBitrateKbps = 1500 } };
        var args = FfmpegArguments.Build(Media(), req, VideoEncoder.SvtAv1, "o");
        Assert.Equal("1500k", After(args, "-b:v"));
        Assert.DoesNotContain("-crf", args);
    }

    [Theory]
    [InlineData(VideoEncoder.Amf, "av1_amf")]
    [InlineData(VideoEncoder.Nvenc, "av1_nvenc")]
    [InlineData(VideoEncoder.Qsv, "av1_qsv")]
    public void HardwareEncodersStillProduceAv1(VideoEncoder e, string name)
    {
        var args = FfmpegArguments.Build(Media(), Request(), e, "o");
        Assert.Equal(name, After(args, "-c:v"));
        Assert.Equal(name, HardwareDetector.FfmpegName(e));
    }

    [Fact]
    public void AmfUsesConstantQpAndP010ForTenBitSources()
    {
        var tenBit = Media() with { Video = Media().Video! with { PixelFormat = "yuv420p10le", BitDepth = 10, ColorTransfer = "smpte2084" } };
        var args = FfmpegArguments.Build(tenBit, Request(), VideoEncoder.Amf, "o");
        Assert.Equal("cqp", After(args, "-rc"));
        Assert.Equal("p010le", After(args, "-pix_fmt"));
        Assert.Equal(FfmpegArguments.AmfQp(QualitySettings.For(QualityPreset.Balanced).HardwareQuality).ToString(), After(args, "-qp_p"));
        Assert.DoesNotContain("qvbr", args);
        Assert.Equal("qvbr", After(FfmpegArguments.Build(Media(), Request(), VideoEncoder.Amf, "o"), "-rc"));   // 8-bit unchanged

        // Presets map to the QPs that matched their QVBR quality, in order.
        Assert.Equal([116, 132, 148], new[] { QualityPreset.HighQuality, QualityPreset.Balanced, QualityPreset.SmallestFile }
            .Select(p => FfmpegArguments.AmfQp(QualitySettings.For(p).HardwareQuality)));
    }

    [Fact]
    public void NoVideoStreamIsAClearError()
    {
        var media = Media() with { Video = null };
        var ex = Assert.Throws<EncodeException>(() => FfmpegArguments.Build(media, Request(), VideoEncoder.SvtAv1, "o"));
        Assert.Contains("video stream", ex.Message);
    }

    [Fact]
    public void PresetsAreOrderedByQuality()
    {
        var hq = QualitySettings.For(QualityPreset.HighQuality);
        var bal = QualitySettings.For(QualityPreset.Balanced);
        var small = QualitySettings.For(QualityPreset.SmallestFile);
        Assert.True(hq.Crf < bal.Crf && bal.Crf < small.Crf);
        Assert.True(hq.HardwareQuality < bal.HardwareQuality && bal.HardwareQuality < small.HardwareQuality);
        Assert.True(hq.Speed <= bal.Speed);
    }

    [Fact]
    public void DisplayStringQuotesArgumentsWithSpaces()
    {
        Assert.Equal("ffmpeg -i \"C:\\a b.mp4\" out", FfmpegArguments.ToDisplayString("ffmpeg", ["-i", @"C:\a b.mp4", "out"]));
    }
}

public class SizeTargetingTests
{
    [Fact]
    public void BitrateFitsBudget()
    {
        // 10 MB over 60 s with 128 kbps audio.
        int kbps = SizeTargeting.CalculateVideoBitrateKbps(10_000_000, TimeSpan.FromSeconds(60), 128);
        double totalBytes = (kbps + 128) * 1000 / 8.0 * 60;
        Assert.True(totalBytes <= 10_000_000 * 0.95 + 1000);
        Assert.True(kbps > 1000);
    }

    [Fact]
    public void LongVideoCanGoBelowMinimum()
    {
        int kbps = SizeTargeting.CalculateVideoBitrateKbps(10_000_000, TimeSpan.FromHours(2), 128);
        Assert.True(kbps < SizeTargeting.MinVideoKbps);
    }
}

public class EncodeJobHelperTests
{
    [Fact]
    public void TempFileLivesBesideOutputAndIsUnique()
    {
        var a = EncodeJob.TempPathFor(@"D:\videos\out.mp4");
        var b = EncodeJob.TempPathFor(@"D:\videos\out.mp4");
        Assert.Equal(@"D:\videos", Path.GetDirectoryName(a));
        Assert.EndsWith(".partial", a);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("av_interleaved_write_frame(): No space left on device", VideoEncoder.SvtAv1, "ran out of space")]
    [InlineData("[av1_amf @ 0x] encoder->Init() failed with error 5", VideoEncoder.Amf, "could not be initialized")]
    [InlineData("C:\\a.mp4: Invalid data found when processing input", VideoEncoder.SvtAv1, "corrupt")]
    [InlineData("C:\\out.mp4: Permission denied", VideoEncoder.SvtAv1, "Access was denied")]
    [InlineData("something unexpected", VideoEncoder.SvtAv1, "something unexpected")]
    public void ExplainsCommonFailures(string line, VideoEncoder e, string expected) =>
        Assert.Contains(expected, EncodeJob.Explain([line], e));

    [Fact]
    public void RequiredSpaceUsesBitrateWhenTargetingSize()
    {
        var media = new MediaInfo("x", "mov", "", 1_000_000_000, TimeSpan.FromSeconds(100), null, null, [], [], 0, 0, 0);
        var req = new EncodeRequest { InputPath = "x", OutputPath = "y.mp4", Quality = new QualitySettings(30, 6, 28, VideoBitrateKbps: 1000) };
        Assert.InRange(EncodeJob.EstimateRequiredBytes(req, media), 15_000_000, 20_000_000);
        Assert.Equal(1_000_000_000, EncodeJob.EstimateRequiredBytes(req with { Quality = new QualitySettings(30, 6, 28) }, media));
    }
}
