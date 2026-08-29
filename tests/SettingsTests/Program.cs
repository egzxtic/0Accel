using ZeroAccel;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
string directory = Path.Combine(Path.GetTempPath(), "0Accel-settings-tests-" + Guid.NewGuid());
try
{
    var store = new SettingsStore(directory);
    Check(store.Load() == new Settings(), "Missing file defaults");
    Check(store.Load().CurveMode == "off", "Fresh settings start with acceleration off");
    Directory.CreateDirectory(directory);
    File.WriteAllText(store.SettingsPath, "{\"Version\":1,\"Dpi\":1600}");
    Check(store.Load().CurveMode == "off" && store.Load().Dpi == 1600, "Profiles without a mode default to OFF");
    Check(store.Load().CapInput == 120, "Legacy missing input cap uses old default in counts/ms");
    File.WriteAllText(store.SettingsPath, "{\"Version\":1,\"Sensitivity\":1.5,\"SensitivityMultiplier\":2,\"CurveMode\":\"neutral\",\"CapInput\":1.2}");
    var legacy = store.Load();
    Check(legacy.Version == 2 && legacy.Sensitivity == 3 && legacy.SensitivityMultiplier == 1 && legacy.CurveMode == "natural" && legacy.CapInput == 120, "Legacy profile migration");
    Check(store.Save(legacy) && store.Load() == legacy && legacy.Validated() == legacy, "Migration is idempotent");
    Check(store.TryLoadFrom(store.SettingsPath, out var migratedAgain) && migratedAgain == legacy, "Migrated profile import");
    foreach (string mode in new[] { "off", "linear", "classic", "natural" })
    {
        Check(store.Save(new Settings { CurveMode = " " + mode.ToUpperInvariant() + " " }), "Save supported curve mode");
        Check(store.Load().CurveMode == mode, "Mode normalization and round trip");
    }
    foreach (string mode in new[] { "jump", "synchronous", "synchronus", "power", "lookup table", "lookup_table", "unknown", "", null! })
    {
        File.WriteAllText(store.SettingsPath, System.Text.Json.JsonSerializer.Serialize(new Settings { CurveMode = mode, Dpi = 3200, Acceleration = .05 }));
        Check(store.TryLoadFrom(store.SettingsPath, out var migrated) && migrated.CurveMode == "off" && migrated.Dpi == 3200 && migrated.Acceleration == .05,
            "Unsupported imported mode falls back to OFF without resetting the profile");
        Check(store.Load().CurveMode == "off", "Unsupported saved mode falls back to OFF");
    }
    Check(store.Save(new Settings { Sensitivity = 1.3, Acceleration = .034, Dpi = 1600, Theme = "Light", StartInTray = true }), "Save");
    var settings = store.Load();
    Check(settings.Sensitivity == 1.3 && settings.Acceleration == .034 && settings.Dpi == 1600 && settings.Theme == "Light" && settings.StartInTray, "Round trip");
    Check(!File.Exists(Path.Combine(directory, "settings.json.tmp")), "Atomic save leaves no temp");
    File.WriteAllText(Path.Combine(directory, "settings.json"), "{broken");
    Check(store.Load() == new Settings() && store.LastError is not null, "Corruption fallback");
    File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"Version\":200}");
    Check(store.Load() == new Settings() && store.LastError is not null, "Unknown schema fallback");
    File.WriteAllText(Path.Combine(directory, "settings.json"), new string('x', 20000));
    Check(store.Load() == new Settings() && store.LastError is not null, "Oversize fallback");
    var invalid = new Settings { Sensitivity = double.NaN, Acceleration = double.PositiveInfinity, Limit = -1, Theme = "unknown", Dpi = 0 };
    var safe = invalid.Validated();
    Check(safe.Sensitivity == 1 && safe.Acceleration == .02 && safe.Limit == 1
        && safe.SensitivityMultiplier == 1 && safe.YxRatio == 1 && safe.Rotation == 0 && safe.CurveMode == "off"
        && safe.CapType == "Output" && safe.GainEnabled && safe.CapInput == 120 && safe.CapOutput == 1.2
        && safe.InputOffset == 15 && !safe.ShowLastMouseMove && !safe.ShowVelocity && safe.ShowGain
        && safe.Dpi is null && safe.Theme == "Dark", "Validation");
    Check(store.Save(invalid), "Invalid values normalized before serialization");
    settings = store.Load();
    var exported = Path.Combine(directory, "custom-export.json");
    Check(store.SaveToFile(settings, exported), "SaveToFile");
    Check(store.TryLoadFrom(exported, out var fromCustom) && fromCustom == safe, "LoadFrom custom path");
    Check(!store.TryLoadFrom(Path.Combine(directory, "missing.json"), out _), "Load missing file");
    Check(store.LastError is not null, "Missing file reports error");
    var impossible = new SettingsStore(Path.Combine(directory, "settings.json", "not-a-directory"));
    Check(!impossible.Save(new Settings()) && impossible.LastError is not null, "Write error surfaced");
    var motionSettings = new Settings { ShowLastMouseMove = true };
    Check(store.Save(motionSettings) && store.Load().ShowLastMouseMove, "Motion preference round trip");
    var natural = new Settings { CurveMode = "natural", Power = 2.75, DecayRate = .125, Limit = 3, GainEnabled = false };
    Check(store.Save(natural) && store.Load() == natural, "Curve parameters persist");
    var badCurve = (natural with { Power = double.NaN, DecayRate = double.PositiveInfinity, CapOutput = .5 }).Validated();
    Check(badCurve.Power == 2 && badCurve.DecayRate == .1 && badCurve.CapOutput == 1, "Curve parameter validation");

    var motion = new MotionSampler(1_000_000);
    Check(!motion.Add(3, 4, 0, out _), "Motion starts without a fabricated interval");
    for (int i = 1; i < 34; i++) Check(!motion.Add(3, 4, i * 1000, out _), "Motion update is rate limited");
    Check(motion.Add(3, 4, 34000, out double speed) && Math.Abs(speed - 5) < 1e-10, "Diagonal distance in counts/ms");
    Check(!motion.Add(0, 0, 35000, out _), "Buttons/zero movement do not produce samples");
    Check(!motion.Add(1000, 0, 200000, out _), "Pause resets the unknown interval");
    Check(motion.Add(34, 0, 234000, out speed) && Math.Abs(speed - 1) < 1e-10, "First post-pause window excludes stale distance");
    Check(!motion.Add(1, 0, 233000, out _), "Backward timestamp resets safely");
    motion.Reset();
    Check(!motion.Add(1, 0, 0, out _), "Reset discards previous device history");
    Check(!motion.Add(2, 0, 0, out _), "Same-timestamp reports do not divide by zero");
    Check(motion.Add(32, 0, 34000, out speed) && Math.Abs(speed - 1) < 1e-10, "Batched reports contribute distance");
    motion.Reset(); motion.Add(int.MinValue, int.MaxValue, 0, out _);
    Check(motion.Add(int.MinValue, int.MaxValue, 34000, out speed) && double.IsFinite(speed) && speed > 0, "Extreme deltas avoid integer overflow");
    motion.Reset(); motion.Add(1, 0, 0, out _);
    int updates = 0;
    for (int i = 1; i <= 8000; i++) if (motion.Add(1, 0, i * 125, out _)) updates++;
    Check(updates is >= 29 and <= 30, "8000 Hz input produces at most 30 UI updates/s");
    Console.WriteLine($"PASS: {checks} settings and motion tests");
}
finally
{
    // Only our unique test directory, never user configuration.
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
}
