using System;
using System.IO;
using System.Text.Json;

namespace ZeroAccel;

public sealed record Settings
{
    public int Version { get; init; } = 2;
    public double Sensitivity { get; init; } = 1;
    public double Acceleration { get; init; } = 0.02;
    public double Limit { get; init; } = 1.5;
    public double SensitivityMultiplier { get; init; } = 1;
    public double YxRatio { get; init; } = 1;
    public int Rotation { get; init; } = 0;
    public string CurveMode { get; init; } = "off";
    public bool GainEnabled { get; init; } = true;
    public string CapType { get; init; } = "Output";
    public double CapInput { get; init; } = 120;
    public double CapOutput { get; init; } = 1.2;
    public double InputOffset { get; init; } = 15;
    public double Power { get; init; } = 2;
    public double DecayRate { get; init; } = .1;
    public bool ShowLastMouseMove { get; init; }
    public bool ShowVelocity { get; init; }
    public bool ShowGain { get; init; } = true;
    public int? Dpi { get; init; }
    public string Theme { get; init; } = "Dark";
    public bool StartInTray { get; init; }

    public Settings Validated() => this with
    {
        Version = 2,
        Sensitivity = Version == 1
            ? Math.Clamp(ValidNumber(Sensitivity, .1, 4, 1) * ValidNumber(SensitivityMultiplier, .1, 6, 1), .1, 24)
            : ValidNumber(Sensitivity, .1, 24, 1),
        Acceleration = ValidNumber(Acceleration, 0, 0.2, 0.02),
        Limit = ValidNumber(Limit, 1, 16, 1.5),
        SensitivityMultiplier = 1, // v1's duplicate multiplier is folded into Sensitivity.
        YxRatio = ValidNumber(YxRatio, 0.25, 8, 1),
        Rotation = NormalizeRotation(Rotation),
        CurveMode = NormalizeCurveMode(CurveMode),
        CapType = NormalizeCapType(CapType),
        CapInput = Version == 1 ? ValidNumber(CapInput, .1, 16, 1.2) * 100 : ValidNumber(CapInput, .1, 1600, 120),
        CapOutput = ValidNumber(CapOutput, 1, 16, 1.2),
        InputOffset = ValidNumber(InputOffset, 0, 200, 15),
        Power = ValidNumber(Power, 1.01, 5, 2),
        DecayRate = ValidNumber(DecayRate, .001, 10, .1),
        Dpi = Dpi is >= 50 and <= 100000 ? Dpi : null,
        Theme = Theme == "Light" ? "Light" : "Dark"
    };
    private static int NormalizeRotation(int value) => value == 90 || value == 180 || value == 270 ? value : 0;
    private static string NormalizeCurveMode(string value) => value?.Trim().ToLowerInvariant() switch
    {
        "linear" => "linear",
        "classic" => "classic",
        "natural" or "neutral" => "natural",
        _ => "off"
    };
    private static string NormalizeCapType(string value) =>
        value?.Trim() is "Input" or "input" ? "Input"
      : value is "Both" or "both" ? "Both"
      : "Output";
    private static double ValidNumber(double n, double min, double max, double fallback) =>
        double.IsFinite(n) ? Math.Clamp(n, min, max) : fallback;
}

public sealed class SettingsStore
{
    private readonly string path;
    public string SettingsPath => path;
    public string? LastError { get; private set; }
    public SettingsStore(string? directory = null) => path = Path.Combine(directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "0Accel"), "settings.json");
    public Settings Load()
    {
        LastError = null;
        try
        {
            if (!File.Exists(path)) return new Settings();
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException();
            return ReadProfile(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { LastError = "M_LoadSettingsFailed"; return new Settings(); }
    }
    public bool Save(Settings settings)
    {
        LastError = null;
        return SaveToFile(settings, path);
    }

    public bool SaveToFile(Settings settings, string filePath)
    {
        LastError = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var tmp = filePath + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings.Validated(), new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(tmp, filePath, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { LastError = "M_SaveSettingsFailed"; return false; }
    }

    public bool TryLoadFrom(string filePath, out Settings settings)
    {
        settings = new Settings();
        LastError = null;
        try
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException();
            if (new FileInfo(filePath).Length > 16384) throw new InvalidDataException();
            settings = ReadProfile(File.ReadAllText(filePath));
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or FileNotFoundException)
        { LastError = "M_ImportFailed"; return false; }
    }

    private static Settings ReadProfile(string json)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement.Deserialize<Settings>();
        if (value is null || value.Version is not 1 and not 2) throw new InvalidDataException();
        if (value.Version == 1 && !document.RootElement.TryGetProperty(nameof(Settings.CapInput), out _))
            value = value with { CapInput = 1.2 };
        return value.Validated();
    }
}
