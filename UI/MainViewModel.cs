using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AeroSceneryNG.Core;
using AeroSceneryNG.Ttc;

namespace AeroSceneryNG.UI
{
    /// <summary>One AFS compile level in the sidebar, with its own optional image zoom.</summary>
    public sealed class LevelOption : INotifyPropertyChanged
    {
        private bool isSelected;
        private bool isEnabled = true;
        private int imageZoom;
        private int inheritedZoom;

        public int Level { get; set; }
        public string Label { get; set; }
        public string Hint { get; set; }

        public event EventHandler Changed;

        public bool IsSelected
        {
            get { return isSelected; }
            set
            {
                if (isSelected == value) return;
                isSelected = value;
                OnPropertyChanged("IsSelected");
                RaiseChanged();
            }
        }

        public bool IsEnabled
        {
            get { return isEnabled; }
            set
            {
                if (isEnabled == value) return;
                isEnabled = value;
                OnPropertyChanged("IsEnabled");
            }
        }

        /// <summary>0 = automatic; otherwise the web-mercator imagery zoom for this level only.</summary>
        public int ImageZoom
        {
            get { return imageZoom; }
            set
            {
                int clamped = Math.Max(0, Math.Min(19, value));
                if (imageZoom == clamped) return;
                imageZoom = clamped;
                OnPropertyChanged("ImageZoom");
                OnPropertyChanged("ZoomText");
                OnPropertyChanged("IsAutoZoom");
                RaiseChanged();
            }
        }

        public bool IsAutoZoom
        {
            get { return imageZoom <= 0; }
        }

        /// <summary>Effective zoom inherited from the global slider / auto rule when this level has no override.</summary>
        public int InheritedZoom
        {
            get { return inheritedZoom; }
            set
            {
                if (inheritedZoom == value) return;
                inheritedZoom = value;
                OnPropertyChanged("InheritedZoom");
                OnPropertyChanged("ZoomText");
            }
        }

        public string ZoomText
        {
            get
            {
                int zoom = imageZoom > 0 ? imageZoom : inheritedZoom;
                return zoom > 0 ? "Z" + zoom : Loc.T("自动");
            }
        }

        public RelayCommand ZoomInCommand { get; set; }
        public RelayCommand ZoomOutCommand { get; set; }
        public RelayCommand AutoZoomCommand { get; set; }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private readonly AppSettings settings;
        private ImageryCache cache;
        private SceneryBuilder builder;
        private CancellationTokenSource cancellation;

        private MapSource selectedSource;
        private GeoBounds selection;
        private bool hasSelection;
        private readonly HashSet<SelectionCell> selectionCells = new HashSet<SelectionCell>();
        private readonly HashSet<SelectionCell> completedCells = new HashSet<SelectionCell>();
        private int gridLevel;
        private int completedCount;
        private int cacheLimitMb;
        private string cacheDirectory;
        private bool autoCleanCache;
        private bool saveOriginalImages;
        private string originalImagesDirectory;
        private int completedScanVersion;
        private string outputDirectory;
        private string sceneryDirectory;
        private string fsgDirectory;
        private bool installIntoScenery;
        private bool packageTme;
        private bool generateElevation;
        private string sceneryName;
        private bool isRunning;
        private double progress;
        private string statusMessage;
        private string detailMessage;
        private bool useAutoZoom;
        private bool suppressLevelSync;
        private int imageZoom;
        private SceneryTarget target;
        private int downloadThreads;
        private bool skipExisting;
        private bool useLzham;
        private string themeName;
        private string cacheSizeText;

        private bool isTaskView;
        private string taskStatus = "准备中";
        private string taskStage = "准备";
        private int taskStageIndex;
        private string taskPercentText = "0%";
        private string taskDownloadText = "0 张 · 0 MB";
        private string taskConvertText = "0 / 0 瓦片";
        private string taskSpeedText = "0 MB/s";
        private string taskElapsedText = "00:00";
        private string taskCurrentText = "";
        private string taskInfoText = "";
        private string taskOutputPath = "";
        private long lastSpeedBytes;
        private DateTime lastSpeedAt = DateTime.UtcNow;
        private double lastSpeed;
        private long taskDownloadedBytes;
        private int taskDownloadedTiles;
        private long taskOutputBytes;
        private int taskDone;
        private int taskTotal;
        private int taskFailed;
        private SceneryJob currentJob;

        public MainViewModel()
        {
            settings = AppSettings.Load();
            gridLevel = settings.GridLevel;
            cacheDirectory = settings.CacheDirectory;
            cacheLimitMb = settings.CacheLimitMb;
            autoCleanCache = settings.AutoCleanCache;
            saveOriginalImages = settings.SaveOriginalImages;
            originalImagesDirectory = settings.OriginalImagesDirectory;
            cache = new ImageryCache(CacheRoot(), settings.DownloadThreads);
            builder = new SceneryBuilder(cache);

            Sources = new ObservableCollection<MapSource>(MapSources.All);
            TaskLogs = new ObservableCollection<string>();
            Layers = new ObservableCollection<LevelOption>();
            for (int level = 9; level <= 15; level++)
            {
                int captured = level;
                var option = new LevelOption
                {
                    Level = level,
                    Label = "Level " + level,
                    Hint = string.Format("{0:F1} m/px", Afs2Grid.MetresPerPixel(level, 35.0)),
                    IsSelected = settings.Levels != null && settings.Levels.Contains(level),
                    ZoomInCommand = new RelayCommand(() => NudgeZoom(captured, +1), () => true),
                    ZoomOutCommand = new RelayCommand(() => NudgeZoom(captured, -1), () => true),
                    AutoZoomCommand = new RelayCommand(() => SetLevelZoom(captured, 0), () => true)
                };
                int savedZoom;
                if (settings.LevelZooms != null && settings.LevelZooms.TryGetValue(level, out savedZoom))
                    option.ImageZoom = savedZoom;
                option.Changed += (s, e) => RefreshEstimates();
                option.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == "ImageZoom") PersistLevels();
                };
                Layers.Add(option);
            }

