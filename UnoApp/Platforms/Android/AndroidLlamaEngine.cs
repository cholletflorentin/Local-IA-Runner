#if ANDROID
using Android.Content;
using Android.Provider;
using AndroidUri = Android.Net.Uri;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace LocalIARunner.Uno.Droid;

public static class AndroidLlamaEngine
{
    private const string EngineArchiveAsset = "llama-b12676-android-arm64.tar.gz";
    private const string EngineVersion = "b12676";
    private const int StartupTimeoutSeconds = 300;
    private static readonly SemaphoreSlim StartGate = new(1, 1);
    private static readonly object OutputLock = new();
    private static readonly Queue<string> RecentOutput = new();
    private static Process? _serverProcess;
    private static string _activeModelPath = "";
    private static int _activePort;

    public static bool IsLoopbackEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && uri.IsLoopback;

    public static Task<(string Path, string DisplayName)> ImportModelAsync(
        AndroidUri uri,
        Action<long>? progress = null) =>
        Task.Run(() => ImportModel(uri, progress));

    private static (string Path, string DisplayName) ImportModel(AndroidUri uri, Action<long>? progress)
    {
        var context = global::Android.App.Application.Context;
        var resolver = context.ContentResolver
            ?? throw new InvalidOperationException("Le sélecteur de fichiers Android n'est pas disponible.");

        var displayName = "modele.gguf";
        using (var cursor = resolver.Query(uri, new[] { OpenableColumns.DisplayName }, null, null, null))
        {
            if (cursor is not null && cursor.MoveToFirst())
            {
                var nameIndex = cursor.GetColumnIndex(OpenableColumns.DisplayName);
                if (nameIndex >= 0 && !cursor.IsNull(nameIndex))
                    displayName = cursor.GetString(nameIndex) ?? displayName;
            }
        }

        displayName = Path.GetFileName(displayName);
        if (!displayName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sélectionne un fichier de modèle au format .gguf.");

        var safeName = new string(displayName
            .Where(character => !Path.GetInvalidFileNameChars().Contains(character))
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "modele.gguf";

        var modelDirectory = GetModelDirectory(context);
        Directory.CreateDirectory(modelDirectory);
        var destination = Path.Combine(modelDirectory, Guid.NewGuid().ToString("N") + "-" + safeName);
        var temporaryPath = destination + ".partial";
        using var input = resolver.OpenInputStream(uri)
            ?? throw new IOException("Android n'a pas pu ouvrir ce fichier.");

        try
        {
            using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024);
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            long reported = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                copied += read;
                if (copied - reported >= 32L * 1024 * 1024)
                {
                    reported = copied;
                    progress?.Invoke(copied);
                }
            }
            output.Flush();
            File.Move(temporaryPath, destination);
            progress?.Invoke(copied);
            return (destination, displayName);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }

    public static void DeleteManagedModel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var context = global::Android.App.Application.Context;
            var managedRoot = Path.GetFullPath(GetModelDirectory(context)).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(managedRoot, StringComparison.Ordinal) && File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch
        {
            // Cleanup must not prevent saving the newly selected model.
        }
    }

    public static async Task EnsureStartedAsync(string modelPath, string endpoint, string apiKey)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Le fichier GGUF choisi est introuvable.", modelPath);
        if (!IsLoopbackEndpoint(endpoint))
            throw new InvalidOperationException("Le moteur embarqué démarre uniquement sur une adresse localhost en HTTP.");

