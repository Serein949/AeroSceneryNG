using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AeroSceneryNG.Core;

namespace AeroSceneryNG.UI
{
    /// <summary>
    /// A self-contained, asynchronous Web-Mercator (EPSG:3857) tile map control for WPF.
    /// <para>
    /// The control renders 256px XYZ tiles, supports fractional zoom, 1:1 panning with
    /// inertia, cursor-anchored smooth zooming, a geo-anchored rubber-band selection and
    /// an async disk-cached tile pipeline. It is a pure <see cref="FrameworkElement"/>:
    /// no XAML, styles or templates are required.
    /// </para>
    /// </summary>
    public class MapCanvas : FrameworkElement
    {
        #region Constants

        /// <summary>Maximum latitude representable by the Web-Mercator projection (degrees).</summary>
        public const double MaxMercatorLatitude = 85.05112878;

        private const int TileSize = 256;
        private const int MaxConcurrentTileRequests = 64;
        private const int MaxCachedTiles = 512;

        /// <summary>
        /// How many zoom levels below the sharp level the full-screen backdrop is taken from. Using a
        /// single shared level for the backdrop means every cell is magnified by the same factor
        /// while the sharp level streams in, instead of each block appearing at its own scale.
        /// </summary>
        private const int BackdropLevelOffset = 3;

        private const double TileFadeMilliseconds = 120.0;
        private const double ZoomAnimationMilliseconds = 180.0;
        private const double PanInertiaMilliseconds = 250.0;
        private const int TileRequestRetries = 2;
        private const int TileRequestTimeoutSeconds = 8;
        private const double EventThrottleMilliseconds = 33.0;
        private const double MinimumPanInertiaPixels = 2.0;
        private const double MaximumPanInertiaPixels = 1600.0;
        private const string DefaultUserAgent = "AeroSceneryNG/1.0 (WPF; MapCanvas)";
        private const string DefaultFileExtension = "jpg";

        #endregion

        #region Static resources

        private static readonly Brush BackgroundBrush = CreateFrozenBrush(0xFF0E1116);
        private static readonly Brush PlaceholderBrush = CreateFrozenBrush(0xFF151A21);
        private static readonly Brush SelectionFillBrush = CreateFrozenBrush(0x2238BDF8);
        private static readonly Brush SelectionLabelBackgroundBrush = CreateFrozenBrush(0xD9101820);
        private static readonly Brush SelectionLabelForegroundBrush = CreateFrozenBrush(0xFFEAF6FF);
        private static readonly Brush StatusBackgroundBrush = CreateFrozenBrush(0xC80E1116);
        private static readonly Brush StatusForegroundBrush = CreateFrozenBrush(0xFFD5DEE8);
        private static readonly Pen SelectionBorderPen = CreateFrozenPen(0xFF38BDF8, 1.5);
        private static readonly Pen GridPen = CreateFrozenPen(0x30FFFFFF, 0.75);
        private static readonly Brush CompletedFillBrush = CreateFrozenBrush(0x1422C55E);
        private static readonly Pen CompletedBorderPen = CreateFrozenPen(0x9A22C55E, 1.0);

        private static readonly Typeface UiTypeface = new Typeface(
            new FontFamily("Segoe UI"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        #endregion

        #region Fields

        // ------- view state -------
        private double _centerLat;
        private double _centerLon;
        private double _zoom = 2.0;
        private double _minZoom = 2.0;
        private double _maxZoom = 18.0;

        // The tile level that is actually drawn. It deliberately lags behind _zoom while the view is
        // moving so the whole screen scales by one uniform factor instead of every cell picking its
        // own level and growing at a different rate.
        private int _displayLevel = -1;
        private double _lastViewMotionMs;
        private double _framePumpUntilMs;

        // ------- tile source -------
        private string _cacheKey;
        private Func<int, int, int, string> _urlBuilder;
        private string _userAgent = DefaultUserAgent;
        private string _referer;
        private string _fileExtension = DefaultFileExtension;
        private int _generation;
        private CancellationTokenSource _loadCancellation = new CancellationTokenSource();

        // ------- overlays -------
        private GeoRect _selection = GeoRect.Empty;
        private readonly List<GeoRect> _selectionCells = new List<GeoRect>();
        private readonly List<GeoRect> _completedCells = new List<GeoRect>();
        private bool _enableSelection = true;
        private int _gridLevel = 12;
        private bool _showGrid = true;
        private string _statusText;

        // ------- cursor -------
        private double _cursorLat;
        private double _cursorLon;
        private bool _cursorValid;

        // ------- dpi -------
        private DpiScale _dpi = new DpiScale(1.0, 1.0);
        private double _pixelsPerDip = 1.0;

        // ------- tile cache (UI thread only) -------
        private readonly Dictionary<TileKey, Tile> _tiles = new Dictionary<TileKey, Tile>(MaxCachedTiles);
        private readonly HashSet<TileKey> _visibleKeys = new HashSet<TileKey>();
        private readonly List<Tile> _visibleTiles = new List<Tile>(256);
        private readonly Queue<TileKey> _pendingRequests = new Queue<TileKey>();
        private readonly HashSet<TileKey> _pendingRequestSet = new HashSet<TileKey>();
        private readonly HashSet<TileKey> _inFlightRequests = new HashSet<TileKey>();
        private long _renderFrame;
        private bool _anyTileFading;

        // ------- interaction -------
        private DragMode _dragMode;
        private Point _dragStartPoint;
        private Point _leftDownPoint;
        private bool _leftDragMoved;
        private Point _dragLastPoint;
        private double _dragLastSampleMs;
        private double _panBaseWorldX;
        private double _panBaseWorldY;
        private double _velocityX;
        private double _velocityY;
        private double _selectionAnchorLat;
        private double _selectionAnchorLon;

        // ------- animations -------
        private bool _viewAnimActive;
        private bool _viewAnimAnchored;
        private double _viewAnimStartMs;
        private double _viewAnimDurationMs;
        private double _viewAnimFromZoom;
        private double _viewAnimFromLat;
        private double _viewAnimFromLon;
        private double _viewAnimToZoom;
        private double _viewAnimToLat;
        private double _viewAnimToLon;
        private double _viewAnimAnchorLat;
        private double _viewAnimAnchorLon;
        private double _viewAnimAnchorX;
        private double _viewAnimAnchorY;

        private bool _inertiaActive;
        private double _inertiaStartMs;
        private double _inertiaDurationMs;
        private double _inertiaStartWorldX;
        private double _inertiaStartWorldY;
        private double _inertiaDeltaWorldX;
        private double _inertiaDeltaWorldY;

        private bool _frameAttached;

        // ------- throttled events -------
        private DispatcherTimer _eventTimer;
        private bool _viewChangedPending;
        private bool _cursorChangedPending;
        private bool _selectionChangedPending;

        // ------- text caches -------
        private FormattedText _statusFormatted;
        private string _statusFormattedText;
        private double _statusFormattedDpi;
        private FormattedText _labelFormatted;
        private string _labelFormattedText;
        private double _labelFormattedDpi;

        #endregion

        #region Construction and lifetime

        /// <summary>Creates an empty map canvas. Call <see cref="SetTileSource"/> to supply tiles.</summary>
        public MapCanvas()
        {
            ClipToBounds = true;
            Focusable = true;
            SnapsToDevicePixels = true;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            Loaded += OnLoadedHandler;
            Unloaded += OnUnloadedHandler;

            try
            {
                ApplyDpi(VisualTreeHelper.GetDpi(this));
            }
            catch (InvalidOperationException)
            {
                // Not connected to a visual tree yet - the default 96 DPI is fine.
            }
        }

        private void OnLoadedHandler(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplyDpi(VisualTreeHelper.GetDpi(this));
            }
            catch (InvalidOperationException)
            {
            }

            if (_viewAnimActive || _inertiaActive || _anyTileFading)
            {
                EnsureFrame();
            }

            InvalidateVisual();
        }

        private void OnUnloadedHandler(object sender, RoutedEventArgs e)
        {
            DetachFrame();
            if (_eventTimer != null)
            {
                _eventTimer.Stop();
            }

            _viewChangedPending = false;
            _cursorChangedPending = false;
            _selectionChangedPending = false;

            ReleaseMouseCaptureIfNeeded();
            _dragMode = DragMode.None;

            // Cancel downloads that are still running and forget the queue; the generation
            // counter makes sure any late results are discarded.
            _generation++;
            CancelPendingLoads();
        }

        /// <inheritdoc />
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            ApplyDpi(newDpi);
            InvalidateVisual();
        }

