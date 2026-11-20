$ErrorActionPreference = 'Stop'
$atlasRoot = Split-Path -Parent $PSScriptRoot
$atlasVersion = ([xml](Get-Content -LiteralPath (Join-Path $atlasRoot 'app\SceneryAtlas.App.csproj') -Raw)).Project.PropertyGroup.Version
$atlasCompiler = if ($env:NSIS_COMPILER) { $env:NSIS_COMPILER } else { Join-Path $atlasRoot 'artifacts\tools\nsis\nsis-3.13\Bin\makensis.exe' }
$atlasSource = Join-Path $atlasRoot "artifacts\SceneryAtlas-$atlasVersion"
$atlasReleases = Join-Path $atlasRoot 'artifacts\releases'
if (!(Test-Path -LiteralPath $atlasCompiler)) { throw 'NSIS 3.13 compiler missing. Download the official ZIP and verify SHA-256 BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8 before extracting to artifacts/tools/nsis.' }
if (!(Test-Path -LiteralPath (Join-Path $atlasSource 'SceneryAtlas.exe'))) { throw 'Publish the application with scripts/package.ps1 first.' }
New-Item -ItemType Directory -Path $atlasReleases -Force | Out-Null
$atlasUninstallList = Join-Path $atlasReleases 'uninstall-files.nsh'
$atlasFiles = Get-ChildItem -LiteralPath $atlasSource -Recurse -File
$atlasDirs = Get-ChildItem -LiteralPath $atlasSource -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending
$atlasLines = foreach ($atlasFile in $atlasFiles) {
    $atlasRelative = [IO.Path]::GetRelativePath($atlasSource, $atlasFile.FullName)
    if ($atlasRelative -match '[\$"\r\n]') { throw "Unsafe file name for installer: $atlasRelative" }
    'Delete "$INSTDIR\' + $atlasRelative + '"'
}
$atlasLines += foreach ($atlasDir in $atlasDirs) {
    $atlasRelative = [IO.Path]::GetRelativePath($atlasSource, $atlasDir.FullName)
    if ($atlasRelative -match '[\$"\r\n]') { throw "Unsafe directory name for installer: $atlasRelative" }
    'RMDir "$INSTDIR\' + $atlasRelative + '"'
}
$atlasLines | Set-Content -LiteralPath $atlasUninstallList -Encoding utf8
& $atlasCompiler "/DAPP_SOURCE=$atlasSource" "/DRELEASE_DIR=$atlasReleases" "/DAPP_VERSION=$atlasVersion" "/DUNINSTALL_LIST=$atlasUninstallList" (Join-Path $PSScriptRoot 'installer.nsi')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$atlasInstaller = Join-Path $atlasReleases "SceneryAtlas-$atlasVersion-Setup.exe"
$atlasHash = (Get-FileHash -LiteralPath $atlasInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
@{version=$atlasVersion;installer="SceneryAtlas-$atlasVersion-Setup.exe";sha256=$atlasHash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $atlasReleases 'latest.json') -Encoding utf8
"$atlasHash  SceneryAtlas-$atlasVersion-Setup.exe" | Set-Content -LiteralPath ($atlasInstaller + '.sha256') -Encoding ascii
Get-Item -LiteralPath $atlasInstaller | Select-Object FullName,Length
