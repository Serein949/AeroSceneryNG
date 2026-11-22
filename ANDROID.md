# AeroScenery NG — Android

Unofficial Android client for generating AeroFly FS Global (FSG) scenery on the phone.

- Current version: **0.5**
- Package: `com.aeroscenery.mobile`
- Min SDK: 24 (Android 7.0)
- ABI: `arm64-v8a` (phone), `x86_64` (emulator)
- Output: ETC2 TTC + `*_images.tme`

## Download

APK: see the repository's **Releases** page, release `v2.32` (shared with the desktop build).

- Size: 48,505,855 bytes
- SHA256: `4F6E7A871677FD95059F1EFDF50BDD7B610797D8FB62F0C43FFA5A576E5832A1`
- Signed with the project's Android release key. The keystore is intentionally **not** in this repository.

## What it does

- ESRI / Bing / Google / Gaode / Tencent / Sentinel-2 / Yandex imagery.
- Drag a rectangle on the map, choose L9–L14, generate directly on the phone.
- Downloads are resumable and cached.
- ETC2 encoding uses a native NEON `etcpak` build; zlib TTC payload.
- TME is packed automatically and can be shared to another app.

## Build

Prerequisites:

- .NET 10 SDK with the `android` workload
- Android SDK, NDK 27.2+
- JDK 17+

```powershell
dotnet build AeroSceneryMobile/AeroSceneryMobile.csproj -c Release
```

Release APK will be under `AeroSceneryMobile/bin/Release/net10.0-android/`.

For a signed release, provide your own keystore:

```powershell
dotnet build AeroSceneryMobile/AeroSceneryMobile.csproj -c Release `
  -p:AndroidKeyStore=true `
  -p:AndroidSigningKeyStore=<path-to.keystore> `
  -p:AndroidSigningKeyAlias=<alias> `
  -p:AndroidSigningKeyPass=<password> `
  -p:AndroidSigningStorePass=<password>
```

## Native ETC2 encoder

`AeroSceneryMobile/native/asng_etcpak.cpp` wraps etcpak's `CompressEtc2Rgb`.
It converts ASNG's RGBA pixels to the BGRA order etcpak expects.

Rebuild the libraries with NDK 27:

```powershell
AeroSceneryMobile/build-native.cmd
```

The prebuilt `AeroSceneryMobile/lib/arm64-v8a/libetcpak.so` and
`AeroSceneryMobile/lib/x86_64/libetcpak.so` are included for convenience.

## Install to FSG

Copy the generated `*_images.tme` to:

```
/storage/emulated/0/Android/data/com.aerofly.aeroflyfsg1/files/
```

The app keeps its working files under
`Android/data/com.aeroscenery.mobile/files/AeroScenery/`.

## Notes

- FSG supports AFS levels up to 14 and imagery zoom up to 17; this app caps there.
- The first tile of an area still has to download dozens of source images, so cold start is network-bound.
- This is a sideloaded APK and is not affiliated with AeroFly.
- ETC2 encoder: etcpak, BSD-3-Clause.
