using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using AeroSceneryNG.Core;

namespace AeroSceneryMobile;

/// <summary>
/// Minimal slippy map: pan with one finger, pinch/+- to zoom, drag a box to select.
/// Tiles are downloaded through the same ImageryCache used by the generator.
/// </summary>
public sealed class MapView : View
{
    private const double MaxLat = 85.05112878;
    private const int TileSize = 256;

    private readonly ImageryCache cache;
    private readonly Paint tilePaint = new Paint(PaintFlags.FilterBitmap) { FilterBitmap = true, Dither = true };
    private readonly Paint placeholderPaint = new Paint { Color = Color.ParseColor("#11151C") };
    private readonly Paint gridPaint = new Paint { Color = Color.ParseColor("#1B2230"), StrokeWidth = 2 };
    private readonly Paint selectionFill = new Paint { Color = Color.ParseColor("#3338BDF8") };
    private readonly Paint selectionStroke = new Paint { Color = Color.ParseColor("#FF38BDF8"), StrokeWidth = 4, AntiAlias = true };
    private readonly Paint textPaint = new Paint { Color = Color.ParseColor("#E6E9EF"), TextSize = 28, AntiAlias = true };
    private readonly Paint smallTextPaint = new Paint { Color = Color.ParseColor("#8A94A6"), TextSize = 22, AntiAlias = true };
    private readonly Dictionary<string, Bitmap> tiles = new Dictionary<string, Bitmap>();
    private readonly HashSet<string> loading = new HashSet<string>();
    private readonly SemaphoreSlim slots = new SemaphoreSlim(6, 6);
    private readonly ScaleGestureDetector scaleDetector;
    private readonly Handler tickHandler = new Handler(Looper.MainLooper);
    private bool tickQueued;

    private MapSource source;
    private double centerLat = 39.9042;
    private double centerLon = 116.4074;
    private int zoom = 10;
    private bool selectMode;
    private bool panning;
    private float lastX, lastY;
    private float startX, startY;
    private RectF dragRect;
    private RectF selectionRect;
    private GeoBounds selection;
    private float pinchAccum = 1f;

    public event Action<GeoBounds> SelectionChanged;
    public event Action<string> StatusChanged;

    public MapView(Context context, ImageryCache cache) : base(context)
    {
        this.cache = cache;
        Focusable = true;
        Clickable = true;
        SetBackgroundColor(Color.ParseColor("#0B0E13"));
        // Software canvas is the most reliable path for many small bitmaps on all OEM GPUs.
        SetLayerType(LayerType.Software, null);
        gridPaint.SetStyle(Paint.Style.Stroke);
        selectionStroke.SetStyle(Paint.Style.Stroke);
        scaleDetector = new ScaleGestureDetector(context, new ScaleListener(this));
    }

    public MapSource Source
    {
        get => source;
        set
        {
            source = value;
            tiles.Clear();
            lock (loading) loading.Clear();
            Invalidate();
        }
    }

    public int Zoom => zoom;

    public GeoBounds Selection => selection;

    public void SetView(double lat, double lon, int z)
    {
        centerLat = Math.Clamp(lat, -MaxLat, MaxLat);
        centerLon = ClampLon(lon);
        zoom = Math.Clamp(z, 3, 18);
        Invalidate();
    }

    public void SetSelectMode(bool enabled)
    {
        selectMode = enabled;
        dragRect = null;
        Invalidate();
    }

    public void ClearSelection()
    {
        selection = default;
        selectionRect = null;
        SelectionChanged?.Invoke(default);
        Invalidate();
    }

