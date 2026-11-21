using System;

namespace AeroSceneryNG.Core
{
    /// <summary>
    /// Describes one imagery source (satellite or aerial) that can be downloaded as
    /// Web-Mercator XYZ tiles and turned into Aerofly FS scenery.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="UrlTemplate"/> uses <c>{s}</c> (subdomain), <c>{x}</c>, <c>{y}</c> and
    /// <c>{z}</c> placeholders. Sources whose addressing differs from plain XYZ also use
    /// private placeholders that only their own <see cref="BuildUrl"/> knows how to fill in
    /// (<c>{quadkey}</c> for Bing, <c>{x16}</c>/<c>{y16}</c>/<c>{ty}</c> for Tencent).
    /// </para>
    /// <para>
    /// Always call <see cref="BuildUrl"/> rather than expanding the template by hand:
    /// it performs the subdomain rotation, quadkey / path arithmetic and key substitution.
    /// </para>
    /// </remarks>
    public sealed class MapSource
    {
        /// <summary>Stable identifier used for persistence, e.g. "tianditu_img".</summary>
        public string Id { get; set; }

        /// <summary>Display name shown in the UI, e.g. "天地图·影像".</summary>
        public string Name { get; set; }

        /// <summary>Two to four character badge used by the source picker, e.g. "天地".</summary>
        public string ShortName { get; set; }

        /// <summary>Localized name for the source list.</summary>
        public string DisplayName
        {
            get { return Loc.T(Name); }
        }

        /// <summary>Localized one-line description for the source list.</summary>
        public string DisplayDescription
        {
            get { return Loc.T(Description); }
        }

        /// <summary>One short Chinese line describing the source and its caveats.</summary>
        public string Description { get; set; }

        /// <summary>
        /// Tile URL template. May contain <c>{s}</c>, <c>{x}</c>, <c>{y}</c>, <c>{z}</c>
        /// and, for a few sources, extra placeholders resolved by <see cref="BuildUrl"/>.
        /// </summary>
        public string UrlTemplate { get; set; }

        /// <summary>Mirror hosts substituted for <c>{s}</c>, e.g. {"0","1","2","3"}; null when the source has none.</summary>
        public string[] Subdomains { get; set; }

        /// <summary>Tile format without a dot: "jpg" or "png".</summary>
        public string FileExtension { get; set; }

        /// <summary>Optional HTTP Referer the tile server expects; null when not needed.</summary>
        public string Referer { get; set; }

        /// <summary>Optional HTTP User-Agent to send; null to let the downloader decide.</summary>
        public string UserAgent { get; set; }

        /// <summary>Lowest supported zoom level (inclusive).</summary>
        public int MinZoom { get; set; }

        /// <summary>Highest supported zoom level (inclusive).</summary>
        public int MaxZoom { get; set; }

        /// <summary>True when the source needs an API key before it returns real imagery.</summary>
        public bool RequiresKey { get; set; }

        /// <summary>Placeholder token used for the key inside <see cref="UrlTemplate"/>, e.g. "{key}"; null when unused.</summary>
        public string KeyPlaceholder { get; set; }

        /// <summary>True when the source is reachable from mainland China without a VPN.</summary>
        public bool IsChinaFriendly { get; set; }

        /// <summary>
        /// Builds the full URL of one tile. Never blocks, never throws and never performs I/O:
        /// when a required key is missing the URL is still returned so the server can answer
        /// with its own error tile.
        /// </summary>
        public Func<int, int, int, string> BuildUrl { get; set; }

        /// <summary>Returns the display name, which keeps the source list readable in a debugger.</summary>
        public override string ToString()
        {
            return Name ?? Id ?? base.ToString();
        }
    }
}
