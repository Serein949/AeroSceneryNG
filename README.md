# AeroScenery NG

AeroFly FS 4 / AeroFly FS Global 地景生成器，用 C# 与 WPF 重写。目标是把原来「装 Java、开好几个窗口、手动点几百个区块、等官方 geoconvert」的流程，压缩成：**在地图上拖一个框 → 选级别 → 点开始**。

- 现代浅色 / 深色界面，无第三方 UI 框架，配置页与任务页带过渡动画
- 中英文界面即时切换，设置里可选语言并自动记住
- 矩形批量框选 + 网格单击微调，已制作区块自动用绿色标记
- 一次生成多个 AFS 级别，每个级别可单独指定影像细节（ZL）
- 同时输出 FS4（桌面 DXT1）与 FSG（手机 ETC2），或只出其中一种
- 64 线程下载（可调到 128）+ HTTP/2 + HTTP Range 断点续传；TTC 编码多线程
- 缓存目录可迁移、容量上限可设，超限自动清理最旧瓦片
- 官方 TTC 容器、zlib / LZHAM 两种载荷、自动打包 `_images.tme`
- 可选额外保存未压缩的 2048×2048 PNG 原图，方便外部修图
- 内置约 10 个图源，支持天地图 / Mapbox 密钥与自定义 XYZ
- CLI 可脚本化，方便批量出图

---

## 1. 运行

需要 Windows 10/11 x64 与 .NET 10 桌面运行时。自己编译：

```powershell
dotnet build AeroSceneryNG.csproj -c Release
.\bin\Release\net10.0-windows\AeroSceneryNG.exe
```

发布成不依赖运行时的单文件：

