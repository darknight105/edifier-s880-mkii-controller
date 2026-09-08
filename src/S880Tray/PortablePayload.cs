using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace S880Tray;

internal sealed record PortablePayloadLocation(
    string BackendExecutable,
    string ProfilePath,
    string BackendSha256,
    string ProfileSha256,
    string Directory,
    bool ExtractedFromEmbeddedResources);

internal static class PortablePayload
{
    private const string BackendResource = "S880Tray.Portable.s880ctl.exe";
    private const string ProfileResource = "S880Tray.Portable.EdifierS880MK2CN.json";
    private const string ManifestResource = "S880Tray.Portable.payload-manifest.json";

    public static bool HasEmbeddedPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo(ManifestResource) is not null;

    public static PortablePayloadLocation Resolve(string appDirectory, string? dataDirectory = null)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var manifest = TryReadManifest(assembly);
        if (manifest is not null) return ExtractEmbedded(assembly, manifest, dataDirectory);

        var adjacentBackend = Path.Combine(appDirectory, "backend", "s880ctl.exe");
        var adjacentProfile = Path.Combine(appDirectory, "profiles", "EdifierS880MK2CN.json");
        if (File.Exists(adjacentBackend) && File.Exists(adjacentProfile))
        {
            return new(
                adjacentBackend,
                adjacentProfile,
                HashFile(adjacentBackend),
                HashFile(adjacentProfile),
                appDirectory,
                false);
        }

        throw new InvalidOperationException(
            "The control program or speaker profile is missing. Build the development layout or publish with scripts/publish-portable.ps1.");
    }

    private static PortablePayloadLocation ExtractEmbedded(Assembly assembly, PayloadManifest manifest, string? dataDirectory)
    {
        ValidateHashText(manifest.BackendSha256, "backend");
        ValidateHashText(manifest.ProfileSha256, "profile");
        var scope = manifest.BackendSha256[..16] + "-" + manifest.ProfileSha256[..16];
        var cacheBase = dataDirectory;
        if (string.IsNullOrWhiteSpace(cacheBase))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new InvalidOperationException("Windows did not provide a Local AppData directory for the internal portable payload cache.");
            cacheBase = Path.Combine(localAppData, "S880Controller");
        }
        var cacheDirectory = Path.Combine(Path.GetFullPath(cacheBase), "payload-cache", scope);
        Directory.CreateDirectory(cacheDirectory);

        using var mutex = new Mutex(false, "Local\\S880Controller-Payload-" + scope);
        var ownsMutex = false;
        try
        {
            try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
                throw new InvalidOperationException("Timed out while another controller instance prepared the portable backend.");

            var backend = Path.Combine(cacheDirectory, "s880ctl.exe");
            var profile = Path.Combine(cacheDirectory, "EdifierS880MK2CN.json");
            EnsureResourceFile(assembly, BackendResource, backend, manifest.BackendSha256);
            EnsureResourceFile(assembly, ProfileResource, profile, manifest.ProfileSha256);
            return new(backend, profile, manifest.BackendSha256, manifest.ProfileSha256, cacheDirectory, true);
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }

    private static PayloadManifest? TryReadManifest(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ManifestResource);
        if (stream is null) return null;
        return JsonSerializer.Deserialize<PayloadManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The embedded portable payload manifest is empty.");
    }

    private static void EnsureResourceFile(Assembly assembly, string resourceName, string destination, string expectedHash)
    {
        if (File.Exists(destination) && HashFile(destination).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return;

        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException("Embedded portable payload is missing: " + resourceName);
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.SequentialScan))
            {
                source.CopyTo(target);
                target.Flush(true);
            }
            var temporaryHash = HashFile(temporary);
            if (!temporaryHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Embedded payload hash mismatch for {Path.GetFileName(destination)}: expected {expectedHash}, got {temporaryHash}.");
            File.Move(temporary, destination, true);
            var finalHash = HashFile(destination);
            if (!finalHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Extracted payload hash mismatch for {Path.GetFileName(destination)}: expected {expectedHash}, got {finalHash}.");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    internal static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ValidateHashText(string hash, string label)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException($"The embedded {label} SHA-256 is invalid.");
    }

    private sealed class PayloadManifest
    {
        public string BackendSha256 { get; set; } = "";
        public string ProfileSha256 { get; set; } = "";
    }
}
