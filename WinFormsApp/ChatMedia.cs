using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LocalAIApp;

public sealed class ChatAttachment
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    [JsonProperty("content_type")]
    public string ContentType { get; set; } = "application/octet-stream";
}

public static class ChatMediaStorage
{
    public const int MaximumAttachments = 10;
    public const long MaximumTotalBytes = 512L * 1024 * 1024;

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LocalIARunner", "ChatMedia");

    public static ChatAttachment Import(string sourcePath)
    {
        var source = new FileInfo(sourcePath);
        if (!source.Exists)
            throw new FileNotFoundException("Le fichier sélectionné est introuvable.", sourcePath);

        var kind = GetKind(source.Extension);
        if (kind == null)
            throw new InvalidDataException("Format non pris en charge. Choisissez une image, un fichier audio ou une vidéo.");
        if (source.Length > MaximumTotalBytes)
            throw new InvalidDataException("Chaque pièce jointe doit faire 512 Mo ou moins.");

        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var safeName = SanitizeFileName(source.Name);
        var destination = Path.Combine(directory, safeName);
        File.Copy(source.FullName, destination, overwrite: false);
        return new ChatAttachment
        {
            Name = source.Name,
            Path = destination,
            Kind = kind,
            ContentType = GetContentType(source.Extension, kind)
        };
    }

    public static ChatAttachment ImportGenerated(string sourcePath)
    {
        var source = new FileInfo(sourcePath);
        if (!source.Exists)
            throw new FileNotFoundException("Le fichier généré est introuvable.", sourcePath);
        var kind = GetKind(source.Extension);
        if (kind == null)
            throw new InvalidDataException("Le fichier généré n’est pas un média reconnu.");

        Directory.CreateDirectory(Root);
        var generatedDirectory = Path.Combine(Root, "generated", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(generatedDirectory);
        var destination = Path.Combine(generatedDirectory, SanitizeFileName(source.Name));
        File.Copy(source.FullName, destination, overwrite: false);
        return new ChatAttachment
        {
            Name = source.Name,
            Path = destination,
            Kind = kind,
            ContentType = GetContentType(source.Extension, kind)
        };
    }

    public static string? GetMediaUrl(string path)
    {
        try
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.GetFullPath(Root).TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
                return null;

            var relative = System.IO.Path.GetRelativePath(root, fullPath);
            var encoded = string.Join("/", relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                .Select(Uri.EscapeDataString));
            return "https://localia.media/" + encoded;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetKind(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" => "image",
        ".mp3" or ".wav" or ".flac" => "audio",
        ".mp4" or ".webm" or ".mov" or ".mkv" or ".avi" or ".mpeg" or ".mpg" => "video",
        _ => null
    };

    private static string GetContentType(string extension, string kind) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".mpeg" or ".mpg" => "video/mpeg",
        _ => kind + "/octet-stream"
    };

    private static string SanitizeFileName(string fileName)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "media" : sanitized;
    }
}