```powershell
dotnet publish AeroSceneryNG.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

输出在 `bin\Release\net10.0-windows\win-x64\publish\`。

首次启动会在 `%LOCALAPPDATA%\AeroSceneryNG\tilecache` 建影像缓存；如果该目录不可写（受限账户、沙箱），会依次退到 `%TEMP%\AeroSceneryNG\tilecache` 和程序目录下的 `tilecache`。

---

## 2. 界面流程

1. **选择制作区域**：左键拖拽 = 矩形批量框选；单击 = 按当前微调网格（L9–L14，默认 L12）加/减一格。卡片会即时显示经纬度范围、网格格数、实际 AFS 瓦片数和预计体积。地图上绿色覆盖表示输出目录里已经做过的区块，方便续做和补漏。
2. **影像图源与目标**：选图源；目标选 `电脑 FS4`、`手机 FSG` 或 `两者都要`。目标下方有 **「同时生成地形高程（DEM，电脑版）」** 开关，勾选后整套地景会自带 Level 10 高程。
3. **输出级别**：勾选需要的 AFS 级别（9–15）。每一行右侧的 `− / 自动 / +` 是该级别单独的影像细节；点中间「自动」恢复 `级别 + 3`。级别应当连续，不要跳级。选 `手机 FSG` 或 `两者都要` 时，Level 15 会自动置灰并取消勾选。
4. **统一影像细节**：不想逐级设置时，用这个滑块统一覆盖；拖动滑块会按 `ZL − 3` 自动勾选输出级别，并保留从当前最低级别到最高级别的连续区间（例如原 L12–L14 拖到 ZL18 → L12–L15）。每一级右侧会同步显示实际使用的 Z 值。
5. **生成选项**：地景名、下载线程、跳过已存在瓦片、LZHAM、自动 TME、输出目录、生成后安装。
6. 点 **开始生成**。界面会自动切到 **任务详情** 页：左侧是总进度、四步阶段（准备 / 下载与转换 / 安装与打包 / 完成）、下载与转换统计、实时日志；右侧是任务信息和 `打开输出目录 / 取消任务 / 重新生成 / 返回配置`。下载、转换、安装仍然在同一个后台任务里连续执行，只是展示分开，不再停留在配置页。

### 设置与影像处理

「设置」里除了天地图 / Mapbox 密钥和目录，还恢复了原版 AeroScenery 的影像处理项：

- **外观**：深色 / 浅色主题即时切换并写入 `%APPDATA%\AeroSceneryNG\settings.json`，下次启动保持。
- **语言 / Language**：设置里可在中文与 English 之间即时切换，界面、任务页、日志、提示和错误信息都会同步翻译；选择会写入设置文件，下次启动保持。图源名称与说明也会跟着切换。
- **影像处理**：亮度、对比度、饱和度、锐度、红 / 绿 / 蓝通道微调，作用在每张 2048×2048 拼接图上，再编码为 TTC。默认关闭，关闭时输出与原始影像完全一致。
- **水面颜色统一**：按颜色识别偏蓝 / 青 / 深色的水体，统一混合到指定颜色（默认 `#1B4F8A`），并做连通区域面积过滤，只处理湖泊、河流等大片水域，避免把蓝色屋顶和停车场地标线也染蓝。这是近似处理，不是 OSM 水面掩膜；遇到蓝色建筑较多的地方可降低强度或关闭。
- **附加输出**：可勾选「同时保存未处理原图（PNG）」，把每块 2048×2048 的拼接图在调色/编码前另存到指定目录（默认 `文档\AeroSceneryNG\originals\<地景名>\L<级别>\`）。默认关闭，不占额外空间。
- **缓存管理**：缓存目录默认在 `%LOCALAPPDATA%\AeroSceneryNG\tilecache`，可改到其他盘；「缓存上限」默认 4096 MB（0 = 不限制），勾选自动清理后，启动和每次生成结束都会删除最旧的瓦片直到低于上限。反馈的「C 盘异常占用」通常是瓦片缓存长期累积，把目录换到 D 盘并设上限即可。

图源说明：

| id | 名称 | 密钥 | 备注 |
| --- | --- | --- | --- |
| `bing` | 必应卫星 | 否 | 全球覆盖稳定，默认 |
| `google` | 谷歌卫星 | 否 | 国内可能需要代理 |
| `esri` | Esri 高清影像 | 否 | World Imagery |
| `esri_clarity` | Esri Clarity | 否 | 城市区域更清晰 |
| `sentinel2` | Sentinel-2 无云 | 否 | 全球免密钥，分辨率较低 |
| `tianditu_img` | 天地图·影像 | 是 | 国内合规图源，需申请 key |
| `amap_sat` | 高德卫星 | 否 | 国内访问快 |
| `tencent_sat` | 腾讯卫星 | 否 | 国内访问快 |
| `mapbox_sat` | Mapbox 卫星 | 是 | 画质好，需 access token |
| `yandex_sat` | Yandex 卫星 | 否 | 部分地区细节好 |

天地图与 Mapbox 的密钥在「设置」里填写。

---

## 3. 输出结构

### FS4（桌面）

```
<输出目录>\fs4\
  aeroscenery.json
  scenery\images\
    map_09_d280_9f00\          ← 9 级网格目录
      map_12_d2c0_9f30.ttc
      map_13_d2c0_9f30.ttc
      ...
```

安装：把 `map_09_*` 目录整个复制到

```
%USERPROFILE%\Documents\Aerofly FS 4\scenery\images\
```

即最终为 `...\scenery\images\map_09_d280_9f00\*.ttc`。勾选「生成后安装」会自动完成这一步。

### FSG（手机）

```
<输出目录>\fsg\
  fsg_scenery_BeijingDemo\
    fsg_scenery_BeijingDemo_images\
      scenery\images\map_09_d280_9f00\*.ttc
<输出目录>\BeijingDemo_images.tme
```

安装：把 `BeijingDemo_images.tme` 复制到

```
/storage/emulated/0/Android/data/com.aerofly.aeroflyfsg1/files/
```

或把 `fsg_scenery_BeijingDemo_images` 文件夹放进同一位置（TME 本质就是 ZIP）。

> 手机端只认 **ETC2**，桌面端只认 **DXT1**；两者不能混用。FSG 官方支持到 AFS Level 14（约 ZL17），更高精度的瓦片游戏不会加载。这也是旧工具在手机上「最多到 ZL16」的根本原因之一：旧流程既可能写错纹理格式，又常常把 Level 14 以上的图塞给手机。本工具在 FSG 目标下会把级别钳制到 14、影像细节钳制到 17。

---

## 4. 命令行

```powershell
dotnet build Cli\AeroSceneryNG.Cli.csproj -c Release

