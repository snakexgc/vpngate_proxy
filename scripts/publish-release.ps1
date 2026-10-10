param([string]$ZipPath = 'dist/vpngate_proxy-win-x64.zip')
$ErrorActionPreference = 'Stop'
foreach ($required in @('GH_TOKEN', 'GITHUB_REPOSITORY', 'GITHUB_SHA', 'GITHUB_RUN_NUMBER')) {
    if (![Environment]::GetEnvironmentVariable($required)) { throw "Missing environment variable: $required" }
}
if ($env:GITHUB_SHA -notmatch '^[0-9a-f]{40}$' -or $env:GITHUB_RUN_NUMBER -notmatch '^\d+$') { throw 'Invalid build identity.' }
[xml]$project = Get-Content 'src/VPNGate.Desktop/VPNGate.Desktop.csproj' -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part project version.' }
$zip = Get-Item -LiteralPath $ZipPath
if ($zip.Extension -ne '.zip') { throw 'Only a ZIP release asset is supported.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($required in @('vpngate_proxy/vpngate_proxy.exe', 'vpngate_proxy/runtime/sing-box.exe', 'vpngate_proxy/runtime/wintun.dll', 'vpngate_proxy/runtime/geosite-cn.srs', 'vpngate_proxy/runtime/geoip-cn.srs')) {
        if ($names -notcontains $required) { throw "ZIP is missing required content: $required" }
    }
} finally { $archive.Dispose() }
$tag = "v$version-build.$env:GITHUB_RUN_NUMBER"
$api = "https://api.github.com/repos/$env:GITHUB_REPOSITORY"
$headers = @{
    Authorization = "Bearer $env:GH_TOKEN"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'vpngate_proxy-release'
}
$release = $null
try { $release = Invoke-RestMethod -Uri "$api/releases/tags/$tag" -Headers $headers }
catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
$isDefault = $env:GITHUB_REF_NAME -eq $env:RELEASE_DEFAULT_BRANCH
if (!$release) {
    $notes = @(
        'Windows x64 portable ZIP.'
        ''
        "Commit: $env:GITHUB_SHA"
        "Workflow: $env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID"
        ''
        'Build and offline checks passed. Only the portable ZIP is attached.'
    ) -join [Environment]::NewLine
    $body = @{
        tag_name = $tag
        target_commitish = $env:GITHUB_SHA
        name = "vpngate_proxy $version (build $env:GITHUB_RUN_NUMBER)"
        body = $notes
        draft = $true
        prerelease = !$isDefault
    } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $headers -ContentType 'application/json' -Body $body
}
if ($release.target_commitish -ne $env:GITHUB_SHA) { throw 'Existing release belongs to a different commit.' }
# Rerunning replaces only this workflow's ZIP, never another asset.
foreach ($asset in $release.assets) {
    if ($asset.name -eq $zip.Name) { Invoke-RestMethod -Method Delete -Uri "$api/releases/assets/$($asset.id)" -Headers $headers | Out-Null }
}
$upload = $release.upload_url -replace '\{\?name,label\}$', ''
$uploadUri = $upload + '?name=' + [Uri]::EscapeDataString($zip.Name)
$asset = Invoke-RestMethod -Method Post -Uri $uploadUri -Headers $headers -ContentType 'application/zip' -InFile $zip.FullName -TimeoutSec 300
if ($asset.size -ne $zip.Length) { throw 'Uploaded ZIP size does not match the local package.' }
$latest = if ($isDefault) { 'true' } else { 'false' }
$publish = @{ draft = $false; prerelease = !$isDefault; make_latest = $latest } | ConvertTo-Json
$published = Invoke-RestMethod -Method Patch -Uri "$api/releases/$($release.id)" -Headers $headers -ContentType 'application/json' -Body $publish
Write-Host "Release: $($published.html_url)"
Write-Host "ZIP: $($asset.browser_download_url)"