        private void ApplyDpi(DpiScale dpi)
        {
            if (dpi.DpiScaleX <= 0.0 || dpi.DpiScaleY <= 0.0 || dpi.PixelsPerDip <= 0.0)
            {
                return;
            }

            if (Math.Abs(dpi.DpiScaleX - _dpi.DpiScaleX) < 1e-9 &&
                Math.Abs(dpi.PixelsPerDip - _pixelsPerDip) < 1e-9)
            {
                return;
            }

            _dpi = dpi;
            _pixelsPerDip = dpi.PixelsPerDip;
            _statusFormatted = null;
            _labelFormatted = null;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Installs (or replaces) the tile source. The <paramref name="cacheKey"/> isolates the
        /// on-disk cache directory; <paramref name="urlBuilder"/> receives (x, y, z) and returns
        /// an XYZ tile URL, or <c>null</c> to skip the tile.
        /// </summary>
        public void SetTileSource(
            string cacheKey,
            Func<int, int, int, string> urlBuilder,
            string userAgent = null,
            string referer = null,
            string fileExtension = "jpg")
        {
            _cacheKey = SanitizeCacheKey(cacheKey);
            _urlBuilder = urlBuilder;
            _userAgent = string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent;
            _referer = string.IsNullOrWhiteSpace(referer) ? null : referer;
            _fileExtension = NormalizeFileExtension(fileExtension);

            _generation++;
            CancelPendingLoads();
            _tiles.Clear();
            _visibleKeys.Clear();
            _visibleTiles.Clear();
            _pendingRequests.Clear();
            _pendingRequestSet.Clear();
            _inFlightRequests.Clear();
            _anyTileFading = false;

            InvalidateVisual();
        }

        /// <summary>Jumps to the given view immediately, cancelling any running animation.</summary>
        public void SetView(double lat, double lon, double zoom)
        {
            StopAnimations();

            _centerLat = ClampLatitude(lat);
            _centerLon = NormalizeLongitude(lon);
            _zoom = ClampZoom(zoom);
            _displayLevel = -1;
            _lastViewMotionMs = 0.0;
            _framePumpUntilMs = NowMs + 420.0;

            InvalidateVisual();
            MarkViewChanged();
        }

        /// <summary>Animates smoothly to the given view. A non-positive duration jumps immediately.</summary>
        public void AnimateTo(double lat, double lon, double zoom, int durationMs = 350)
        {
            double targetLat = ClampLatitude(lat);
            double targetLon = NormalizeLongitude(lon);
            double targetZoom = ClampZoom(zoom);

            if (durationMs <= 0)
            {
                SetView(targetLat, targetLon, targetZoom);
                return;
            }

            StopAnimations();

            _viewAnimActive = true;
            _viewAnimAnchored = false;
            _viewAnimStartMs = NowMs;
            _viewAnimDurationMs = durationMs;
            _viewAnimFromLat = _centerLat;
            _viewAnimFromLon = _centerLon;
            _viewAnimFromZoom = _zoom;
            _viewAnimToLat = targetLat;
            _viewAnimToLon = targetLon;
            _viewAnimToZoom = targetZoom;
            EnsureFrame();
        }

        /// <summary>Current (possibly fractional) zoom level.</summary>
        public double ZoomLevel
        {
            get { return _zoom; }
        }

        /// <summary>Minimum zoom level. Defaults to 2.</summary>
        public double MinZoom
        {
            get { return _minZoom; }
            set
            {
                double clamped = Clamp(value, 0.5, 22.0);
                if (Math.Abs(clamped - _minZoom) < 1e-9)
                {
                    return;
                }

                _minZoom = clamped;
                if (_maxZoom < _minZoom)
                {
                    _maxZoom = _minZoom;
                }

                _zoom = ClampZoom(_zoom);
                InvalidateVisual();
                MarkViewChanged();
            }
        }

        /// <summary>Optional theme surface colour drawn behind map tiles.</summary>
        public Brush SurfaceBrush { get; set; }

        /// <summary>Maximum zoom level. Defaults to 18.</summary>
        public double MaxZoom
        {
            get { return _maxZoom; }
            set
            {
                double clamped = Clamp(value, 0.5, 22.0);
                if (Math.Abs(clamped - _maxZoom) < 1e-9)
                {
                    return;
                }

                _maxZoom = clamped;
                if (_minZoom > _maxZoom)
                {
                    _minZoom = _maxZoom;
                }

                _zoom = ClampZoom(_zoom);
                InvalidateVisual();
                MarkViewChanged();
            }
        }

        /// <summary>Latitude of the viewport centre, in degrees.</summary>
        public double CenterLat
        {
            get { return _centerLat; }
        }

        /// <summary>Longitude of the viewport centre, in degrees.</summary>
        public double CenterLon
        {
            get { return _centerLon; }
        }

        /// <summary>Geographic bounds of the viewport (empty until the control has a size).</summary>
        public GeoRect ViewBounds
        {
            get
            {
                double width = RenderSize.Width;
                double height = RenderSize.Height;
                if (width <= 0.0 || height <= 0.0)
                {
                    return GeoRect.Empty;
                }

                double north;
                double west;
                double south;
                double east;
                ScreenToGeo(new Point(0.0, 0.0), out north, out west);
                ScreenToGeo(new Point(width, height), out south, out east);
                return new GeoRect(north, south, west, east);
            }
        }

        /// <summary>Raised (throttled) after the centre, zoom or viewport size changed.</summary>
        public event EventHandler ViewChanged;

        /// <summary>Raised (throttled) when the cursor moved to a new geographic position.</summary>
        public event EventHandler MouseGeoPositionChanged;

        /// <summary>Latitude of the cursor, in degrees.</summary>
        public double CursorLat
        {
            get { return _cursorLat; }
        }

        /// <summary>Longitude of the cursor, in degrees.</summary>
        public double CursorLon
        {
            get { return _cursorLon; }
        }

        /// <summary>Enables rubber-band selection via shift+left-drag or right-drag. Defaults to true.</summary>
        public bool EnableSelection
        {
            get { return _enableSelection; }
            set { _enableSelection = value; }
        }

        /// <summary>AFS grid level used for click-to-toggle fine tuning.</summary>
        public int GridLevel
        {
            get { return _gridLevel; }
            set
            {
                int clamped = Math.Clamp(value, 9, 14);
                if (_gridLevel == clamped) return;
                _gridLevel = clamped;
                InvalidateVisual();
            }
        }

        /// <summary>Draw the AFS selection grid; disabled at zoom levels where cells are tiny.</summary>
        public bool ShowGrid
        {
            get { return _showGrid; }
            set
            {
                if (_showGrid == value) return;
                _showGrid = value;
                InvalidateVisual();
            }
        }

        /// <summary>Raised when the user clicks the map without dragging (lat, lon).</summary>
        public event Action<double, double> GeoClicked;

        /// <summary>
        /// Replaces the highlighted selection with individual AFS cells. No event is raised, so the
        /// owner can call this after converting a rectangle without re-entering its own handler.
        /// </summary>
        public void SetSelectionCells(IEnumerable<GeoRect> cells)
        {
            _selectionCells.Clear();
            GeoRect bounds = GeoRect.Empty;
            if (cells != null)
            {
                foreach (GeoRect cell in cells)
                {
                    if (cell.IsEmpty) continue;
                    _selectionCells.Add(cell);
                    bounds = bounds.IsEmpty ? cell : Union(bounds, cell);
                }
            }

            _selection = _selectionCells.Count > 0 ? bounds : GeoRect.Empty;
            InvalidateVisual();
        }

        /// <summary>Replaces the green "already generated" overlay.</summary>
        public void SetCompletedCells(IEnumerable<GeoRect> cells)
        {
            _completedCells.Clear();
            if (cells != null)
            {
                foreach (GeoRect cell in cells)
                {
                    if (!cell.IsEmpty) _completedCells.Add(cell);
                }
            }
            InvalidateVisual();
        }

        /// <summary>
        /// The selected geographic rectangle. Stored in geographic coordinates, so it stays
        /// anchored while the map pans and zooms.
        /// </summary>
        public GeoRect Selection
        {
            get { return _selection; }
            set { SetSelection(value, true); }
        }

        /// <summary>Clears the current selection.</summary>
        public void ClearSelection()
        {
            _selectionCells.Clear();
            SetSelection(GeoRect.Empty, true);
        }

        /// <summary>Raised (throttled) while a selection is being dragged, and once on release.</summary>
        public event EventHandler SelectionChanged;

        /// <summary>Raised once when a rectangle drag finishes (not during the drag).</summary>
        public event EventHandler SelectionCommitted;

        /// <summary>Optional overlay text drawn in the bottom-left corner. May be null or empty.</summary>
        public string StatusText
        {
            get { return _statusText; }
            set
            {
                if (string.Equals(_statusText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _statusText = value;
                InvalidateVisual();
            }
        }

        /// <summary>Requests a redraw of the control.</summary>
        public void Invalidate()
        {
            InvalidateVisual();
        }

        #endregion

        #region Projection

        /// <summary>Size of the whole world in pixels at the given zoom level.</summary>
        private static double WorldSizePixels(double zoom)
        {
            return TileSize * Math.Pow(2.0, zoom);
        }

        /// <summary>Projects a longitude (degrees) to a world X coordinate at the given zoom.</summary>
        private static double LongitudeToWorldX(double lon, double zoom)
        {
            return (lon + 180.0) / 360.0 * WorldSizePixels(zoom);
        }

        /// <summary>Projects a latitude (degrees) to a world Y coordinate at the given zoom (y down).</summary>
        private static double LatitudeToWorldY(double lat, double zoom)
        {
            lat = ClampLatitude(lat);
            double radians = lat * Math.PI / 180.0;
            double mercator = Math.Log(Math.Tan(radians) + 1.0 / Math.Cos(radians));
            double normalized = (1.0 - mercator / Math.PI) * 0.5;
            return normalized * WorldSizePixels(zoom);
        }

        /// <summary>Converts a world X coordinate at the given zoom back to longitude.</summary>
        private static double WorldXToLongitude(double x, double zoom)
        {
            return x / WorldSizePixels(zoom) * 360.0 - 180.0;
        }

        /// <summary>Converts a world Y coordinate at the given zoom back to latitude.</summary>
        private static double WorldYToLatitude(double y, double zoom)
        {
            double normalized = y / WorldSizePixels(zoom);
            double n = Math.PI - 2.0 * Math.PI * normalized;
            return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
        }

        /// <summary>Clamps a latitude to the Web-Mercator valid range.</summary>
        private static double ClampLatitude(double lat)
        {
            if (double.IsNaN(lat))
            {
                return 0.0;
            }

            if (lat > MaxMercatorLatitude)
            {
                return MaxMercatorLatitude;
            }

            if (lat < -MaxMercatorLatitude)
            {
                return -MaxMercatorLatitude;
            }

            return lat;
        }

        /// <summary>Wraps a longitude into [-180, 180].</summary>
        private static double NormalizeLongitude(double lon)
        {
            if (double.IsNaN(lon) || double.IsInfinity(lon))
            {
                return 0.0;
            }

            while (lon > 180.0)
            {
                lon -= 360.0;
            }

            while (lon < -180.0)
            {
                lon += 360.0;
            }

            return lon;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }

            if (value > maximum)
            {
                return maximum;
            }

            return value;
        }

        private static double ClampZoom(double zoom)
        {
            if (double.IsNaN(zoom))
            {
                return 2.0;
            }

            return Clamp(zoom, 0.5, 22.0);
        }

        private static int Modulo(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static double EaseOutCubic(double t)
        {
            double inverse = 1.0 - t;
            return 1.0 - inverse * inverse * inverse;
        }

        private double ClampZoomToRange(double zoom)
        {
            return Clamp(zoom, _minZoom, _maxZoom);
        }

        private void ScreenToGeo(Point point, out double lat, out double lon)
        {
            double centerX = LongitudeToWorldX(_centerLon, _zoom);
            double centerY = LatitudeToWorldY(_centerLat, _zoom);
            double worldX = centerX + (point.X - RenderSize.Width * 0.5);
            double worldY = centerY + (point.Y - RenderSize.Height * 0.5);
            lon = NormalizeLongitude(WorldXToLongitude(worldX, _zoom));
            lat = ClampLatitude(WorldYToLatitude(worldY, _zoom));
        }

        private Point GeoToScreen(double lat, double lon)
        {
            double centerX = LongitudeToWorldX(_centerLon, _zoom);
            double centerY = LatitudeToWorldY(_centerLat, _zoom);
            double x = LongitudeToWorldX(lon, _zoom) - centerX + RenderSize.Width * 0.5;
            double y = LatitudeToWorldY(lat, _zoom) - centerY + RenderSize.Height * 0.5;
            return new Point(x, y);
        }

        /// <summary>Describes the tile grid that covers the current viewport.</summary>
        private struct ViewLayout
        {
            public int TileZoom;
            public double Scale;
            public double OriginX;
            public double OriginY;
            public int MinTileX;
            public int MaxTileX;
            public int MinTileY;
            public int MaxTileY;
            public int TilesPerAxis;
        }

        private bool TryComputeLayout(double width, double height, out ViewLayout layout)
        {
            layout = new ViewLayout();

            if (width <= 0.0 || height <= 0.0 || double.IsNaN(width) || double.IsNaN(height))
            {
                return false;
            }

            return ComputeLayout(width, height, RefreshDisplayLevel(), out layout);
        }

        /// <summary>
        /// Chooses the level whose native resolution best matches the current zoom, clamped to the
        /// provider's limits.
        /// </summary>
        private int PreferredTileZoom()
        {
            // Never round past the provider's maximum: beyond that we would request tiles the server
            // does not have.
            int tileZoom = (int)Math.Round(_zoom, MidpointRounding.AwayFromZero);
            if (tileZoom > _maxZoom)
            {
                tileZoom = (int)Math.Floor(_zoom + 1e-9);
            }

            if (tileZoom < 0)
            {
                tileZoom = 0;
            }

            if (tileZoom > 22)
            {
                tileZoom = 22;
            }

            return tileZoom;
        }

        /// <summary>
        /// Returns the level to draw this frame, holding the previous one while the view is in motion
        /// (zoom animation, inertia, drag) so the image scales as a whole. It catches up as soon as
        /// the view settles, and it never holds on so long that a level would be stretched absurdly.
        /// </summary>
        private int RefreshDisplayLevel()
        {
            int ideal = PreferredTileZoom();
            bool moving = _viewAnimActive || _inertiaActive || _dragMode != DragMode.None;

            if (moving)
            {
                _lastViewMotionMs = NowMs;
            }

            if (_displayLevel < 0)
            {
                _displayLevel = ideal;
                return _displayLevel;
            }

            if (ideal == _displayLevel)
            {
                return _displayLevel;
            }

            // A single wheel notch moves the zoom by a quarter level, so during a wheel gesture the
            // gap stays small. Jumping several levels at once (double click, AnimateTo, fly-to) would
            // make the held level unusably blurry or would explode the tile count, so don't hold then.
            bool tooFar = Math.Abs(_zoom - _displayLevel) > 1.75;

            if (tooFar || (!moving && NowMs - _lastViewMotionMs > 160.0))
            {
                _displayLevel = ideal;
            }

            return _displayLevel;
        }

        /// <summary>Builds the tile grid for a specific zoom level at the current view position.</summary>
        private bool ComputeLayout(double width, double height, int tileZoom, out ViewLayout layout)
        {
            layout = new ViewLayout();

            if (width <= 0.0 || height <= 0.0 || double.IsNaN(width) || double.IsNaN(height))
            {
                return false;
            }

            if (tileZoom < 0)
            {
                tileZoom = 0;
            }

            if (tileZoom > 22)
            {
                tileZoom = 22;
            }

            int tilesPerAxis = 1 << tileZoom;
            if (tilesPerAxis < 1)
            {
                tilesPerAxis = 1;
            }

            // Screen pixels per tile edge at this view. The layout works entirely in the current
            // (fractional) zoom's world space, so tiles are spaced by Scale rather than by TileSize;
            // mixing the two would leave gaps whenever the zoom is between two levels.
            double scale = TileSize * Math.Pow(2.0, _zoom - tileZoom);
            double originX = LongitudeToWorldX(_centerLon, _zoom) - width * 0.5;
            double originY = LatitudeToWorldY(_centerLat, _zoom) - height * 0.5;

            int minTileX = (int)Math.Floor(originX / scale);
            int maxTileX = (int)Math.Floor((originX + width - 0.001) / scale);
            int minTileY = (int)Math.Floor(originY / scale);
            int maxTileY = (int)Math.Floor((originY + height - 0.001) / scale);

            if (minTileY < 0)
            {
                minTileY = 0;
            }

            if (maxTileY > tilesPerAxis - 1)
            {
                maxTileY = tilesPerAxis - 1;
            }

            layout.TileZoom = tileZoom;
            layout.Scale = scale;
            layout.OriginX = originX;
            layout.OriginY = originY;
            layout.MinTileX = minTileX;
            layout.MaxTileX = maxTileX;
            layout.MinTileY = minTileY;
            layout.MaxTileY = maxTileY;
            layout.TilesPerAxis = tilesPerAxis;
            return true;
        }

        #endregion

        #region Rendering

        /// <inheritdoc />
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            Size size = RenderSize;
            if (size.Width <= 0.0 || size.Height <= 0.0)
            {
                return;
            }

            dc.DrawRectangle(SurfaceBrush ?? BackgroundBrush, null, new Rect(0.0, 0.0, size.Width, size.Height));

            ViewLayout layout;
            if (_urlBuilder != null && TryComputeLayout(size.Width, size.Height, out layout))
            {
                bool backdropPainted = false;
                if (NeedsBackdrop(layout, size))
                {
                    backdropPainted = DrawBackdropLayer(dc, layout, size);
                }

                DrawTiles(dc, layout, size, backdropPainted);

            }

            DrawGrid(dc, size);
            DrawSelection(dc, size);
            DrawStatus(dc, size);

            if (_anyTileFading)
            {
                EnsureFrame();
            }
        }

        private void DrawTiles(DrawingContext dc, ViewLayout layout, Size size, bool backdropPainted)
        {
            _renderFrame++;
            _visibleKeys.Clear();
            _visibleTiles.Clear();
            _anyTileFading = false;

            double tilePixels = layout.Scale;
            double dpiScaleX = _dpi.DpiScaleX > 0.0 ? _dpi.DpiScaleX : 1.0;
            double dpiScaleY = _dpi.DpiScaleY > 0.0 ? _dpi.DpiScaleY : 1.0;
            bool integerScale = Math.Abs(tilePixels - Math.Round(tilePixels)) < 1e-3;
            double drawWidth = integerScale ? tilePixels : tilePixels + 0.5;
            double drawHeight = integerScale ? tilePixels : tilePixels + 0.5;

            for (int tileY = layout.MinTileY; tileY <= layout.MaxTileY; tileY++)
            {
                for (int tileX = layout.MinTileX; tileX <= layout.MaxTileX; tileX++)
                {
                    int wrappedX = Modulo(tileX, layout.TilesPerAxis);
                    TileKey key = new TileKey(layout.TileZoom, wrappedX, tileY);
                    Tile tile = GetOrCreateTile(key);

                    double left = tileX * tilePixels - layout.OriginX;
                    double top = tileY * tilePixels - layout.OriginY;

                    if (integerScale)
                    {
                        // Snap to whole device pixels so 1:1 tiles stay crisp and seamless.
                        left = Math.Round(left * dpiScaleX) / dpiScaleX;
                        top = Math.Round(top * dpiScaleY) / dpiScaleY;
                    }

                    if (left + drawWidth < 0.0 || top + drawHeight < 0.0 ||
                        left > size.Width || top > size.Height)
                    {
                        continue;
                    }

                    _visibleKeys.Add(key);
                    _visibleTiles.Add(tile);

                    double opacity = TileOpacity(tile);
                    if (tile.Image != null && opacity > 0.002)
                    {
                        Rect destination = new Rect(left, top, drawWidth, drawHeight);
                        if (opacity >= 0.999)
                        {
                            dc.DrawImage(tile.Image, destination);
                        }
                        else
                        {
                            dc.PushOpacity(opacity);
                            dc.DrawImage(tile.Image, destination);
                            dc.Pop();
                            _anyTileFading = true;
                        }
                    }
                    else
                    {
                        if (tile.Image != null)
                        {
                            _anyTileFading = true;
                        }

                        // Fall back to the shared backdrop layer under this cell, which is drawn at one
                        // single magnification for the whole screen. Filling each cell from its own
                        // ancestor would make neighbouring cells scale differently and is exactly what
                        // makes a zoom look like a patchwork of blocks.
                        RequestAncestorTiles(layout, tileX, tileY);

                        if (!backdropPainted && drawWidth > 2.0 && drawHeight > 2.0)
                        {
                            dc.DrawRoundedRectangle(
                                PlaceholderBrush,
                                null,
                                new Rect(left + 1.0, top + 1.0, drawWidth - 2.0, drawHeight - 2.0),
                                3.0,
                                3.0);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// True when at least one visible cell of the sharp layer has no imagery yet, in which case a
        /// backdrop passes needs to be drawn under it.
        /// </summary>
        private bool NeedsBackdrop(ViewLayout layout, Size size)
        {
            double tilePixels = layout.Scale;

            for (int tileY = layout.MinTileY; tileY <= layout.MaxTileY; tileY++)
            {
                for (int tileX = layout.MinTileX; tileX <= layout.MaxTileX; tileX++)
                {
                    Tile tile;
                    TileKey key = new TileKey(layout.TileZoom, Modulo(tileX, layout.TilesPerAxis), tileY);
                    if (!_tiles.TryGetValue(key, out tile) || tile.Image == null)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Draws a single coarse layer under the sharp tiles, covering the whole viewport at one
        /// uniform magnification. The deepest already-cached level below the display level wins, so a
        /// zoom-out or a level change never leaves holes, and every fallback cell is scaled by the same
        /// factor instead of each one picking its own ancestor.
        /// </summary>
        private bool DrawBackdropLayer(DrawingContext dc, ViewLayout layout, Size size)
        {
            for (int down = 1; down <= BackdropLevelOffset + 1; down++)
            {
                int level = layout.TileZoom - down;
                if (level < 0)
                {
                    return false;
                }

                int factor = 1 << down;
                double tilePixels = layout.Scale * factor;
                if (tilePixels < 8.0)
                {
                    continue;
                }

                int perAxis = 1 << level;
                int minX = (int)Math.Floor(layout.OriginX / tilePixels);
                int maxX = (int)Math.Floor((layout.OriginX + size.Width - 0.001) / tilePixels);
                int minY = (int)Math.Floor(layout.OriginY / tilePixels);
                int maxY = (int)Math.Floor((layout.OriginY + size.Height - 0.001) / tilePixels);

                if (minY < 0)
                {
                    minY = 0;
                }

                if (maxY > perAxis - 1)
                {
                    maxY = perAxis - 1;
                }

                if (minY > maxY)
                {
                    continue;
                }

                bool complete = true;
                for (int tileY = minY; tileY <= maxY && complete; tileY++)
                {
                    for (int tileX = minX; tileX <= maxX && complete; tileX++)
                    {
                        Tile backdrop;
                        if (!_tiles.TryGetValue(new TileKey(level, Modulo(tileX, perAxis), tileY), out backdrop) ||
                            backdrop.Image == null)
                        {
                            complete = false;
                        }
                    }
                }

                if (!complete)
                {
                    // Warm the cache for this level; it becomes the backdrop a frame later.
                    RequestBackdropTiles(level, minX, maxX, minY, maxY, perAxis);
                    continue;
                }

                for (int tileY = minY; tileY <= maxY; tileY++)
                {
                    for (int tileX = minX; tileX <= maxX; tileX++)
                    {
                        Tile backdrop;
                        if (!_tiles.TryGetValue(new TileKey(level, Modulo(tileX, perAxis), tileY), out backdrop) ||
                            backdrop.Image == null)
                        {
                            continue;
                        }

                        double left = Math.Round(tileX * tilePixels - layout.OriginX);
                        double top = Math.Round(tileY * tilePixels - layout.OriginY);
                        dc.DrawImage(backdrop.Image, new Rect(left, top, tilePixels + 1.0, tilePixels + 1.0));
                    }
                }

                return true;
            }

            return false;
        }

        private void RequestBackdropTiles(int level, int minX, int maxX, int minY, int maxY, int perAxis)
        {
            for (int tileY = minY; tileY <= maxY; tileY++)
            {
                for (int tileX = minX; tileX <= maxX; tileX++)
                {
                    TileKey key = new TileKey(level, Modulo(tileX, perAxis), tileY);
                    Tile existing;
                    if (_tiles.TryGetValue(key, out existing) && (existing.Image != null || existing.Failed))
                    {
                        continue;
                    }

                    EnqueueTileRequest(key);
                }
            }
        }

        /// <summary>
        /// Queues the coarse tiles that back the sharp layer, so the fallback imagery exists on the
        /// first visit to an area.
        /// </summary>
        private void RequestAncestorTiles(ViewLayout layout, int tileX, int tileY)
        {
            for (int up = 1; up <= 2; up++)
            {
                int ancestorZoom = layout.TileZoom - up;
                if (ancestorZoom < 0)
                {
                    return;
                }

                int perAxis = 1 << ancestorZoom;
                TileKey key = new TileKey(ancestorZoom, Modulo(tileX >> up, perAxis), tileY >> up);
                Tile ancestor;
                if (_tiles.TryGetValue(key, out ancestor) && (ancestor.Image != null || ancestor.Failed))
                {
                    continue;
                }

                EnqueueTileRequest(key);
            }
        }

        private void DrawGrid(DrawingContext dc, Size size)
        {
            if (_urlBuilder == null)
            {
                return;
            }

            GeoRect view = ViewBounds;
            if (view.IsEmpty) return;

            int level = Math.Clamp(_gridLevel, 9, 14);
            int gx0 = Afs2Grid.TileX(view.West, level);
            int gx1 = Afs2Grid.TileX(view.East, level);
            int gy0 = Afs2Grid.TileY(view.South, level);
            int gy1 = Afs2Grid.TileY(view.North, level);

            double cellWidth = Math.Abs(GeoToScreen(0.0, Afs2Grid.LonAtGridX(Math.Max(0, gx0) + 1, level)).X -
                                         GeoToScreen(0.0, Afs2Grid.LonAtGridX(Math.Max(0, gx0), level)).X);
            if (_showGrid && cellWidth >= 26.0)
            {
                int max = (1 << level) - 1;
                for (int gx = Math.Max(0, gx0); gx <= Math.Min(max, gx1 + 1); gx++)
                {
                    double lon = Afs2Grid.LonAtGridX(gx, level);
                    Point a = GeoToScreen(view.North, lon);
                    Point b = GeoToScreen(view.South, lon);
                    dc.DrawLine(GridPen, a, b);
                }

                for (int gy = Math.Max(0, gy0); gy <= Math.Min(max, gy1 + 1); gy++)
                {
                    double lat = Afs2Grid.LatAtGridY(gy, level);
                    Point a = GeoToScreen(lat, view.West);
                    Point b = GeoToScreen(lat, view.East);
                    dc.DrawLine(GridPen, a, b);
                }
            }

            int drawn = 0;
            foreach (GeoRect cell in _completedCells)
            {
                if (drawn++ > 2500) break;
                DrawCell(dc, cell, size, CompletedFillBrush, CompletedBorderPen);
            }
        }

        private void DrawCell(DrawingContext dc, GeoRect cell, Size size, Brush fill, Pen border)
        {
            if (cell.IsEmpty) return;
            Point northWest = GeoToScreen(cell.North, cell.West);
            Point southEast = GeoToScreen(cell.South, cell.East);
            double left = Math.Min(northWest.X, southEast.X);
            double top = Math.Min(northWest.Y, southEast.Y);
            double right = Math.Max(northWest.X, southEast.X);
            double bottom = Math.Max(northWest.Y, southEast.Y);
            if (right < 0.0 || bottom < 0.0 || left > size.Width || top > size.Height) return;

            Rect rect = new Rect(left, top, Math.Max(1.0, right - left), Math.Max(1.0, bottom - top));
            dc.DrawRectangle(fill, border, rect);
        }

        private void DrawSelection(DrawingContext dc, Size size)
        {
            if (_selection.IsEmpty)
            {
                return;
            }

            if (_selectionCells.Count > 0)
            {
                foreach (GeoRect cell in _selectionCells)
                {
                    DrawCell(dc, cell, size, SelectionFillBrush, SelectionBorderPen);
                }
            }
            else
            {
                Point northWest = GeoToScreen(_selection.North, _selection.West);
                Point southEast = GeoToScreen(_selection.South, _selection.East);

                double left = Math.Min(northWest.X, southEast.X);
                double top = Math.Min(northWest.Y, southEast.Y);
                double right = Math.Max(northWest.X, southEast.X);
                double bottom = Math.Max(northWest.Y, southEast.Y);

                if (right < 0.0 || bottom < 0.0 || left > size.Width || top > size.Height)
                {
                    return;
                }

                Rect rect = new Rect(left, top, Math.Max(1.0, right - left), Math.Max(1.0, bottom - top));
                dc.DrawRectangle(SelectionFillBrush, null, rect);
                dc.DrawRectangle(null, SelectionBorderPen, rect);
            }

            Point labelNw = GeoToScreen(_selection.North, _selection.West);
            Point labelSe = GeoToScreen(_selection.South, _selection.East);
            Rect labelRect = new Rect(
                Math.Min(labelNw.X, labelSe.X),
                Math.Min(labelNw.Y, labelSe.Y),
                Math.Max(1.0, Math.Abs(labelSe.X - labelNw.X)),
                Math.Max(1.0, Math.Abs(labelSe.Y - labelNw.Y)));

            FormattedText label = GetSelectionLabel();
            if (label != null)
            {
                double boxWidth = label.Width + 10.0;
                double boxHeight = label.Height + 6.0;
                double boxX = labelRect.Left + 8.0;
                double boxY = labelRect.Top + 8.0;

                if (boxX + boxWidth > size.Width - 4.0)
                {
                    boxX = Math.Max(4.0, size.Width - boxWidth - 4.0);
                }

                if (boxY + boxHeight > size.Height - 4.0)
                {
                    boxY = Math.Max(4.0, size.Height - boxHeight - 4.0);
                }

                dc.DrawRoundedRectangle(
                    SelectionLabelBackgroundBrush,
                    null,
                    new Rect(boxX, boxY, boxWidth, boxHeight),
                    3.0,
                    3.0);
                dc.DrawText(label, new Point(boxX + 5.0, boxY + 3.0));
            }
        }

        private void DrawStatus(DrawingContext dc, Size size)        {
            if (string.IsNullOrEmpty(_statusText))
            {
                return;
            }

            FormattedText text = GetStatusFormatted();
            if (text == null)
            {
                return;
            }

            double boxWidth = text.Width + 14.0;
            double boxHeight = text.Height + 8.0;
            double boxX = 8.0;
            double boxY = Math.Max(8.0, size.Height - boxHeight - 8.0);

            dc.DrawRoundedRectangle(StatusBackgroundBrush, null, new Rect(boxX, boxY, boxWidth, boxHeight), 4.0, 4.0);
            dc.DrawText(text, new Point(boxX + 7.0, boxY + 4.0));
        }

        private double TileOpacity(Tile tile)
        {
            if (double.IsNaN(tile.RevealStartMs))
            {
                return 1.0;
            }

            double age = NowMs - tile.RevealStartMs;
            if (age <= 0.0)
            {
                return 0.0;
            }

            if (age >= TileFadeMilliseconds)
            {
                return 1.0;
            }

            return age / TileFadeMilliseconds;
        }

        private FormattedText GetStatusFormatted()
        {
            if (string.IsNullOrEmpty(_statusText))
            {
                return null;
            }

            if (_statusFormatted != null &&
                string.Equals(_statusFormattedText, _statusText, StringComparison.Ordinal) &&
                Math.Abs(_statusFormattedDpi - _pixelsPerDip) < 1e-9)
            {
                return _statusFormatted;
            }

            _statusFormattedText = _statusText;
            _statusFormattedDpi = _pixelsPerDip;
            _statusFormatted = new FormattedText(
                _statusText,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                UiTypeface,
                11.5,
                StatusForegroundBrush,
                _pixelsPerDip);
            return _statusFormatted;
        }

        private FormattedText GetSelectionLabel()
        {
            if (_selection.IsEmpty)
            {
                return null;
            }

            double latSpan = Math.Abs(_selection.North - _selection.South);
            double lonSpan = Math.Abs(_selection.East - _selection.West);
            string text = string.Format(CultureInfo.InvariantCulture, "{0:0.000}\u00B0 \u00D7 {1:0.000}\u00B0", lonSpan, latSpan);

            if (_labelFormatted != null &&
                string.Equals(_labelFormattedText, text, StringComparison.Ordinal) &&
                Math.Abs(_labelFormattedDpi - _pixelsPerDip) < 1e-9)
            {
                return _labelFormatted;
            }

            _labelFormattedText = text;
            _labelFormattedDpi = _pixelsPerDip;
            _labelFormatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                UiTypeface,
                11.0,
                SelectionLabelForegroundBrush,
                _pixelsPerDip);
            return _labelFormatted;
        }

        /// <inheritdoc />
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            InvalidateVisual();
            MarkViewChanged();
        }

        #endregion

        #region Tile cache and loading

        private struct TileKey : IEquatable<TileKey>
        {
            public readonly int Z;
            public readonly int X;
            public readonly int Y;

            public TileKey(int z, int x, int y)
            {
                Z = z;
                X = x;
                Y = y;
            }

            public bool Equals(TileKey other)
            {
                return Z == other.Z && X == other.X && Y == other.Y;
            }

            public override bool Equals(object obj)
            {
                return obj is TileKey && Equals((TileKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Z;
                    hash = (hash * 397) ^ X;
                    hash = (hash * 397) ^ Y;
                    return hash;
                }
            }

            public override string ToString()
            {
                return string.Format(CultureInfo.InvariantCulture, "z{0}/{1}/{2}", Z, X, Y);
            }
        }

        private sealed class Tile
        {
            public readonly TileKey Key;
            public BitmapSource Image;
            public bool Queued;
            public bool Failed;
            public double RevealStartMs = double.NaN;
            public long LastUsedFrame;

            public Tile(TileKey key)
            {
                Key = key;
            }
        }

        /// <summary>Immutable snapshot of the tile source used by a single request.</summary>
        private sealed class TileRequestContext
        {
            public string CacheKey;
            public Func<int, int, int, string> UrlBuilder;
            public string UserAgent;
            public string Referer;
            public string Extension;
        }

        private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

        private static HttpClient CreateSharedHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler();
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            HttpClient client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(TileRequestTimeoutSeconds);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/*,*/*;q=0.8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            return client;
        }

        private Tile GetOrCreateTile(TileKey key)
        {
            Tile tile;
            if (_tiles.TryGetValue(key, out tile))
            {
                tile.LastUsedFrame = _renderFrame;
                return tile;
            }

            tile = new Tile(key);
            tile.LastUsedFrame = _renderFrame;
            _tiles[key] = tile;
            EnqueueTileRequest(key);
            TrimTileCache();
            return tile;
        }

        private void EnqueueTileRequest(TileKey key)
        {
            if (_urlBuilder == null || _cacheKey == null)
            {
                return;
            }

            if (_pendingRequestSet.Contains(key) || _inFlightRequests.Contains(key))
            {
                return;
            }

            Tile tile;
            if (_tiles.TryGetValue(key, out tile) && tile.Failed)
            {
                return;
            }

            _pendingRequestSet.Add(key);
            _pendingRequests.Enqueue(key);
            PumpTileRequests();
        }

        private void PumpTileRequests()
        {
            while (_inFlightRequests.Count < MaxConcurrentTileRequests && _pendingRequests.Count > 0)
            {
                TileKey key = _pendingRequests.Dequeue();
                _pendingRequestSet.Remove(key);

                if (!_tiles.ContainsKey(key))
                {
                    continue;
                }

                _inFlightRequests.Add(key);

                TileRequestContext context = new TileRequestContext
                {
                    CacheKey = _cacheKey,
                    UrlBuilder = _urlBuilder,
                    UserAgent = _userAgent,
                    Referer = _referer,
                    Extension = _fileExtension
                };

                int generation = _generation;
                CancellationToken token = _loadCancellation.Token;

                Task.Run(async () =>
                {
                    BitmapSource image = null;
                    try
                    {
                        image = await LoadTileImageAsync(key, context, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        image = null;
                    }
                    catch (Exception)
                    {
                        image = null;
                    }

                    TryPostToDispatcher(() => CompleteTileRequest(generation, key, image));
                });
            }
        }

        private void CompleteTileRequest(int generation, TileKey key, BitmapSource image)
        {
            _inFlightRequests.Remove(key);

            if (generation != _generation)
            {
                return;
            }

            Tile tile;
            if (!_tiles.TryGetValue(key, out tile))
            {
                return;
            }

            tile.Queued = false;

            if (image != null)
            {
                tile.Image = image;
                tile.Failed = false;
                tile.RevealStartMs = NowMs;
                InvalidateVisual();
                EnsureFrame();
            }
            else
            {
                tile.Failed = true;
                InvalidateVisual();
            }

            PumpTileRequests();
        }

        private void TrimTileCache()
        {
            while (_tiles.Count > MaxCachedTiles)
            {
                TileKey? victimKey = null;
                long oldestFrame = long.MaxValue;

                foreach (KeyValuePair<TileKey, Tile> pair in _tiles)
                {
                    if (_visibleKeys.Contains(pair.Key))
                    {
                        continue;
                    }

                    if (pair.Value.LastUsedFrame < oldestFrame)
                    {
                        oldestFrame = pair.Value.LastUsedFrame;
                        victimKey = pair.Key;
                    }
                }

                if (victimKey == null)
                {
                    return;
                }

                _tiles.Remove(victimKey.Value);
            }
        }

        private static async Task<BitmapSource> LoadTileImageAsync(TileKey key, TileRequestContext context, CancellationToken token)
        {
            string path = BuildTileCachePath(context.CacheKey, context.Extension, key);

            byte[] data = TryReadAllBytes(path);
            if (data != null)
            {
                BitmapSource cached = TryDecodeTile(data);
                if (cached != null)
                {
                    return cached;
                }

                TryDeleteFile(path);
            }

            string url = context.UrlBuilder != null
                ? context.UrlBuilder(key.X, key.Y, key.Z)
                : null;

            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            data = await DownloadTileAsync(url, context, token).ConfigureAwait(false);
            if (data == null || data.Length == 0)
            {
                return null;
            }

            BitmapSource image = TryDecodeTile(data);
            if (image == null)
            {
                return null;
            }

            TryWriteAllBytes(path, data);
            return image;
        }

        private static async Task<byte[]> DownloadTileAsync(string url, TileRequestContext context, CancellationToken token)
        {
            for (int attempt = 0; attempt <= TileRequestRetries; attempt++)
            {
                if (token.IsCancellationRequested)
                {
                    return null;
                }

                try
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        string userAgent = context.UserAgent ?? DefaultUserAgent;
                        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                        if (!string.IsNullOrEmpty(context.Referer))
                        {
                            request.Headers.TryAddWithoutValidation("Referer", context.Referer);
                        }

                        using (HttpResponseMessage response = await SharedHttpClient
                            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                            .ConfigureAwait(false))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // Per-request timeout - fall through to the retry delay.
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (HttpRequestException)
                {
                    // Network failure - fall through to the retry delay.
                }
                catch (IOException)
                {
                    // Connection dropped - fall through to the retry delay.
                }

                if (attempt < TileRequestRetries)
                {
                    try
                    {
                        await Task.Delay(250 * (attempt + 1), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                }
            }

            return null;
        }

        private static BitmapSource TryDecodeTile(byte[] data)
        {
            try
            {
                using (MemoryStream stream = new MemoryStream(data, 0, data.Length, false, false))
                {
                    BitmapImage image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    image.DecodePixelWidth = TileSize;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (FileFormatException)
            {
                return null;
            }
        }

        private static string BuildTileCachePath(string cacheKey, string extension, TileKey key)
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AeroSceneryNG",
                "tilecache",
                cacheKey);

            return Path.Combine(
                root,
                key.Z.ToString(CultureInfo.InvariantCulture),
                key.X.ToString(CultureInfo.InvariantCulture),
                key.Y.ToString(CultureInfo.InvariantCulture) + "." + extension);
        }

        private static byte[] TryReadAllBytes(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                byte[] data = File.ReadAllBytes(path);
                return data.Length > 0 ? data : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void TryWriteAllBytes(string path, byte[] data)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(path, data);
            }
            catch (IOException)
            {
                // A failed cache write must never break the map.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string SanitizeCacheKey(string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                return "default";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(cacheKey.Length);
            for (int i = 0; i < cacheKey.Length; i++)
            {
                char c = cacheKey[i];
                bool isInvalid = c == '/' || c == '\\' || Array.IndexOf(invalid, c) >= 0;
                builder.Append(isInvalid ? '_' : c);
            }

            string result = builder.ToString().Trim();
            return result.Length == 0 ? "default" : result;
        }

        private static string NormalizeFileExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return DefaultFileExtension;
            }

            string trimmed = extension.Trim().TrimStart('.');
            if (trimmed.Length == 0)
            {
                return DefaultFileExtension;
            }

            for (int i = 0; i < trimmed.Length; i++)
            {
                if (!char.IsLetterOrDigit(trimmed[i]))
                {
                    return DefaultFileExtension;
                }
            }

            return trimmed.ToLowerInvariant();
        }

        private void CancelPendingLoads()
        {
            CancellationTokenSource old = _loadCancellation;
            _loadCancellation = new CancellationTokenSource();

            try
            {
                old.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                old.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void TryPostToDispatcher(Action action)
        {
            Dispatcher dispatcher = Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            try
            {
                dispatcher.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // Dispatcher shut down between the check and the post.
            }
            catch (TaskCanceledException)
            {
            }
        }

        #endregion

        #region Animation and frame pump

        private static double NowMs
        {
            get { return Clock.Elapsed.TotalMilliseconds; }
        }

        private void EnsureFrame()
        {
            if (_frameAttached)
            {
                return;
            }

            _frameAttached = true;
            CompositionTarget.Rendering += OnCompositionRendering;
        }

        private void DetachFrame()
        {
            if (!_frameAttached)
            {
                return;
            }

            _frameAttached = false;
            CompositionTarget.Rendering -= OnCompositionRendering;
        }

        private void OnCompositionRendering(object sender, EventArgs e)
        {
            bool active = false;

            if (_viewAnimActive)
            {
                AdvanceViewAnimation();
                active = true;
            }

            if (_inertiaActive)
            {
                AdvanceInertia();
                active = true;
            }

            if (_anyTileFading)
            {
                active = true;
            }

            // Keep pumping for a short while after an interaction ends so the display level can catch
            // up with the settled zoom (the level is only allowed to change once the view is still).
            if (NowMs < _framePumpUntilMs)
            {
                active = true;
            }

            if (active)
            {
                InvalidateVisual();
            }
            else
            {
                DetachFrame();
            }
        }

        private void AdvanceViewAnimation()
        {
            double elapsed = NowMs - _viewAnimStartMs;
            double t = _viewAnimDurationMs <= 0.0 ? 1.0 : elapsed / _viewAnimDurationMs;
            if (t > 1.0)
            {
                t = 1.0;
            }

            if (t < 0.0)
            {
                t = 0.0;
            }

            double eased = EaseOutCubic(t);

            if (_viewAnimAnchored)
            {
                double zoom = _viewAnimFromZoom + (_viewAnimToZoom - _viewAnimFromZoom) * eased;
                ApplyAnchoredZoom(_viewAnimAnchorLat, _viewAnimAnchorLon, _viewAnimAnchorX, _viewAnimAnchorY, zoom);
            }
            else
            {
                _zoom = _viewAnimFromZoom + (_viewAnimToZoom - _viewAnimFromZoom) * eased;
                _centerLat = _viewAnimFromLat + (_viewAnimToLat - _viewAnimFromLat) * eased;
                _centerLon = _viewAnimFromLon + (_viewAnimToLon - _viewAnimFromLon) * eased;
            }

            MarkViewChanged();

            if (t >= 1.0)
            {
                _viewAnimActive = false;

                if (_viewAnimAnchored)
                {
                    ApplyAnchoredZoom(_viewAnimAnchorLat, _viewAnimAnchorLon, _viewAnimAnchorX, _viewAnimAnchorY, _viewAnimToZoom);
                }
                else
                {
                    _zoom = _viewAnimToZoom;
                    _centerLat = _viewAnimToLat;
                    _centerLon = _viewAnimToLon;
                }
            }
        }

        private void AdvanceInertia()
        {
            double elapsed = NowMs - _inertiaStartMs;
            double t = _inertiaDurationMs <= 0.0 ? 1.0 : elapsed / _inertiaDurationMs;
            if (t > 1.0)
            {
                t = 1.0;
            }

            if (t < 0.0)
            {
                t = 0.0;
            }

            double eased = EaseOutCubic(t);
            double worldX = _inertiaStartWorldX + _inertiaDeltaWorldX * eased;
            double worldY = _inertiaStartWorldY + _inertiaDeltaWorldY * eased;

            _centerLon = NormalizeLongitude(WorldXToLongitude(worldX, _zoom));
            _centerLat = ClampLatitude(WorldYToLatitude(worldY, _zoom));

            MarkViewChanged();

            if (t >= 1.0)
            {
                _inertiaActive = false;
            }
        }

        private void ApplyAnchoredZoom(double anchorLat, double anchorLon, double anchorX, double anchorY, double zoom)
        {
            _zoom = ClampZoomToRange(zoom);
            _framePumpUntilMs = NowMs + 420.0;

            double worldX = LongitudeToWorldX(anchorLon, _zoom) - (anchorX - RenderSize.Width * 0.5);
            double worldY = LatitudeToWorldY(anchorLat, _zoom) - (anchorY - RenderSize.Height * 0.5);

            _centerLon = NormalizeLongitude(WorldXToLongitude(worldX, _zoom));
            _centerLat = ClampLatitude(WorldYToLatitude(worldY, _zoom));
        }

        private void BeginAnchoredZoom(Point anchorPoint, double targetZoom)
        {
            targetZoom = ClampZoomToRange(targetZoom);

            if (Math.Abs(targetZoom - _zoom) < 1e-9)
            {
                return;
            }

            double anchorLat;
            double anchorLon;
            ScreenToGeo(anchorPoint, out anchorLat, out anchorLon);

            StopAnimations();

            _viewAnimActive = true;
            _viewAnimAnchored = true;
            _viewAnimStartMs = NowMs;
            _viewAnimDurationMs = ZoomAnimationMilliseconds;
            _viewAnimFromZoom = _zoom;
            _viewAnimToZoom = targetZoom;
            _viewAnimAnchorLat = anchorLat;
            _viewAnimAnchorLon = anchorLon;
            _viewAnimAnchorX = anchorPoint.X;
            _viewAnimAnchorY = anchorPoint.Y;
            EnsureFrame();
        }

        private void StopAnimations()
        {
            _viewAnimActive = false;
            _inertiaActive = false;

            if (!_anyTileFading)
            {
                DetachFrame();
            }
        }

        #endregion

        #region Interaction

        private enum DragMode
        {
            None,
            Pan,
            SelectFromLeft,
            SelectFromRight
        }

        /// <inheritdoc />
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            Point position = e.GetPosition(this);
            UpdateCursorPosition(position);

            switch (_dragMode)
            {
                case DragMode.Pan:
                    UpdatePan(position);
                    break;
                case DragMode.SelectFromLeft:
                case DragMode.SelectFromRight:
                    if (!_leftDragMoved &&
                        (Math.Abs(position.X - _leftDownPoint.X) > 4.0 ||
                         Math.Abs(position.Y - _leftDownPoint.Y) > 4.0))
                    {
                        _leftDragMoved = true;
                        _selectionCells.Clear();
                        BeginSelection(_leftDownPoint, _dragMode);
                    }

                    if (_leftDragMoved)
                    {
                        UpdateSelectionDrag(position);
                    }
                    break;
            }
        }

        /// <inheritdoc />
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            Focus();

            if (e.ClickCount == 2)
            {
                Point zoomPoint = e.GetPosition(this);
                bool zoomOut = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                BeginAnchoredZoom(zoomPoint, _zoom + (zoomOut ? -1.0 : 1.0));
                e.Handled = true;
                return;
            }

            Point point = e.GetPosition(this);

            // Left drag selects a rectangle; a left click without movement toggles one grid cell.
            // Panning is on the right button.
            _leftDownPoint = point;
            _leftDragMoved = false;

            if (_enableSelection)
            {
                _dragMode = DragMode.SelectFromLeft;
                ScreenToGeo(point, out _selectionAnchorLat, out _selectionAnchorLon);
                Cursor = Cursors.Cross;
            }
            else
            {
                BeginPan(point);
            }

            CaptureMouse();
            e.Handled = true;
        }

        /// <inheritdoc />
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            if (_dragMode == DragMode.Pan)
            {
                EndPan();
                ReleaseMouseCaptureIfNeeded();
                e.Handled = true;
            }
            else if (_dragMode == DragMode.SelectFromLeft)
            {
                if (_leftDragMoved)
                {
                    EndSelection();
                }
                else
                {
                    _dragMode = DragMode.None;
                    Cursor = Cursors.Arrow;
                    ScreenToGeo(e.GetPosition(this), out double lat, out double lon);
                    Action<double, double> handler = GeoClicked;
                    if (handler != null) handler(lat, lon);
                }

                ReleaseMouseCaptureIfNeeded();
                e.Handled = true;
            }
        }

        /// <inheritdoc />
        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);

            Focus();
            BeginPan(e.GetPosition(this));
            CaptureMouse();
            e.Handled = true;
        }

        /// <inheritdoc />
        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);

            if (_dragMode == DragMode.Pan)
            {
                EndPan();
                ReleaseMouseCaptureIfNeeded();
                e.Handled = true;
            }
        }

        /// <inheritdoc />
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);

            if (e.Delta == 0)
            {
                return;
            }

            double steps = e.Delta / 120.0;
            BeginAnchoredZoom(e.GetPosition(this), _zoom + 0.25 * steps);
            e.Handled = true;
        }

        /// <inheritdoc />
        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _cursorValid = false;
        }

        /// <inheritdoc />
        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);

