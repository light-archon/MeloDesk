# Сторонние компоненты MeloDesk

- SkiaSharp и Skottie 4.153.1 — https://github.com/mono/SkiaSharp/releases/tag/v4.153.1, MIT; нативный Skia использует BSD и лицензии перечисленных в проекте зависимостей. Используются для WebP и TGS.
- Распакованный yt-dlp_win.zip 2026.10.26: SHA-256 FD51B212CF8380C7A52EE08C0D6BE7F3C59B2F2C832EAF7B45ACFAE6EAB57419. Официальный дистрибутив из того же релиза yt-dlp.

- TDLib 1.8.67 — https://github.com/tdlib/td, Boost Software License 1.0. Windows сборка: NuGet `tdlib.native.win-x64` от проекта https://github.com/ForNeVeR/tdlib.native.
- LibVLCSharp 3.10.2 и LibVLC Windows 3.0.24 — https://code.videolan.org/videolan/LibVLCSharp и https://www.videolan.org/vlc/libvlc.html. Лицензии LGPL; нативные библиотеки поставляются отдельно и могут быть заменены. Исходники LibVLC: https://code.videolan.org/videolan/vlc/-/tree/3.0.24.
- yt-dlp 2026.10.26 — https://github.com/yt-dlp/yt-dlp/releases/tag/2026.10.26. Лицензия основного проекта Unlicense; состав зависимостей и условия официального исполняемого файла описаны в проекте. Текст основной лицензии: `tools/yt-dlp-LICENSE.txt`.
- Deno 2.9.8 — https://github.com/denoland/deno/releases/tag/v2.9.8, MIT. Текст лицензии: `tools/deno-LICENSE.md`.
- Microsoft .NET и System.Security.Cryptography.ProtectedData — https://github.com/dotnet/runtime, MIT.

Контрольные суммы загрузок SHA-256:

```
yt-dlp.exe 2026.10.26
f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d

deno-x86_64-pc-windows-msvc.zip 2.9.8
cba84b26e0df5ae7f74fc66b86a95b866872a86292ebd84fa1e959927edd93bf
```

Обе загрузки проверены по контрольным суммам соответствующих релизов.
