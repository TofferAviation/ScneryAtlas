$ErrorActionPreference = 'Stop'
$atlasRoot = Split-Path -Parent $PSScriptRoot
Push-Location $atlasRoot
try {
    npm run build:web
    if ($LASTEXITCODE -ne 0) { throw 'The globe renderer build failed.' }

    $atlasPreviewMutex = $null
    $atlasPreviewRunning = [Threading.Mutex]::TryOpenExisting('Local\SceneryAtlas-Desktop-Preview', [ref]$atlasPreviewMutex)
    if ($atlasPreviewRunning) {
        $atlasPreviewMutex.Dispose()
        Write-Host 'Scenery Atlas Preview is open. Its new renderer will reload automatically; no install needed.'
        return
    }

    dotnet build app/SceneryAtlas.App.csproj -c Release -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'The desktop preview build failed.' }
    $atlasExe = Join-Path $atlasRoot 'app\bin\Release\net10.0-windows\SceneryAtlas.exe'
    if (!(Test-Path -LiteralPath $atlasExe)) { throw 'The desktop preview executable was not produced.' }
    $env:SCENERY_ATLAS_PREVIEW = '1'
    $env:SCENERY_ATLAS_DATA = Join-Path $atlasRoot 'artifacts\preview-profile'
    $env:SCENERY_ATLAS_WEB_ROOT = Join-Path $atlasRoot 'app\Web'
    New-Item -ItemType Directory -Path $env:SCENERY_ATLAS_DATA -Force | Out-Null
    $atlasPreviewProcess = Start-Process -FilePath $atlasExe -WorkingDirectory (Split-Path -Parent $atlasExe) -PassThru
    Start-Sleep -Seconds 2
    $atlasPreviewProcess.Refresh()
    if ($atlasPreviewProcess.HasExited) { throw "Scenery Atlas Preview exited immediately with code $($atlasPreviewProcess.ExitCode)." }
    Write-Host 'Opened Scenery Atlas Preview. It has a separate local profile and can run beside the installed app.'
    Write-Host 'For browser UI changes, run npm run preview again; the open preview will reload automatically.'
}
finally { Pop-Location }
