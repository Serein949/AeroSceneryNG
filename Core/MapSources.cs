using System;
using System.Collections.Generic;
using System.Globalization;

namespace AeroSceneryNG.Core
{
    /// <summary>
    /// Catalog of built-in imagery sources plus a factory for user-defined XYZ sources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every <see cref="MapSource.BuildUrl"/> is a pure function: it rotates mirror hosts
    /// deterministically, performs any source-specific path arithmetic (Bing quadkey, Tencent
    /// row split) and substitutes the 天地图 key. It never performs I/O, so it is safe to call
    /// from many download threads at once.
    /// </para>
    /// <para>
    /// The 天地图 (Tianditu) key is a process-wide setting: set
    /// <see cref="TiandituKey"/> once (for example after loading user settings) and every
    /// subsequent <c>BuildUrl</c> call picks it up.
    /// </para>
    /// </remarks>
    public static class MapSources
    {
        /// <summary>Browser-like User-Agent sent with tile requests; several tile servers reject unknown clients.</summary>
        public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

        private static readonly object SyncRoot = new object();
        private static readonly List<MapSource> Sources = new List<MapSource>(8);
        private static readonly Dictionary<string, MapSource> ById =
            new Dictionary<string, MapSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Immutable snapshot of <see cref="All"/>, replaced whenever a source is added.</summary>
        private static IReadOnlyList<MapSource> _snapshot;

        private static string _tiandituKey = string.Empty;
        private static string _mapboxKey = string.Empty;

        static MapSources()
        {
            Add(Bing());
            Add(Google());
            Add(Esri());
            Add(TiandituImage());
            Add(AmapSatellite());
            Add(TencentSatellite());
            Add(EsriClarity());
            Add(SentinelCloudless());
            Add(MapboxSatellite());
            Add(YandexSatellite());
        }

        /// <summary>
        /// All registered sources, in display order. The list is an immutable snapshot, so it is
        /// safe to enumerate while a custom source is being added.
        /// </summary>
        public static IReadOnlyList<MapSource> All
        {
            get { return _snapshot; }
        }

        /// <summary>
        /// API key for 天地图 (Tianditu). Defaults to <see cref="string.Empty"/>; assigning null
        /// is treated as an empty key. A missing key does not throw: the tile URL is still built
        /// and the server answers with an error tile.
        /// </summary>
        public static string TiandituKey
        {
            get { return _tiandituKey; }
            set { _tiandituKey = value ?? string.Empty; }
        }

        /// <summary>Mapbox access token; only the Mapbox satellite source needs it.</summary>
        public static string MapboxKey
        {
            get { return _mapboxKey; }
            set { _mapboxKey = value ?? string.Empty; }
        }

        /// <summary>
        /// Looks up a source by <see cref="MapSource.Id"/> (case-insensitive).
        /// Returns null when the id is unknown or empty, which keeps loading a saved project
        /// with a since-removed source from crashing.
        /// </summary>
        public static MapSource Get(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            lock (SyncRoot)
            {
                MapSource source;
                return ById.TryGetValue(id, out source) ? source : null;
            }
        }

        /// <summary>
        /// Creates (and registers) a user-defined XYZ source, e.g. a private tile server.
        /// </summary>
        /// <param name="name">Display name; also the base of the generated id.</param>
        /// <param name="urlTemplate">
        /// Tile URL template containing {x}, {y} and {z}; {s} is also allowed and then resolves
        /// to "0" because no mirror list is known for a custom source.
        /// </param>
        /// <param name="extension">Tile format, "jpg" or "png" (a leading dot is ignored).</param>
        /// <returns>The new source; it is also present in <see cref="All"/> and reachable via <see cref="Get"/>.</returns>
        public static MapSource CreateCustomXyz(string name, string urlTemplate, string extension)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A display name is required.", "name");
            }

