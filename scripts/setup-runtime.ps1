param([string]$ProxyUrl = 'http://127.0.0.1:10808')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$cacheRoot = Join-Path $projectRoot '.cache\downloads'
$runtimeRoot = Join-Path $projectRoot 'runtime'
New-Item -ItemType Directory -Force -Path $cacheRoot,$runtimeRoot | Out-Null

function Download-Verified([string]$Url, [string]$Destination, [string]$Hash) {
    if ((Test-Path -LiteralPath $Destination) -and (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Hash) { return }
    $curlArgs = @('--fail','--silent','--show-error','--location','--retry','2','--connect-timeout','20','--max-time','240')
    if ($ProxyUrl) { $curlArgs += @('--proxy', $ProxyUrl) }
    & curl.exe @curlArgs $Url --output $Destination
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $Url" }
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Hash) { throw "SHA256 mismatch: $Destination" }
}

$coreArchive = Join-Path $cacheRoot 'sing-box-1.14.2-windows-amd64.zip'
Download-Verified 'https://github.com/SagerNet/sing-box/releases/download/v1.14.2/sing-box-1.14.2-windows-amd64.zip' $coreArchive 'c2d8bfff918755808781dfdeeb8581b6c91eb3a243d9a7b55483cfc0c0684d32'
$coreExtract = Join-Path $cacheRoot 'sing-box'
Expand-Archive -LiteralPath $coreArchive -DestinationPath $coreExtract -Force
$coreSource = Join-Path $coreExtract 'sing-box-1.14.2-windows-amd64'
Copy-Item -LiteralPath (Join-Path $coreSource 'sing-box.exe') -Destination (Join-Path $runtimeRoot 'sing-box.exe')
Copy-Item -LiteralPath (Join-Path $coreSource 'LICENSE') -Destination (Join-Path $runtimeRoot 'LICENSE-sing-box')

$driverArchive = Join-Path $cacheRoot 'wintun-0.14.1.zip'
Download-Verified 'https://www.wintun.net/builds/wintun-0.14.1.zip' $driverArchive '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'
$driverExtract = Join-Path $cacheRoot 'wintun'
Expand-Archive -LiteralPath $driverArchive -DestinationPath $driverExtract -Force
Copy-Item -LiteralPath (Join-Path $driverExtract 'wintun\bin\amd64\wintun.dll') -Destination (Join-Path $runtimeRoot 'wintun.dll')
Copy-Item -LiteralPath (Join-Path $driverExtract 'wintun\LICENSE.txt') -Destination (Join-Path $runtimeRoot 'LICENSE-wintun.txt')
if ((Get-AuthenticodeSignature (Join-Path $runtimeRoot 'wintun.dll')).Status -ne 'Valid') { throw 'Wintun signature validation failed.' }
Download-Verified 'https://raw.githubusercontent.com/SagerNet/sing-geosite/df6851d87aeaeb9d6a9464288b317e7ff2288d78/geosite-cn.srs' (Join-Path $runtimeRoot 'geosite-cn.srs') '7e5edaa3156918ebb732629f7b4ab3f03040b9940456438f976c2046033fc15c'
Download-Verified 'https://raw.githubusercontent.com/SagerNet/sing-geoip/7fe82a879ad2666526730c195b55a6d8d9147908/geoip-cn.srs' (Join-Path $runtimeRoot 'geoip-cn.srs') 'ebee603fdf402314b44b9f653cdcf6d9cc9c41e84e2b3515e3123c5c920a93bc'
Write-Host 'sing-box 1.14.2 and Wintun 0.14.1 are ready. No v2rayN files were modified.'
