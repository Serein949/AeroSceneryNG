# AeroScenery NG 效率与格式报告（v2.31）

## 一、转换速度

测试机 16 核；每块纹理 2048×2048、12 级 mip、LZHAM 载荷。

| 编码 | 1 线程 | 8 线程 | 16 线程 | 输出体积 |
| --- | --- | --- | --- | --- |
| DXT1（FS4） | 581 ms | 116 ms | 81 ms | 约 1.7 MB |
| ETC2（FSG） | 7909 ms | 1613 ms | 1284 ms | 约 1.0 MB |

原版官方 geoconvert 约 10.5 秒/块，且必须前台等待。当前 DXT1 快 90–130 倍，ETC2 快 6.5–8 倍。ETC2 仍是主要瓶颈，v2.31 已把块搜索从 8 表×4 候选×2 轮降到 4 表×2 候选×1 轮，画质仍可用。

## 二、下载速度

v2.31 默认 64 下载线程，设置上限 128；HttpClient 使用 SocketsHttpHandler、HTTP/2 多连接、每服务器 256 连接。

实测（同一网络）：

- Esri 1 块 L9 冷启动：1.4 MB / 13.9 秒，约 100 KB/s；暖缓存 3.6–3.8 秒。
- 高德 12 块 L9：下载 21.2 MB / 14.9 秒，约 1.4 MB/s；输出 23.8 MB。
- 把线程从 64 提到 128 没有提升（15.9 秒）。

结论：20 MB/s 的目标在测试网络下受图源服务器和出口带宽限制，不是客户端并发不足。程序已把并发和连接池放到较高水平；实际速度取决于所选图源和网络。

## 三、TTC / 压缩

- TTC 头 256 字节：magic `0x303A`、version `0x100`、level、payload 长度、原始长度、宽高、mip 数、格式 ID（DXT1=10、DXT5=12、ETC2=21）。
- 载荷支持 LZHAM（默认，`tmcompress` 64 字节头 + LZHAM 块）、zlib（兼容参考实现）、原样存储。
- 官方商店里的 Basis 瓦片格式 ID 是 `0x12345671`，不是 DXT/ETC2；本工具不生成 Basis，只生成游戏可直接读取的 DXT1/ETC2。
- DXT1/ETC2 是固定码率块压缩，2048² + 12 级 mip 的原始体积固定为 2,796,216 字节；LZHAM/zlib 只能在此基础上再压 10%–40%，不能拿 Basis 的 8.6× 来要求 DXT/ETC2。

## 四、高程 TTH

- 1028×1028、16 位归一化样本，`米 = min + raw/65535 × (max-min)`。
- 1024² 主体 + 每边 2 像素裙边，第 0 行是南边。
- 数据偏移 320，LZHAM 压缩；北京 L10 与官方对比平均误差 0.57 m。

## 五、复现

```powershell
dotnet run --project Cli\AeroSceneryNG.Cli.csproj -c Release -- --bench
dotnet run --project Cli\AeroSceneryNG.Cli.csproj -c Release -- `
  --bbox 39.93,39.89,116.43,116.39 --levels 12,13,14 `
  --source esri --zoom 15 --target both --out demoout --name BeijingDemo `
  --cache tilecache --download-threads 64
```