            if (string.IsNullOrWhiteSpace(urlTemplate))
            {
                throw new ArgumentException("A URL template is required.", "urlTemplate");
            }

            // The template must at least be able to address a tile.
            if (urlTemplate.IndexOf("{x}", StringComparison.Ordinal) < 0 ||
                urlTemplate.IndexOf("{y}", StringComparison.Ordinal) < 0 ||
                urlTemplate.IndexOf("{z}", StringComparison.Ordinal) < 0)
            {
                throw new ArgumentException("The URL template must contain {x}, {y} and {z}.", "urlTemplate");
            }

            string ext = (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
            if (ext.Length == 0)
            {
                ext = "jpg";
            }

            string template = urlTemplate.Trim();
            string[] subdomains = template.IndexOf("{s}", StringComparison.Ordinal) >= 0
                ? new[] { "0" }
                : null;

            var source = new MapSource
            {
                Id = BuildCustomId(name),
                Name = name.Trim(),
                ShortName = ShortBadge(name),
                Description = "自定义 XYZ 图层",
                UrlTemplate = template,
                Subdomains = subdomains,
                FileExtension = ext,
                Referer = null,
                UserAgent = DefaultUserAgent,
                MinZoom = 0,
                MaxZoom = 20,
                RequiresKey = false,
                KeyPlaceholder = null,
                IsChinaFriendly = true,
                BuildUrl = (x, y, z) => Expand(template, x, y, z, Pick(subdomains, x, y, z), null),
            };

            Add(source);
            return source;
        }

        // ---------------------------------------------------------------- built-in sources

        /// <summary>
        /// Bing Maps satellite imagery. Addressed by quadkey rather than x/y/z, so the template
        /// carries a {quadkey} placeholder that <see cref="TileMath.ToQuadKey"/> fills in.
        /// </summary>
        private static MapSource Bing()
        {
            const string template = "https://ecn.t{s}.tiles.virtualearth.net/tiles/a{quadkey}.jpeg?g=1";
            string[] subdomains = { "0", "1", "2", "3" };

            return new MapSource
            {
                Id = "bing",
                Name = "必应卫星",
                ShortName = "必应",
                Description = "微软必应卫星影像，全球覆盖，无需密钥",
                UrlTemplate = template,
                Subdomains = subdomains,
                FileExtension = "jpg",
                Referer = null,
                UserAgent = DefaultUserAgent,
                MinZoom = 1,
                MaxZoom = 19,
                RequiresKey = false,
                KeyPlaceholder = null,
                IsChinaFriendly = true,
                BuildUrl = (x, y, z) =>
                    Expand(template, x, y, z, Pick(subdomains, x, y, z), null)
                        .Replace("{quadkey}", TileMath.ToQuadKey(x, y, z)),
            };
        }

        /// <summary>Google satellite layer (<c>lyrs=s</c>). Not reachable from mainland China without a proxy.</summary>
        private static MapSource Google()
        {
            const string template = "https://mt{s}.google.com/vt/lyrs=s&x={x}&y={y}&z={z}";
            string[] subdomains = { "0", "1", "2", "3" };

            return new MapSource
            {
                Id = "google",
                Name = "谷歌卫星",
                ShortName = "谷歌",
                Description = "谷歌卫星影像，清晰度高，大陆需代理访问",
                UrlTemplate = template,
                Subdomains = subdomains,
                FileExtension = "jpg",
                Referer = null,
                UserAgent = DefaultUserAgent,
                MinZoom = 1,
                MaxZoom = 20,
                RequiresKey = false,
                KeyPlaceholder = null,
                IsChinaFriendly = false,
                BuildUrl = (x, y, z) => Expand(template, x, y, z, Pick(subdomains, x, y, z), null),
            };
        }