        var endpointUri = new Uri(endpoint);
        var endpointPath = endpointUri.AbsolutePath.TrimEnd('/');
        if (endpointPath.Length > 0 && !endpointPath.Equals("/v1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Pour le moteur local, utilise l'adresse localhost à la racine ou terminée par /v1.");
        var port = endpointUri.Port;

        await StartGate.WaitAsync();
        try
        {
            if (IsRunning(_serverProcess)
                && _activePort == port
                && string.Equals(_activeModelPath, modelPath, StringComparison.Ordinal))
            {
                await WaitUntilReadyAsync(endpoint, apiKey, _serverProcess!);
                return;
            }

            await StopCoreAsync();

            // Reuse a compatible llama.cpp server if the user already started one on localhost.
            if (await IsModelsEndpointReadyAsync(endpoint, apiKey))
                return;

            if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                throw new PlatformNotSupportedException("Le moteur intégré de cette APK fonctionne sur les appareils Android ARM64.");

            var context = global::Android.App.Application.Context;
            var runtimeDirectory = await EnsureEngineExtractedAsync(context);
            var serverPath = Directory
                .EnumerateFiles(runtimeDirectory, "llama-server", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (serverPath is null)
                throw new FileNotFoundException("L'archive llama.cpp de l'APK ne contient pas llama-server.");

            File.SetUnixFileMode(serverPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var processInfo = new ProcessStartInfo
            {
                FileName = serverPath,
                WorkingDirectory = Path.GetDirectoryName(serverPath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            processInfo.ArgumentList.Add("-m");
            processInfo.ArgumentList.Add(modelPath);
            processInfo.ArgumentList.Add("--host");
            processInfo.ArgumentList.Add("127.0.0.1");
            processInfo.ArgumentList.Add("--port");
            processInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var libraryDirectories = Directory
                .EnumerateFiles(runtimeDirectory, "*.so", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var inheritedLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            var libraryPath = string.Join(Path.PathSeparator, libraryDirectories);
            if (!string.IsNullOrWhiteSpace(inheritedLibraryPath))
                libraryPath = string.IsNullOrWhiteSpace(libraryPath)
                    ? inheritedLibraryPath
                    : libraryPath + Path.PathSeparator + inheritedLibraryPath;
            processInfo.Environment["LD_LIBRARY_PATH"] = libraryPath;

            var temporaryDirectory = Path.Combine(context.FilesDir!.AbsolutePath, "LocalIARunner", "Tmp");
            Directory.CreateDirectory(temporaryDirectory);
            processInfo.Environment["TMPDIR"] = temporaryDirectory;

            ResetOutput();
            var process = new Process { StartInfo = processInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => AddOutput(args.Data);
            process.ErrorDataReceived += (_, args) => AddOutput(args.Data);
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Android n'a pas lancé llama-server.");
                _serverProcess = process;
                _activeModelPath = modelPath;
                _activePort = port;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                process.Dispose();
                throw new InvalidOperationException(
                    "Android n'a pas pu exécuter llama-server depuis le stockage privé de l'application. " + ex.Message,
                    ex);
            }

            try
            {
                await WaitUntilReadyAsync(endpoint, apiKey, process);
            }
            catch
            {
                await StopCoreAsync();
                throw;
            }
        }
        finally
        {
            StartGate.Release();
        }
    }

    public static void Stop()
    {
        var process = _serverProcess;
        _serverProcess = null;
        _activeModelPath = "";
        _activePort = 0;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task StopCoreAsync()
    {
        var process = _serverProcess;
        _serverProcess = null;
        _activeModelPath = "";
        _activePort = 0;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool IsRunning(Process? process)
    {
        try { return process is not null && !process.HasExited; }
        catch { return false; }
    }

    private static async Task<string> EnsureEngineExtractedAsync(Context context)
    {
        var filesDirectory = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Le stockage privé Android est indisponible.");
        var runtimeDirectory = Path.Combine(filesDirectory, "LocalIARunner", "llama-" + EngineVersion);
        var readyMarker = Path.Combine(runtimeDirectory, ".ready");
        var serverAlreadyExtracted = Directory.Exists(runtimeDirectory)
            && File.Exists(readyMarker)
            && Directory.EnumerateFiles(runtimeDirectory, "llama-server", SearchOption.AllDirectories).Any();
        if (serverAlreadyExtracted)
            return runtimeDirectory;

        var stagingDirectory = Path.Combine(filesDirectory, "LocalIARunner", "llama-extracting");
        if (Directory.Exists(stagingDirectory))
            Directory.Delete(stagingDirectory, recursive: true);
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            using var archiveAsset = context.Assets!.Open(EngineArchiveAsset);
            using var gzip = new GZipStream(archiveAsset, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            var pendingHardLinks = new List<(string Path, string Target)>();
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(false, CancellationToken.None)) is not null)
            {
                var targetPath = GetSafeArchivePath(stagingDirectory, entry.Name);
                if (targetPath is null)
                {
                    if (entry.DataStream is not null)
                        await entry.DataStream.CopyToAsync(Stream.Null);
                    continue;
                }

                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                    case TarEntryType.DirectoryList:
                        Directory.CreateDirectory(targetPath);
                        if (entry.DataStream is not null)
                            await entry.DataStream.CopyToAsync(Stream.Null);
                        break;
                    case TarEntryType.RegularFile:
                    case TarEntryType.V7RegularFile:
                    case TarEntryType.ContiguousFile:
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                        await using (var output = new FileStream(
                            targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                        {
                            if (entry.DataStream is not null)
                                await entry.DataStream.CopyToAsync(output, 1024 * 1024);
                        }
                        break;
                    case TarEntryType.SymbolicLink:
                        CreateSafeSymbolicLink(stagingDirectory, targetPath, entry.LinkName);
                        break;
                    case TarEntryType.HardLink:
                    {
                        var hardLinkTarget = GetSafeArchivePath(stagingDirectory, entry.LinkName);
                        if (hardLinkTarget is not null)
                            pendingHardLinks.Add((targetPath, hardLinkTarget));
                        break;
                    }
                    default:
                        if (entry.DataStream is not null)
                            await entry.DataStream.CopyToAsync(Stream.Null);
                        break;
                }
            }

            foreach (var link in pendingHardLinks)
            {
                if (!File.Exists(link.Target))
                    throw new InvalidDataException("L'archive llama.cpp contient un lien matériel invalide.");
                Directory.CreateDirectory(Path.GetDirectoryName(link.Path)!);
                File.Copy(link.Target, link.Path, overwrite: true);
            }

            if (!Directory.EnumerateFiles(stagingDirectory, "llama-server", SearchOption.AllDirectories).Any())
                throw new InvalidDataException("llama-server est absent de l'archive intégrée à l'APK.");
            await File.WriteAllTextAsync(readyMarker, EngineVersion);
            if (Directory.Exists(runtimeDirectory))
                Directory.Delete(runtimeDirectory, recursive: true);
            Directory.Move(stagingDirectory, runtimeDirectory);
            return runtimeDirectory;
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
            throw;
        }
    }

    private static string? GetSafeArchivePath(string rootDirectory, string archivePath)
    {
        var relativePath = archivePath.Replace('\\', '/');
        while (relativePath.StartsWith("./", StringComparison.Ordinal))
            relativePath = relativePath[2..];
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;
        if (relativePath.StartsWith("/", StringComparison.Ordinal)
            || relativePath.Contains(':')
            || relativePath.Split('/').Any(part => part == ".."))
            throw new InvalidDataException("L'archive llama.cpp contient un chemin non sûr.");

        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException("Un chemin de l'archive sort de son dossier.");
        return destination;
    }

    private static void CreateSafeSymbolicLink(string root, string linkPath, string linkName)
    {
        var link = linkName.Replace('\\', '/');
        if (Path.IsPathRooted(link) || link.Contains(':'))
            throw new InvalidDataException("L'archive llama.cpp contient un lien symbolique non sûr.");
        var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, link.Replace('/', Path.DirectorySeparatorChar)));
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(rootPath, StringComparison.Ordinal))
            throw new InvalidDataException("Un lien symbolique de l'archive sort de son dossier.");
        var relativeTarget = Path.GetRelativePath(Path.GetDirectoryName(linkPath)!, target);
        File.CreateSymbolicLink(linkPath, relativeTarget);
    }

    private static async Task WaitUntilReadyAsync(string endpoint, string apiKey, Process process)
    {
        var apiUrl = BuildModelsUrl(endpoint);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException("llama-server s'est arrêté pendant le chargement. " + GetOutputTail());
            if (await IsModelsEndpointReadyAsync(apiUrl, apiKey, client, CancellationToken.None))
                return;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        throw new TimeoutException(
            "llama-server n'a pas chargé le modèle après cinq minutes. Vérifie que le téléphone a assez de mémoire et d'espace libre. "
            + GetOutputTail());
    }

    private static async Task<bool> IsModelsEndpointReadyAsync(string endpoint, string apiKey)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        return await IsModelsEndpointReadyAsync(BuildModelsUrl(endpoint), apiKey, client, CancellationToken.None);
    }

    private static async Task<bool> IsModelsEndpointReadyAsync(
        string modelsUrl,
        string apiKey,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static string BuildModelsUrl(string endpoint)
    {
        var root = endpoint.TrimEnd('/');
        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? root + "/models"
            : root + "/v1/models";
    }

    private static string GetModelDirectory(Context context) =>
        Path.Combine(context.FilesDir!.AbsolutePath, "LocalIARunner", "Models");

    private static void ResetOutput()
    {
        lock (OutputLock)
            RecentOutput.Clear();
    }

    private static void AddOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (OutputLock)
        {
            RecentOutput.Enqueue(line);
            while (RecentOutput.Count > 80)
                RecentOutput.Dequeue();
        }
    }

    private static string GetOutputTail()
    {
        lock (OutputLock)
            return RecentOutput.Count == 0 ? "" : " Sortie : " + string.Join(" | ", RecentOutput);
    }
}
#endif
