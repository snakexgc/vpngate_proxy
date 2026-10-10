param([string]$OutputDirectory = 'artifacts/ui-log-regression')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $projectRoot 'src/VPNGate.Desktop/bin/Release/net10.0-windows/vpngate_proxy.exe'
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
$process = Start-Process -FilePath $exe -ArgumentList @('--ui-smoke', ('"' + $output + '"'), '--log-ui') -WindowStyle Hidden -PassThru
try {
    if (!$process.WaitForExit(45000)) { throw 'GUI did not respond within 45 seconds.' }
    $report = Get-Content -LiteralPath (Join-Path $output 'checks.json') -Raw | ConvertFrom-Json
    if (!$report.success) { throw "GUI log regression failed: $($report.error)" }
    Write-Host "GUI remained responsive during $($report.replayedLines) log lines; maximum input delay: $([Math]::Round($report.maxUiGapMs, 1)) ms."
} finally {
    if (!$process.HasExited) {
        [void]$process.CloseMainWindow()
        if (!$process.WaitForExit(5000)) { $process.Kill() }
    }
    $process.Dispose()
}
