using System.IO;
using System.Text.Json;
using Av1Encoder.Engine;

namespace Av1Encoder.App;

public sealed class AppSettings
{
    /// <summary>Null means "same folder as the source video".</summary>
    public string? OutputFolder { get; set; }
    public QualityPreset Preset { get; set; } = QualityPreset.Balanced;
    public bool UseCustomQuality { get; set; }
    public int CustomCrf { get; set; } = 32;
    public int CustomSpeed { get; set; } = 6;
    public bool KeepAudio { get; set; } = true;
    public VideoEncoder Encoder { get; set; } = VideoEncoder.SvtAv1;
    /// <summary>Optional per-preset overrides of the built-in defaults, for tuning without a rebuild.</summary>
    public Dictionary<QualityPreset, QualitySettings>? PresetOverrides { get; set; }
    /// <summary>Extra SVT-AV1 parameters, e.g. "tune=0". Advanced; empty by default.</summary>
    public string? SvtParams { get; set; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    static string FilePath => Path.Combine(AppLog.DataDir, "settings.json");

    public QualitySettings QualityFor(QualityPreset preset) =>
        PresetOverrides?.GetValueOrDefault(preset) ?? QualitySettings.For(preset);

    public static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch (Exception ex) { AppLog.Write("Settings could not be read; using defaults. " + ex.Message); }
        return new();
    }

    public void Save()
    {
        try { Directory.CreateDirectory(AppLog.DataDir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); }
        catch (Exception ex) { AppLog.Write("Settings could not be saved: " + ex.Message); }
    }
}

/// <summary>Application log: a file under %LOCALAPPDATA% plus an in-memory copy for the log window.</summary>
public static class AppLog
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AV1 Video Encoder");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string LogFile => Path.Combine(LogDir, "app.log");

    static readonly object Gate = new();
    static readonly List<string> Lines = new();
    public static event Action<string>? LineWritten;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        lock (Gate)
        {
            Lines.Add(line);
            if (Lines.Count > 5000) Lines.RemoveRange(0, 1000);
            try
            {
                Directory.CreateDirectory(LogDir);
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 5 * 1024 * 1024)
                    File.Move(LogFile, Path.Combine(LogDir, "app.old.log"), true);
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch { } // logging must never break the app
        }
        LineWritten?.Invoke(line);
    }

    public static string Snapshot() { lock (Gate) return string.Join(Environment.NewLine, Lines); }
}
