# Сборка MeloDesk 0.19.2 для Windows x64

Исходники соответствуют приложению из этого выпуска. Это снимок, подмодули уже включены в Native/; git submodule update для него не требуется. История и личные файлы исходной рабочей машины исключены. В этой сессии повторная сборка на полностью чистом компьютере не выполнялась.

## Нативный клиент

Требования и подробности описаны в Native/docs/building-win.md: Visual Studio 2026 C++ Build Tools, MSVC v143 14.44, Windows SDK 10.0.26100.0, Python 3.10, Git и инструменты, которые загружает upstream prepare.py. Запускайте команды в x64 Native Tools Command Prompt, инициализированном с -vcvars_ver=14.44. CMake и Ninja должны быть в PATH; подготовка внешних библиотек занимает значительное время.

В корне распакованных исходников выполните:

```bat
python Native\Telegram\build\prepare\prepare.py skip-release
cd Native\Telegram
python configure.py x64 -G "Ninja Multi-Config" -C ..\..\packaging\api-environment.cmake
cmake --build ..\out --config Debug --target Telegram --parallel 2
```

Перед configure задайте в окружении MELODESK_API_ID и MELODESK_API_HASH своей зарегистрированной Telegram API application. Не добавляйте значения в исходники. CMakeCache.txt и build logs могут содержать их и не должны публиковаться. Конфигурация Debug использует /O2 /Ob2, /DEBUG /OPT:REF /OPT:ICF. Результат Native/out/Debug/Telegram.exe при упаковке переименовывается в MeloDesk.exe. Native Release для данного выпуска не используется.

Внешние Libraries/ и ThirdParty/ создаются рядом с Native/. Точные исходники и версии их зависимостей задаёт Native/Telegram/build/prepare/prepare.py; это часть соответствующих сборочных исходников. См. также Native/LEGAL и Native/docs/api_credentials.md.

## Музыкальный модуль и дистрибутив

Установите .NET SDK 8 и Inno Setup 6. Запустите из корня:

```powershell
./Music/setup-tools.ps1
python ./packaging/setup-proxy.py
dotnet publish ./Music/MusicHost/MeloDesk.MusicHost.csproj -c Release -r win-x64 --self-contained true -o ./artifacts/runtime/MusicHost
New-Item -ItemType Directory ./artifacts/runtime -Force
Copy-Item ./Native/out/Debug/Telegram.exe ./artifacts/runtime/MeloDesk.exe
Copy-Item ./Music/Assets/MeloDesk.ico ./artifacts/runtime/MeloDesk.ico
Copy-Item ./LICENSE,./LEGAL,./THIRD-PARTY-NOTICES.md,./README.md ./artifacts/runtime/
Copy-Item ./packaging/promotions.json ./artifacts/runtime/promotions.json
New-Item -ItemType Directory ./artifacts/runtime/MusicHost/licenses -Force
Copy-Item ./Licenses/* ./artifacts/runtime/MusicHost/licenses/ -Force
& 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe' ./packaging/MeloDesk.iss
```

setup-proxy.py требует Python с pip и загружает Python Embedded 3.13.17 с проверкой контрольной суммы, а также wheel-пакеты для CPython 3.13/Windows x64 из Proxy/requirements.txt. Не используйте персональный каталог установленного клиента как шаблон дистрибутива.

Для проверки музыкального кода: dotnet run --project Music/tests/MeloDesk.RegressionTests/MeloDesk.RegressionTests.csproj. Микшерные проверки требуют Windows. В выпущенном пакете сохранён MusicHost 0.19.0: для него ранее прошли47проверок; нативная часть0.19.2 успешно собрана. Визуальная проверка и проверка запуска на другом компьютере остаются ручными.

## Зависимости и лицензии

Proxy/ содержит исходники TgWsProxy 1.11.2 и MIT-лицензию. Остальные пакеты указаны в requirements.txt; их исходники доступны по тем же закреплённым версиям на PyPI. MusicHost восстанавливает версии NuGet из csproj. Архив исходников TagLibSharp и его лицензия включены в Licenses/. Ссылки на исходники LibVLC, LibVLCSharp, yt-dlp, Deno и .NET приведены в THIRD-PARTY-NOTICES.md. Перед публикацией своих изменений сохраните эти сведения и соответствующие исходники.
