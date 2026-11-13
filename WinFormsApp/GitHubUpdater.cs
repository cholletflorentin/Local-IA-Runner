using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace LocalAIApp
{
    internal enum UpdatePackageKind
    {
        Installer,
        Portable
    }

    internal sealed class GitHubReleaseAsset
    {
        public required string Name { get; init; }
        public required Uri DownloadUri { get; init; }
        public required string Sha256 { get; init; }
        public long? Size { get; init; }
    }

    internal sealed class GitHubReleaseInfo
    {
        public required Version Version { get; init; }
        public required string Tag { get; init; }
        public required string ReleaseUrl { get; init; }
        public required string Notes { get; init; }
        public GitHubReleaseAsset? Installer { get; init; }
        public GitHubReleaseAsset? Portable { get; init; }

        public GitHubReleaseAsset GetAsset(UpdatePackageKind kind) => kind switch
        {
            UpdatePackageKind.Installer => Installer
                ?? throw new InvalidDataException("L'installateur est absent de la dernière release GitHub."),
            _ => Portable
                ?? throw new InvalidDataException("Le paquet portable est absent de la dernière release GitHub.")
        };
    }

    internal static class GitHubUpdater
    {
        private const string ReleaseApiUrl = "https://api.github.com/repos/cholletflorentin/Local-IA-Runner/releases/latest";
        private const string ReleaseManifestAssetUrl = "https://github.com/cholletflorentin/Local-IA-Runner/releases/latest/download/latest-release.json";
        private const string ReleaseManifestUrl = "https://raw.githubusercontent.com/cholletflorentin/Local-IA-Runner/main/latest-release.json";
        private static readonly HttpClient HttpClient = CreateHttpClient();

        private const string ApplyScript = """
            param(
                [Parameter(Mandatory=$true)][int]$WaitPid,
                [Parameter(Mandatory=$true)][string]$PackagePath,
                [Parameter(Mandatory=$true)][string]$InstallDirectory,
                [Parameter(Mandatory=$true)][string]$Mode
            )

            $ErrorActionPreference = 'Stop'
            $stagingDirectory = $null
            $backupDirectory = $null
            $changedFiles = [System.Collections.Generic.List[object]]::new()

            function Show-UpdateFailure([string]$Message) {
                try {
                    Add-Type -AssemblyName System.Windows.Forms
                    [System.Windows.Forms.MessageBox]::Show(
                        $Message,
                        'Local IA Runner',
                        [System.Windows.Forms.MessageBoxButtons]::OK,
                        [System.Windows.Forms.MessageBoxIcon]::Error
                    ) | Out-Null
                } catch { }
            }

            try {
                try { Wait-Process -Id $WaitPid -ErrorAction SilentlyContinue } catch { }
                while (Get-Process -Name 'Local IA Runner' -ErrorAction SilentlyContinue) {
                    Start-Sleep -Seconds 2
                }

                if ($Mode -eq 'installer') {
                    $directoryArgument = '/DIR="' + $InstallDirectory + '"'
                    $setup = Start-Process -FilePath $PackagePath `
                        -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', $directoryArgument) `
                        -Wait -PassThru
                    if ($setup.ExitCode -ne 0) {
                        throw "L'installateur a échoué (code $($setup.ExitCode))."
                    }
                } else {
                    $stagingDirectory = Join-Path $env:TEMP ('LocalIARunner-update-' + [guid]::NewGuid().ToString('N'))
                    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
                    Expand-Archive -LiteralPath $PackagePath -DestinationPath $stagingDirectory -Force

                    $payload = Get-ChildItem -LiteralPath $stagingDirectory -Directory |
                        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Local IA Runner.exe') } |
                        Select-Object -First 1
                    if (-not $payload) {
                        throw 'Le ZIP ne contient pas les fichiers attendus de Local IA Runner.'
                    }

                    $payloadRoot = [IO.Path]::GetFullPath($payload.FullName).TrimEnd('\') + '\'
                    $backupDirectory = Join-Path $stagingDirectory 'backup'
                    foreach ($file in Get-ChildItem -LiteralPath $payload.FullName -File -Recurse) {
                        $sourcePath = [IO.Path]::GetFullPath($file.FullName)
                        if (-not $sourcePath.StartsWith($payloadRoot, [StringComparison]::OrdinalIgnoreCase)) {
                            throw 'Un chemin du paquet portable sort de son dossier.'
                        }
                        $relativePath = $sourcePath.Substring($payloadRoot.Length)
                        $destinationPath = Join-Path $InstallDirectory $relativePath
                        $destinationDirectory = Split-Path -Parent $destinationPath
                        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null

                        $alreadyExisted = Test-Path -LiteralPath $destinationPath
                        $backupPath = $null
                        if ($alreadyExisted) {
                            $backupPath = Join-Path $backupDirectory $relativePath
                            New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force | Out-Null
                            Copy-Item -LiteralPath $destinationPath -Destination $backupPath -Force
                        }
                        $changedFiles.Add([pscustomobject]@{
                            Destination = $destinationPath
                            Existed = $alreadyExisted
                            Backup = $backupPath
                        })

                        $copied = $false
                        for ($attempt = 0; $attempt -lt 10; $attempt++) {
                            try {
                                Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
                                $copied = $true
                                break
                            } catch {
                                Start-Sleep -Seconds 1
                            }
                        }
                        if (-not $copied) {
                            throw "Impossible de remplacer le fichier $relativePath. Fermez les autres instances de l'application puis réessayez."
                        }
                    }
                }

                $appPath = Join-Path $InstallDirectory 'Local IA Runner.exe'
                if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
                    throw 'L’application mise à jour est introuvable après l’installation.'
                }
                Start-Process -FilePath $appPath -WorkingDirectory $InstallDirectory

            } catch {
                if ($Mode -eq 'portable') {
                    for ($index = $changedFiles.Count - 1; $index -ge 0; $index--) {
                        $changed = $changedFiles[$index]
                        try {
                            if ($changed.Existed -and (Test-Path -LiteralPath $changed.Backup)) {
                                Copy-Item -LiteralPath $changed.Backup -Destination $changed.Destination -Force
                            } elseif (-not $changed.Existed -and (Test-Path -LiteralPath $changed.Destination)) {
                                Remove-Item -LiteralPath $changed.Destination -Force
                            }
                        } catch { }
                    }
                }
                Show-UpdateFailure ("La mise à jour n'a pas pu être appliquée.`n`n" + $_.Exception.Message)
            } finally {
                if ($stagingDirectory -and (Test-Path -LiteralPath $stagingDirectory)) {
                    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
                }
                if (Test-Path -LiteralPath $PackagePath) {
                    $packageDeleted = $false
                    for ($attempt = 0; $attempt -lt 10; $attempt++) {
                        try {
                            Remove-Item -LiteralPath $PackagePath -Force -ErrorAction Stop
                            if (-not (Test-Path -LiteralPath $PackagePath)) {
                                $packageDeleted = $true
                                break
                            }
                        } catch { }
                        Start-Sleep -Milliseconds 500
                    }
                    if (-not $packageDeleted -and (Test-Path -LiteralPath $PackagePath)) {
                        try {
                            Add-Type -AssemblyName System.Windows.Forms
                            [System.Windows.Forms.MessageBox]::Show(
                                "La mise à jour est terminée, mais le fichier téléchargé n'a pas pu être supprimé :`n$PackagePath",
                                'Local IA Runner',
                                [System.Windows.Forms.MessageBoxButtons]::OK,
                                [System.Windows.Forms.MessageBoxIcon]::Warning
                            ) | Out-Null
                        } catch { }
                    }
                }
                if ($PSCommandPath -and (Test-Path -LiteralPath $PSCommandPath)) {
                    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
                }
            }
            """;

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LocalIARunner", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public static Version GetCurrentVersion()
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }

        public static UpdatePackageKind GetPackageKind()
        {
            // All in-app updates use the Windows installer. For portable copies,
            // StartApplyHelper passes the current app directory as Inno Setup's /DIR.
            return UpdatePackageKind.Installer;
        }

        public static async Task<GitHubReleaseInfo?> CheckLatestAsync(Version currentVersion, CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var release = await GetLatestReleaseMetadataAsync(timeout.Token);
            var tag = (string?)release["tag_name"] ?? "";
            var normalizedTag = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
            if (!Version.TryParse(normalizedTag, out var latestVersion) || latestVersion <= currentVersion)
                return null;

            var assets = release["assets"] as JArray ?? new JArray();
            var installer = assets
                .OfType<JObject>()
                .Where(asset => string.Equals((string?)asset["name"], "LocalIARunner-Setup-x64.exe", StringComparison.Ordinal))
                .Select(ParseAsset)
                .FirstOrDefault(asset => asset != null);
            var expectedPortableName = $"LocalIARunner-Portable-x64-{normalizedTag}.zip";
            var portable = assets
                .OfType<JObject>()
                .Where(asset => string.Equals((string?)asset["name"], expectedPortableName, StringComparison.Ordinal))
                .Select(ParseAsset)
                .FirstOrDefault(asset => asset != null);

            return new GitHubReleaseInfo
            {
                Version = latestVersion,
                Tag = tag,
                ReleaseUrl = (string?)release["html_url"] ?? "https://github.com/cholletflorentin/Local-IA-Runner/releases/latest",
                Notes = (string?)release["body"] ?? "",
                Installer = installer,
                Portable = portable
            };
        }

        private static async Task<JObject> GetLatestReleaseMetadataAsync(CancellationToken cancellationToken)
        {
            var manifestFailures = new List<string>();
            var manifestUrls = new[] { ReleaseManifestAssetUrl, ReleaseManifestUrl };
            foreach (var baseUrl in manifestUrls)
            {
                try
                {
                    var manifestUrl = $"{baseUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
                    using var response = await HttpClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        manifestFailures.Add($"{new Uri(baseUrl).Host} : HTTP {(int)response.StatusCode} ({response.ReasonPhrase})");
                        continue;
                    }

                    var manifestJson = await response.Content.ReadAsStringAsync(cancellationToken);
                    return JObject.Parse(manifestJson);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
                {
                    manifestFailures.Add($"{new Uri(baseUrl).Host} : {ex.Message}");
                }
            }

            using var apiRequest = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
            apiRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var apiResponse = await HttpClient.SendAsync(
                apiRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var apiContent = await apiResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!apiResponse.IsSuccessStatusCode)
            {
                var apiMessage = "";
                try { apiMessage = (string?)JObject.Parse(apiContent)["message"] ?? ""; } catch { }
                var detail = string.IsNullOrWhiteSpace(apiMessage) ? "" : $" Détail : {apiMessage}";
                throw new HttpRequestException(
                    $"GitHub a refusé la vérification (HTTP {(int)apiResponse.StatusCode}).{detail} " +
                    $"Les manifestes de release sont également indisponibles ({string.Join(" ; ", manifestFailures)}).");
            }

            return JObject.Parse(apiContent);
        }

        private static GitHubReleaseAsset? ParseAsset(JObject asset)
        {
            var name = (string?)asset["name"];
            var urlText = (string?)asset["browser_download_url"];
            var digest = (string?)asset["digest"];
            if (string.IsNullOrWhiteSpace(name) ||
                !Uri.TryCreate(urlText, UriKind.Absolute, out var url) ||
                !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(digest) ||
                !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return null;

            var sha256 = digest[7..];
            if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
                return null;

            return new GitHubReleaseAsset
            {
                Name = name,
                DownloadUri = url,
                Sha256 = sha256,
                Size = (long?)asset["size"]
            };
        }

        public static async Task<string> DownloadAssetAsync(
            GitHubReleaseAsset asset,
            IProgress<double>? progress,
            CancellationToken cancellationToken = default)
        {
            var updateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalIARunner",
                "Updates");
            Directory.CreateDirectory(updateDirectory);
            var downloadPath = Path.Combine(updateDirectory, $"{Path.GetFileNameWithoutExtension(asset.Name)}-{Guid.NewGuid():N}{Path.GetExtension(asset.Name)}");

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(45));
                using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUri);
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();

                var finalUri = response.RequestMessage?.RequestUri;
                if (finalUri == null || !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !(string.Equals(finalUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                      finalUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("GitHub a redirigé le téléchargement vers une adresse inattendue.");

                await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var destination = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[131072];
                    long received = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                        received += read;
                        if (asset.Size is > 0)
                            progress?.Report(Math.Clamp(received * 100d / asset.Size.Value, 0, 100));
                    }
                    await destination.FlushAsync(timeout.Token);
                    if (asset.Size.HasValue && received != asset.Size.Value)
                        throw new InvalidDataException($"Taille incorrecte pour {asset.Name} : {received} octets reçus.");
                }

                await using var downloadedFile = new FileStream(downloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloadedFile, timeout.Token));
                if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"La vérification SHA-256 de {asset.Name} a échoué.");

                progress?.Report(100);
                return downloadPath;
            }
            catch
            {
                try { if (File.Exists(downloadPath)) File.Delete(downloadPath); } catch { }
                throw;
            }
        }

        public static void DeleteDownloadedAsset(string? packagePath)
        {
            if (string.IsNullOrWhiteSpace(packagePath))
                return;

            try
            {
                var updateDirectory = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LocalIARunner",
                    "Updates"));
                var updateRoot = updateDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var fullPath = Path.GetFullPath(packagePath);
                var fileName = Path.GetFileName(fullPath);
                if (!fullPath.StartsWith(updateRoot, StringComparison.OrdinalIgnoreCase) ||
                    !(fileName.StartsWith("LocalIARunner-Setup-x64-", StringComparison.OrdinalIgnoreCase) ||
                      fileName.StartsWith("LocalIARunner-Portable-x64-", StringComparison.OrdinalIgnoreCase)))
                    return;

                if (File.Exists(fullPath))
                    File.Delete(fullPath);
            }
            catch
            {
                // A failed or interrupted download is removed on the next cleanup attempt.
            }
        }

        public static Process StartApplyHelper(string packagePath)
        {
            var appDirectory = NormalizeDirectory(AppContext.BaseDirectory);
            var isInstaller = GetPackageKind() == UpdatePackageKind.Installer;
            var scriptPath = Path.Combine(Path.GetTempPath(), $"LocalIARunner-update-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(scriptPath, ApplyScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var powerShellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (!File.Exists(powerShellPath))
                throw new FileNotFoundException("Windows PowerShell est introuvable.", powerShellPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = powerShellPath,
                WorkingDirectory = appDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-WaitPid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("-PackagePath");
            startInfo.ArgumentList.Add(Path.GetFullPath(packagePath));
            startInfo.ArgumentList.Add("-InstallDirectory");
            startInfo.ArgumentList.Add(appDirectory);
            startInfo.ArgumentList.Add("-Mode");
            startInfo.ArgumentList.Add(isInstaller ? "installer" : "portable");

            try
            {
                return Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Le programme de mise à jour n'a pas pu démarrer.");
            }
            catch
            {
                try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
                throw;
            }
        }

        private static string NormalizeDirectory(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