# 北京二环，FS4 + FSG，级别 12/13/14，统一 ZL15
dotnet run --project Cli\AeroSceneryNG.Cli.csproj -c Release -- `
  --bbox 39.93,39.89,116.43,116.39 `
  --levels 12,13,14 --source esri --zoom 15 `
  --target both --out demoout --name BeijingDemo `
  --cache tilecache --workers 8 --download-threads 64

# 分级别指定细节：L12→ZL13，L14→ZL17
... --level-zooms 12:13,14:17

# 校验输出
dotnet run --project Cli\AeroSceneryNG.Cli.csproj -c Release -- --verify demoout

# 把某个 TTC 解成 PNG 看画质
... --png demoout\fs4\scenery\images\map_09_d280_9f00\map_14_d2c4_9f30.ttc

# 只跑编码基准
... --bench

# 打开影像处理并统一水面颜色
... --tune --brightness 8 --saturation 10 --water --water-color "#1B4F8A"
```

主要参数：

- `--bbox 北,南,东,西`：区域
- `--levels 12,13,14`：AFS 级别，逗号分隔
- `--level-zooms 12:13,14:17`：每个级别的影像 ZL；优先级高于 `--zoom`
- `--target fs4|fsg|both`；`both` 共用手机端上限（Level 14 / 影像 Z17）
- `--ttc-codec zlib|lzham`：默认 `lzham`；`zlib` 为兼容模式
- `--workers`：TTC 编码线程；`--download-threads`：下载并发
- `--force`：不跳过已存在的瓦片
- `--no-tme`：不打包 FSG 的 `_images.tme`
- `--install --fsg-dir <path>`：生成后复制
- `--tune`：启用影像处理；也可只给下面任意一项
- `--brightness --contrast --saturation --sharpness`：−100..100
- `--red --green --blue`：通道增益，−100..100
- `--water`：统一水体颜色；`--water-color #RRGGBB`、`--water-strength 0..100`
- `--originals`：额外保存未调色的 2048×2048 PNG 原图；`--originals-dir <path>` 指定目录
- `--download-threads`：默认 64，最大 128；实际速度主要受图源服务器和本地网络限制

---

## 5. 性能

16 核测试机，2048×2048、12 级 mip、LZHAM 载荷：

| 编码 | 1 线程 | 8 线程 | 16 线程 |
| --- | ---: | ---: | ---: |
| DXT1（FS4） | 581 ms/瓦片 | 116 ms/瓦片 | 81 ms/瓦片 |
| ETC2（FSG） | 7909 ms/瓦片 | 1613 ms/瓦片 | 1284 ms/瓦片 |

真实网络端到端（北京，11 个区块，L12–14，DXT1 + ETC2，含下载与 TME）：**29 秒，46 MB**。

ETC2 编码明显比 DXT1 重，这是手机纹理压缩本身的代价；只做 FS4 时速度非常快。低配机器可以在界面里把下载线程降到 4–8，编码线程保持默认（`CPU/2`，2–6）。

---

## 6. 格式与兼容性

TTC 是 256 字节头 + `tmcompress` 块：

- 头：magic `0x303A`、version `0x100`、level、payload 长度、原始长度、宽高、mip 数、格式号
- 格式号：`10` DXT1、`12` DXT5、`21` ETC2
- 载荷：64 字节 `tmcompress` 头（magic `0xA810BEF4`）+ LZHAM 或 zlib 流

默认使用 **LZHAM**，因为同样画质下体积比 zlib 小约 10%，且容器结构与官方 `tmcompress` 块一致；生成的 LZHAM TTC 已用独立的 AeroflyConv 解码器验证可读。参考实现（chrispriv 的 aeroscenery-mod）写的是 zlib，如果遇到极老版本不认 LZHAM，可在界面取消「LZHAM 压缩」或加 `--ttc-codec zlib` 切换。

官方商店地景里的 `map_*.ttc` 使用 Basis Universal（格式号 `0x12345671/72`），不是 DXT/ETC2。这类文件本工具只做识别、不做解码；自己生成的地景不依赖它们。

---

## 7. 项目结构

