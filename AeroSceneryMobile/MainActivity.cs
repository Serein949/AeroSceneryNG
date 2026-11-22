using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using IOPath = System.IO.Path;
using AndroidX.Core.Content;
using AeroSceneryNG.Core;
using AeroSceneryNG.Ttc;

namespace AeroSceneryMobile;

[Activity(Label = "AeroScenery NG", MainLauncher = true, Theme = "@android:style/Theme.Material.NoActionBar", ScreenOrientation = Android.Content.PM.ScreenOrientation.Portrait)]
public class MainActivity : Activity
{
    private readonly Color bg = Color.ParseColor("#0B0E13");
    private readonly Color panel = Color.ParseColor("#11151C");
    private readonly Color card = Color.ParseColor("#161B24");
    private readonly Color border = Color.ParseColor("#232A36");
    private readonly Color text = Color.ParseColor("#E6E9EF");
    private readonly Color muted = Color.ParseColor("#8A94A6");
    private readonly Color accent = Color.ParseColor("#38BDF8");
    private readonly Color accentDark = Color.ParseColor("#06121C");

    private ImageryCache cache;
    private MapView map;
    private TextView selectionText;
    private TextView statusText;
    private ProgressBar progress;
    private Button selectButton;
    private Button startButton;
    private Button shareButton;
    private Button sourceButton;
    private readonly List<(int Level, CheckBox Box)> levels = new List<(int, CheckBox)>();

    private MapSource source;
    private List<MapSource> sources;
    private int sourceIndex;
    private GeoBounds bounds;
    private CancellationTokenSource cts;
    private ScenerySummary lastSummary;
    private bool running;

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window.SetStatusBarColor(bg);
        Window.SetNavigationBarColor(bg);
        Window.AddFlags(WindowManagerFlags.KeepScreenOn);

        string cacheRoot = IOPath.Combine(FilesDir.AbsolutePath, "tilecache");
        cache = new ImageryCache(cacheRoot, 6);
        sources = MapSources.All.Where(s => !s.RequiresKey).ToList();
        source = sources.FirstOrDefault(s => s.Id == "esri") ?? sources[0];
        sourceIndex = sources.IndexOf(source);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(bg);
        root.SetPadding(0, GetSystemDimension("status_bar_height"), 0, GetSystemDimension("navigation_bar_height"));
        SetContentView(root);

        root.AddView(BuildHeader(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(58)));

        var mapFrame = new FrameLayout(this);
        map = new MapView(this, cache) { Source = source };
        map.SelectionChanged += OnSelectionChanged;
        map.SetView(39.9042, 116.4074, 10);
        mapFrame.AddView(map, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var hint = MakeText("拖拽平移 · 双指缩放 · 选择“框选”后拖出区域", 11, muted);
        hint.Background = (Round(Color.ParseColor("#CC11151C"), 18));
        hint.SetPadding(Dp(12), Dp(7), Dp(12), Dp(7));
        var hintParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(12), TopMargin = Dp(12) };
        mapFrame.AddView(hint, hintParams);

        var zoomBar = new LinearLayout(this) { Orientation = Orientation.Vertical };
        zoomBar.AddView(MakeSmallButton("+", () => map.ZoomBy(1)));
        zoomBar.AddView(MakeSmallButton("−", () => map.ZoomBy(-1)));
        var zoomParams = new FrameLayout.LayoutParams(Dp(44), ViewGroup.LayoutParams.WrapContent) { Gravity = GravityFlags.Right | GravityFlags.Bottom, RightMargin = Dp(12), BottomMargin = Dp(58) };
        mapFrame.AddView(zoomBar, zoomParams);

