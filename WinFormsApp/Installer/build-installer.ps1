[CmdletBinding()]
param(
    [string]$InnoCompiler,
    [string]$AppVersion = '1.0.0'
)

$ErrorActionPreference = 'Stop'
if ($AppVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version invalide '$AppVersion'. Le format attendu est majeur.mineur.correctif."
}
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$iconPath = Join-Path $projectRoot 'WinFormsApp\Assets\LocalIARunner.ico'
$encodedIconPath = "$iconPath.b64"
if (-not (Test-Path -LiteralPath $iconPath) -and (Test-Path -LiteralPath $encodedIconPath)) {
    $encodedIcon = (Get-Content -LiteralPath $encodedIconPath -Raw).Trim()
    [IO.File]::WriteAllBytes($iconPath, [Convert]::FromBase64String($encodedIcon))
}
if (-not (Test-Path -LiteralPath $iconPath)) {
    throw "Icône de l'application manquante: $iconPath"
}
$artifactRoot = Join-Path $projectRoot 'artifacts'
$stageDir = Join-Path $artifactRoot 'installer-stage'
$downloadDir = Join-Path $artifactRoot 'installer-downloads'
$outputDir = Join-Path $artifactRoot 'installer'
$projectFile = Join-Path $projectRoot 'WinFormsApp\LocalAIApp.csproj'
$innoScript = Join-Path $PSScriptRoot 'setup.iss'
$projectRootFull = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\')
$artifactRootFull = [IO.Path]::GetFullPath($artifactRoot)
if (Test-Path -LiteralPath $artifactRoot) {
    $artifactRootFull = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $artifactRoot).Path)
}
$stageDirFull = [IO.Path]::GetFullPath($stageDir)
if (-not $artifactRootFull.StartsWith($projectRootFull + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not $stageDirFull.StartsWith($artifactRootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refus de nettoyer un dossier de staging hors de $projectRootFull"
}

if (Test-Path -LiteralPath $stageDir) {
    $stageItem = Get-Item -LiteralPath $stageDir -Force
    if ($stageItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Refus de nettoyer un dossier de staging qui est un point de réanalyse: $stageDir"
    }
    $resolvedStageDir = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $stageDir).Path)
    if (-not $resolvedStageDir.StartsWith($artifactRootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refus de nettoyer un dossier de staging résolu hors de $artifactRootFull"
    }
    Remove-Item -LiteralPath $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageDir, $downloadDir, $outputDir | Out-Null

Write-Host '[1/7] Publication autonome .NET x64...'
$publishOutput = & dotnet publish $projectFile `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $stageDir `
    "-p:Version=$AppVersion" `
    '-p:PublishSingleFile=true' `
    '-p:IncludeNativeLibrariesForSelfExtract=true' `
    '-p:EnableCompressionInSingleFile=true' 2>&1
$publishExitCode = $LASTEXITCODE
$publishOutput | ForEach-Object { Write-Host $_ }
if ($publishExitCode -ne 0) {
    $publishDiagnostics = @($publishOutput | Where-Object { $_.ToString() -match '(?i)(error|failed|NU\d{4})' } | Select-Object -Last 20)
    if ($publishDiagnostics.Count -eq 0) {
        $publishDiagnostics = @($publishOutput | Select-Object -Last 20)
    }
    throw "dotnet publish a échoué avec le code $publishExitCode`n$($publishDiagnostics -join [Environment]::NewLine)"
}

$appExe = Join-Path $stageDir 'Local IA Runner.exe'
if (-not (Test-Path -LiteralPath $appExe)) {
    throw "L'exécutable publié est absent: $appExe"
}

function Get-Sha256Hash {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        $stream.Dispose()
        $sha256.Dispose()
    }
}

function Get-VerifiedDownload {
    param(
        [string]$Uri,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Sha256
    )

    if (Test-Path -LiteralPath $Path) {
        $existingHash = Get-Sha256Hash -Path $Path
        if ($existingHash -ne $Sha256) {
            Remove-Item -LiteralPath $Path -Force
        }
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        if ([string]::IsNullOrWhiteSpace($Uri)) {
            throw "Fichier absent du cache et URL de téléchargement indisponible: $Path"
        }
        & curl.exe --fail --location --silent --show-error --retry 3 --connect-timeout 30 --max-time 1200 --output $Path $Uri
        if ($LASTEXITCODE -ne 0) {
            Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
            throw "Téléchargement échoué (curl $LASTEXITCODE): $Uri"
        }
    }

    $actualHash = Get-Sha256Hash -Path $Path
    if ($actualHash -ne $Sha256) {
        Remove-Item -LiteralPath $Path -Force
        throw "Le SHA-256 téléchargé ne correspond pas pour $Uri"
    }
}