            if (_dragMode == DragMode.Pan)
            {
                EndPan();
            }
            else if (_dragMode == DragMode.SelectFromLeft || _dragMode == DragMode.SelectFromRight)
            {
                if (_leftDragMoved)
                {
                    EndSelection();
                }
                else
                {
                    _dragMode = DragMode.None;
                    Cursor = Cursors.Arrow;
                }
            }
        }

        private void UpdateCursorPosition(Point position)
        {
            double lat;
            double lon;
            ScreenToGeo(position, out lat, out lon);

            if (_cursorValid &&
                Math.Abs(lat - _cursorLat) < 1e-10 &&
                Math.Abs(lon - _cursorLon) < 1e-10)
            {
                return;
            }

            _cursorLat = lat;
            _cursorLon = lon;
            _cursorValid = true;
            _cursorChangedPending = true;
            ScheduleEventFlush();
        }

        private void BeginPan(Point point)
        {
            StopAnimations();

            _dragMode = DragMode.Pan;
            _dragStartPoint = point;
            _dragLastPoint = point;
            _dragLastSampleMs = NowMs;
            _panBaseWorldX = LongitudeToWorldX(_centerLon, _zoom);
            _panBaseWorldY = LatitudeToWorldY(_centerLat, _zoom);
            _velocityX = 0.0;
            _velocityY = 0.0;
            Cursor = Cursors.SizeAll;
        }

