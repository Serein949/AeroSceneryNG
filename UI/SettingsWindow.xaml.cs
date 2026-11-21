using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AeroSceneryNG.Core;

namespace AeroSceneryNG.UI
{
    public partial class SettingsWindow : Window
    {
        private bool loading;

        /// <summary>Parameterless constructor used by the XAML designer only.</summary>
        public SettingsWindow()
        {
            InitializeComponent();
            Rect workArea = SystemParameters.WorkArea;
            MaxHeight = Math.Max(520, workArea.Height - 24);
            MaxWidth = Math.Max(640, workArea.Width - 24);
            Height = Math.Min(820, MaxHeight);
            Loaded += (s, e) => Populate(null);
            Loaded += SettingsWindow_Loaded;
        }

        private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Window does not accept a RenderTransform; animate opacity only.
            Opacity = 1;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
        }

        public SettingsWindow(MainViewModel viewModel) : this()
        {
            if (viewModel != null)
            {
                DataContext = viewModel;
                Populate(viewModel);
            }
        }

        private MainViewModel ViewModel => DataContext as MainViewModel;

        private void Populate(MainViewModel viewModel)
        {
            if (viewModel == null)
            {
                viewModel = ViewModel;
            }

            if (viewModel == null)
            {
                CacheSizeText.Text = "—";
                return;
            }

            loading = true;
            try
            {
                TiandituBox.Text = viewModel.TiandituKey;
                MapboxBox.Text = viewModel.MapboxKey;
                SceneryBox.Text = viewModel.SceneryDirectory;
                FsgBox.Text = viewModel.FsgDirectory;

                AppSettings s = viewModel.Settings;
                CacheDirBox.Text = s.CacheDirectory ?? "";
                CacheLimitSlider.Value = s.CacheLimitMb;
                AutoCleanCheck.IsChecked = s.AutoCleanCache;
                SaveOriginalCheck.IsChecked = s.SaveOriginalImages;
                OriginalPanel.IsEnabled = s.SaveOriginalImages;
                OriginalBox.Text = s.OriginalImagesDirectory ?? "";
                DarkThemeRadio.IsChecked = s.IsDarkTheme;
                LightThemeRadio.IsChecked = !s.IsDarkTheme;
                ChineseRadio.IsChecked = !string.Equals(s.Language, Loc.English, StringComparison.OrdinalIgnoreCase);
                EnglishRadio.IsChecked = string.Equals(s.Language, Loc.English, StringComparison.OrdinalIgnoreCase);

                ProcessingCheck.IsChecked = s.EnableImageProcessing;
                ProcessingPanel.IsEnabled = s.EnableImageProcessing;
                BrightnessSlider.Value = s.BrightnessAdjustment;
                ContrastSlider.Value = s.ContrastAdjustment;
                SaturationSlider.Value = s.SaturationAdjustment;
                SharpnessSlider.Value = s.SharpnessAdjustment;
                RedSlider.Value = s.RedAdjustment;
                GreenSlider.Value = s.GreenAdjustment;
                BlueSlider.Value = s.BlueAdjustment;

                WaterCheck.IsChecked = s.WaterTintEnabled;
                WaterPanel.IsEnabled = s.WaterTintEnabled;
                WaterColorBox.Text = string.IsNullOrWhiteSpace(s.WaterTintColor) ? "#1B4F8A" : s.WaterTintColor;
                WaterStrengthSlider.Value = s.WaterTintStrength;

                UpdateTuningLabels();
            }
            finally
            {
                loading = false;
            }

            RefreshCacheSize();
        }

        private void UpdateTuningLabels()
        {
            BrightnessValue.Text = Signed(BrightnessSlider.Value);
            ContrastValue.Text = Signed(ContrastSlider.Value);
            SaturationValue.Text = Signed(SaturationSlider.Value);
            SharpnessValue.Text = SharpnessSlider.Value.ToString("F0");
            RgbValue.Text = Signed(RedSlider.Value) + " / " + Signed(GreenSlider.Value) + " / " + Signed(BlueSlider.Value);
            WaterStrengthValue.Text = WaterStrengthSlider.Value.ToString("F0") + "%";
            if (CacheLimitValue != null)
            {
                CacheLimitValue.Text = CacheLimitSlider.Value <= 0
                    ? Loc.T("不限制")
                    : CacheLimitSlider.Value.ToString("F0") + " MB";
            }
        }

        private static string Signed(double value)
        {
            return (value > 0 ? "+" : "") + value.ToString("F0");
        }