```
Core/        AFS 网格与投影、图源、下载缓存、拼接、TTC 写出、TME、任务调度
Ttc/         DXT1/DXT5/ETC2 编码器、LZHAM、TTC 读写与解码
Tth/         AFS 高程瓦片读写、Terrarium DEM 采样
UI/          WPF 界面、地图控件、视图模型
Theme/       Controls.xaml + Dark.xaml + Light.xaml
Cli/         命令行入口
docs/        效率与格式笔记
```

关键文件：

- `Core\Afs2Grid.cs` — AFS 世界网格、瓦片命名 `map_LL_XXXX_YYYY`
- `Core\MosaicComposer.cs` — 把 Web Mercator 影像重投影到 AFS 瓦片
- `Core\SceneryBuilder.cs` — 多级别、双目标、断点跳过、安装、TME
- `Core\ImageryCache.cs` — 多线程下载、Range 续传、磁盘/内存缓存
- `Ttc\TtcWriter.cs` / `Ttc\TtcReader.cs` — 容器读写
- `Ttc\EtcEncoder.cs` — ETC2 编码
- `Tth\TthFile.cs` / `Tth\TthWriter.cs` — 官方高程瓦片 `maph_*.tth` 读写
- `Tth\TerrariumDem.cs` — 从免密钥 Terrarium DEM 生成高程网格

---

## 8. 高程（实验）

AFS4 的高程瓦片是 `scenery\elevation\maph_LL_XXXX_YYYY.tth`，格式已从官方文件反推并验证：

- 256 字节头：magic `0x0009FBF1`，层级、数据长度、原始长度、最低/最高海拔、1028×1028 网格、2 字节采样
- 60 字节 `tmcompress` 块头 + LZHAM 载荷
- 2,113,568 字节解压后是 1028×1028 的 **uint16**，按 `-500..9000 m` 线性归一化
- 1024×1024 主体，四周各 2 像素裙边；**第 0 行在南边**（和影像瓦片相反）

命令行原型：

```powershell
AeroSceneryNG.Cli.exe --tth-gen --level 10 --grid 843,637 --out maph_10_d2c0_9f40.tth
AeroSceneryNG.Cli.exe --tth maph_10_d2c0_9f40.tth --tth-png
```

`--grid` 是 AFS 网格索引；例如北京 `843,637` 对应 `maph_10_d2c0_9f40`。用 Terrarium DEM（免密钥）生成后与官方同瓦片比较，平均误差约 **0.57 m**，结构、压缩比一致（官方 839,878 B，生成 848,212 B）。

图形界面已接入：在「2 影像图源与目标」目标选择下方勾选 **同时生成地形高程（DEM，电脑版）**，生成影像时会按选区自动生成 `output\fs4\scenery\elevation\maph_10_*.tth`，和 `output\fs4\scenery\images` 组成一套完整 FS4 地景；勾选「生成后安装」时会复制到 `文档\Aerofly FS 4\scenery\elevation`。命令行对应 `--elevation`。高程瓦片固定使用 Level 10（与官方自带数据一致），与影像级别无关。

---

## 9. 许可与声明

本项目原创代码采用 **MIT License**，见 `LICENSE`。第三方组件、算法和商标说明见 `THIRD-PARTY-NOTICES.md`，其中最重要的是：

- `SixLabors.ImageSharp 3.1.5` 使用 Six Labors Split License，不适用本项目的 MIT 许可。
- LZHAM 算法来自 Rich Geldreich 的公有领域实现，C# 移植来自作者的 AeroflyConv 项目。
- ETC2 编码器的 EAC 布局与表搜索参考了 etcpak（BSD-3-Clause）。

本项目是**非官方工具**，与 AeroFly / Aerofly FS 的开发商和发行商无关联。仓库不包含任何官方游戏 `.ttc` / `.tth`、图源缓存、API 密钥或下载的影像。使用者需自行遵守各图源的服务条款。

---

## 10. Android 版

仓库同时包含 Android 客户端源码（`AeroSceneryMobile/`），用于在手机上直接生成 FSG 地景：

- 版本 **0.5**，`arm64-v8a` / `x86_64`，Android 7.0+。
- 原生 NEON ETC2 编码（etcpak），实测 2 个 L9 瓦片热缓存约 1 秒。
- APK 见 Releases 的 `android-v0.5`，构建与安装说明见 [`ANDROID.md`](ANDROID.md)。
- 签名 keystore 不包含在仓库中。