            selectedSource = MapSources.Get(settings.SourceId) ?? Sources.FirstOrDefault();
            outputDirectory = settings.OutputDirectory;
            sceneryDirectory = settings.SceneryDirectory;
            fsgDirectory = settings.FsgDirectory;
            installIntoScenery = settings.InstallIntoScenery;
            packageTme = settings.PackageTme;
            generateElevation = settings.GenerateElevation;
            sceneryName = settings.SceneryName;
            useAutoZoom = settings.ImageZoom <= 0;
            imageZoom = settings.ImageZoom > 0 ? settings.ImageZoom : 14;
            target = settings.TargetKind;
            downloadThreads = settings.DownloadThreads;
            skipExisting = settings.SkipExistingTiles;
            useLzham = settings.UseLzham;
            themeName = settings.IsDarkTheme ? "Dark" : "Light";
            TiandituKey = settings.TiandituKey;
            MapboxKey = settings.MapboxKey;

            statusMessage = Loc.T("在地图上拖拽框选区域，选择级别后点击“开始生成”。");
            detailMessage = "";

            StartCommand = new RelayCommand(async () => await StartAsync(), () => HasSelection && !IsRunning && SelectedSource != null);
            CancelCommand = new RelayCommand(Cancel, () => IsRunning);
            ClearSelectionCommand = new RelayCommand(() => ClearSelection(), () => HasSelection && !IsRunning);
            BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsRunning);
            BrowseSceneryCommand = new RelayCommand(BrowseScenery, () => !IsRunning);
            BrowseFsgCommand = new RelayCommand(BrowseFsg, () => !IsRunning);
            OpenOutputCommand = new RelayCommand(() => OpenFolder(OutputDirectory), () => Directory.Exists(OutputDirectory));
            ToggleThemeCommand = new RelayCommand(ToggleTheme, () => true);
            ClearCacheCommand = new RelayCommand(ClearCache, () => !IsRunning);
            UseAllLevelsCommand = new RelayCommand(() => SetAllLevels(true), () => !IsRunning);
            UseNoLevelsCommand = new RelayCommand(() => SetAllLevels(false), () => !IsRunning);
            ShowConfigCommand = new RelayCommand(() => IsTaskView = false, () => true);
            OpenTaskOutputCommand = new RelayCommand(() => OpenFolder(taskOutputPath),
                () => !string.IsNullOrEmpty(taskOutputPath) && Directory.Exists(taskOutputPath));
            GridLevelMinusCommand = new RelayCommand(() => NudgeGridLevel(-1), () => !IsRunning);
            GridLevelPlusCommand = new RelayCommand(() => NudgeGridLevel(1), () => !IsRunning);
            BrowseOriginalsCommand = new RelayCommand(BrowseOriginals, () => !IsRunning);

            ClampZoomsForTarget();
            UpdateInheritedZooms();
            RefreshCacheSize();
            if (autoCleanCache && cacheLimitMb > 0)
            {
                long limit = (long)cacheLimitMb * 1048576L;
                _ = Task.Run(() => { try { cache.EnforceLimit(limit); } catch { } });
            }
            RefreshEstimates();
            _ = RefreshCompletedAsync();
        }

        // ---------------- data exposed to the view ----------------

        public ObservableCollection<MapSource> Sources { get; private set; }
        public ObservableCollection<LevelOption> Layers { get; private set; }
        public ObservableCollection<string> TaskLogs { get; private set; }

        public MapSource SelectedSource
        {
            get { return selectedSource; }
            set
            {
                if (selectedSource == value) return;
                selectedSource = value;
                settings.SourceId = value != null ? value.Id : "bing";
                settings.Save();
                OnPropertyChanged("SelectedSource");
                OnPropertyChanged("SourceHint");
                OnPropertyChanged("NeedsKey");
                RefreshEstimates();
                SourceChanged(this, EventArgs.Empty);
            }
        }

        public string SourceHint
        {
            get { return selectedSource != null ? selectedSource.DisplayDescription : ""; }
        }

        public bool NeedsKey
        {
            get { return selectedSource != null && selectedSource.RequiresKey; }
        }

        public string TiandituKey { get; set; }

        public string MapboxKey { get; set; }

        public bool HasSelection
        {
            get { return hasSelection; }
            private set { if (hasSelection != value) { hasSelection = value; OnPropertyChanged("HasSelection"); } }
        }

        public string SelectionText
        {
            get
            {
                if (!hasSelection) return Loc.T("尚未选择区域");
                return Loc.F("北 {0:F4}°   南 {1:F4}°\n西 {2:F4}°   东 {3:F4}°",
                    selection.North, selection.South, selection.West, selection.East);
            }
        }

        public string EstimateText { get; private set; }

        public string OutputDirectory
        {
            get { return outputDirectory; }
            set
            {
                outputDirectory = value;
                settings.OutputDirectory = value;
                settings.Save();
                OnPropertyChanged("OutputDirectory");
                _ = RefreshCompletedAsync();
            }
        }

        public string SceneryDirectory
        {
            get { return sceneryDirectory; }
            set { sceneryDirectory = value; settings.SceneryDirectory = value; settings.Save(); OnPropertyChanged("SceneryDirectory"); }
        }

        public string FsgDirectory
        {
            get { return fsgDirectory; }
            set { fsgDirectory = value; settings.FsgDirectory = value; settings.Save(); OnPropertyChanged("FsgDirectory"); }
        }

        public bool InstallIntoScenery
        {
            get { return installIntoScenery; }
            set { installIntoScenery = value; settings.InstallIntoScenery = value; settings.Save(); OnPropertyChanged("InstallIntoScenery"); }
        }

        public bool PackageTme
        {
            get { return packageTme; }
            set { packageTme = value; settings.PackageTme = value; settings.Save(); OnPropertyChanged("PackageTme"); }
        }

        public bool GenerateElevation
        {
            get { return generateElevation; }
            set { generateElevation = value; settings.GenerateElevation = value; settings.Save(); OnPropertyChanged("GenerateElevation"); }
        }

        public string SceneryName
        {
            get { return sceneryName; }
            set
            {
                sceneryName = value ?? "";
                settings.SceneryName = value;
                settings.Save();
                OnPropertyChanged("SceneryName");
            }
        }

        public string ThemeName
        {
            get { return themeName; }
        }

        public bool IsDarkTheme
        {
            get { return themeName == "Dark"; }
        }

        public string ThemeIcon
        {
            get { return IsDarkTheme ? "☀" : "☾"; }
        }

        public string ThemeTip
        {
            get { return Loc.T(IsDarkTheme ? "切换到浅色主题" : "切换到深色主题"); }
        }

        public string CacheSizeText
        {
            get { return cacheSizeText; }
            private set { cacheSizeText = value; OnPropertyChanged("CacheSizeText"); }
        }

        public string CachePathText
        {
            get { return cache.RootDirectory; }
        }

        public string CacheDirectory
        {
            get { return cacheDirectory; }
            set
            {
                string next = value ?? "";
                if (string.Equals(cacheDirectory, next, StringComparison.Ordinal)) return;
                cacheDirectory = next;
                settings.CacheDirectory = next;
                settings.Save();
                RecreateCache();
                OnPropertyChanged("CacheDirectory");
                OnPropertyChanged("CachePathText");
            }
        }

        public int CacheLimitMb
        {
            get { return cacheLimitMb; }
            set
            {
                int clamped = Math.Clamp(value, 0, 102400);
                if (cacheLimitMb == clamped) return;
                cacheLimitMb = clamped;
                settings.CacheLimitMb = clamped;
                settings.Save();
                OnPropertyChanged("CacheLimitMb");
                OnPropertyChanged("CacheLimitText");
                if (autoCleanCache) ApplyCacheLimit();
            }
        }

        public string CacheLimitText
        {
            get { return cacheLimitMb <= 0 ? Loc.T("不限制") : cacheLimitMb + " MB"; }
        }

        public bool AutoCleanCache
        {
            get { return autoCleanCache; }
            set
            {
                autoCleanCache = value;
                settings.AutoCleanCache = value;
                settings.Save();
                OnPropertyChanged("AutoCleanCache");
                if (value) ApplyCacheLimit();
            }
        }

        public bool SaveOriginalImages
        {
            get { return saveOriginalImages; }
            set
            {
                saveOriginalImages = value;
                settings.SaveOriginalImages = value;
                settings.Save();
                OnPropertyChanged("SaveOriginalImages");
            }
        }

        public string OriginalImagesDirectory
        {
            get { return originalImagesDirectory; }
            set
            {
                originalImagesDirectory = value ?? "";
                settings.OriginalImagesDirectory = originalImagesDirectory;
                settings.Save();
                OnPropertyChanged("OriginalImagesDirectory");
            }
        }

        public int GridLevel
        {
            get { return gridLevel; }
            set
            {
                int clamped = Math.Clamp(value, 9, 14);
                if (gridLevel == clamped) return;
                gridLevel = clamped;
                settings.GridLevel = clamped;
                settings.Save();
                OnPropertyChanged("GridLevel");
                OnPropertyChanged("GridLevelText");
                if (hasSelection)
                {
                    SetSelection(new GeoBounds(selection.North, selection.South, selection.East, selection.West));
                }
                else
                {
                    ClearSelection();
                }
            }
        }

        public string GridLevelText
        {
            get { return Loc.F("微调网格 L{0}", gridLevel); }
        }

        public string SelectionGridText
        {
            get
            {
                if (!hasSelection) return Loc.F("网格 L{0}", gridLevel);
                return Loc.F("网格 L{0} · {1} 格", gridLevel, selectionCells.Count);
            }
        }

        public string CompletedText
        {
            get { return Loc.F("绿色 = 已制作（{0} 格）", completedCount); }
        }

        public int SelectionVersion { get; private set; }

        public IReadOnlyCollection<SelectionCell> SelectedCells
        {
            get { return selectionCells; }
        }

        public IReadOnlyCollection<SelectionCell> CompletedCellSet
        {
            get { return completedCells; }
        }

        public List<GeoRect> SelectionCellRects()
        {
            return selectionCells.Select(c => new GeoRect(c.North, c.South, c.West, c.East)).ToList();
        }

        public List<GeoRect> CompletedCellRects()
        {
            return completedCells.Select(c => new GeoRect(c.North, c.South, c.West, c.East)).ToList();
        }

        public SceneryTarget Target
        {
            get { return target; }
            set
            {
                if (target == value) return;
                target = value;
                settings.Target = value.ToString().ToLowerInvariant();
                settings.Save();
                OnPropertyChanged("Target");
                OnPropertyChanged("TargetFs4");
                OnPropertyChanged("TargetFsg");
                OnPropertyChanged("TargetBoth");
                OnPropertyChanged("TargetHint");
                ClampZoomsForTarget();
                RefreshEstimates();
                _ = RefreshCompletedAsync();
            }
        }

        public bool TargetFs4
        {
            get { return target == SceneryTarget.Fs4; }
            set { if (value) Target = SceneryTarget.Fs4; }
        }

        public bool TargetFsg
        {
            get { return target == SceneryTarget.Fsg; }
            set { if (value) Target = SceneryTarget.Fsg; }
        }

        public bool TargetBoth
        {
            get { return target == SceneryTarget.Both; }
            set { if (value) Target = SceneryTarget.Both; }
        }

        public string TargetHint
        {
            get
            {
                switch (target)
                {
                    case SceneryTarget.Fsg:
                        return Loc.T("手机版 · ETC2 纹理 · 最高 Level 14 / 影像 Z17");
                    case SceneryTarget.Both:
                        return Loc.T("两套文件 · 共用上限 Level 14 / 影像 Z17");
                    default:
                        return Loc.T("电脑版 · DXT1 纹理 · 最高 Level 15 / 影像 Z19");
                }
            }
        }

        public int DownloadThreads
        {
            get { return downloadThreads; }
            set
            {
                int clamped = Math.Max(1, Math.Min(128, value));
                if (downloadThreads == clamped) return;
                downloadThreads = clamped;
                settings.DownloadThreads = clamped;
                settings.Save();
                OnPropertyChanged("DownloadThreads");
                OnPropertyChanged("DownloadThreadsText");
                if (!isRunning) RecreateCache();
            }
        }

        public string DownloadThreadsText
        {
            get { return Loc.F("{0} 线程", downloadThreads); }
        }

        public bool SkipExisting
        {
            get { return skipExisting; }
            set { skipExisting = value; settings.SkipExistingTiles = value; settings.Save(); OnPropertyChanged("SkipExisting"); }
        }

        public bool UseLzham
        {
            get { return useLzham; }
            set { useLzham = value; settings.UseLzham = value; settings.Save(); OnPropertyChanged("UseLzham"); }
        }

        public bool UseAutoZoom
        {
            get { return useAutoZoom; }
            set
            {
                useAutoZoom = value;
                settings.ImageZoom = value ? -1 : imageZoom;
                settings.Save();
                OnPropertyChanged("UseAutoZoom");
                OnPropertyChanged("SliderEnabled");
                OnPropertyChanged("ZoomHint");
                UpdateInheritedZooms();
                RefreshEstimates();
            }
        }

        public bool SliderEnabled
        {
            get { return !useAutoZoom; }
        }

        public int ImageZoom
        {
            get { return imageZoom; }
            set
            {
                int clamped = Math.Max(2, Math.Min(19, value));
                if (imageZoom == clamped) return;
                imageZoom = clamped;
                if (!useAutoZoom) { settings.ImageZoom = clamped; settings.Save(); }
                OnPropertyChanged("ImageZoom");
                OnPropertyChanged("ZoomHint");
                UpdateInheritedZooms();
                if (!suppressLevelSync) SyncLevelsToZoom();
                RefreshEstimates();
            }
        }

        public string ZoomHint
        {
            get
            {
                if (useAutoZoom) return Loc.T("自动（各级 = Level + 3）");
                int matched = MatchedLevel(imageZoom);
                return Loc.F("统一 Z{0} · 最高 Level {1}", imageZoom, matched);
            }
        }

        private int MatchedLevel(int zoom)
        {
            return Math.Clamp(zoom - 3, 9, target.MaxAfLevel());
        }

        /// <summary>The global detail slider drives the output-level ticks; levels from the current base up to ZL-3 stay continuous.</summary>
        private void SyncLevelsToZoom()
        {
            int finest = MatchedLevel(imageZoom);
            var selected = Layers.Where(l => l.IsSelected && l.IsEnabled).Select(l => l.Level).ToList();
            int floor = selected.Count > 0 ? Math.Min(selected.Min(), finest) : finest;

            bool changed = false;
            foreach (LevelOption layer in Layers)
            {
                bool select = layer.IsEnabled && layer.Level >= floor && layer.Level <= finest;
                if (layer.IsSelected != select)
                {
                    layer.IsSelected = select;
                    changed = true;
                }
            }
            if (changed)
            {
                PersistLevels();
                if (!IsRunning)
                {
                    int count = Layers.Count(l => l.IsSelected);
                    StatusMessage = floor == finest
                        ? Loc.F("影像 Z{0} → 已自动选中 Level {1}", imageZoom, finest)
                        : Loc.F("影像 Z{0} → 输出 Level {1}–{2}（{3} 级）", imageZoom, floor, finest, count);
                }
            }
        }

        /// <summary>Keep each level row showing the zoom it will actually use.</summary>
        private void UpdateInheritedZooms()
        {
            int max = target.MaxImageZoom();
            foreach (LevelOption layer in Layers)
            {
                int zoom = useAutoZoom ? MosaicComposer.AutoZoom(layer.Level) : imageZoom;
                layer.InheritedZoom = Math.Clamp(zoom, 2, max);
            }
        }

        public bool IsRunning
        {
            get { return isRunning; }
            private set
            {
                if (isRunning == value) return;
                isRunning = value;
                OnPropertyChanged("IsRunning");
                StartCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                ClearSelectionCommand.RaiseCanExecuteChanged();
                BrowseOutputCommand.RaiseCanExecuteChanged();
                ClearCacheCommand.RaiseCanExecuteChanged();
            }
        }

        public double Progress
        {
            get { return progress; }
            private set { progress = value; OnPropertyChanged("Progress"); }
        }

        public string StatusMessage
        {
            get { return statusMessage; }
            private set { statusMessage = value; OnPropertyChanged("StatusMessage"); }
        }

        public string DetailMessage
        {
            get { return detailMessage; }
            private set { detailMessage = value; OnPropertyChanged("DetailMessage"); }
        }

        // ---------------- task detail page ----------------

        public bool IsTaskView
        {
            get { return isTaskView; }
            private set
            {
                if (isTaskView == value) return;
                isTaskView = value;
                OnPropertyChanged("IsTaskView");
            }
        }

        public string TaskStatus
        {
            get { return Loc.T(taskStatus); }
            private set { taskStatus = value; OnPropertyChanged("TaskStatus"); }
        }

        public string TaskStage
        {
            get { return Loc.T(taskStage); }
            private set { taskStage = value; OnPropertyChanged("TaskStage"); }
        }

        public int TaskStageIndex
        {
            get { return taskStageIndex; }
            private set { taskStageIndex = value; OnPropertyChanged("TaskStageIndex"); }
        }

        public string TaskPercentText
        {
            get { return taskPercentText; }
            private set { taskPercentText = value; OnPropertyChanged("TaskPercentText"); }
        }

        public string TaskDownloadText
        {
            get { return taskDownloadText; }
            private set { taskDownloadText = value; OnPropertyChanged("TaskDownloadText"); }
        }

        public string TaskConvertText
        {
            get { return taskConvertText; }
            private set { taskConvertText = value; OnPropertyChanged("TaskConvertText"); }
        }

        public string TaskSpeedText
        {
            get { return taskSpeedText; }
            private set { taskSpeedText = value; OnPropertyChanged("TaskSpeedText"); }
        }

        public string TaskElapsedText
        {
            get { return taskElapsedText; }
            private set { taskElapsedText = value; OnPropertyChanged("TaskElapsedText"); }
        }

        public string TaskCurrentText
        {
            get { return taskCurrentText; }
            private set { taskCurrentText = value; OnPropertyChanged("TaskCurrentText"); }
        }

        public string TaskInfoText
        {
            get { return taskInfoText; }
            private set { taskInfoText = value; OnPropertyChanged("TaskInfoText"); }
        }

        public string TaskOutputPath
        {
            get { return taskOutputPath; }
            private set
            {
                taskOutputPath = value;
                OnPropertyChanged("TaskOutputPath");
                OpenTaskOutputCommand.RaiseCanExecuteChanged();
            }
        }

        public RelayCommand StartCommand { get; private set; }
        public RelayCommand CancelCommand { get; private set; }
        public RelayCommand ClearSelectionCommand { get; private set; }
        public RelayCommand BrowseOutputCommand { get; private set; }
        public RelayCommand BrowseSceneryCommand { get; private set; }
        public RelayCommand BrowseFsgCommand { get; private set; }
        public RelayCommand OpenOutputCommand { get; private set; }
        public RelayCommand ToggleThemeCommand { get; private set; }
        public RelayCommand ClearCacheCommand { get; private set; }
        public RelayCommand UseAllLevelsCommand { get; private set; }
        public RelayCommand UseNoLevelsCommand { get; private set; }
        public RelayCommand ShowConfigCommand { get; private set; }
        public RelayCommand OpenTaskOutputCommand { get; private set; }
        public RelayCommand GridLevelMinusCommand { get; private set; }
        public RelayCommand GridLevelPlusCommand { get; private set; }
        public RelayCommand BrowseOriginalsCommand { get; private set; }

        public event EventHandler SourceChanged = delegate { };
        public event EventHandler ThemeChanged = delegate { };

        /// <summary>Raw preferences for the settings dialog (image processing, paths, keys).</summary>
        public AppSettings Settings
        {
            get { return settings; }
        }

        public void SaveSettings()
        {
            settings.Save();
        }

        public string Language
        {
            get { return settings.Language; }
        }

        /// <summary>Switches language immediately, persists the choice and refreshes generated strings.</summary>
        public void SetLanguage(string language)
        {
            string next = string.Equals(language, Loc.English, StringComparison.OrdinalIgnoreCase)
                ? Loc.English
                : Loc.Chinese;
            settings.Language = next;
            settings.Save();
            Loc.Language = next;
            RefreshLocalizedText();
        }

        /// <summary>Re-evaluates every localized binding after a language change.</summary>
        private void RefreshLocalizedText()
        {
            OnPropertyChanged(string.Empty);
            RefreshSources();
            RefreshEstimates();
            OnPropertyChanged("SelectionVersion");
            UpdateTaskStatTexts();
            if (currentJob != null)
            {
                TaskInfoText = BuildTaskInfo(currentJob);
            }

            if (taskStatus == "已完成" || taskStatus == "部分完成")
            {
                TaskCurrentText = taskFailed > 0
                    ? Loc.F("{0} 个区块失败", taskFailed)
                    : Loc.T("全部区块生成成功");
            }
            else if (taskStatus == "已取消")
            {
                TaskCurrentText = Loc.T("任务已取消");
            }
            else if (taskStatus == "失败")
            {
                TaskCurrentText = StatusMessage;
            }
            else if (!IsRunning)
            {
                StatusMessage = Loc.T("在地图上拖拽框选区域，选择级别后点击“开始生成”。");
            }
        }

        /// <summary>Recreates the source list so localized names and descriptions are picked up.</summary>
        private void RefreshSources()
        {
            string selectedId = selectedSource != null ? selectedSource.Id : settings.SourceId;
            List<MapSource> items = MapSources.All.ToList();
            Sources.Clear();
            foreach (MapSource source in items)
            {
                Sources.Add(source);
            }

            selectedSource = items.FirstOrDefault(s => string.Equals(s.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                             ?? items.FirstOrDefault();
            OnPropertyChanged("SelectedSource");
            OnPropertyChanged("SourceHint");
            OnPropertyChanged("NeedsKey");
        }

        private void UpdateTaskStatTexts()
        {
            TaskDownloadText = Loc.F("{0} · {1} 张影像", FormatSize(taskDownloadedBytes), taskDownloadedTiles);
            TaskConvertText = Loc.F("{0} / {1} 瓦片 · {2}", taskDone, taskTotal, FormatSize(taskOutputBytes));
        }

        private string BuildTaskInfo(SceneryJob job)
        {
            return Loc.F(
                "区域  {0:F4}°N / {1:F4}°S / {2:F4}°W / {3:F4}°E\n图源  {4}\n级别  {5}\n目标  {6}\n输出  {7}",
                job.North, job.South, job.West, job.East,
                job.Source != null ? job.Source.DisplayName : "—",
                string.Join(" / ", job.Levels.Select(l => "L" + l)),
                job.Target.DisplayName(),
                job.OutputDirectory);
        }

        // ---------------- selection ----------------

        public void SetSelection(GeoBounds bounds)
        {
            selectionCells.Clear();
            if (bounds.IsValid)
            {
                foreach (SelectionCell cell in TileSelection.CellsForBounds(bounds.North, bounds.South,
                             bounds.East, bounds.West, gridLevel))
                {
                    selectionCells.Add(cell);
                }
            }

            selection = UnionOfCells(selectionCells);
            HasSelection = bounds.IsValid && selectionCells.Count > 0;
            OnPropertyChanged("SelectionText");
            OnPropertyChanged("SelectionGridText");
            SelectionVersion++;
            OnPropertyChanged("SelectionVersion");
            RefreshEstimates();
        }

        public void ToggleCell(double latitude, double longitude)
        {
            if (IsRunning) return;

            SelectionCell cell = TileSelection.CellAt(latitude, longitude, gridLevel);
            if (!selectionCells.Add(cell))
            {
                selectionCells.Remove(cell);
            }

            selection = UnionOfCells(selectionCells);
            HasSelection = selectionCells.Count > 0;
            OnPropertyChanged("HasSelection");
            OnPropertyChanged("SelectionText");
            OnPropertyChanged("SelectionGridText");
            SelectionVersion++;
            OnPropertyChanged("SelectionVersion");
            RefreshEstimates();
        }

        public void SetCompletedCells(HashSet<SelectionCell> cells)
        {
            completedCells.Clear();
            if (cells != null)
            {
                foreach (SelectionCell cell in cells) completedCells.Add(cell);
            }
            completedCount = completedCells.Count;
            OnPropertyChanged("CompletedText");
            OnPropertyChanged("SelectionVersion");
        }

        public void ClearSelection()
        {
            selectionCells.Clear();
            hasSelection = false;
            selection = default(GeoBounds);
            OnPropertyChanged("HasSelection");
            OnPropertyChanged("SelectionText");
            OnPropertyChanged("SelectionGridText");
            SelectionVersion++;
            OnPropertyChanged("SelectionVersion");
            RefreshEstimates();
        }

        private static GeoBounds UnionOfCells(IEnumerable<SelectionCell> cells)
        {
            bool any = false;
            double north = -90, south = 90, east = -180, west = 180;
            foreach (SelectionCell cell in cells)
            {
                any = true;
                north = Math.Max(north, cell.North);
                south = Math.Min(south, cell.South);
                east = Math.Max(east, cell.East);
                west = Math.Min(west, cell.West);
            }
            return any ? new GeoBounds(north, south, east, west) : default(GeoBounds);
        }

        private void RefreshEstimates()
        {
            if (!hasSelection)
            {
                EstimateText = Loc.T("拖拽地图框选，或单击网格微调");
                OnPropertyChanged("EstimateText");
                StartCommand.RaiseCanExecuteChanged();
                return;
            }

            int totalTiles = 0;
            double metresPerPixel = 0;
            List<int> levels = SelectedLevels();
            totalTiles = TileSelection.Count(selectionCells, levels);
            foreach (var layer in Layers.Where(l => l.IsSelected))
            {
                metresPerPixel = Math.Max(metresPerPixel, Afs2Grid.MetresPerPixel(layer.Level, selection.CenterLat));
            }

            if (totalTiles == 0)
            {
                EstimateText = Loc.T("请至少选择一个级别");
            }
            else
            {
                double megabytes = totalTiles * 0.33;
                string size = megabytes < 10.0 ? megabytes.ToString("F1") + " MB" : megabytes.ToString("F0") + " MB";
                EstimateText = Loc.F("{0} 个瓦片 · 最细 {1:F1} m/px · 约 {2}", totalTiles, metresPerPixel, size);
            }

            OnPropertyChanged("EstimateText");
            StartCommand.RaiseCanExecuteChanged();
        }

        private List<int> SelectedLevels()
        {
            var levels = Layers.Where(l => l.IsSelected).Select(l => l.Level).OrderBy(l => l).ToList();
            if (levels.Count == 0) levels.Add(13);
            return levels;
        }

        private void SetAllLevels(bool value)
        {
            foreach (var layer in Layers) layer.IsSelected = value;
            PersistLevels();
        }

        private void NudgeZoom(int level, int delta)
        {
            var option = Layers.FirstOrDefault(l => l.Level == level);
            if (option == null) return;
            int baseline = option.ImageZoom > 0 ? option.ImageZoom : MosaicComposer.AutoZoom(level);
            SetLevelZoom(level, Math.Max(8, Math.Min(19, baseline + delta)));
        }

        private void SetLevelZoom(int level, int zoom)
        {
            var option = Layers.FirstOrDefault(l => l.Level == level);
            if (option == null) return;
            option.ImageZoom = zoom;
            PersistLevels();
        }

        private void PersistLevels()
        {
            settings.Levels = Layers.Where(l => l.IsSelected).Select(l => l.Level).ToArray();
            settings.LevelZooms = Layers.Where(l => l.ImageZoom > 0)
                                        .ToDictionary(l => l.Level, l => l.ImageZoom);
            settings.Save();
            RefreshEstimates();
        }

        private void ClampZoomsForTarget()
        {
            int maxLevel = target.MaxAfLevel();
            int maxZoom = target.MaxImageZoom();
            foreach (var layer in Layers)
            {
                layer.IsEnabled = layer.Level <= maxLevel;
                if (!layer.IsEnabled && layer.IsSelected)
                {
                    layer.IsSelected = false;
                }
                if (layer.ImageZoom > maxZoom)
                {
                    layer.ImageZoom = maxZoom;
                }
            }
            PersistLevels();
            OnPropertyChanged("ZoomHint");

            int max = target.MaxImageZoom();
            bool previousSuppress = suppressLevelSync;
            suppressLevelSync = true;
            try
            {
                if (imageZoom > max) ImageZoom = max;
                foreach (var layer in Layers)
                    if (layer.ImageZoom > max) layer.ImageZoom = max;
            }
            finally
            {
                suppressLevelSync = previousSuppress;
            }
            UpdateInheritedZooms();
        }

        // ---------------- cache / overlay ----------------

        private void NudgeGridLevel(int delta)
        {
            GridLevel = gridLevel + delta;
        }

        private string CacheRoot()
        {
            return string.IsNullOrWhiteSpace(cacheDirectory)
                ? ImageryCache.DefaultCacheDirectory
                : cacheDirectory;
        }

        private void RecreateCache()
        {
            ImageryCache old = cache;
            cache = new ImageryCache(CacheRoot(), downloadThreads);
            builder = new SceneryBuilder(cache);
            try { old.Dispose(); } catch { }
            OnPropertyChanged("CachePathText");
            RefreshCacheSize();
        }

        private void ApplyCacheLimit()
        {
            if (!autoCleanCache || cacheLimitMb <= 0) return;
            try
            {
                cache.EnforceLimit((long)cacheLimitMb * 1048576L);
                RefreshCacheSize();
            }
            catch (Exception ex)
            {
                StatusMessage = "清理缓存失败：" + ex.Message;
            }
        }

        public async Task RefreshCompletedAsync()
        {
            int version = ++completedScanVersion;
            try
            {
                string output = outputDirectory;
                SceneryTarget scanTarget = target;
                HashSet<SelectionCell> cells = await Task.Run(() => CompletedScans.Scan(output, scanTarget));
                if (version != completedScanVersion) return;
                SetCompletedCells(cells);
            }
            catch
            {
                // A missing or locked output folder simply means no overlay.
            }
        }

        private void BrowseOriginals()
        {
            string chosen = PickFolder("选择原图输出目录", originalImagesDirectory);
            if (chosen != null) OriginalImagesDirectory = chosen;
        }

        // ---------------- generation ----------------

        private async Task StartAsync()
        {
            if (!HasSelection || IsRunning) return;

            MapSources.TiandituKey = TiandituKey ?? "";
            MapSources.MapboxKey = MapboxKey ?? "";

            var levelZooms = Layers.Where(l => l.ImageZoom > 0)
                                    .ToDictionary(l => l.Level, l => l.ImageZoom);

            var job = new SceneryJob
            {
                Source = selectedSource,
                North = selection.North,
                South = selection.South,
                East = selection.East,
                West = selection.West,
                Levels = SelectedLevels(),
                LevelZooms = levelZooms,
                SelectedCells = selectionCells.ToList(),
                SelectionLevel = gridLevel,
                ImageZoom = useAutoZoom ? -1 : imageZoom,
                OutputDirectory = outputDirectory,
                SaveOriginalImages = saveOriginalImages,
                OriginalImagesDirectory = originalImagesDirectory,
                Target = target,
                InstallIntoScenery = installIntoScenery,
                SceneryDirectory = sceneryDirectory,
                FsgDirectory = fsgDirectory,
                PackageTme = packageTme,
                GenerateElevation = generateElevation,
                SceneryName = AppSettings.SanitiseName(sceneryName),
                SkipExisting = skipExisting,
                DownloadThreads = downloadThreads,
                Codec = useLzham ? TtcCodec.Lzham : TtcCodec.Zlib,
                Tuning = ImageTuningOptions.FromSettings(settings)
            };

            currentJob = job;
            cancellation = new CancellationTokenSource();
            TaskLogs.Clear();
            taskDownloadedBytes = 0;
            taskDownloadedTiles = 0;
            taskOutputBytes = 0;
            taskDone = 0;
            taskTotal = 0;
            taskFailed = 0;
            TaskStatus = "准备中";
            TaskStage = "准备";
            TaskStageIndex = 0;
            TaskPercentText = "0%";
            TaskDownloadText = Loc.T("0 张 · 0 MB");
            TaskConvertText = Loc.T("0 / 0 瓦片");
            TaskSpeedText = "0 MB/s";
            TaskElapsedText = "00:00";
            TaskCurrentText = Loc.T("正在规划区块…");
            TaskOutputPath = "";
            lastSpeed = 0;
            lastSpeedBytes = 0;
            lastSpeedAt = DateTime.UtcNow;
            TaskInfoText = BuildTaskInfo(job);

            IsTaskView = true;
            IsRunning = true;
            Progress = 0;
            StatusMessage = Loc.T("开始生成…");
            DetailMessage = "";
            AddTaskLog(Loc.F("开始生成 · {0} · {1}",
                selectedSource != null ? selectedSource.DisplayName : "—",
                string.Join(",", SelectedLevels().Select(l => "L" + l))));

            var reporter = new Progress<SceneryProgress>(p =>
            {
                Progress = p.Fraction;
                StatusMessage = p.Message;
                TaskPercentText = (p.Fraction * 100).ToString("F0") + "%";
                TaskStage = string.IsNullOrEmpty(p.Stage) ? "下载并转换" : p.Stage;
                TaskStageIndex = StageIndex(p.Stage);
                TaskStatus = p.Stage == "完成" ? (p.Failed > 0 ? "部分完成" : "已完成") : "生成中";
                taskDownloadedBytes = p.DownloadedBytes;
                taskDownloadedTiles = p.DownloadedTiles;
                taskOutputBytes = p.BytesDownloaded;
                taskDone = p.Done;
                taskTotal = p.Total;
                taskFailed = p.Failed;
                UpdateTaskStatTexts();
                TaskElapsedText = TimeSpan.FromSeconds(p.ElapsedSeconds).ToString(@"mm\:ss");
                if (!string.IsNullOrEmpty(p.CurrentTile)) TaskCurrentText = p.CurrentTile;

                DateTime now = DateTime.UtcNow;
                double seconds = (now - lastSpeedAt).TotalSeconds;
                if (seconds >= 0.35)
                {
                    double instant = Math.Max(0, p.DownloadedBytes - lastSpeedBytes) / seconds;
                    lastSpeed = lastSpeed <= 0 ? instant : lastSpeed * 0.55 + instant * 0.45;
                    lastSpeedBytes = p.DownloadedBytes;
                    lastSpeedAt = now;
                    TaskSpeedText = FormatSpeed(lastSpeed);
                }

                if (!string.IsNullOrEmpty(p.Message))
                    AddTaskLog(p.Message);
            });

            try
            {
                ScenerySummary summary = await builder.RunAsync(job, reporter, cancellation.Token);
                ApplyCacheLimit();
                _ = RefreshCompletedAsync();
                Progress = 1;
                TaskPercentText = "100%";
                TaskStageIndex = 3;
                TaskStage = "完成";
                taskFailed = summary.Failed;
                TaskStatus = summary.Failed > 0 ? "部分完成" : "已完成";
                TaskCurrentText = summary.Failed > 0
                    ? Loc.F("{0} 个区块失败", summary.Failed)
                    : Loc.T("全部区块生成成功");
                StatusMessage = Loc.F("完成：{0} 个瓦片，用时 {1:F0} 秒", summary.Tiles, summary.Elapsed.TotalSeconds);
                string detail = summary.Failed > 0 ? Loc.F("{0} 个失败", summary.Failed) : Loc.T("全部成功");
                if (!string.IsNullOrEmpty(summary.TmePath)) detail += Loc.T(" · 已打包 TME");
                if (summary.ElevationTiles > 0) detail += Loc.F(" · 高程 {0} 瓦片", summary.ElevationTiles);
                DetailMessage = detail;
                TaskOutputPath = summary.Fs4ImagesDirectory ?? summary.FsgImagesDirectory ?? OutputDirectory;
                AddTaskLog(StatusMessage);

                foreach (string error in summary.Errors.Take(50)) AddTaskLog(Loc.F("失败：{0}", error));
                OnPropertyChanged("OutputDirectory");
            }
            catch (OperationCanceledException)
            {
                TaskStatus = "已取消";
                StatusMessage = Loc.T("已取消");
                TaskCurrentText = Loc.T("任务已取消");
                AddTaskLog(Loc.T("任务已取消"));
            }
            catch (Exception ex)
            {
                TaskStatus = "失败";
                StatusMessage = Loc.F("生成失败：{0}", ex.Message);
                TaskCurrentText = ex.Message;
                AddTaskLog(Loc.F("生成失败：{0}", ex.Message));
            }
            finally
            {
                IsRunning = false;
                cancellation.Dispose();
                cancellation = null;
                StartCommand.RaiseCanExecuteChanged();
            }
        }

        private static int StageIndex(string stage)
        {
            switch (stage)
            {
                case "准备": return 0;
                case "安装":
                case "打包": return 2;
                case "完成": return 3;
                default: return 1;
            }
        }

        private void AddTaskLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (TaskLogs.Count > 0 && TaskLogs[TaskLogs.Count - 1] == message) return;
            TaskLogs.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            while (TaskLogs.Count > 400) TaskLogs.RemoveAt(0);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1073741824) return (bytes / 1073741824.0).ToString("F2") + " GB";
            if (bytes >= 1048576) return (bytes / 1048576.0).ToString("F1") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("F0") + " KB";
            return bytes + " B";
        }

        private static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond >= 1048576) return (bytesPerSecond / 1048576.0).ToString("F1") + " MB/s";
            if (bytesPerSecond >= 1024) return (bytesPerSecond / 1024.0).ToString("F0") + " KB/s";
            return bytesPerSecond.ToString("F0") + " B/s";
        }

        private void Cancel()
        {
            var cts = cancellation;
            if (cts != null) cts.Cancel();
        }

        private void BrowseOutput()
        {
            string chosen = PickFolder(Loc.T("选择输出目录"), outputDirectory);
            if (chosen != null) OutputDirectory = chosen;
        }

        private void BrowseScenery()
        {
            string chosen = PickFolder(Loc.T("选择 Aerofly FS 4 的 scenery\\images 目录"), sceneryDirectory);
            if (chosen != null) SceneryDirectory = chosen;
        }

        private void BrowseFsg()
        {
            string chosen = PickFolder(Loc.T("选择手机 FSG 的 files 目录（可留空）"), fsgDirectory);
            if (chosen != null) FsgDirectory = chosen;
        }

        private static string PickFolder(string title, string initial)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = title,
                Multiselect = false
            };
            try
            {
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
                    dialog.InitialDirectory = initial;
            }
            catch { }

            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }

        private void OpenFolder(string path)
        {
            try
            {
                if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch { }
        }

        private void ToggleTheme()
        {
            SetTheme(!IsDarkTheme);
        }

        /// <summary>Applies and persists a theme immediately (also used by the settings dialog).</summary>
        public void SetTheme(bool dark)
        {
            string name = dark ? "Dark" : "Light";
            if (themeName == name) return;
            themeName = name;
            settings.Theme = name;
            settings.Save();
            OnPropertyChanged("ThemeName");
            OnPropertyChanged("IsDarkTheme");
            OnPropertyChanged("ThemeIcon");
            OnPropertyChanged("ThemeTip");
            ThemeChanged(this, EventArgs.Empty);
        }

        private void ClearCache()
        {
            try
            {
                cache.ClearDiskCache();
                cache.ClearMemory();
                RefreshCacheSize();
                StatusMessage = Loc.T("缓存已清空");
            }
            catch (Exception ex)
            {
                StatusMessage = Loc.F("清空缓存失败：{0}", ex.Message);
            }
        }

        public void RefreshCacheSize()
        {
            try
            {
                long bytes = cache.CacheSizeBytes();
                CacheSizeText = bytes >= 1073741824
                    ? string.Format("{0:F1} GB", bytes / 1073741824.0)
                    : string.Format("{0:F0} MB", bytes / 1048576.0);
            }
            catch { CacheSizeText = "—"; }
        }

        public void Shutdown()
        {
            settings.Levels = Layers.Where(l => l.IsSelected).Select(l => l.Level).ToArray();
            settings.Save();
            Cancel();
            cache.Dispose();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
