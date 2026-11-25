#if !ANDROID
using System.Diagnostics;
using System.Net.Http.Headers;

namespace LocalIARunner.Uno;

/// <summary>Starts and owns the llama.cpp server bundled with desktop releases.</summary>
internal static class DesktopLlamaEngine
{
    private const int StartupTimeoutSeconds = 300;
    private static readonly SemaphoreSlim StartGate = new(1, 1);
    private static readonly object OutputLock = new();
    private static readonly Queue<string> RecentOutput = new();
    private static Process? _serverProcess;
    private static string _activeModelPath = "";
    private static int _activePort;
    private static bool _shuttingDown;

    public static async Task EnsureStartedAsync(string modelPath, string endpoint, string apiKey)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Le fichier GGUF choisi est introuvable.", modelPath);
        if (!modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Le moteur local attend un fichier de modèle .gguf.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
            throw new InvalidOperationException("Le moteur llama.cpp intégré démarre uniquement sur une adresse HTTP localhost.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath.TrimEnd('/').Length > 0
                && !uri.AbsolutePath.TrimEnd('/').Equals("/v1", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Pour le moteur local, utilise localhost à la racine ou avec /v1.");

        modelPath = Path.GetFullPath(modelPath);
        await StartGate.WaitAsync();
        try
        {
            if (_shuttingDown)
                throw new InvalidOperationException("L'application est en cours de fermeture.");

            if (IsRunning(_serverProcess)
                && _activePort == uri.Port
                && string.Equals(_activeModelPath, modelPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                await WaitUntilReadyAsync(endpoint, apiKey, _serverProcess!);
                return;
            }

            await StopCoreAsync();

            // Do not launch a second server when the user already has one at this endpoint.
            if (await IsModelsEndpointReadyAsync(endpoint, apiKey))
                return;

            var (serverPath, runtimeRoot) = FindBundledServer();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(serverPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

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
            processInfo.ArgumentList.Add(uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddBundledLibrariesToPath(processInfo, runtimeRoot);

            ResetOutput();
            var process = new Process { StartInfo = processInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => AddOutput(args.Data);
            process.ErrorDataReceived += (_, args) => AddOutput(args.Data);
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Le système n'a pas lancé llama-server.");
                _serverProcess = process;
                _activeModelPath = modelPath;
                _activePort = uri.Port;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                process.Dispose();
                throw new InvalidOperationException("Impossible de démarrer llama-server depuis le dossier llama-bin. " + ex.Message, ex);
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

    public static async Task StopAsync()
    {
        await StartGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            StartGate.Release();
        }
    }

    public static void Stop()
    {
        _shuttingDown = true;
        var process = _serverProcess;
        _serverProcess = null;
        _activeModelPath = "";
        _activePort = 0;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
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

    private static (string ServerPath, string RuntimeRoot) FindBundledServer()
    {
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "llama-bin");
        if (!Directory.Exists(runtimeRoot))
            throw new DirectoryNotFoundException("Le moteur llama.cpp intégré est introuvable. Vérifie que le dossier llama-bin de l'archive est complet.");

        var serverName = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
        var serverPath = Directory.EnumerateFiles(runtimeRoot, serverName, SearchOption.AllDirectories).FirstOrDefault();
        if (serverPath is null)
            throw new FileNotFoundException($"{serverName} est absent du dossier llama-bin de cette version.");
        return (serverPath, runtimeRoot);
    }

    private static void AddBundledLibrariesToPath(ProcessStartInfo processInfo, string runtimeRoot)
    {
        var librarySuffix = OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        var libraryDirectories = Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.Contains(librarySuffix, StringComparison.OrdinalIgnoreCase);
            })
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Append(Path.GetDirectoryName(processInfo.FileName)!)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();

        var variableName = OperatingSystem.IsMacOS() ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH";
        var inheritedPath = Environment.GetEnvironmentVariable(variableName);
        var libraryPath = string.Join(Path.PathSeparator, libraryDirectories);
        if (!string.IsNullOrWhiteSpace(inheritedPath))
            libraryPath = libraryPath + Path.PathSeparator + inheritedPath;
        processInfo.Environment[variableName] = libraryPath;
    }

    private static async Task WaitUntilReadyAsync(string endpoint, string apiKey, Process process)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var modelsUrl = BuildModelsUrl(endpoint);
        var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning(process))
                throw new InvalidOperationException("llama-server s'est arrêté pendant le chargement du modèle. " + GetOutputTail());
            if (await IsModelsEndpointReadyAsync(modelsUrl, apiKey, client))
                return;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException("llama-server n'a pas chargé le modèle après cinq minutes. Vérifie la mémoire disponible et le fichier GGUF. " + GetOutputTail());
    }

    private static async Task<bool> IsModelsEndpointReadyAsync(string endpoint, string apiKey)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        return await IsModelsEndpointReadyAsync(BuildModelsUrl(endpoint), apiKey, client);
    }

    private static async Task<bool> IsModelsEndpointReadyAsync(string modelsUrl, string apiKey, HttpClient client)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
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
                process.Kill(entireProcessTree: true);
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
            return RecentOutput.Count == 0 ? "" : " Dernières lignes : " + string.Join(" | ", RecentOutput);
    }
}
#endif
