param([string]$PackageName = 'IMGC-Extractor-1.0.1-win-x64')
$ErrorActionPreference = 'Stop'
if ($PackageName -notmatch '^[A-Za-z0-9._-]+$') { throw 'PackageName must be a simple name.' }
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$destination = Join-Path $artifacts $PackageName
$zip = Join-Path $artifacts ($PackageName + '.zip')
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $zip)) { throw 'Choose a fresh PackageName; existing packages are not overwritten.' }
& dotnet publish (Join-Path $PSScriptRoot 'src\App\App.csproj') -c Release -r win-x64 --self-contained true '-p:RuntimeFrameworkVersion=8.0.32' '-p:PublishSingleFile=true' '-p:EnableCompressionInSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false' -o $destination --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md'),(Join-Path $PSScriptRoot 'VALIDATION.md'),(Join-Path $PSScriptRoot 'LICENSE') -Destination $destination
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $destination -Recurse
Compress-Archive -LiteralPath $destination -DestinationPath $zip -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
Set-Content -LiteralPath ($zip + '.sha256.txt') -Value ($hash.Hash + '  ' + [IO.Path]::GetFileName($zip)) -Encoding Ascii
Write-Host "Portable package: $zip"