        /// <summary>Esri World Imagery, served by ArcGIS in z/y/x order.</summary>
        private static MapSource Esri()
        {
            const string template = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";

            return new MapSource
            {
                Id = "esri",
                Name = "Esri 高清影像",
                ShortName = "Esri",
                Description = "Esri World Imagery，全球高清，无需密钥",
                UrlTemplate = template,
                Subdomains = null,
                FileExtension = "jpg",
                Referer = null,
                UserAgent = DefaultUserAgent,
                MinZoom = 1,
                MaxZoom = 19,
                RequiresKey = false,
                KeyPlaceholder = null,
                IsChinaFriendly = true,
                BuildUrl = (x, y, z) => Expand(template, x, y, z, null, null),
            };
        }

        /// <summary>
        /// 天地图 satellite layer in WMTS form (layer "img", tile matrix set "c"). The service
        /// requires a free API key; without one it answers HTTP 418 with an HTML page, which is
        /// why <see cref="BuildUrl"/> deliberately still returns a URL instead of throwing.
        /// </summary>
        private static MapSource TiandituImage()
        {
            const string template =
                "https://t{s}.tianditu.gov.cn/img_c/wmts?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
                "&LAYER=img&STYLE=default&TILEMATRIXSET=c&FORMAT=tiles" +
                "&TILEMATRIX={z}&TILEROW={y}&TILECOL={x}&tk={key}";
            string[] subdomains = { "0", "1", "2", "3", "4", "5", "6", "7" };

            return new MapSource
            {
                Id = "tianditu_img",
                Name = "天地图·影像",
                ShortName = "天地",
                Description = "国家测绘局影像，国内高清、需申请密钥",
                UrlTemplate = template,
                Subdomains = subdomains,
                FileExtension = "jpg",
                Referer = null,
                UserAgent = DefaultUserAgent,
                MinZoom = 1,
                MaxZoom = 18,
                RequiresKey = true,
                KeyPlaceholder = "{key}",
                IsChinaFriendly = true,
                // The key is read at call time so setting TiandituKey later takes effect.
                BuildUrl = (x, y, z) => Expand(template, x, y, z, Pick(subdomains, x, y, z), TiandituKey),
            };
        }

        /// <summary>高德 satellite imagery (<c>style=6</c>); mirror hosts are webst01..webst04.</summary>
        private static MapSource AmapSatellite()
        {
            const string template = "https://webst0{s}.is.autonavi.com/appmaptile?style=6&x={x}&y={y}&z={z}";
            string[] subdomains = { "1", "2", "3", "4" };

            return new MapSource
            {
                Id = "amap_sat",
                Name = "高德卫星",
                ShortName = "高德",
                Description = "高德卫星影像，国内访问快，无需密钥",
                UrlTemplate = template,
                Subdomains = subdomains,
                FileExtension = "jpg",
                Referer = null,
                UserAgent = DefaultUserAgent,
                MinZoom = 1,
                MaxZoom = 18,
                RequiresKey = false,
                KeyPlaceholder = null,
                IsChinaFriendly = true,
                BuildUrl = (x, y, z) => Expand(template, x, y, z, Pick(subdomains, x, y, z), null),
            };
        }

