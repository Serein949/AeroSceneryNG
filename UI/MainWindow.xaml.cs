using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AeroSceneryNG.Core;

namespace AeroSceneryNG.UI
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel viewModel;
        private MapCanvas map;

        public MainWindow()
        {
            InitializeComponent();

            // Never open taller or wider than the desktop: the sidebar footer holds the primary
            // action, and a window that ran past the screen edge used to hide it completely.
            Rect workArea = SystemParameters.WorkArea;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = workArea.Left;
            Top = workArea.Top;
            Width = Math.Max(MinWidth, workArea.Width);
            Height = Math.Max(MinHeight, workArea.Height);

            viewModel = new MainViewModel();
            DataContext = viewModel;
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            viewModel.TaskLogs.CollectionChanged += (s, e) =>
            {
                if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add
                    || viewModel.TaskLogs.Count == 0
                    || TaskLogList == null)
                {
                    return;
                }
                string latest = viewModel.TaskLogs[viewModel.TaskLogs.Count - 1];
                Dispatcher.BeginInvoke(new Action(() => TaskLogList.ScrollIntoView(latest)));
            };
            App.ApplyTheme(viewModel.IsDarkTheme);
            viewModel.ThemeChanged += (s, e) =>
            {
                App.ApplyTheme(viewModel.IsDarkTheme);
                ApplyMapSurface();
            };

            Loaded += MainWindow_Loaded;
            Closed += (s, e) => viewModel.Shutdown();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            AttachMap();
        }

        /// <summary>Creates the map control and wires it to the view model. Called once the window is up.</summary>
        private void AttachMap()
        {
            map = new MapCanvas
            {
                MinZoom = 2,
                MaxZoom = 20,
                EnableSelection = true,
                SurfaceBrush = TryFindResource("BgBrush") as Brush
            };
            map.MouseGeoPositionChanged += Map_MouseGeoPositionChanged;
            map.SelectionChanged += Map_SelectionChanged;
            map.SelectionCommitted += Map_SelectionCommitted;
            map.GeoClicked += Map_GeoClicked;
            map.ViewChanged += Map_ViewChanged;
            map.GridLevel = viewModel.GridLevel;
            MapHost.Content = map;
            SyncMapSelection();

            viewModel.SourceChanged += (s, e) => ApplySource();
            ApplySource();

            // Start over Beijing so the first thing the user sees is imagery their network can reach.
            map.SetView(39.9042, 116.4074, 11);
            UpdateZoomLabel();
        }

        /// <summary>Keeps the map backdrop in step with the current light or dark palette.</summary>
        private void ApplyMapSurface()
        {
            if (map == null)
            {
                return;
            }

            map.SurfaceBrush = TryFindResource("BgBrush") as Brush;
            map.InvalidateVisual();
        }

        /// <summary>Hands the currently selected imagery source to the map.</summary>
        private void ApplySource()
        {
            MapSource source = viewModel.SelectedSource;
            if (source == null || map == null)
            {
                return;
            }

            MapSources.TiandituKey = viewModel.TiandituKey;
            MapSources.MapboxKey = viewModel.MapboxKey;
            map.MinZoom = Math.Max(2, source.MinZoom);
            map.MaxZoom = source.MaxZoom;
            map.SetTileSource(
                source.Id,
                source.BuildUrl,
                source.UserAgent,
                source.Referer,
                source.FileExtension);

            double zoom = Math.Min(map.ZoomLevel, source.MaxZoom);
            map.SetView(map.CenterLat, map.CenterLon, zoom);
        }

        private void Map_MouseGeoPositionChanged(object sender, EventArgs e)
        {
            if (map == null)
            {
                return;
            }

            CursorLabel.Text = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:0.0000}°  {1:0.0000}°",
                map.CursorLat,
                map.CursorLon);
        }

        private void Map_ViewChanged(object sender, EventArgs e)
        {
            UpdateZoomLabel();
        }

        private void UpdateZoomLabel()
        {
            if (map != null)
                ZoomLabel.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, "Z{0:0.0}", map.ZoomLevel);
        }

        private void Map_SelectionChanged(object sender, EventArgs e)
        {
            if (map == null)
            {
                return;
            }

            GeoRect selection = map.Selection;
            if (selection.IsEmpty)
            {
                viewModel.ClearSelection();
            }
            else
            {
                viewModel.SetSelection(new GeoBounds(selection.North, selection.South, selection.East, selection.West));
            }
        }

        private void Map_SelectionCommitted(object sender, EventArgs e)
        {
            Map_SelectionChanged(sender, e);
            SyncMapSelection();
        }

        private void Map_GeoClicked(double lat, double lon)
        {
            viewModel.ToggleCell(lat, lon);
            SyncMapSelection();
        }

        /// <summary>Pushes the view model's AFS cell sets into the map overlay.</summary>
        private void SyncMapSelection()
        {
            if (map == null)
            {
                return;
            }

            map.GridLevel = viewModel.GridLevel;
            map.SetSelectionCells(viewModel.SelectionCellRects());
            map.SetCompletedCells(viewModel.CompletedCellRects());
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsTaskView):
                    AnimatePage(viewModel.IsTaskView);
                    break;
                case nameof(MainViewModel.SelectionVersion):
                case nameof(MainViewModel.GridLevel):
                    SyncMapSelection();
                    break;
            }
        }

        /// <summary>Short, transform/opacity-only transition between the config and task pages.</summary>
        private void AnimatePage(bool showTask)
        {
            if (showTask)
            {
                TaskView.Visibility = Visibility.Visible;
                TaskView.Opacity = 1;
                TaskView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
                TaskTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
            }
            else
            {
                ConfigView.Visibility = Visibility.Visible;
                ConfigView.Opacity = 1;
                ConfigView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
                ConfigTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            if (map == null) return;
            map.AnimateTo(map.CenterLat, map.CenterLon, Math.Min(map.MaxZoom, map.ZoomLevel + 1), 180);
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            if (map == null) return;
            map.AnimateTo(map.CenterLat, map.CenterLon, Math.Max(map.MinZoom, map.ZoomLevel - 1), 180);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximise();
                return;
            }
            try { DragMove(); } catch { }
        }

        private void ToggleMaximise()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Minimise_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Maximise_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximise();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsWindow(viewModel)
            {
                Owner = this
            };
            dialog.ShowDialog();
            ApplySource();
            viewModel.RefreshCacheSize();
            SyncMapSelection();
            _ = viewModel.RefreshCompletedAsync();
        }
    }
}
