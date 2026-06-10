param(
    [string]$Configuration = 'Release',
    [string]$ZipName = 'NT106-DrawingApp-setup.zip'
)

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$setupRoot = Resolve-Path $PSScriptRoot
$appsRoot = Join-Path $setupRoot 'apps'

function Assert-NoSetupAppProcesses {
    param([string]$AppsRoot)

    $resolvedAppsRoot = (Resolve-Path $AppsRoot -ErrorAction SilentlyContinue)
    if ($null -eq $resolvedAppsRoot) {
        return
    }

    $rootPath = $resolvedAppsRoot.Path.TrimEnd('\') + '\'
    $running = @(
        Get-Process -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    -not [string]::IsNullOrWhiteSpace($_.Path) -and
                    $_.Path.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)
                } catch {
                    $false
                }
            } |
            Select-Object ProcessName, Id, Path
    )

    if ($running.Count -gt 0) {
        $details = $running | Format-Table -AutoSize | Out-String
        throw "Dang co app chay tu setup/apps nen khong the package an toan. Hay dong cac cua so DrawingClient/DrawingServer/LoadBalancer hoac stop process roi chay lai.`n$details"
    }
}

dotnet restore (Join-Path $repoRoot 'NT106_DrawingApp.sln') /p:RestorePackagesConfig=true
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

dotnet build (Join-Path $repoRoot 'NT106_DrawingApp.sln') -c $Configuration -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Assert-NoSetupAppProcesses -AppsRoot $appsRoot
Remove-Item -Recurse -Force $appsRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $appsRoot | Out-Null

function Copy-AppOutput {
    param(
        [string]$ExeName,
        [string]$BuildRoot,
        [string]$Destination
    )

    $exe = Get-ChildItem -Path $BuildRoot -Recurse -Filter $ExeName | Select-Object -First 1
    if ($null -eq $exe) {
        throw "Khong tim thay $ExeName trong $BuildRoot"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Recurse -Force (Join-Path $exe.DirectoryName '*') $Destination
}

function Assert-RequiredFiles {
    param(
        [string]$Destination,
        [string[]]$RequiredFiles
    )

    $missing = @()
    foreach ($f in $RequiredFiles) {
        if (-not (Test-Path (Join-Path $Destination $f))) {
            $missing += $f
        }
    }
    if ($missing.Count -gt 0) {
        throw "Goi thieu file bat buoc trong '$Destination': $($missing -join ', '). Build $Configuration co the chua copy DLL (CopyLocal) - kiem tra reference/HintPath va restore packages."
    }
}

$clientOut = Join-Path $repoRoot "DrawingClient\bin\$Configuration"
$serverOut = Join-Path $repoRoot "DrawingServer\bin\$Configuration"
$lbOut = Join-Path $repoRoot "LoadBalancer\bin\$Configuration"

Copy-AppOutput -ExeName 'DrawingClient.exe' -BuildRoot $clientOut -Destination (Join-Path $appsRoot 'DrawingClient')
Copy-AppOutput -ExeName 'DrawingServer.exe' -BuildRoot $serverOut -Destination (Join-Path $appsRoot 'DrawingServer')
Copy-AppOutput -ExeName 'LoadBalancer.exe' -BuildRoot $lbOut -Destination (Join-Path $appsRoot 'LoadBalancer')

# Bao dam client co du DLL chay AI line art (WebP->PNG): ImageSharp + cac transitive System.* (CopyLocal).
Assert-RequiredFiles -Destination (Join-Path $appsRoot 'DrawingClient') -RequiredFiles @(
    'DrawingClient.exe',
    'SharedLib.dll',
    'Newtonsoft.Json.dll',
    'SixLabors.ImageSharp.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll',
    'System.Text.Encoding.CodePages.dll'
)

$lbServers = Join-Path $repoRoot 'LoadBalancer\servers.json'
if (Test-Path $lbServers) {
    Copy-Item -Force $lbServers (Join-Path $appsRoot 'LoadBalancer\servers.json')
}

$cert = Join-Path $repoRoot 'DrawingServer\server.pfx'
if (Test-Path $cert) {
    Copy-Item -Force $cert (Join-Path $appsRoot 'DrawingServer\server.pfx')
}

$zipPath = Join-Path $repoRoot $ZipName
$stageRoot = Join-Path $repoRoot 'local\tmp_setup_package'
$stageSetup = Join-Path $stageRoot 'setup'
Remove-Item -Recurse -Force $stageRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stageSetup | Out-Null
Copy-Item -Recurse -Force (Join-Path $setupRoot '*') $stageSetup
Copy-Item -Force (Join-Path $setupRoot '.env.example') (Join-Path $stageSetup '.env.example')
Remove-Item -Force (Join-Path $stageSetup '.env') -ErrorAction SilentlyContinue
Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stageRoot 'setup') -DestinationPath $zipPath -Force
Write-Host "Da tao goi: $zipPath"
