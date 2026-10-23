param([string]$WrappedExecutable, [string]$KnownElf)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Check\Check.csproj'
$generated = Join-Path $PSScriptRoot 'generated'
& dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
$check = Join-Path $root 'src\Check\bin\Release\net8.0\Check.exe'
function Run-Check([string[]]$Arguments) {
    & $check @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Failed: $Arguments" }
}
Run-Check -Arguments @('self-test')
if (!(Test-Path -LiteralPath (Join-Path $generated 'valid.imgc'))) { throw 'Generate fixtures with build_fixtures_windows.py first (development WSL only).' }
Run-Check -Arguments @('integration-test', $generated)
Run-Check -Arguments @('compare-stream', (Join-Path $generated 'valid.imgc'), (Join-Path $generated 'valid.img'))
foreach ($format in @('imgc','img')) {
    $output = Join-Path $generated ('test-' + $format + '-' + [guid]::NewGuid().ToString('N'))
    Run-Check -Arguments @('extract', (Join-Path $generated "valid.$format"), $output)
    & python (Join-Path $PSScriptRoot 'verify_fixture.py') $output (Join-Path $generated 'expected.json')
    if ($LASTEXITCODE -ne 0) { throw 'Fixture comparison failed.' }
}
foreach ($variant in @('missing-label','missing-file','case-collision','ambiguous-backup','missing-backup')) {
    & $check inspect (Join-Path $generated ($variant + '.imgc'))
    if ($LASTEXITCODE -eq 0) { throw "Invalid fixture accepted: $variant" }
}
$output = Join-Path $generated ('test-unlabelled-' + [guid]::NewGuid().ToString('N'))
Run-Check -Arguments @('extract', (Join-Path $generated 'unlabelled-backup.imgc'), $output)
& python (Join-Path $PSScriptRoot 'verify_fixture.py') $output (Join-Path $generated 'expected.json')
if ($LASTEXITCODE -ne 0) { throw 'Unlabelled backup fixture comparison failed.' }
$receipt = Get-Content -Raw -LiteralPath (Join-Path $output 'Extraction-report.json') | ConvertFrom-Json
if ($receipt.backupDiscovery -notlike '*unlabelled ext filesystem*') { throw 'Missing backup discovery provenance.' }
if ($WrappedExecutable -and $KnownElf) { Run-Check -Arguments @('preparation-test', $WrappedExecutable, $KnownElf, $generated) }
Write-Host 'All selected tests passed.'