        private void UpdatePan(Point point)
        {
            double dx = point.X - _dragStartPoint.X;
            double dy = point.Y - _dragStartPoint.Y;

            double worldX = _panBaseWorldX - dx;
            double worldY = _panBaseWorldY - dy;

            _centerLon = NormalizeLongitude(WorldXToLongitude(worldX, _zoom));
            _centerLat = ClampLatitude(WorldYToLatitude(worldY, _zoom));

            double now = NowMs;
            double deltaSeconds = now - _dragLastSampleMs;
            if (deltaSeconds > 0.5)
            {
                double instanceVelocityX = (point.X - _dragLastPoint.X) / deltaSeconds;
                double instanceVelocityY = (point.Y - _dragLastPoint.Y) / deltaSeconds;
                _velocityX = _velocityX * 0.6 + instanceVelocityX * 0.4;
                _velocityY = _velocityY * 0.6 + instanceVelocityY * 0.4;
                _dragLastPoint = point;
                _dragLastSampleMs = now;
            }

            InvalidateVisual();
            MarkViewChanged();
        }

        private void EndPan()
        {
            if (_dragMode != DragMode.Pan)
            {
                return;
            }

            _dragMode = DragMode.None;
            Cursor = Cursors.Arrow;

            double speed = Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);
            double duration = PanInertiaMilliseconds;
            double deltaX = -_velocityX * duration / 3.0;
            double deltaY = -_velocityY * duration / 3.0;
            double distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

