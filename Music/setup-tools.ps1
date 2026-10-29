$ErrorActionPreference = 'Stop'
$toolDirectory = Join-Path $PSScriptRoot 'tools'
New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null

$youtubeTool = Join-Path $toolDirectory 'yt-dlp.exe'
Invoke-WebRequest 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe' -OutFile $youtubeTool
$youtubeHash = (Get-FileHash -LiteralPath $youtubeTool -Algorithm SHA256).Hash.ToLowerInvariant()
if ($youtubeHash -ne 'f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d') { throw 'yt-dlp checksum mismatch' }

$fastArchive = Join-Path $toolDirectory 'yt-dlp_win.zip'
Invoke-WebRequest 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp_win.zip' -OutFile $fastArchive
if ((Get-FileHash $fastArchive).Hash -ne 'FD51B212CF8380C7A52EE08C0D6BE7F3C59B2F2C832EAF7B45ACFAE6EAB57419') { throw 'yt-dlp folder bundle checksum mismatch' }
Expand-Archive $fastArchive (Join-Path $toolDirectory 'yt-dlp-fast') -Force
Remove-Item -LiteralPath $fastArchive

$denoArchive = Join-Path $toolDirectory 'deno-runtime.zip'
Invoke-WebRequest 'https://github.com/denoland/deno/releases/download/v2.9.8/deno-x86_64-pc-windows-msvc.zip' -OutFile $denoArchive
$denoHash = (Get-FileHash -LiteralPath $denoArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($denoHash -ne 'cba84b26e0df5ae7f74fc66b86a95b866872a86292ebd84fa1e959927edd93bf') { throw 'Deno checksum mismatch' }
Expand-Archive -LiteralPath $denoArchive -DestinationPath $toolDirectory -Force
Remove-Item -LiteralPath $denoArchive
Write-Output 'yt-dlp and Deno are ready.'