Write-Host '[2/7] Téléchargement et vérification de WebView2...'
$webViewBootstrapperCache = Join-Path $downloadDir 'MicrosoftEdgeWebView2Setup.exe'
$webViewBootstrapperUrl = 'https://go.microsoft.com/fwlink/?LinkId=2124703'
$webViewBootstrapperValid = $false
if (Test-Path -LiteralPath $webViewBootstrapperCache) {
    $cachedSignature = Get-AuthenticodeSignature -FilePath $webViewBootstrapperCache
    $webViewBootstrapperValid = $cachedSignature.Status -eq [System.Management.Automation.SignatureStatus]::Valid -and
        $cachedSignature.SignerCertificate.Subject -match 'Microsoft Corporation'
}
if (-not $webViewBootstrapperValid) {
    Remove-Item -LiteralPath $webViewBootstrapperCache -Force -ErrorAction SilentlyContinue
    & curl.exe --fail --location --silent --show-error --retry 3 --connect-timeout 30 --max-time 300 --output $webViewBootstrapperCache $webViewBootstrapperUrl
    if ($LASTEXITCODE -ne 0) {
        Remove-Item -LiteralPath $webViewBootstrapperCache -Force -ErrorAction SilentlyContinue
        throw "Téléchargement du bootstrapper WebView2 échoué (curl $LASTEXITCODE)."
    }
}
$webViewSignature = Get-AuthenticodeSignature -FilePath $webViewBootstrapperCache
if ($webViewSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $webViewSignature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
    Remove-Item -LiteralPath $webViewBootstrapperCache -Force -ErrorAction SilentlyContinue
    throw "Le bootstrapper WebView2 n'a pas une signature Microsoft valide."
}
$webViewDependencies = Join-Path $stageDir 'Dependencies'
New-Item -ItemType Directory -Force -Path $webViewDependencies | Out-Null
Copy-Item -LiteralPath $webViewBootstrapperCache -Destination (Join-Path $webViewDependencies 'MicrosoftEdgeWebView2Setup.exe') -Force

Write-Host '[3/7] Téléchargement et vérification du Python portable...'
$pythonZip = Join-Path $downloadDir 'python-3.13.17-embed-amd64.zip'
Get-VerifiedDownload `
    -Uri 'https://www.python.org/ftp/python/3.13.17/python-3.13.17-embed-amd64.zip' `
    -Path $pythonZip `
    -Sha256 '9E35319BFFEDAB8B36AD44D8F377CB4F1733BCEC257A0B6D04D94F01486DDB38'
$pythonDir = Join-Path $stageDir 'PythonRuntime'
New-Item -ItemType Directory -Force -Path $pythonDir | Out-Null
Expand-Archive -LiteralPath $pythonZip -DestinationPath $pythonDir -Force
$pythonExe = Join-Path $pythonDir 'python.exe'
if (-not (Test-Path -LiteralPath $pythonExe)) {
    throw 'python.exe est absent du paquet Python embarqué.'
}
& $pythonExe -I -S -c 'import base64, html.parser, http.client, ipaddress, json, pathlib, platform, ssl, subprocess, tarfile, tempfile, threading, urllib.error, urllib.request, zipfile; print(platform.python_version())'
if ($LASTEXITCODE -ne 0) {
    throw 'Le runtime Python embarqué ne peut pas charger les modules standard requis.'
}

Write-Host '[4/7] Téléchargement et vérification du moteur llama.cpp CPU...'
$llamaZip = Join-Path $downloadDir 'llama-b12244-bin-win-cpu-x64.zip'
Get-VerifiedDownload `
    -Uri 'https://github.com/ggml-org/llama.cpp/releases/download/b12244/llama-b12244-bin-win-cpu-x64.zip' `
    -Path $llamaZip `
    -Sha256 '318F11FADCBC3A86541EC55D9930CC76646C6B8D2FF18798C8477E9FF0DC452A'
$llamaDir = Join-Path $stageDir 'llama-bin'
New-Item -ItemType Directory -Force -Path $llamaDir | Out-Null
Expand-Archive -LiteralPath $llamaZip -DestinationPath $llamaDir -Force
if (-not (Test-Path -LiteralPath (Join-Path $llamaDir 'llama-server.exe'))) {
    throw 'llama-server.exe est absent du paquet llama.cpp.'
}

