// Downloads PX4 parameter metadata (QGroundControl format) and caches locally.
using System.Collections.Frozen;
using System.Globalization;
using System.Xml.Linq;
using DivyaLink.Models;

namespace DivyaLink.Services.Firmware;

public sealed class Px4ParameterMetadataService
{
    private const string MetadataUrl =
        "https://raw.githubusercontent.com/mavlink/qgroundcontrol/master/src/FirmwarePlugin/PX4/PX4ParameterFactMetaData.xml";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly ILogger<Px4ParameterMetadataService> _log;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private FrozenDictionary<string, ParameterMeta>? _cache;

    public string MetadataStatus { get; private set; } = "Not loaded";
    public bool MetadataLoaded { get; private set; }

    public Px4ParameterMetadataService(HttpClient http, ILogger<Px4ParameterMetadataService> log)
    {
        _http = http;
        _log = log;
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DivyaLink", "param_metadata");
        Directory.CreateDirectory(cacheDir);
        _cachePath = Path.Combine(cacheDir, "px4.parameters.xml");
    }

    public async Task AnnotateAsync(IEnumerable<ParameterEntry> entries, CancellationToken ct = default)
    {
        var dict = await EnsureLoadedAsync(ct);
        foreach (var entry in entries)
        {
            if (!dict.TryGetValue(entry.Id.ToUpperInvariant(), out var meta)) continue;
            entry.DisplayName   = meta.HumanName;
            entry.Description   = meta.Description;
            entry.Units         = meta.Units;
            entry.RangeMin      = meta.RangeMin;
            entry.RangeMax      = meta.RangeMax;
            entry.Increment     = meta.Increment;
            entry.AllowedValues = meta.AllowedValues;
        }
    }

    public async Task<ParameterMeta?> GetMetaAsync(string paramName, CancellationToken ct = default)
    {
        var dict = await EnsureLoadedAsync(ct);
        dict.TryGetValue(paramName.ToUpperInvariant(), out var meta);
        return meta;
    }

    public void PurgeCache()
    {
        try
        {
            if (File.Exists(_cachePath)) File.Delete(_cachePath);
            _loadLock.Wait();
            try
            {
                _cache = null;
                MetadataLoaded = false;
                MetadataStatus = "Not loaded (purged)";
            }
            finally
            {
                _loadLock.Release();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[PX4 Metadata] Failed to purge cache");
        }
    }

    private async Task<FrozenDictionary<string, ParameterMeta>> EnsureLoadedAsync(CancellationToken ct)
    {
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_cache != null) return _cache;
            var xml = await GetXmlAsync(ct);
            _cache = ParseXml(xml);
            MetadataLoaded = true;
            MetadataStatus = $"Loaded {_cache.Count} PX4 parameters";
            _log.LogInformation("[PX4 Metadata] {Status}", MetadataStatus);
            return _cache;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<string> GetXmlAsync(CancellationToken ct)
    {
        if (File.Exists(_cachePath))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath);
            if (age < CacheTtl)
            {
                MetadataStatus = "Loaded from cache";
                return await File.ReadAllTextAsync(_cachePath, ct);
            }
            _log.LogInformation("[PX4 Metadata] Cache expired, refreshing from QGC metadata");
        }

        _log.LogInformation("[PX4 Metadata] Downloading from {Url}", MetadataUrl);
        var xml = await _http.GetStringAsync(MetadataUrl, ct);
        await File.WriteAllTextAsync(_cachePath, xml, ct);
        MetadataStatus = "Downloaded from QGC metadata";
        return xml;
    }

    private static FrozenDictionary<string, ParameterMeta> ParseXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var dict = new Dictionary<string, ParameterMeta>(StringComparer.OrdinalIgnoreCase);

        foreach (var param in doc.Descendants("parameter"))
        {
            var name = param.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var shortDesc = param.Element("short_desc")?.Value?.Trim() ?? name;
            var longDesc  = param.Element("long_desc")?.Value?.Trim() ?? shortDesc;
            var units     = param.Element("unit")?.Value?.Trim() ?? "";

            float? min = ParseFloat(param.Element("min")?.Value);
            float? max = ParseFloat(param.Element("max")?.Value);
            float? inc = ParseFloat(param.Element("increment")?.Value);

            Dictionary<int, string>? values = null;
            var valuesEl = param.Element("values");
            if (valuesEl != null)
            {
                values = new Dictionary<int, string>();
                foreach (var v in valuesEl.Elements("value"))
                {
                    if (int.TryParse(v.Attribute("code")?.Value, out var code))
                        values[code] = v.Value.Trim();
                }
            }

            dict[name.ToUpperInvariant()] = new ParameterMeta(
                shortDesc, longDesc, units, min, max, inc,
                values?.Count > 0 ? values : null);
        }

        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static float? ParseFloat(string? text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
