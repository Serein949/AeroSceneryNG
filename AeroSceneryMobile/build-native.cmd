@echo off
setlocal
set NDK=%LOCALAPPDATA%\Android\Sdk\ndk\27.2.12479018
if not exist "%NDK%" (
  echo NDK not found at %NDK%
  echo Install with: sdkmanager "ndk;27.2.12479018"
  exit /b 1
)
set BIN=%NDK%\toolchains\llvm\prebuilt\windows-x86_64\bin
pushd "%~dp0"
if not exist lib\arm64-v8a mkdir lib\arm64-v8a
if not exist lib\x86_64 mkdir lib\x86_64

call "%BIN%\aarch64-linux-android24-clang++.cmd" -O3 -fPIC -shared -DNDEBUG ^
  -Ietcpak-src\etcpak-master native\asng_etcpak.cpp ^
  etcpak-src\etcpak-master\ProcessRGB.cpp etcpak-src\etcpak-master\Tables.cpp ^
  -o lib\arm64-v8a\libetcpak.so -static-libstdc++ "-Wl,-z,max-page-size=16384"
if errorlevel 1 exit /b 1

call "%BIN%\x86_64-linux-android24-clang++.cmd" -O3 -fPIC -shared -DNDEBUG ^
  -Ietcpak-src\etcpak-master native\asng_etcpak.cpp ^
  etcpak-src\etcpak-master\ProcessRGB.cpp etcpak-src\etcpak-master\Tables.cpp ^
  -o lib\x86_64\libetcpak.so -static-libstdc++ "-Wl,-z,max-page-size=16384"
if errorlevel 1 exit /b 1

echo Built lib\arm64-v8a\libetcpak.so and lib\x86_64\libetcpak.so
popd