# llama.cpp's Windows binaries depend on the MSVC v14 runtime. Copy them from
# the protected Windows system directory beside llama-server so the package
# does not require a system-wide Visual C++ installation or administrator rights.
$systemDirectory = Join-Path $env:WINDIR 'System32'
foreach ($runtimeName in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    $runtimeSource = Join-Path $systemDirectory $runtimeName
    if (-not (Test-Path -LiteralPath $runtimeSource)) {
        throw "Dépendance MSVC manquante sur la machine de compilation: $runtimeSource"
    }
    if ((Get-Item -LiteralPath $runtimeSource).Length -le 0) {
        throw "Dépendance MSVC vide dans le dossier système Windows: $runtimeSource"
    }
    Copy-Item -LiteralPath $runtimeSource -Destination (Join-Path $llamaDir $runtimeName) -Force
}

Write-Host '[5/7] Téléchargement et vérification des moteurs stable-diffusion.cpp Vulkan et CPU...'
$sdReleases = @()
$sdApiError = $null
try {
    $sdReleases = Invoke-RestMethod `
        -Uri 'https://api.github.com/repos/leejet/stable-diffusion.cpp/releases?per_page=20' `
        -Headers @{ 'User-Agent' = 'LocalIARunner-build'; 'Accept' = 'application/vnd.github+json' }
}
catch {
    $sdApiError = $_.Exception.Message
}
$sdRelease = $null
$sdAssets = @{}
foreach ($candidateRelease in $sdReleases) {
    $vulkanAsset = $candidateRelease.assets |
        Where-Object { $_.name -match '^sd-.*-bin-win-vulkan-x64\.zip$' } |
        Select-Object -First 1
    $cpuAsset = $candidateRelease.assets |
        Where-Object { $_.name -match '^sd-.*-bin-win-cpu-x64\.zip$' } |
        Select-Object -First 1
    if ($vulkanAsset -and $cpuAsset) {
        $sdRelease = $candidateRelease
        $sdAssets = @{ vulkan = $vulkanAsset; cpu = $cpuAsset }
        break
    }
}

if (-not $sdRelease) {
    if ($sdApiError) {
        Write-Warning "GitHub ne fournit pas les métadonnées de release ($sdApiError). Recherche d'une paire de moteurs déjà téléchargée."
    }

    foreach ($cachedVulkan in (Get-ChildItem -LiteralPath $downloadDir -Filter 'sd-*-bin-win-vulkan-x64.zip' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending)) {
        $cachedMatch = [regex]::Match($cachedVulkan.Name, '^(?<prefix>sd-.+)-bin-win-vulkan-x64\.zip$')
        if (-not $cachedMatch.Success) {
            continue
        }

        $cachedCpu = Join-Path $downloadDir ($cachedMatch.Groups['prefix'].Value + '-bin-win-cpu-x64.zip')
        if (-not (Test-Path -LiteralPath $cachedCpu -PathType Leaf)) {
            continue
        }

        $sdRelease = [pscustomobject]@{ tag_name = $cachedMatch.Groups['prefix'].Value; assets = @() }
        $sdAssets = @{
            vulkan = [pscustomobject]@{
                name = $cachedVulkan.Name
                digest = 'sha256:' + (Get-Sha256Hash -Path $cachedVulkan.FullName)
                browser_download_url = ''
            }
            cpu = [pscustomobject]@{
                name = [IO.Path]::GetFileName($cachedCpu)
                digest = 'sha256:' + (Get-Sha256Hash -Path $cachedCpu)
                browser_download_url = ''
            }
        }
        Write-Warning "Réutilisation des moteurs en cache : $($cachedVulkan.Name)"
        break
    }
}

if (-not $sdRelease) {
    throw "Aucune release ou paire de moteurs stable-diffusion.cpp Windows x64 Vulkan et CPU en cache n'a été trouvée."
}

