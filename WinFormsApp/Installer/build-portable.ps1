param(
    [string]$AppVersion = '1.0.0'
)

$ErrorActionPreference = 'Stop'
if ($AppVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version invalide '$AppVersion'. Le format attendu est majeur.mineur.correctif."
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$artifactRoot = Join-Path $projectRoot 'artifacts'
$stageDir = Join-Path $artifactRoot 'installer-stage'
$portableRoot = Join-Path $artifactRoot 'portable'
$portableName = "LocalIARunner-Portable-x64-$AppVersion"
$portableDir = Join-Path $portableRoot $portableName
$archivePath = Join-Path $portableRoot "$portableName.zip"
$projectRootFull = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\')
$artifactRootFull = [IO.Path]::GetFullPath($artifactRoot)
$stageDirFull = [IO.Path]::GetFullPath($stageDir).TrimEnd('\')
$portableRootFull = [IO.Path]::GetFullPath($portableRoot)
$portableDirFull = [IO.Path]::GetFullPath($portableDir)

if (-not $artifactRootFull.StartsWith($projectRootFull + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not $portableRootFull.StartsWith($artifactRootFull + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not $portableDirFull.StartsWith($portableRootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refus de générer le paquet portable hors de $projectRootFull"
}

if (-not (Test-Path -LiteralPath (Join-Path $stageDir 'Local IA Runner.exe')) -or
    -not (Test-Path -LiteralPath (Join-Path $stageDir 'PythonRuntime\python.exe')) -or
    -not (Test-Path -LiteralPath (Join-Path $stageDir 'PythonBackend\ai_backend.py')) -or
    -not (Test-Path -LiteralPath (Join-Path $stageDir 'llama-bin\llama-server.exe')) -or
    -not (Test-Path -LiteralPath (Join-Path $stageDir 'stable-diffusion.cpp\windows-vulkan\sd-server.exe')) -or
    -not (Test-Path -LiteralPath (Join-Path $stageDir 'stable-diffusion.cpp\windows-cpu\sd-server.exe'))) {
    throw "La publication autonome est incomplète : $stageDir"
}

New-Item -ItemType Directory -Force -Path $portableRoot | Out-Null
$portableRootItem = Get-Item -LiteralPath $portableRoot -Force
if ($portableRootItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw "Refus d'utiliser un dossier portable qui est un point de réanalyse : $portableRoot"
}

if (Test-Path -LiteralPath $portableDir) {
    $portableDirItem = Get-Item -LiteralPath $portableDir -Force
    if ($portableDirItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Refus de remplacer un dossier portable qui est un point de réanalyse : $portableDir"
    }
    $resolvedPortableDir = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $portableDir).Path)
    if (-not $resolvedPortableDir.StartsWith($portableRootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refus de nettoyer un dossier portable résolu hors de $portableRootFull"
    }
    Remove-Item -LiteralPath $portableDir -Recurse -Force
}

if (Test-Path -LiteralPath $archivePath) {
    $archiveItem = Get-Item -LiteralPath $archivePath -Force
    if ($archiveItem.PSIsContainer -or $archiveItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Refus de remplacer une archive portable qui n'est pas un fichier normal : $archivePath"
    }
    Remove-Item -LiteralPath $archivePath -Force
}

Write-Host "[1/2] Copie des fichiers de l’application portable..."
New-Item -ItemType Directory -Force -Path $portableDir | Out-Null
Get-ChildItem -LiteralPath $stageDir -File -Recurse | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
    $relativePath = $_.FullName.Substring($stageDirFull.Length).TrimStart('\')
    $destination = Join-Path $portableDir $relativePath
    $destinationDirectory = Split-Path -Parent $destination
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
}

Write-Host '[2/2] Compression du paquet ZIP...'
Compress-Archive -LiteralPath $portableDir -DestinationPath $archivePath -CompressionLevel Optimal
$portableInfo = Get-Item -LiteralPath $portableDir
$archiveInfo = Get-Item -LiteralPath $archivePath
Write-Host "Dossier portable prêt : $($portableInfo.FullName)"
Write-Host "Archive portable prête : $($archiveInfo.FullName) ($([math]::Round($archiveInfo.Length / 1MB, 1)) Mo)"

Write-Host 'Nettoyage des anciennes versions portables locales...'
$portableReleasePattern = '^LocalIARunner-Portable-x64(?:-\d+\.\d+\.\d+)?(?:\.zip)?$'
$keptReleaseNames = @($portableName, "$portableName.zip")
foreach ($oldRelease in Get-ChildItem -LiteralPath $portableRoot -Force) {
    if ($oldRelease.Name -notmatch $portableReleasePattern -or $oldRelease.Name -in $keptReleaseNames) {
        continue
    }

    $oldReleaseFull = [IO.Path]::GetFullPath($oldRelease.FullName)
    if (-not $oldReleaseFull.StartsWith($portableRootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refus de nettoyer une ancienne version hors de $portableRootFull"
    }
    if ($oldRelease.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        Write-Warning 'Ancienne version conservée car un point de réanalyse empêche son nettoyage.'
        continue
    }

    try {
        if ($oldRelease.PSIsContainer) {
            $resolvedOldRelease = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $oldRelease.FullName).Path)
            if (-not $resolvedOldRelease.StartsWith($portableRootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Le chemin résolu sort du dossier portable : $resolvedOldRelease"
            }
            $nestedReparsePoint = Get-ChildItem -LiteralPath $oldRelease.FullName -Force -Recurse |
                Where-Object { $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) } |
                Select-Object -First 1
            if ($nestedReparsePoint) {
                Write-Warning "Ancienne version conservée car elle contient un point de réanalyse : $($oldRelease.FullName)"
                continue
            }
            Remove-Item -LiteralPath $oldRelease.FullName -Recurse -Force
        }
        else {
            Remove-Item -LiteralPath $oldRelease.FullName -Force
        }
        Write-Host "Ancienne version supprimée : $($oldRelease.Name)"
    }
    catch {
        Write-Warning ('Ancienne version conservée, suppression impossible : ' + $oldRelease.FullName + '. Ferme le programme correspondant puis relance compile.bat.')
    }
}