        private void RefreshCacheSize()
        {
            try
            {
                string root = (CacheDirBox.Text ?? "").Trim();
                if (root.Length == 0) root = ImageryCache.DefaultCacheDirectory;
                using (var probe = new ImageryCache(root))
                {
                    long bytes = probe.CacheSizeBytes();
                    CacheSizeText.Text = bytes >= 1073741824
                        ? string.Format("{0:F2} GB", bytes / 1073741824.0)
                        : string.Format("{0:F1} MB", bytes / 1048576.0);
                }
            }
            catch
            {
                CacheSizeText.Text = "—";
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void Processing_Checked(object sender, RoutedEventArgs e)
        {
            if (ProcessingPanel != null && ProcessingCheck != null)
                ProcessingPanel.IsEnabled = ProcessingCheck.IsChecked == true;
        }

        private void Water_Checked(object sender, RoutedEventArgs e)
        {
            if (WaterPanel != null && WaterCheck != null)
                WaterPanel.IsEnabled = WaterCheck.IsChecked == true;
        }

        private void Original_Checked(object sender, RoutedEventArgs e)
        {
            if (OriginalPanel != null && SaveOriginalCheck != null)
                OriginalPanel.IsEnabled = SaveOriginalCheck.IsChecked == true;
        }

        private void CacheLimit_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!loading) UpdateTuningLabels();
        }

        private void Language_Checked(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }

            bool english = EnglishRadio != null && EnglishRadio.IsChecked == true;
            ViewModel?.SetLanguage(english ? Loc.English : Loc.Chinese);
            UpdateTuningLabels();
        }

        private void BrowseCache_Click(object sender, RoutedEventArgs e)
        {
            string chosen = PickFolder(Loc.T("选择瓦片缓存目录"), CacheDirBox.Text);
            if (chosen != null)
            {
                CacheDirBox.Text = chosen;
                RefreshCacheSize();
            }
        }

        private void BrowseOriginal_Click(object sender, RoutedEventArgs e)
        {
            string chosen = PickFolder(Loc.T("选择原图输出目录"), OriginalBox.Text);
            if (chosen != null) OriginalBox.Text = chosen;
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
                if (!string.IsNullOrWhiteSpace(initial) && System.IO.Directory.Exists(initial))
                    dialog.InitialDirectory = initial;
            }
            catch { }
            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }

        private void Tuning_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!loading) UpdateTuningLabels();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            MainViewModel vm = ViewModel;
            if (vm == null)
            {
                Close();
                return;
            }

            vm.TiandituKey = TiandituBox.Text.Trim();
            vm.MapboxKey = MapboxBox.Text.Trim();
            MapSources.TiandituKey = vm.TiandituKey;
            MapSources.MapboxKey = vm.MapboxKey;
            if (!string.IsNullOrWhiteSpace(SceneryBox.Text)) vm.SceneryDirectory = SceneryBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(FsgBox.Text)) vm.FsgDirectory = FsgBox.Text.Trim();

            vm.CacheDirectory = (CacheDirBox.Text ?? "").Trim();
            vm.CacheLimitMb = (int)Math.Round(CacheLimitSlider.Value);
            vm.AutoCleanCache = AutoCleanCheck.IsChecked == true;
            vm.SaveOriginalImages = SaveOriginalCheck.IsChecked == true;
            vm.OriginalImagesDirectory = (OriginalBox.Text ?? "").Trim();

            AppSettings s = vm.Settings;
            s.EnableImageProcessing = ProcessingCheck.IsChecked == true;
            s.BrightnessAdjustment = (int)Math.Round(BrightnessSlider.Value);
            s.ContrastAdjustment = (int)Math.Round(ContrastSlider.Value);
            s.SaturationAdjustment = (int)Math.Round(SaturationSlider.Value);
            s.SharpnessAdjustment = (int)Math.Round(SharpnessSlider.Value);
            s.RedAdjustment = (int)Math.Round(RedSlider.Value);
            s.GreenAdjustment = (int)Math.Round(GreenSlider.Value);
            s.BlueAdjustment = (int)Math.Round(BlueSlider.Value);

            s.WaterTintEnabled = WaterCheck.IsChecked == true;
            s.WaterTintStrength = (int)Math.Round(WaterStrengthSlider.Value);
            string color = (WaterColorBox.Text ?? "").Trim();
            s.WaterTintColor = color.Length == 0 ? "#1B4F8A" : color;

            bool dark = DarkThemeRadio.IsChecked == true;
            vm.SetTheme(dark);
            vm.SaveSettings();

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string root = (CacheDirBox.Text ?? "").Trim();
                if (root.Length == 0) root = ImageryCache.DefaultCacheDirectory;
                using (var probe = new ImageryCache(root))
                {
                    probe.ClearDiskCache();
                    probe.ClearMemory();
                }
                CacheSizeText.Text = "0.0 MB";
            }
            catch
            {
                // Cache cleanup is best-effort; the next run re-downloads whatever is missing.
            }
        }
    }
}