        /// <summary>
        /// 腾讯 satellite imagery. Its URL splits the world into 16×16 tile blocks and — this is
        /// the part that is easy to get wrong — numbers the rows from the <b>south</b> (TMS order),
        /// so the row used in the path is <c>2^z - 1 - y</c>, not <c>y</c>. Fetching with the
        /// unflipped row silently returns a blank white 256×256 JPEG.
        /// </summary>
        private static MapSource TencentSatellite()
        {
            const string template = "https://p{s}.map.gtimg.com/sateTiles/{z}/{x16}/{y16}/{x}_{ty}.jpg";
            string[] subdomains = { "1", "2", "3" };

            return new MapSource
            {
                Id = "tencent_sat",
                Name = "腾讯卫星",
                ShortName = "腾讯",
                Description = "腾讯卫星影像，国内访问快，无需密钥",
                UrlTemplate = template,
                Subdomains = subdomains,
                FileExtension = "jpg",
                Referer = null,
                UserAgent = DefaultUserAgent,
                MinZoom = 1,
                MaxZoom = 18,
                RequiresKey = false,
                KeyPlaceholder = null,
                IsChinaFriendly = true,
                BuildUrl = (x, y, z) =>
                {
                    int max = (1 << ClampZoom(z)) - 1;

                    // Keep malformed addresses from producing negative path segments.
                    int tx = Math.Min(Math.Max(x, 0), max);
                    int ty = max - Math.Min(Math.Max(y, 0), max);

                    return Expand(template, x, y, z, Pick(subdomains, x, y, z), null)
                        .Replace("{x16}", (tx / 16).ToString(CultureInfo.InvariantCulture))
                        .Replace("{y16}", (ty / 16).ToString(CultureInfo.InvariantCulture))
                        .Replace("{ty}", ty.ToString(CultureInfo.InvariantCulture));
                },
            };
        }

        /// <summary>Esri "Clarity" enhanced imagery, often sharper than World Imagery in cities.</summary>
        private static MapSource EsriClarity()
        {
            const string template =
                "https://clarity.maptiles.arcgis.com/arcgis/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";

            return new MapSource
            {
                Id = "esri_clarity",
                Name = "Esri Clarity",
                ShortName = "Esri+",
                Description = "Esri 增强影像，城市区域更清晰，无需密钥",
                UrlTemplate = template,
                Subdomains = null,
                FileExtension = "jpg",
                Referer = null,
                UserAgent = DefaultUserAgent,
                MinZoom = 1,
                MaxZoom = 19,
                RequiresKey = false,
                KeyPlaceholder = null,
                IsChinaFriendly = true,
                BuildUrl = (x, y, z) => Expand(template, x, y, z, null, null),
            };
        }

        /// <summary>Sentinel-2 cloudless mosaic (EOX). Global, 10 m class, no key.</summary>
        private static MapSource SentinelCloudless()
        {
            const string template =
                "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless-2020_3857/default/g/{z}/{y}/{x}.jpg";

            return new MapSource
            {
                Id = "sentinel2",
                Name = "Sentinel-2 无云",
                ShortName = "S2",
                Description = "欧洲航天局哨兵二号无云影像，全球免密钥，分辨率较低",
                UrlTemplate = template,
                Subdomains = null,
                FileExtension = "jpg",
                Referer = null,
                UserAgent = DefaultUserAgent,
                MinZoom = 1,
                MaxZoom = 16,
                RequiresKey = false,
                KeyPlaceholder = null,
                IsChinaFriendly = true,
                BuildUrl = (x, y, z) => Expand(template, x, y, z, null, null),
            };
        }

        /// <summary>Mapbox satellite (512 px tiles). Requires a free access token.</summary>
        private static MapSource MapboxSatellite()
        {
            const string template = "https://api.mapbox.com/v4/mapbox.satellite/{z}/{x}/{y}@2x.jpg?access_token={key}";

            return new MapSource
            {
                Id = "mapbox_sat",
                Name = "Mapbox 卫星",
                ShortName = "MBX",
                Description = "Mapbox 卫星影像，画质好，需要免费访问令牌",
                UrlTemplate = template,
                Subdomains = null,
                FileExtension = "jpg",
                Referer = null,
                UserAgent = DefaultUserAgent,
                MinZoom = 1,
                MaxZoom = 19,
                RequiresKey = true,
                KeyPlaceholder = "Mapbox Token",
                IsChinaFriendly = false,
                BuildUrl = (x, y, z) => Expand(template, x, y, z, null, MapboxKey),
            };
        }

