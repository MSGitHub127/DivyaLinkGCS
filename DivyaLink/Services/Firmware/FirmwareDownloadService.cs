using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DivyaLink.Services.Firmware;

public sealed class FirmwareDownloadService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FirmwareDownloadService> _log;
    private readonly object _manifestLock = new();
    private bool _isDownloadingManifest = false;

    public FirmwareDownloadService(IHttpClientFactory httpClientFactory, ILogger<FirmwareDownloadService> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    /// <summary>
    /// Supported flight controller boards with fast-path template URL mapping.
    /// </summary>
    public static readonly List<BoardDetails> SupportedBoards = new()
    {
        new BoardDetails { BoardId = 50, Name = "Pixhawk 4 (FMUv5)", Px4TargetName = "px4_fmu-v5", ArduPilotPlatformName = "Pixhawk4" },
        new BoardDetails { BoardId = 9, Name = "Cube Black / Pixhawk 2 (FMUv3)", Px4TargetName = "px4_fmu-v3", ArduPilotPlatformName = "CubeBlack" },
        new BoardDetails { BoardId = 8, Name = "Pixhawk 1 (FMUv2)", Px4TargetName = "px4_fmu-v2", ArduPilotPlatformName = "Pixhawk1" },
        new BoardDetails { BoardId = 140, Name = "Cube Orange / Orange+", Px4TargetName = "cubepilot_cubeorange", ArduPilotPlatformName = "CubeOrange" },
        new BoardDetails { BoardId = 11, Name = "Pixracer (FMUv4)", Px4TargetName = "px4_fmu-v4", ArduPilotPlatformName = "Pixracer" },
        new BoardDetails { BoardId = 13, Name = "Pixhawk 3 Pro (FMUv4Pro)", Px4TargetName = "px4_fmu-v4pro", ArduPilotPlatformName = "Pixhawk3Pro" },
        new BoardDetails { BoardId = 51, Name = "Pixhawk 5X (FMUv5X)", Px4TargetName = "px4_fmu-v5x", ArduPilotPlatformName = "Pixhawk5X" },
        new BoardDetails { BoardId = 52, Name = "Pixhawk 6C (FMUv6)", Px4TargetName = "px4_fmu-v6", ArduPilotPlatformName = "Pixhawk6C" },
        new BoardDetails { BoardId = 54, Name = "Pixhawk 6X (FMUv6X)", Px4TargetName = "px4_fmu-v6x", ArduPilotPlatformName = "Pixhawk6X" }
    };

    public sealed class BoardDetails
    {
        public int BoardId { get; set; }
        public string Name { get; set; } = "";
        public string Px4TargetName { get; set; } = "";
        public string ArduPilotPlatformName { get; set; } = "";
    }

    public sealed class FirmwareManifestEntry
    {
        [JsonPropertyName("board_id")] public int? BoardId { get; set; }
        [JsonPropertyName("vehicletype")] public string VehicleType { get; set; } = "";
        [JsonPropertyName("platform")] public string Platform { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("mav-firmware-version-type")] public string VersionType { get; set; } = "";
        [JsonPropertyName("mav-firmware-version-str")] public string VersionStr { get; set; } = "";
        [JsonPropertyName("format")] public string Format { get; set; } = "";
        [JsonPropertyName("brand_name")] public string BrandName { get; set; } = "";
        [JsonPropertyName("latest")] public int Latest { get; set; }
    }

    private sealed class FirmwareManifestRoot
    {
        [JsonPropertyName("firmware")] public List<FirmwareManifestEntry> Firmware { get; set; } = new();
    }

    /// <summary>
    /// Resolve the download URL for the requested firmware.
    /// </summary>
    public async Task<string> GetFirmwareUrlAsync(
        FirmwareFamily family,
        string channel, // "stable", "beta", "master" (for PX4) / "latest" (for ArduPilot)
        string vehicleType, // "Copter", "Plane", "Rover", "Sub"
        int boardId,
        Action<string>? statusCallback = null,
        Action<double>? progressCallback = null,
        CancellationToken ct = default)
    {
        statusCallback?.Invoke("Resolving download URL...");

        if (family == FirmwareFamily.PX4)
        {
            var board = SupportedBoards.FirstOrDefault(b => b.BoardId == boardId);
            string targetName = board != null ? board.Px4TargetName : $"px4_fmu-v{boardId}";
            
            // Build the standard PX4 S3 URL
            string px4Channel = channel.ToLowerInvariant() switch
            {
                "stable" => "stable",
                "beta" => "beta",
                _ => "master"
            };

            return $"https://px4-travis.s3.amazonaws.com/Firmware/{px4Channel}/{targetName}_default.px4";
        }
        else // ArduPilot
        {
            var board = SupportedBoards.FirstOrDefault(b => b.BoardId == boardId);
            if (board != null)
            {
                // Fast path for supported boards
                string fileVehicle = vehicleType.ToLowerInvariant() switch
                {
                    "copter" => "arducopter",
                    "plane" => "arduplane",
                    "rover" => "ardurover",
                    "sub" => "ardusub",
                    _ => "arducopter"
                };

                string apChannel = channel.ToLowerInvariant() switch
                {
                    "stable" => "stable",
                    "beta" => "beta",
                    _ => "latest"
                };

                return $"https://firmware.ardupilot.org/{vehicleType}/{apChannel}/{board.ArduPilotPlatformName}/{fileVehicle}.apj";
            }

            // Fallback: download and search the 67MB ArduPilot manifest
            statusCallback?.Invoke("Custom board ID detected. Fetching ArduPilot manifest...");
            string manifestPath = await EnsureManifestDownloadedAsync(progressCallback, ct);

            statusCallback?.Invoke("Parsing manifest database...");
            var manifest = await LoadManifestAsync(manifestPath, ct);

            statusCallback?.Invoke("Searching manifest for compatible firmware...");
            var matches = manifest.Firmware.Where(f =>
                f.BoardId == boardId &&
                f.Format.Equals("apj", StringComparison.OrdinalIgnoreCase) &&
                f.VehicleType.Equals(vehicleType, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!matches.Any())
            {
                throw new InvalidOperationException($"No compatible ArduPilot APJ firmware found in manifest for Board ID {boardId} ({vehicleType}).");
            }

            // Filter by channel
            List<FirmwareManifestEntry> filteredMatches;
            if (channel.Equals("stable", StringComparison.OrdinalIgnoreCase))
            {
                filteredMatches = matches.Where(f => f.VersionType.StartsWith("STABLE", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else if (channel.Equals("beta", StringComparison.OrdinalIgnoreCase))
            {
                filteredMatches = matches.Where(f => f.VersionType.StartsWith("BETA", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else
            {
                filteredMatches = matches.Where(f => f.VersionType.Equals("DEV", StringComparison.OrdinalIgnoreCase) || f.Latest == 1).ToList();
            }

            if (!filteredMatches.Any())
            {
                // Fallback to whatever is available if the requested channel is empty
                filteredMatches = matches;
            }

            // Sort by version descending and return the latest one
            var bestMatch = filteredMatches
                .OrderByDescending(f => ParseVersion(f.VersionStr))
                .First();

            return bestMatch.Url;
        }
    }

    /// <summary>
    /// Downloads the firmware from a URL and saves it to a temp path.
    /// </summary>
    public async Task<string> DownloadFirmwareToFileAsync(
        string url,
        Action<double> progressCallback,
        CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DivyaLink", "downloads");
        Directory.CreateDirectory(tempDir);
        var ext = Path.GetExtension(url).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
        {
            ext = url.Contains(".px4") ? ".px4" : ".apj";
        }
        var destinationPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}{ext}");

        using var client = _httpClientFactory.CreateClient("FirmwareDownloaderClient");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, read, ct);
            totalRead += read;

            if (contentLength.HasValue)
            {
                double pct = (double)totalRead * 100.0 / contentLength.Value;
                progressCallback?.Invoke(pct);
            }
            else
            {
                progressCallback?.Invoke(-1);
            }
        }

        return destinationPath;
    }

    private async Task<string> EnsureManifestDownloadedAsync(Action<double>? progressCallback, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DivyaLink");
        Directory.CreateDirectory(tempDir);
        var manifestPath = Path.Combine(tempDir, "manifest.json");

        bool shouldDownload = true;
        lock (_manifestLock)
        {
            if (File.Exists(manifestPath))
            {
                var fileInfo = new FileInfo(manifestPath);
                if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc < TimeSpan.FromHours(24) && fileInfo.Length > 10 * 1024 * 1024)
                {
                    shouldDownload = false;
                }
            }
        }

        if (!shouldDownload)
        {
            progressCallback?.Invoke(100.0);
            return manifestPath;
        }

        lock (_manifestLock)
        {
            if (_isDownloadingManifest)
            {
                throw new InvalidOperationException("Manifest download is already in progress. Please try again in a few seconds.");
            }
            _isDownloadingManifest = true;
        }

        try
        {
            string url = "https://firmware.ardupilot.org/manifest.json";
            using var client = _httpClientFactory.CreateClient("FirmwareDownloaderClient");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            
            var tempFile = manifestPath + ".tmp";
            using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[16384];
                long totalRead = 0;
                int read;

                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read, ct);
                    totalRead += read;

                    if (contentLength.HasValue)
                    {
                        double pct = (double)totalRead * 100.0 / contentLength.Value;
                        progressCallback?.Invoke(pct);
                    }
                    else
                    {
                        progressCallback?.Invoke(-1);
                    }
                }
            }

            lock (_manifestLock)
            {
                if (File.Exists(manifestPath)) File.Delete(manifestPath);
                File.Move(tempFile, manifestPath);
            }

            return manifestPath;
        }
        finally
        {
            lock (_manifestLock)
            {
                _isDownloadingManifest = false;
            }
        }
    }

    private static async Task<FirmwareManifestRoot> LoadManifestAsync(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var manifest = await JsonSerializer.DeserializeAsync<FirmwareManifestRoot>(fs, cancellationToken: ct);
        return manifest ?? new FirmwareManifestRoot();
    }

    private static Version ParseVersion(string versionStr)
    {
        if (string.IsNullOrEmpty(versionStr)) return new Version(0, 0, 0);
        var clean = versionStr.TrimStart('v', 'V');
        
        // Remove trailing descriptors (e.g., "-rc1")
        int dashIdx = clean.IndexOf('-');
        if (dashIdx > 0) clean = clean.Substring(0, dashIdx);

        if (Version.TryParse(clean, out var v)) return v;
        return new Version(0, 0, 0);
    }
}
