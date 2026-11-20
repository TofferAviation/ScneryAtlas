$ErrorActionPreference = 'Stop'
$atlasRoot = Split-Path -Parent $PSScriptRoot
$atlasVersion = ([xml](Get-Content -LiteralPath (Join-Path $atlasRoot 'app\SceneryAtlas.App.csproj') -Raw)).Project.PropertyGroup.Version
Push-Location $atlasRoot
try {
    $atlasBuild = Join-Path $atlasRoot 'artifacts\publish-build\'
    $atlasOutput = Join-Path $atlasRoot "artifacts\SceneryAtlas-$atlasVersion"
    dotnet publish app/SceneryAtlas.App.csproj -c Release -r win-x64 --self-contained true "-p:BaseOutputPath=$atlasBuild" -o $atlasOutput
    if ($LASTEXITCODE -ne 0) { throw 'The desktop publish failed.' }
    Copy-Item -LiteralPath (Join-Path $atlasRoot 'README.md') -Destination (Join-Path $atlasOutput 'README.md')
    Copy-Item -LiteralPath (Join-Path $atlasRoot 'catalogue-template.json') -Destination (Join-Path $atlasOutput 'catalogue-template.json')
    $atlasLicences = Join-Path $atlasOutput 'Licences'
    New-Item -ItemType Directory -Path $atlasLicences -Force | Out-Null
    foreach ($atlasDependency in @('react', 'react-dom', 'three')) {
        Copy-Item -LiteralPath (Join-Path $atlasRoot "node_modules\$atlasDependency\LICENSE") -Destination (Join-Path $atlasLicences "$atlasDependency-LICENSE.txt")
    }
    $atlasWebViewPackage = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.web.webview2\1.0.4129.50'
    foreach ($atlasLegal in @('LICENSE.txt', 'NOTICE.txt')) {
        Copy-Item -LiteralPath (Join-Path $atlasWebViewPackage $atlasLegal) -Destination (Join-Path $atlasLicences "WebView2-$atlasLegal")
    }
    foreach ($atlasFramework in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
        $atlasPackageRoot = Join-Path $env:USERPROFILE ".nuget\packages\$atlasFramework"
        if (Test-Path -LiteralPath $atlasPackageRoot) {
            $atlasRuntimeVersion = Get-ChildItem -LiteralPath $atlasPackageRoot -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
            Get-ChildItem -LiteralPath $atlasRuntimeVersion.FullName -File | Where-Object { $_.Name -match 'LICENSE|NOTICE' } | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $atlasLicences "$atlasFramework-$($_.Name)")
            }
        }
    }
    $atlasArchive = Join-Path $atlasRoot "artifacts\SceneryAtlas-$atlasVersion-win-x64.zip"
    Compress-Archive -LiteralPath $atlasOutput -DestinationPath $atlasArchive -Force
    $atlasChecksum = Get-FileHash -LiteralPath $atlasArchive -Algorithm SHA256
    "$($atlasChecksum.Hash.ToLowerInvariant())  SceneryAtlas-$atlasVersion-win-x64.zip" | Set-Content -LiteralPath ($atlasArchive + '.sha256') -Encoding ascii
    Get-Item -LiteralPath $atlasArchive | Select-Object FullName, Length
}
finally { Pop-Location }
