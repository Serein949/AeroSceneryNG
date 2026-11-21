using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroSceneryNG.Core
{
    /// <summary>Persisted user preferences (JSON, never crashes the app on corrupt input).</summary>
    public sealed class AppSettings
    {
        public string SourceId { get; set; }

        /// <summary>Global image zoom override, -1 = automatic (AFS level + 3).</summary>
        public int ImageZoom { get; set; }

        /// <summary>Per-level image zoom override; 0 or missing = automatic.</summary>
        public Dictionary<int, int> LevelZooms { get; set; }

        public int[] Levels { get; set; }

        /// <summary>fs4, fsg or both.</summary>
        public string Target { get; set; }

        public string OutputDirectory { get; set; }
        public string SceneryDirectory { get; set; }
        public string FsgDirectory { get; set; }
        public bool InstallIntoScenery { get; set; }

        /// <summary>Package the FSG images folder into &lt;name&gt;_images.tme.</summary>
        public bool PackageTme { get; set; }

        /// <summary>Also generate AFS4 terrain elevation (.tth) from the Terrarium DEM.</summary>
        public bool GenerateElevation { get; set; }
        public string SceneryName { get; set; }

        /// <summary>Dark or Light.</summary>
        public string Theme { get; set; }

        /// <summary>zh or en.</summary>
        public string Language { get; set; }

        public int DownloadThreads { get; set; }
        public int WorkerThreads { get; set; }
        public bool SkipExistingTiles { get; set; }

        /// <summary>Interactive selection grid level (9-14).</summary>
        public int GridLevel { get; set; }

        /// <summary>Custom tile-cache directory; empty = %LOCALAPPDATA%\AeroSceneryNG\tilecache.</summary>
        public string CacheDirectory { get; set; }

        /// <summary>Disk cache limit in MB; 0 = unlimited.</summary>
        public int CacheLimitMb { get; set; }

        /// <summary>Trim the oldest cached tiles when the limit is exceeded.</summary>
        public bool AutoCleanCache { get; set; }

        /// <summary>Also write the unmodified 2048px mosaic as PNG.</summary>
        public bool SaveOriginalImages { get; set; }
        public string OriginalImagesDirectory { get; set; }

        /// <summary>Use LZHAM (official chunk format) instead of zlib for TTC payloads.</summary>
        public bool UseLzham { get; set; }

        // ---- optional image processing (mirrors the original AeroScenery image settings) ----
        public bool EnableImageProcessing { get; set; }
        public int BrightnessAdjustment { get; set; }
        public int ContrastAdjustment { get; set; }
        public int SaturationAdjustment { get; set; }
        public int SharpnessAdjustment { get; set; }
        public int RedAdjustment { get; set; }
        public int GreenAdjustment { get; set; }
        public int BlueAdjustment { get; set; }

        /// <summary>Colour-based water unification (not an OSM water mask).</summary>
        public bool WaterTintEnabled { get; set; }
        public string WaterTintColor { get; set; }
        public int WaterTintStrength { get; set; }

        public string TiandituKey { get; set; }
        public string MapboxKey { get; set; }

        public double LastLat { get; set; }
        public double LastLon { get; set; }
        public int LastZoom { get; set; }

        public AppSettings()
        {
            SourceId = "bing";
            ImageZoom = -1;
            LevelZooms = new Dictionary<int, int>();
            Levels = new[] { 12, 13, 14 };
            Target = "fs4";
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            OutputDirectory = Path.Combine(docs, "AeroSceneryNG", "output");
            SceneryDirectory = Path.Combine(docs, "Aerofly FS 4", "scenery", "images");
            FsgDirectory = "";
            InstallIntoScenery = true;
            PackageTme = true;
            GenerateElevation = false;
            SceneryName = "MyScenery";
            Theme = "Dark";
            Language = "zh";
            DownloadThreads = 64;
            WorkerThreads = 0;
            SkipExistingTiles = true;
            GridLevel = 12;
            CacheDirectory = "";
            CacheLimitMb = 4096;
            AutoCleanCache = true;
            SaveOriginalImages = false;
            OriginalImagesDirectory = Path.Combine(docs, "AeroSceneryNG", "originals");
            UseLzham = true;
            EnableImageProcessing = false;
            WaterTintEnabled = false;
            WaterTintColor = "#1B4F8A";
            WaterTintStrength = 100;
            TiandituKey = "";
            MapboxKey = "";
            LastLat = 39.9042;
            LastLon = 116.4074;
            LastZoom = 11;
        }

        public static string SettingsPath
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("AEROSCENERY_SETTINGS");
                if (!string.IsNullOrWhiteSpace(custom))
                {
                    return Path.GetFullPath(custom);
                }

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AeroSceneryNG", "settings.json");
            }
        }

        [JsonIgnore]
        public SceneryTarget TargetKind
        {
            get
            {
                SceneryTarget t;
                return Enum.TryParse(Target, true, out t) ? t : SceneryTarget.Fs4;
            }
        }

        [JsonIgnore]
        public bool IsDarkTheme
        {
            get { return !string.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase); }
        }

        public static AppSettings Load()
        {
            try
            {
                string path = SettingsPath;
                if (File.Exists(path))
                {
                    AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path),
                        JsonOptions);
                    if (loaded != null)
                    {
                        loaded.Normalise();
                        return loaded;
                    }
                }
            }
            catch { }

            AppSettings fresh = new AppSettings();
            fresh.Normalise();
            return fresh;
        }

        public void Save()
        {
            try
            {
                string path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch
            {
                // Preferences must never take the app down.
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private void Normalise()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (string.IsNullOrEmpty(SourceId)) SourceId = "bing";
            if (Levels == null || Levels.Length == 0) Levels = new[] { 12, 13, 14 };
            LevelZooms = LevelZooms ?? new Dictionary<int, int>();

            if (string.IsNullOrEmpty(Target)) Target = "fs4";
            if (TargetKind == SceneryTarget.Fs4) Target = "fs4";
            else if (TargetKind == SceneryTarget.Fsg) Target = "fsg";
            else Target = "both";

            if (string.IsNullOrEmpty(OutputDirectory))
                OutputDirectory = Path.Combine(docs, "AeroSceneryNG", "output");
            if (string.IsNullOrEmpty(SceneryDirectory))
                SceneryDirectory = Path.Combine(docs, "Aerofly FS 4", "scenery", "images");
            FsgDirectory = FsgDirectory ?? "";
            SceneryName = SanitiseName(string.IsNullOrWhiteSpace(SceneryName) ? "MyScenery" : SceneryName);

            if (LastZoom < 2 || LastZoom > 19) LastZoom = 11;
            if (double.IsNaN(LastLat) || Math.Abs(LastLat) > 85) LastLat = 39.9042;
            if (double.IsNaN(LastLon) || Math.Abs(LastLon) > 180) LastLon = 116.4074;

            if (ImageZoom != -1 && (ImageZoom < 8 || ImageZoom > 19)) ImageZoom = -1;
            DownloadThreads = Math.Clamp(DownloadThreads, 1, 128);
            WorkerThreads = Math.Clamp(WorkerThreads, 0, 16);
            GridLevel = Math.Clamp(GridLevel, 9, 14);
            CacheLimitMb = Math.Clamp(CacheLimitMb, 0, 102400);
            CacheDirectory = CacheDirectory ?? "";
            if (string.IsNullOrWhiteSpace(OriginalImagesDirectory))
                OriginalImagesDirectory = Path.Combine(docs, "AeroSceneryNG", "originals");
            TiandituKey = TiandituKey ?? "";
            MapboxKey = MapboxKey ?? "";
            if (!string.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase)) Theme = "Dark";
            else Theme = "Light";
            if (!string.Equals(Language, Loc.English, StringComparison.OrdinalIgnoreCase)) Language = Loc.Chinese;
            else Language = Loc.English;

            BrightnessAdjustment = Math.Clamp(BrightnessAdjustment, -100, 100);
            ContrastAdjustment = Math.Clamp(ContrastAdjustment, -100, 100);
            SaturationAdjustment = Math.Clamp(SaturationAdjustment, -100, 100);
            SharpnessAdjustment = Math.Clamp(SharpnessAdjustment, 0, 100);
            RedAdjustment = Math.Clamp(RedAdjustment, -100, 100);
            GreenAdjustment = Math.Clamp(GreenAdjustment, -100, 100);
            BlueAdjustment = Math.Clamp(BlueAdjustment, -100, 100);
            WaterTintStrength = Math.Clamp(WaterTintStrength, 0, 100);
            if (string.IsNullOrWhiteSpace(WaterTintColor)) WaterTintColor = "#1B4F8A";
        }

        public static string SanitiseName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            return string.IsNullOrEmpty(name) ? "MyScenery" : name;
        }
    }
}
