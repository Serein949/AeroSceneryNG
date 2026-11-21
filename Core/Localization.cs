using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace AeroSceneryNG.Core
{
    /// <summary>
    /// Tiny runtime localizer. Chinese is the source language: every key is the Chinese
    /// string itself, so a missing translation degrades to the original text instead of
    /// showing a raw resource identifier.
    /// </summary>
    public static class Loc
    {
        public const string Chinese = "zh";
        public const string English = "en";

        private static string language = Chinese;

        public static event EventHandler LanguageChanged;

        public static string Language
        {
            get { return language; }
            set
            {
                string next = string.Equals(value, English, StringComparison.OrdinalIgnoreCase)
                    ? English
                    : Chinese;
                if (next == language)
                {
                    return;
                }

                language = next;
                CultureInfo.CurrentUICulture = next == English
                    ? CultureInfo.GetCultureInfo("en-US")
                    : CultureInfo.GetCultureInfo("zh-CN");
                LocalizationSource.Instance.RaiseChanged();
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static bool IsEnglish
        {
            get { return language == English; }
        }

        public static string T(string chinese)
        {
            if (chinese == null)
            {
                return string.Empty;
            }

            if (language != English)
            {
                return chinese;
            }

            return EnglishText.TryGetValue(chinese, out string text) ? text : chinese;
        }

        public static string F(string chinese, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, T(chinese), args);
        }

        private static readonly Dictionary<string, string> EnglishText =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Title bar and window chrome
                ["Aerofly FS 4 / FS Global 地景生成器"] = "Aerofly FS 4 / FS Global scenery generator",
                ["设置与密钥"] = "Settings & keys",
                ["设置"] = "Settings",
                ["最小化"] = "Minimize",
                ["最大化"] = "Maximize",
                ["关闭"] = "Close",

                // Selection
                ["1  选择制作区域"] = "1  Select area",
                ["单击网格 · 拖拽批量"] = "Click cells · drag to select",
                ["降低微调网格"] = "Coarser grid",
                ["提高微调网格"] = "Finer grid",
                ["清除"] = "Clear",
                ["尚未选择区域"] = "No area selected",
                ["北 {0:F4}°   南 {1:F4}°\n西 {2:F4}°   东 {3:F4}°"] =
                    "North {0:F4}°   South {1:F4}°\nWest {2:F4}°   East {3:F4}°",
                ["微调网格 L{0}"] = "Grid L{0}",
                ["网格 L{0}"] = "Grid L{0}",
                ["网格 L{0} · {1} 格"] = "Grid L{0} · {1} cells",
                ["绿色 = 已制作（{0} 格）"] = "Green = generated ({0} cells)",
                ["拖拽地图框选，或单击网格微调"] = "Drag on the map, or click cells to fine-tune",
                ["请至少选择一个级别"] = "Select at least one level",
                ["{0} 个瓦片 · 最细 {1:F1} m/px · 约 {2}"] = "{0} tiles · {1:F1} m/px · about {2}",

                // Source and target
                ["2  影像图源与目标"] = "2  Imagery source & target",
                ["国内"] = "CN",
                ["密钥"] = "Key",
                ["电脑 FS4"] = "Desktop FS4",
                ["手机 FSG"] = "Mobile FSG",
                ["两者都要"] = "Both",
                ["同时生成地形高程（DEM，电脑版）"] = "Also generate terrain elevation (DEM, desktop)",
                ["Level 10 · 输出到 scenery/elevation 目录，需勾选安装才会复制进游戏"] =
                    "Level 10 · written to scenery/elevation; enable install to copy it into the sim",
                ["电脑版 · DXT1 纹理 · 最高 Level 15 / 影像 Z19"] =
                    "Desktop · DXT1 · up to Level 15 / imagery Z19",
                ["手机版 · ETC2 纹理 · 最高 Level 14 / 影像 Z17"] =
                    "Mobile · ETC2 · up to Level 14 / imagery Z17",
                ["两套文件 · 共用上限 Level 14 / 影像 Z17"] =
                    "Both formats · shared cap Level 14 / imagery Z17",

                // Levels
                ["3  输出级别"] = "3  Output levels",
                ["全选"] = "All",
                ["清空"] = "None",
                ["降低该级别的影像细节"] = "Lower imagery detail for this level",
                ["提高该级别的影像细节"] = "Raise imagery detail for this level",
                ["清除本级覆盖，跟随全局 ZL 或 Level + 3"] =
                    "Clear this level's override and follow the global ZL or Level + 3",
                ["统一影像细节"] = "Global image detail",
                ["统一自动匹配（未单独设置的级别）"] = "Auto-match levels without overrides",
                ["自动（各级 = Level + 3）"] = "Auto (each level = Level + 3)",
                ["统一 Z{0} · 最高 Level {1}"] = "Global Z{0} · highest Level {1}",
                ["影像 Z{0} → 已自动选中 Level {1}"] = "Imagery Z{0} → selected Level {1}",
                ["影像 Z{0} → 输出 Level {1}–{2}（{3} 级）"] =
                    "Imagery Z{0} → output Level {1}–{2} ({3} levels)",

                // Options
                ["4  生成选项"] = "4  Generation options",
                ["地景名称"] = "Scenery name",
                ["下载线程"] = "Download threads",
                ["跳过已存在的瓦片（断点续作）"] = "Skip existing tiles (resume)",
                ["LZHAM 压缩（官方格式，体积更小）"] = "LZHAM compression (official, smaller)",
                ["自动打包为 TME（手机版）"] = "Package as TME (mobile)",
                ["输出目录"] = "Output folder",
                ["浏览"] = "Browse",
                ["完成后自动安装到 Aerofly FS 4 场景目录"] =
                    "Install into the Aerofly FS 4 scenery folder when done",
                ["打开输出目录"] = "Open output folder",
                ["开始生成"] = "Start",
                ["取消"] = "Cancel",
                ["{0} 线程"] = "{0} threads",
                ["在地图上拖拽框选区域，选择级别后点击“开始生成”。"] =
                    "Drag on the map to select an area, choose levels, then press Start.",

                // Map
                ["左键拖拽框选 · 单击网格微调 · 右键平移 · 滚轮缩放"] =
                    "Left-drag to select · click cells to fine-tune · right-drag to pan · wheel to zoom",
                ["放大"] = "Zoom in",
                ["缩小"] = "Zoom out",
                ["AeroScenery NG · 多线程下载 · LZHAM / ETC2"] =
                    "AeroScenery NG · multi-threaded download · LZHAM / ETC2",

                // Task page
                ["←  返回配置"] = "←  Back to setup",
                ["任务详情"] = "Task details",
                ["准备"] = "Preparing",
                ["准备中"] = "Preparing",
                ["下载与转换"] = "Download & convert",
                ["下载并转换"] = "Download & convert",
                ["安装与打包"] = "Install & package",
                ["安装"] = "Install",
                ["打包"] = "Package",
                ["高程"] = "Elevation",
                ["完成"] = "Done",
                ["下载影像"] = "Downloaded imagery",
                ["转换纹理"] = "Converted textures",
                ["实时日志"] = "Live log",
                ["任务信息"] = "Task information",
                ["当前处理"] = "Current tile",
                ["取消任务"] = "Cancel task",
                ["重新生成"] = "Generate again",
                ["返回配置"] = "Back to setup",
                ["0 张 · 0 MB"] = "0 images · 0 MB",
                ["0 / 0 瓦片"] = "0 / 0 tiles",
                ["正在规划区块…"] = "Planning tiles…",
                ["{0} 张影像"] = "{0} images",
                ["{0} · {1} 张影像"] = "{0} · {1} images",
                ["{0} / {1} 瓦片 · {2}"] = "{0} / {1} tiles · {2}",
                ["生成中"] = "Running",
                ["部分完成"] = "Partially complete",
                ["已完成"] = "Completed",
                ["全部区块生成成功"] = "All tiles generated successfully",
                ["{0} 个区块失败"] = "{0} tiles failed",
                ["完成：{0} 个瓦片，用时 {1:F0} 秒"] = "Done: {0} tiles in {1:F0}s",
                ["全部成功"] = "All succeeded",
                ["{0} 个失败"] = "{0} failed",
                [" · 已打包 TME"] = " · TME packed",
                [" · 高程 {0} 瓦片"] = " · {0} elevation tiles",
                ["失败：{0}"] = "Failed: {0}",
                ["任务已取消"] = "Task cancelled",
                ["已取消"] = "Cancelled",
                ["失败"] = "Failed",
                ["生成失败：{0}"] = "Generation failed: {0}",
                ["区域  {0:F4}°N / {1:F4}°S / {2:F4}°W / {3:F4}°E\n图源  {4}\n级别  {5}\n目标  {6}\n输出  {7}"] =
                    "Area  {0:F4}°N / {1:F4}°S / {2:F4}°W / {3:F4}°E\nSource  {4}\nLevels  {5}\nTarget  {6}\nOutput  {7}",
                ["开始生成…"] = "Starting…",
                ["开始生成 · {0} · {1}"] = "Started · {0} · {1}",

                // Settings
                ["外观"] = "Appearance",
                ["深色主题"] = "Dark theme",
                ["浅色主题"] = "Light theme",
                ["主题会立即切换并记住，下次启动保持不变。"] =
                    "Theme changes instantly and is remembered for next launch.",
                ["语言 / Language"] = "Language",
                ["中文"] = "中文",
                ["影像处理"] = "Image processing",
                ["启用"] = "Enable",
                ["对每张 2048×2048 影像做色彩校正；默认关闭，关闭时完全保留原始影像。"] =
                    "Colour-grade each 2048×2048 image. Off by default; when off the original image is kept.",
                ["亮度"] = "Brightness",
                ["对比度"] = "Contrast",
                ["饱和度"] = "Saturation",
                ["锐度"] = "Sharpness",
                ["红 / 绿 / 蓝通道"] = "Red / green / blue channels",
                ["水面颜色统一"] = "Unify water colour",
                ["按颜色识别偏蓝的水体并统一到指定颜色。这是近似处理，不是 OSM 水面掩膜；会误判蓝色屋顶，可随时关闭。"] =
                    "Detect bluish water by colour and tint it to the chosen colour. This is an approximation, not an OSM water mask; blue roofs may be affected. Can be turned off at any time.",
                ["目标颜色（#RRGGBB）"] = "Target colour (#RRGGBB)",
                ["天地图密钥"] = "Tianditu key",
                ["天地图国家影像在中国大陆分辨率最高（最高约 0.5 m/px）。在 tianditu.gov.cn 免费注册后，把浏览器端密钥贴到这里。"] =
                    "Tianditu has the highest resolution in mainland China (up to about 0.5 m/px). Register free at tianditu.gov.cn and paste the browser key here.",
                ["Mapbox 访问令牌"] = "Mapbox access token",
                ["只有 Mapbox 卫星图源需要，免费账号即可；留空时该图源不可用。"] =
                    "Only required by the Mapbox satellite source; a free account is enough. Leave blank to disable that source.",
                ["Aerofly FS 4 场景目录"] = "Aerofly FS 4 scenery folder",
                ["电脑版 .ttc 会按 map_09_xxxx_yyyy 子目录复制到这里（通常是 文档/Aerofly FS 4/scenery/images）。"] =
                    "Desktop .ttc tiles are copied here into map_09_xxxx_yyyy subfolders (usually Documents/Aerofly FS 4/scenery/images).",
                ["手机 FSG 工作目录（可选）"] = "Mobile FSG working folder (optional)",
                ["如果手机通过 USB 挂载为磁盘，可填写 Android/data/com.aerofly.aeroflyfsg1/files；否则先输出到电脑再复制。"] =
                    "If the phone is mounted as a drive over USB, use Android/data/com.aerofly.aeroflyfsg1/files; otherwise generate on the PC and copy afterwards.",
                ["附加输出"] = "Extra output",
                ["同时保存未处理原图（PNG）"] = "Also save unprocessed source images (PNG)",
                ["每块 2048×2048 的原始拼接图单独保存，方便外部修图或留档；默认关闭。"] =
                    "Save each raw 2048×2048 mosaic for external editing or archiving. Off by default.",
                ["缓存管理"] = "Cache management",
                ["缓存目录（可移到空间更大的磁盘）"] = "Cache folder (move it to a larger drive)",
                ["缓存上限"] = "Cache limit",
                ["超过上限时自动清理最旧的瓦片"] = "Automatically remove the oldest tiles above the limit",
                ["如果 C 盘空间异常减少，通常是瓦片缓存累积；把缓存目录改到其他盘即可。生成结束后会自动按上限清理。"] =
                    "Unexpected C: usage is usually tile cache growth; move the cache folder to another drive. It is trimmed to the limit after each run.",
                ["清空缓存"] = "Clear cache",
                ["保存设置"] = "Save settings",
                ["不限制"] = "Unlimited",
                ["清除缓存"] = "Clear cache",

                // Scenery targets
                ["Aerofly FS Global（手机端 · ETC2）"] = "Aerofly FS Global (mobile · ETC2)",
                ["同时生成 FS4 + FSG"] = "FS4 + FSG together",
                ["Aerofly FS 4（电脑端 · DXT1）"] = "Aerofly FS 4 (desktop · DXT1)",

                // Image sources
                ["自定义 XYZ 图层"] = "Custom XYZ layer",
                ["必应卫星"] = "Bing satellite",
                ["必应"] = "Bing",
                ["微软必应卫星影像，全球覆盖，无需密钥"] = "Microsoft Bing satellite imagery, global, no key required",
                ["谷歌卫星"] = "Google satellite",
                ["谷歌"] = "Google",
                ["谷歌卫星影像，清晰度高，大陆需代理访问"] = "Google satellite imagery, high detail; a proxy may be needed in mainland China",
                ["Esri 高清影像"] = "Esri World Imagery",
                ["Esri World Imagery，全球高清，无需密钥"] = "Esri World Imagery, global, no key required",
                ["天地图·影像"] = "Tianditu imagery",
                ["天地"] = "Tianditu",
                ["国家测绘局影像，国内高清、需申请密钥"] = "National mapping imagery, high detail in China; key required",
                ["高德卫星"] = "Amap satellite",
                ["高德"] = "Amap",
                ["高德卫星影像，国内访问快，无需密钥"] = "Amap satellite imagery, fast in China, no key required",
                ["腾讯卫星"] = "Tencent satellite",
                ["腾讯"] = "Tencent",
                ["腾讯卫星影像，国内访问快，无需密钥"] = "Tencent satellite imagery, fast in China, no key required",
                ["Esri 增强影像，城市区域更清晰，无需密钥"] = "Esri enhanced imagery, clearer in cities, no key required",
                ["Sentinel-2 无云"] = "Sentinel-2 cloudless",
                ["欧洲航天局哨兵二号无云影像，全球免密钥，分辨率较低"] = "ESA Sentinel-2 cloudless imagery, global, no key, lower resolution",
                ["Mapbox 卫星"] = "Mapbox satellite",
                ["Mapbox 卫星影像，画质好，需要免费访问令牌"] = "Mapbox satellite imagery, good quality, free token required",
                ["Yandex 卫星"] = "Yandex satellite",
                ["Yandex 卫星影像，部分地区覆盖较好，无需密钥"] = "Yandex satellite imagery, good coverage in some regions, no key required",

                // Builder and validation
                ["请选择一个图源。"] = "Select an imagery source.",
                ["请至少选择一个 AFS 层级。"] = "Select at least one AFS level.",
                ["请选择输出目录。"] = "Choose an output folder.",
                ["选择范围内没有可生成的区块。"] = "There are no tiles to generate in the selected area.",
                ["准备中：共 {0} 个区块 · {1} 个转换线程 · {2} 个下载线程…"] =
                    "Preparing: {0} tiles · {1} converters · {2} download threads…",
                ["跳过已完成 {0}"] = "Skipped {0}",
                ["下载 {0}"] = "Downloading {0}",
                ["失败 {0}：{1}"] = "Failed {0}: {1}",
                ["生成地形高程：0 / {0}…"] = "Generating elevation: 0 / {0}…",
                ["高程 {0} / {1}：{2}"] = "Elevation {0} / {1}: {2}",
                ["高程 {0}：{1}"] = "Elevation {0}: {1}",
                ["安装高程到 Aerofly FS 4…"] = "Installing elevation into Aerofly FS 4…",
                ["安装到 Aerofly FS 4…"] = "Installing into Aerofly FS 4…",
                ["打包 FSG _images.tme…"] = "Packing FSG _images.tme…",
                ["复制到 FSG 目录失败：{0}"] = "Copying to the FSG folder failed: {0}",
                ["完成：{0} 个区块，{1:F1} MB，用时 {2:F0} 秒{3}"] =
                    "Done: {0} tiles, {1:F1} MB in {2:F0}s{3}",
                ["，{0} 个失败"] = ", {0} failed",

                // Pickers and cache
                ["选择输出目录"] = "Choose output folder",
                ["选择 Aerofly FS 4 的 scenery\\\\images 目录"] = "Choose the Aerofly FS 4 scenery\\\\images folder",
                ["选择手机 FSG 的 files 目录（可留空）"] = "Choose the mobile FSG files folder (optional)",
                ["选择原图输出目录"] = "Choose the source-image output folder",
                ["选择瓦片缓存目录"] = "Choose the tile cache folder",
                ["缓存已清空"] = "Cache cleared",
                ["缓存已清空：{0}"] = "Cache cleared: {0}",
                ["清理缓存失败：{0}"] = "Clearing cache failed: {0}",
                ["清空缓存失败：{0}"] = "Clearing cache failed: {0}",

                // Errors
                ["发生了一个未处理的错误：\n\n{0}\n\n{1}"] = "An unhandled error occurred:\n\n{0}\n\n{1}",
            };
    }

    /// <summary>Binding source for the <c>Tr</c> markup extension.</summary>
    public sealed class LocalizationSource : INotifyPropertyChanged
    {
        public static LocalizationSource Instance { get; } = new LocalizationSource();

        private LocalizationSource()
        {
        }

        public int Version
        {
            get { return 0; }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        internal void RaiseChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
        }
    }
}