$sdRoot = Join-Path $stageDir 'stable-diffusion.cpp'
foreach ($variant in @('vulkan', 'cpu')) {
    $asset = $sdAssets[$variant]
    $expectedHash = [string]$asset.digest -replace '^sha256:', ''
    if ($expectedHash -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "Empreinte SHA-256 absente de la release stable-diffusion.cpp ($($asset.name))."
    }

    $engineArchive = Join-Path $downloadDir $asset.name
    Get-VerifiedDownload `
        -Uri $asset.browser_download_url `
        -Path $engineArchive `
        -Sha256 $expectedHash

    $variantDir = Join-Path $sdRoot "windows-$variant"
    New-Item -ItemType Directory -Force -Path $variantDir | Out-Null
    Expand-Archive -LiteralPath $engineArchive -DestinationPath $variantDir -Force
    $sdServer = Get-ChildItem -LiteralPath $variantDir -Filter 'sd-server.exe' -File -Recurse |
        Select-Object -First 1
    if (-not $sdServer) {
        throw "sd-server.exe est absent du paquet stable-diffusion.cpp ($variant)."
    }

    # Include the app-local MSVC runtime so the bundled engine can start on
    # Windows installations that do not already have the Visual C++ runtime.
    foreach ($runtimeName in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
        $runtimeSource = Join-Path $systemDirectory $runtimeName
        if (-not (Test-Path -LiteralPath $runtimeSource)) {
            throw "Dépendance MSVC manquante sur la machine de compilation: $runtimeSource"
        }
        Copy-Item -LiteralPath $runtimeSource -Destination (Join-Path $sdServer.DirectoryName $runtimeName) -Force
    }
}

$licensesDir = Join-Path $stageDir 'Licenses'
New-Item -ItemType Directory -Force -Path $licensesDir | Out-Null
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$licenseFiles = @(
    [pscustomobject]@{ Source = Join-Path $PSScriptRoot 'licenses\llama.cpp-LICENSE.txt'; Destination = Join-Path $licensesDir 'llama.cpp-LICENSE.txt' },
    [pscustomobject]@{ Source = Join-Path $nugetRoot 'newtonsoft.json\13.0.3\LICENSE.md'; Destination = Join-Path $licensesDir 'Newtonsoft.Json-LICENSE.md' },
    [pscustomobject]@{ Source = Join-Path $nugetRoot 'microsoft.web.webview2\1.0.4314.35\LICENSE.txt'; Destination = Join-Path $licensesDir 'Microsoft.Web.WebView2-LICENSE.txt' }
)
$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$dotnetInstallDir = Split-Path -Parent $dotnetCommand.Source
$licenseFiles += [pscustomobject]@{ Source = Join-Path $dotnetInstallDir 'LICENSE.txt'; Destination = Join-Path $licensesDir 'dotnet-LICENSE.txt' }
foreach ($licensePair in $licenseFiles) {
    $sourceLicense = $licensePair.Source
    $destinationLicense = $licensePair.Destination
    if (-not (Test-Path -LiteralPath $sourceLicense)) {
        throw "Licence tierce manquante: $sourceLicense"
    }
    Copy-Item -LiteralPath $sourceLicense -Destination $destinationLicense -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $stageDir 'THIRD-PARTY-NOTICES.txt') -Force

Write-Host '[6/7] Recherche du compilateur Inno Setup...'
if (-not $InnoCompiler) {
    $compilerCandidates = @(
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe')
    )
    $InnoCompiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw 'ISCC.exe introuvable. Installe Inno Setup 6 ou 7, ou passe son chemin avec -InnoCompiler.'
}

Write-Host '[7/7] Création de LocalIARunner-Setup-x64.exe...'
$oldStage = $env:LOCALIA_INSTALLER_STAGE
$oldOutput = $env:LOCALIA_INSTALLER_OUTPUT
$oldRoot = $env:LOCALIA_PROJECT_ROOT
$oldVersion = $env:LOCALIA_INSTALLER_VERSION
$locationPushed = $false
try {
    $env:LOCALIA_INSTALLER_STAGE = $stageDir
    $env:LOCALIA_INSTALLER_OUTPUT = $outputDir
    $env:LOCALIA_PROJECT_ROOT = $projectRoot
    $env:LOCALIA_INSTALLER_VERSION = $AppVersion
    Push-Location $PSScriptRoot
    $locationPushed = $true
    & $InnoCompiler '/Qp' $innoScript
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC a échoué avec le code $LASTEXITCODE"
    }
}
finally {
    if ($locationPushed) { Pop-Location }
    $env:LOCALIA_INSTALLER_STAGE = $oldStage
    $env:LOCALIA_INSTALLER_OUTPUT = $oldOutput
    $env:LOCALIA_PROJECT_ROOT = $oldRoot
    $env:LOCALIA_INSTALLER_VERSION = $oldVersion
}

$installerPath = Join-Path $outputDir 'LocalIARunner-Setup-x64.exe'
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "L'installateur attendu n'a pas été créé: $installerPath"
}
$installerInfo = Get-Item -LiteralPath $installerPath
Write-Host "Installateur prêt: $($installerInfo.FullName) ($([math]::Round($installerInfo.Length / 1MB, 1)) Mo)"