        /// <summary>Yandex satellite imagery; useful where Bing/Google coverage is weak.</summary>
        private static MapSource YandexSatellite()
        {
            const string template = "https://core-sat.maps.yandex.net/tiles?l=sat&v=3.1000&x={x}&y={y}&z={z}";

            return new MapSource
            {
                Id = "yandex_sat",
                Name = "Yandex 卫星",
                ShortName = "Yandex",
                Description = "Yandex 卫星影像，部分地区覆盖较好，无需密钥",
                UrlTemplate = template,
                Subdomains = null,
                FileExtension = "jpg",
                Referer = "https://yandex.com/maps/",
                UserAgent = DefaultUserAgent,
                MinZoom = 1,
                MaxZoom = 19,
                RequiresKey = false,
                KeyPlaceholder = null,
                IsChinaFriendly = true,
                BuildUrl = (x, y, z) => Expand(template, x, y, z, null, null),
            };
        }

        // ---------------------------------------------------------------- helpers

        private static void Add(MapSource source)
        {
            lock (SyncRoot)
            {
                // Replace an entry with the same id (only possible for custom sources).
                MapSource existing;
                if (ById.TryGetValue(source.Id, out existing))
                {
                    Sources.Remove(existing);
                }

                ById[source.Id] = source;
                Sources.Add(source);
                _snapshot = Sources.ToArray();
            }
        }

        /// <summary>
        /// Fills {s}, {x}, {y}, {z} and {key}. Placeholders are replaced individually, so
        /// {x} never eats {x16}; any leftover placeholder is left untouched for the caller
        /// (Bing/Tencent) to resolve.
        /// </summary>
        private static string Expand(string template, int x, int y, int z, string subdomain, string key)
        {
            string url = template;
            if (subdomain != null)
            {
                url = url.Replace("{s}", subdomain);
            }

            url = url.Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
                     .Replace("{y}", y.ToString(CultureInfo.InvariantCulture))
                     .Replace("{z}", z.ToString(CultureInfo.InvariantCulture));

            if (key != null)
            {
                url = url.Replace("{key}", key);
            }

            return url;
        }

        /// <summary>
        /// Picks a mirror host deterministically. The coordinates are hashed rather than simply
        /// summed: with <c>(x + y) % n</c> and an even n, neighbouring tiles in a row alternate
        /// between only two hosts, which defeats the point of having four or eight mirrors.
        /// </summary>
        private static string Pick(string[] subdomains, int x, int y, int z)
        {
            if (subdomains == null || subdomains.Length == 0)
            {
                return string.Empty;
            }

            if (subdomains.Length == 1)
            {
                return subdomains[0];
            }

            unchecked
            {
                uint h = ((uint)x * 73856093u) ^ ((uint)y * 19349663u) ^ ((uint)z * 83492791u);
                return subdomains[(int)(h % (uint)subdomains.Length)];
            }
        }

        private static int ClampZoom(int z)
        {
            if (z < 0)
            {
                return 0;
            }

            return z > 30 ? 30 : z;
        }

        /// <summary>Stable id for a custom source: "custom_" plus an ASCII slug of the name.</summary>
        private static string BuildCustomId(string name)
        {
            string slug = Slug(name);
            if (slug.Length == 0)
            {
                // Names made only of non-ASCII characters still need a stable, unique id.
                slug = (name.Trim().GetHashCode() & 0x7FFFFFFF).ToString("x8", CultureInfo.InvariantCulture);
            }

            return "custom_" + slug;
        }

        private static string Slug(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            bool lastDash = false;

            foreach (char c in text.Trim().ToLowerInvariant())
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (ok)
                {
                    sb.Append(c);
                    lastDash = false;
                }
                else if (!lastDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }

            while (sb.Length > 0 && sb[sb.Length - 1] == '-')
            {
                sb.Length--;
            }

            return sb.ToString();
        }

        /// <summary>Best-effort 2..4 character badge for the source picker.</summary>
        private static string ShortBadge(string name)
        {
            string trimmed = name.Trim();
            return trimmed.Length <= 4 ? trimmed : trimmed.Substring(0, 4);
        }
    }
}