            if (speed < 1e-4 || distance < MinimumPanInertiaPixels)
            {
                _velocityX = 0.0;
                _velocityY = 0.0;
                return;
            }

            if (distance > MaximumPanInertiaPixels)
            {
                double scale = MaximumPanInertiaPixels / distance;
                deltaX *= scale;
                deltaY *= scale;
            }

            _inertiaActive = true;
            _inertiaStartMs = NowMs;
            _inertiaDurationMs = duration;
            _inertiaStartWorldX = LongitudeToWorldX(_centerLon, _zoom);
            _inertiaStartWorldY = LatitudeToWorldY(_centerLat, _zoom);
            _inertiaDeltaWorldX = deltaX;
            _inertiaDeltaWorldY = deltaY;
            _velocityX = 0.0;
            _velocityY = 0.0;
            EnsureFrame();
        }

        private void BeginSelection(Point point, DragMode mode)
        {
            StopAnimations();

            ScreenToGeo(point, out _selectionAnchorLat, out _selectionAnchorLon);
            _dragMode = mode;
            _dragStartPoint = point;
            Cursor = Cursors.Cross;

            GeoRect initial = new GeoRect(
                _selectionAnchorLat,
                _selectionAnchorLat,
                _selectionAnchorLon,
                _selectionAnchorLon);
            SetSelection(initial, false);
        }

        private void UpdateSelectionDrag(Point point)
        {
            double lat;
            double lon;
            ScreenToGeo(point, out lat, out lon);

            GeoRect rect = new GeoRect(
                Math.Max(_selectionAnchorLat, lat),
                Math.Min(_selectionAnchorLat, lat),
                Math.Min(_selectionAnchorLon, lon),
                Math.Max(_selectionAnchorLon, lon));

            SetSelection(rect, false);
        }

        private void EndSelection()
        {
            _dragMode = DragMode.None;
            Cursor = Cursors.Arrow;

            if (_selection.IsEmpty)
            {
                InvalidateVisual();
            }

            RaiseSelectionChangedNow();
            EventHandler committed = SelectionCommitted;
            if (committed != null) committed(this, EventArgs.Empty);
            InvalidateVisual();
        }

        private void SetSelection(GeoRect rect, bool raiseImmediately)
        {
            if (RectsEqual(_selection, rect))
            {
                return;
            }

            _selection = rect;
            _selectionCells.Clear();
            InvalidateVisual();

            if (raiseImmediately)
            {
                RaiseSelectionChangedNow();
            }
            else
            {
                _selectionChangedPending = true;
                ScheduleEventFlush();
            }
        }

        private void RaiseSelectionChangedNow()
        {
            _selectionChangedPending = false;
            EventHandler handler = SelectionChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private static GeoRect Union(GeoRect a, GeoRect b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            return new GeoRect(
                Math.Max(a.North, b.North),
                Math.Min(a.South, b.South),
                Math.Min(a.West, b.West),
                Math.Max(a.East, b.East));
        }

        private static bool RectsEqual(GeoRect a, GeoRect b)
        {
            if (a.IsEmpty && b.IsEmpty)
            {
                return true;
            }

            if (a.IsEmpty != b.IsEmpty)
            {
                return false;
            }

            return Math.Abs(a.North - b.North) < 1e-12 &&
                   Math.Abs(a.South - b.South) < 1e-12 &&
                   Math.Abs(a.West - b.West) < 1e-12 &&
                   Math.Abs(a.East - b.East) < 1e-12;
        }

        private void ReleaseMouseCaptureIfNeeded()
        {
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }

        #endregion

        #region Throttled events

        private void MarkViewChanged()
        {
            _viewChangedPending = true;
            ScheduleEventFlush();
        }

        private void ScheduleEventFlush()
        {
            if (_eventTimer == null)
            {
                _eventTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
                _eventTimer.Interval = TimeSpan.FromMilliseconds(EventThrottleMilliseconds);
                _eventTimer.Tick += OnEventTimerTick;
            }

            if (!_eventTimer.IsEnabled)
            {
                _eventTimer.Start();
            }
        }

        private void OnEventTimerTick(object sender, EventArgs e)
        {
            if (_viewChangedPending)
            {
                _viewChangedPending = false;
                EventHandler viewHandler = ViewChanged;
                if (viewHandler != null)
                {
                    viewHandler(this, EventArgs.Empty);
                }
            }

            if (_cursorChangedPending)
            {
                _cursorChangedPending = false;
                EventHandler cursorHandler = MouseGeoPositionChanged;
                if (cursorHandler != null)
                {
                    cursorHandler(this, EventArgs.Empty);
                }
            }

            if (_selectionChangedPending)
            {
                _selectionChangedPending = false;
                EventHandler selectionHandler = SelectionChanged;
                if (selectionHandler != null)
                {
                    selectionHandler(this, EventArgs.Empty);
                }
            }

            if (!_viewChangedPending && !_cursorChangedPending && !_selectionChangedPending)
            {
                DispatcherTimer timer = sender as DispatcherTimer;
                if (timer != null)
                {
                    timer.Stop();
                }
            }
        }

        #endregion

        #region Helpers

        private static Brush CreateFrozenBrush(uint argb)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF)));
            brush.Freeze();
            return brush;
        }

        private static Pen CreateFrozenPen(uint argb, double thickness)
        {
            Pen pen = new Pen(CreateFrozenBrush(argb), thickness);
            pen.Freeze();
            return pen;
        }

        #endregion
    }
}
