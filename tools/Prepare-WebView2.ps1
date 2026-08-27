param(
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Destination
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$work = Join-Path $env:TEMP ('CatLayer_WebView2_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    $packagePath = Join-Path $work 'webview2.nupkg'
    $extractPath = Join-Path $work 'pkg'
    $packageUrl = 'https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/' + $Version

    Invoke-WebRequest -UseBasicParsing -Uri $packageUrl -OutFile $packagePath

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $extractPath)

    $core = Join-Path $extractPath 'lib\net462\Microsoft.Web.WebView2.Core.dll'
    $winForms = Join-Path $extractPath 'lib\net462\Microsoft.Web.WebView2.WinForms.dll'
    if (-not (Test-Path $core)) { throw 'Microsoft.Web.WebView2.Core.dll (net462) was not found in the NuGet package.' }
    if (-not (Test-Path $winForms)) { throw 'Microsoft.Web.WebView2.WinForms.dll (net462) was not found in the NuGet package.' }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item $core (Join-Path $Destination 'Microsoft.Web.WebView2.Core.dll') -Force
    Copy-Item $winForms (Join-Path $Destination 'Microsoft.Web.WebView2.WinForms.dll') -Force

    foreach ($arch in @('x86', 'x64', 'arm64')) {
        $loader = Join-Path $extractPath ('runtimes\win-' + $arch + '\native\WebView2Loader.dll')
        if (Test-Path $loader) {
            $target = Join-Path $Destination ('native\' + $arch)
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Copy-Item $loader (Join-Path $target 'WebView2Loader.dll') -Force
        }
    }

    if (-not (Test-Path (Join-Path $Destination 'native\x86\WebView2Loader.dll'))) { throw 'x86 WebView2Loader.dll was not found.' }
    if (-not (Test-Path (Join-Path $Destination 'native\x64\WebView2Loader.dll'))) { throw 'x64 WebView2Loader.dll was not found.' }
    if (-not (Test-Path (Join-Path $Destination 'native\arm64\WebView2Loader.dll'))) { throw 'arm64 WebView2Loader.dll was not found.' }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