    public void ZoomBy(int delta)
    {
        zoom = Math.Clamp(zoom + delta, 3, 18);
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        canvas.DrawColor(Color.ParseColor("#0B0E13"));
        if (Width <= 0 || Height <= 0 || source == null) return;

        double total = TileSize * Math.Pow(2, zoom);
        double worldX = LonToX(centerLon, total);
        double worldY = LatToY(centerLat, total);
        double originX = worldX - Width / 2.0;
        double originY = worldY - Height / 2.0;

        int firstX = (int)Math.Floor(originX / TileSize);
        int lastX = (int)Math.Floor((originX + Width) / TileSize);
        int firstY = (int)Math.Floor(originY / TileSize);
        int lastY = (int)Math.Floor((originY + Height) / TileSize);
        int maxTile = (1 << zoom) - 1;

        for (int ty = firstY; ty <= lastY; ty++)
        {
            for (int tx = firstX; tx <= lastX; tx++)
            {
                float left = (float)(tx * TileSize - originX);
                float top = (float)(ty * TileSize - originY);
                var dest = new RectF(left, top, left + TileSize, top + TileSize);
                if (tx < 0 || ty < 0 || tx > maxTile || ty > maxTile)
                {
                    canvas.DrawRect(dest, placeholderPaint);
                    continue;
                }

                string key = source.Id + "/" + zoom + "/" + tx + "/" + ty;
                if (tiles.TryGetValue(key, out Bitmap bmp) && bmp != null && !bmp.IsRecycled)
                {
                    canvas.DrawBitmap(bmp, new Rect(0, 0, bmp.Width, bmp.Height), dest, tilePaint);
                }
                else
                {
                    canvas.DrawRect(dest, placeholderPaint);
                    RequestTile(tx, ty);
                    EnsureTick();
                }
                canvas.DrawRect(dest, gridPaint);
            }
        }

        RectF rect = selectionRect;
        if (selectMode && dragRect != null) rect = dragRect;
        if (rect != null && rect.Width() > 1 && rect.Height() > 1)
        {
            canvas.DrawRect(rect, selectionFill);
            canvas.DrawRect(rect, selectionStroke);
        }

        string label = "Z" + zoom + "  " + (selectMode ? "框选中：拖出一个矩形" : "拖动平移 · 双指缩放") + "  tiles=" + tiles.Count;
        canvas.DrawText(label, 24, Height - 24, smallTextPaint);
        canvas.DrawText("Esri / AeroScenery", Width - 230, Height - 24, smallTextPaint);
    }

    private void EnsureTick()
    {
        if (tickQueued) return;
        tickQueued = true;
        tickHandler.PostDelayed(() =>
        {
            tickQueued = false;
            Invalidate();
        }, 1200);
    }

    protected override void OnDetachedFromWindow()
    {
        tickHandler.RemoveCallbacksAndMessages(null);
        base.OnDetachedFromWindow();
    }

    private void RequestTile(int x, int y)
    {
        if (source == null) return;
        MapSource src = source;
        string key = src.Id + "/" + zoom + "/" + x + "/" + y;
        lock (loading)
        {
            if (loading.Contains(key)) return;
            loading.Add(key);
        }
        Android.Util.Log.Info("AeroSceneryMap", "request " + key);
        _ = LoadTileAsync(key, src, x, y, zoom);
    }