        root.AddView(mapFrame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        root.AddView(BuildPanel(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        UpdateSelectionText();
        UpdateLevelsEnabled();
    }

    private View BuildHeader()
    {
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(Dp(16), 0, Dp(16), 0);
        header.SetBackgroundColor(bg);

        var logo = MakeText("A", 18, accentDark);
        logo.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        logo.Gravity = GravityFlags.Center;
        logo.Background = (Round(accent, 10));
        header.AddView(logo, new LinearLayout.LayoutParams(Dp(34), Dp(34)));

        var title = MakeText("AeroScenery NG", 17, text);
        title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        title.SetPadding(Dp(10), 0, 0, 0);
        header.AddView(title);

        var version = MakeText("0.5", 10, muted);
        version.Background = (Round(card, 8));
        version.SetPadding(Dp(6), Dp(2), Dp(6), Dp(2));
        var vp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(8) };
        header.AddView(version, vp);

        var spacer = new Space(this);
        header.AddView(spacer, new LinearLayout.LayoutParams(0, 1, 1f));
        var tag = MakeText("FSG · ETC2 · NEON", 10, muted);
        header.AddView(tag);
        return header;
    }

    private View BuildPanel()
    {
        var panelLayout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panelLayout.SetPadding(Dp(14), Dp(12), Dp(14), Dp(12));
        panelLayout.Background = (Round(panel, 20));

        var row1 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row1.SetGravity(GravityFlags.CenterVertical);
        sourceButton = MakeButton(source.Name, false);
        sourceButton.Click += (s, e) => CycleSource();
        row1.AddView(sourceButton, new LinearLayout.LayoutParams(0, Dp(42), 1f));
        var target = MakeText("手机 FSG · 最高 L14", 11, muted);
        target.Gravity = GravityFlags.CenterVertical | GravityFlags.Right;
        var tp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(42)) { LeftMargin = Dp(10) };
        row1.AddView(target, tp);
        panelLayout.AddView(row1);

        var levelScroll = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false };
        var levelRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        levelRow.SetPadding(0, Dp(8), 0, Dp(6));
        for (int level = 9; level <= 14; level++)
        {
            int captured = level;
            var box = new CheckBox(this) { Text = "L" + level, TextSize = 12 };
            box.SetTextColor(text);
            box.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(accent);
            box.Checked = level >= 12 && level <= 14;
            box.CheckedChange += (s, e) => UpdateSelectionText();
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(38)) { RightMargin = Dp(4) };
            levelRow.AddView(box, lp);
            levels.Add((level, box));
        }
        levelScroll.AddView(levelRow);
        panelLayout.AddView(levelScroll);

        var selRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        selRow.SetGravity(GravityFlags.CenterVertical);
        selectionText = MakeText("尚未选择区域", 11, muted);
        selectionText.Gravity = GravityFlags.CenterVertical;
        selRow.AddView(selectionText, new LinearLayout.LayoutParams(0, Dp(52), 1f));

        selectButton = MakeButton("框选", false);
        selectButton.Click += (s, e) => ToggleSelect();
        selRow.AddView(selectButton, new LinearLayout.LayoutParams(Dp(62), Dp(38)));

        var clear = MakeButton("清除", false);
        clear.Click += (s, e) =>
        {
            bounds = default;
            map.ClearSelection();
            UpdateSelectionText();
        };
        var cp = new LinearLayout.LayoutParams(Dp(62), Dp(38)) { LeftMargin = Dp(6) };
        selRow.AddView(clear, cp);
        panelLayout.AddView(selRow);

        progress = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000, Progress = 0 };
        progress.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(accent);
        panelLayout.AddView(progress, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(4)));

        statusText = MakeText("选择一个区域，勾选输出级别，然后开始生成。", 11, muted);
        statusText.SetPadding(0, Dp(5), 0, Dp(8));
        panelLayout.AddView(statusText);

        var actionRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        startButton = MakeButton("开始生成", true);
        startButton.Enabled = false;
        startButton.Click += async (s, e) => await OnStartOrCancelAsync();
        actionRow.AddView(startButton, new LinearLayout.LayoutParams(0, Dp(46), 1f));

        shareButton = MakeButton("分享 TME", false);
        shareButton.Visibility = ViewStates.Gone;
        shareButton.Click += (s, e) => ShareTme();
        var sp = new LinearLayout.LayoutParams(Dp(110), Dp(46)) { LeftMargin = Dp(8) };
        actionRow.AddView(shareButton, sp);
        panelLayout.AddView(actionRow);

        return panelLayout;
    }

    private void CycleSource()
    {
        if (running) return;
        sourceIndex = (sourceIndex + 1) % sources.Count;
        source = sources[sourceIndex];
        sourceButton.Text = source.Name;
        map.Source = source;
        statusText.Text = "图源：" + source.Name + " · " + source.Description;
    }

    private void ToggleSelect()
    {
        bool next = selectButton.Text != "取消框选";
        map.SetSelectMode(next);
        selectButton.Text = next ? "取消框选" : "框选";
        selectButton.SetTextColor(next ? accent : text);
    }

    private void OnSelectionChanged(GeoBounds b)
    {
        bounds = b;
        RunOnUiThread(() =>
        {
            UpdateSelectionText();
            if (selectButton.Text == "取消框选")
            {
                selectButton.Text = "框选";
                map.SetSelectMode(false);
            }
        });
    }

    private void UpdateSelectionText()
    {
        if (!bounds.IsValid)
        {
            selectionText.Text = "尚未选择区域";
            startButton.Enabled = false;
            return;
        }

        int count = 0;
        foreach (var (level, box) in levels)
        {
            if (box.Checked)
                count += Afs2Grid.TilesForBounds(bounds.North, bounds.South, bounds.East, bounds.West, level).Count;
        }

        selectionText.Text = string.Format("北 {0:F4}°  南 {1:F4}°\n西 {2:F4}°  东 {3:F4}°\n{4} 个 AFS 瓦片 · 约 {5:F1} MB",
            bounds.North, bounds.South, bounds.West, bounds.East, count, count * 0.33);
        startButton.Enabled = running || count > 0;
    }

    private void UpdateLevelsEnabled()
    {
        foreach (var (level, box) in levels)
        {
            box.Enabled = level <= 14;
            if (level > 14) box.Checked = false;
        }
    }

    private async Task OnStartOrCancelAsync()
    {
        if (running)
        {
            cts?.Cancel();
            statusText.Text = "正在取消…";
            return;
        }
        await StartGenerationAsync();
    }

    private async Task StartGenerationAsync()
    {
        if (running || !bounds.IsValid) return;
        var selectedLevels = levels.Where(x => x.Box.Checked).Select(x => x.Level).ToList();
        if (selectedLevels.Count == 0)
        {
            statusText.Text = "至少勾选一个输出级别。";
            return;
        }

        string root = IOPath.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath, "AeroScenery");
        EtcCodec.FastMode = true;
        var job = new SceneryJob
        {
            Source = source,
            North = bounds.North,
            South = bounds.South,
            East = bounds.East,
            West = bounds.West,
            Levels = selectedLevels,
            Target = SceneryTarget.Fsg,
            OutputDirectory = root,
            SceneryName = "MobileScenery",
            PackageTme = true,
            SkipExisting = true,
            DownloadThreads = 32,
            WorkerThreads = 4,
            Codec = TtcCodec.Zlib
        };

        running = true;
        startButton.Text = "取消任务";
        startButton.Enabled = true;
        shareButton.Visibility = ViewStates.Gone;
        progress.Progress = 0;
        statusText.Text = "准备中…";
        cts = new CancellationTokenSource();

        var reporter = new Progress<SceneryProgress>(p =>
        {
            RunOnUiThread(() =>
            {
                progress.Progress = (int)Math.Round(Math.Clamp(p.Fraction, 0, 1) * 1000);
                statusText.Text = string.Format("{0}  {1}%  {2}/{3}  {4}",
                    p.Stage ?? "", (int)Math.Round(p.Fraction * 100), p.Done, p.Total, p.Message ?? "");
            });
        });

        try
        {
            var builder = new SceneryBuilder(cache);
            lastSummary = await builder.RunAsync(job, reporter, cts.Token);
            progress.Progress = 1000;
            if (lastSummary.Bytes <= 0)
                statusText.Text = string.Format("完成：{0} 个瓦片均已存在，已跳过；用时 {1:F0} 秒", lastSummary.Tiles, lastSummary.Elapsed.TotalSeconds);
            else
                statusText.Text = string.Format("完成：{0} 个瓦片，{1:F1} MB，用时 {2:F0} 秒", lastSummary.Tiles, lastSummary.Bytes / 1048576.0, lastSummary.Elapsed.TotalSeconds);
            shareButton.Visibility = string.IsNullOrEmpty(lastSummary.TmePath) ? ViewStates.Gone : ViewStates.Visible;
        }
        catch (System.OperationCanceledException)
        {
            statusText.Text = "已取消。已下载的瓦片会保留，下次继续。";
        }
        catch (Exception ex)
        {
            statusText.Text = "失败：" + ex.GetType().Name + " " + ex.Message;
        }
        finally
        {
            running = false;
            startButton.Text = "开始生成";
            startButton.Enabled = bounds.IsValid;
            cts?.Dispose();
            cts = null;
        }
    }

    private void ShareTme()
    {
        string tme = lastSummary?.TmePath;
        if (string.IsNullOrEmpty(tme) || !File.Exists(tme))
        {
            string root = IOPath.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir.AbsolutePath, "AeroScenery");
            if (Directory.Exists(root))
            {
                tme = Directory.GetFiles(root, "*_images.tme", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }
        if (string.IsNullOrEmpty(tme) || !File.Exists(tme))
        {
            statusText.Text = "没有可分享的 TME，请先生成一次。";
            return;
        }
        try
        {
            var file = new Java.IO.File(tme);
            var uri = FileProvider.GetUriForFile(this, PackageName + ".fileprovider", file);
            var intent = new Intent(Intent.ActionSend);
            intent.SetType("application/zip");
            intent.PutExtra(Intent.ExtraStream, uri);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            StartActivity(Intent.CreateChooser(intent, "分享 FSG 地景包"));
        }
        catch (Exception ex)
        {
            statusText.Text = "分享失败：" + ex.Message;
        }
    }

    protected override void OnDestroy()
    {
        cts?.Cancel();
        cache?.Dispose();
        base.OnDestroy();
    }

    private int Dp(int value) => (int)Math.Round(value * Resources.DisplayMetrics.Density);

    private int GetSystemDimension(string name)
    {
        int id = Resources.GetIdentifier(name, "dimen", "android");
        return id > 0 ? Resources.GetDimensionPixelSize(id) : 0;
    }

    private TextView MakeText(string value, float size, Color color)
    {
        var view = new TextView(this) { Text = value };
        view.SetTextColor(color);
        view.SetTextSize(Android.Util.ComplexUnitType.Sp, size);
        return view;
    }

    private Button MakeButton(string value, bool primary)
    {
        var button = new Button(this) { Text = value,  };
        button.SetTextSize(Android.Util.ComplexUnitType.Sp, 13);
        button.SetTextColor(primary ? accentDark : text);
        button.Background = (Round(primary ? accent : card, 12));
        button.StateListAnimator = null;
        button.SetPadding(Dp(12), 0, Dp(12), 0);
        return button;
    }

    private Button MakeSmallButton(string value, Action action)
    {
        var button = new Button(this) { Text = value,  };
        button.SetTextSize(Android.Util.ComplexUnitType.Sp, 18);
        button.SetTextColor(text);
        button.Background = (Round(Color.ParseColor("#DD11151C"), 12));
        button.StateListAnimator = null;
        button.Click += (s, e) => action();
        button.LayoutParameters = new LinearLayout.LayoutParams(Dp(44), Dp(44));
        return button;
    }

    private GradientDrawable Round(Color color, int radiusDp)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(color);
        drawable.SetCornerRadius(Dp(radiusDp));
        drawable.SetStroke(Dp(1), border);
        return drawable;
    }
}
