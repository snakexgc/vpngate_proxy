param([switch]$SkipRuntime, [switch]$SkipTests, [string]$ProxyUrl = 'http://127.0.0.1:10808')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$previousHttpsProxy = $env:HTTPS_PROXY
$previousHttpProxy = $env:HTTP_PROXY
Push-Location $projectRoot
try {
    if (!$SkipRuntime) { & (Join-Path $PSScriptRoot 'setup-runtime.ps1') -ProxyUrl $ProxyUrl }
    if ($ProxyUrl) { $env:HTTPS_PROXY = $ProxyUrl; $env:HTTP_PROXY = $ProxyUrl }
    dotnet build vpngate_proxy.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (!$SkipTests) {
        & '.\tests\VPNGate.SelfTest\bin\Release\net10.0-windows\VPNGate.SelfTest.exe' --output 'artifacts\unit-tests.json'
        if ($LASTEXITCODE -ne 0) { throw 'Self-test failed.' }
    }
    dotnet publish src\VPNGate.Desktop\VPNGate.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o dist\vpngate_proxy --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md -Destination dist\vpngate_proxy\README.md
    Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination dist\vpngate_proxy\THIRD-PARTY-NOTICES.md
    Copy-Item -LiteralPath LICENSE -Destination dist\vpngate_proxy\LICENSE
    $runtimeVersion = ((Get-Content 'src\VPNGate.Desktop\bin\Release\net10.0-windows\win-x64\vpngate_proxy.runtimeconfig.json' -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $netCorePackage = Join-Path $nugetRoot "microsoft.netcore.app.runtime.win-x64\$runtimeVersion"
    $desktopPackage = Join-Path $nugetRoot "microsoft.windowsdesktop.app.runtime.win-x64\$runtimeVersion"
    Copy-Item -LiteralPath (Join-Path $netCorePackage 'LICENSE.TXT') -Destination dist\vpngate_proxy\runtime\LICENSE-dotnet.txt
    Copy-Item -LiteralPath (Join-Path $netCorePackage 'THIRD-PARTY-NOTICES.TXT') -Destination dist\vpngate_proxy\runtime\THIRD-PARTY-NOTICES-dotnet.txt
    Copy-Item -LiteralPath (Join-Path $desktopPackage 'LICENSE') -Destination dist\vpngate_proxy\runtime\LICENSE-wpf.txt
    Compress-Archive -Path dist\vpngate_proxy -DestinationPath dist\vpngate_proxy-win-x64.zip -Force
    Write-Host 'Executable: dist\vpngate_proxy\vpngate_proxy.exe'
    Write-Host 'Portable archive: dist\vpngate_proxy-win-x64.zip'
} finally { $env:HTTPS_PROXY = $previousHttpsProxy; $env:HTTP_PROXY = $previousHttpProxy; Pop-Location }