    private async Task LoadTileAsync(string key, MapSource src, int x, int y, int z)
    {
        await slots.WaitAsync().ConfigureAwait(false);
        try
        {
            Android.Util.Log.Info("AeroSceneryMap", "begin " + key);
            byte[] bytes = await cache.GetTileBytesAsync(src, z, x, y, CancellationToken.None).ConfigureAwait(false);
            Android.Util.Log.Info("AeroSceneryMap", "bytes " + key + " = " + (bytes?.Length ?? -1));
            if (bytes == null || bytes.Length < 100) return;
            var options = new BitmapFactory.Options
            {
                InPreferredConfig = Bitmap.Config.Argb8888,
                InSampleSize = 1
            };
            Bitmap bmp = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, options);
            if (bmp == null) return;
            Post(() =>
            {
                tiles[key] = bmp;
                Invalidate();
            });
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("AeroSceneryMap", "tile " + z + "/" + x + "/" + y + " failed: " + ex);
        }
        finally
        {
            lock (loading) loading.Remove(key);
            slots.Release();
        }
    }

    public override bool OnTouchEvent(MotionEvent e)
    {
        scaleDetector.OnTouchEvent(e);
        if (scaleDetector.IsInProgress)
        {
            return true;
        }

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                lastX = e.GetX();
                lastY = e.GetY();
                startX = lastX;
                startY = lastY;
                if (selectMode)
                {
                    dragRect = new RectF(lastX, lastY, lastX, lastY);
                    Invalidate();
                }
                else
                {
                    panning = true;
                }
                return true;

            case MotionEventActions.Move:
                if (selectMode && dragRect != null)
                {
                    float x = e.GetX(), y = e.GetY();
                    dragRect.Set(Math.Min(startX, x), Math.Min(startY, y), Math.Max(startX, x), Math.Max(startY, y));
                    Invalidate();
                    return true;
                }
                if (panning)
                {
                    float dx = e.GetX() - lastX;
                    float dy = e.GetY() - lastY;
                    lastX = e.GetX();
                    lastY = e.GetY();
                    PanBy(dx, dy);
                    return true;
                }
                break;

            case MotionEventActions.Up:
                if (selectMode && dragRect != null)
                {
                    if (dragRect.Width() > 24 && dragRect.Height() > 24)
                    {
                        selection = ScreenRectToGeo(dragRect);
                        selectionRect = new RectF(dragRect);
                        SelectionChanged?.Invoke(selection);
                    }
                    dragRect = null;
                    Invalidate();
                    return true;
                }
                panning = false;
                return true;

            case MotionEventActions.Cancel:
                dragRect = null;
                panning = false;
                Invalidate();
                return true;
        }
        return base.OnTouchEvent(e);
    }

    private void PanBy(float dx, float dy)
    {
        double total = TileSize * Math.Pow(2, zoom);
        double x = LonToX(centerLon, total) - dx;
        double y = LatToY(centerLat, total) - dy;
        centerLon = XToLon(x, total);
        centerLat = YToLat(y, total);
        Invalidate();
    }

    private GeoBounds ScreenRectToGeo(RectF rect)
    {
        double total = TileSize * Math.Pow(2, zoom);
        double originX = LonToX(centerLon, total) - Width / 2.0;
        double originY = LatToY(centerLat, total) - Height / 2.0;
        double left = originX + rect.Left;
        double right = originX + rect.Right;
        double top = originY + rect.Top;
        double bottom = originY + rect.Bottom;
        return new GeoBounds(YToLat(top, total), YToLat(bottom, total), XToLon(right, total), XToLon(left, total));
    }

    private static double LonToX(double lon, double total) => (lon + 180.0) / 360.0 * total;

    private static double XToLon(double x, double total) => x / total * 360.0 - 180.0;

    private static double LatToY(double lat, double total)
    {
        lat = Math.Clamp(lat, -MaxLat, MaxLat);
        double rad = lat * Math.PI / 180.0;
        return (1.0 - Math.Log(Math.Tan(rad) + 1.0 / Math.Cos(rad)) / Math.PI) / 2.0 * total;
    }

    private static double YToLat(double y, double total)
    {
        double n = Math.PI - 2.0 * Math.PI * y / total;
        return Math.Atan(Math.Sinh(n)) * 180.0 / Math.PI;
    }

    private static double ClampLon(double lon)
    {
        while (lon > 180) lon -= 360;
        while (lon < -180) lon += 360;
        return lon;
    }

    private sealed class ScaleListener : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        private readonly MapView owner;
        public ScaleListener(MapView owner) { this.owner = owner; }

        public override bool OnScale(ScaleGestureDetector detector)
        {
            owner.pinchAccum *= detector.ScaleFactor;
            if (owner.pinchAccum > 1.35f)
            {
                owner.ZoomBy(1);
                owner.pinchAccum = 1f;
            }
            else if (owner.pinchAccum < 0.74f)
            {
                owner.ZoomBy(-1);
                owner.pinchAccum = 1f;
            }
            return true;
        }
    }
}
