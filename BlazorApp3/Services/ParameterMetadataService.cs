// Services/ParameterMetadataService.cs
// Downloads the ArduPilot parameter dictionary (apm.pdef.xml) from the
// ArduPilot autotest server and caches it locally for offline/field use.
//
// Mission Planner does exactly the same thing: on first load it downloads
// the XML dictionary and caches it.  Subsequent loads hit the local file
// unless the cache is older than the configured TTL.
//
// SUPPORTED VEHICLE TYPES
//   ArduCopter  → the default for DivyaLink
//   ArduPlane, Rover, ArduSub, Blimp  → selectable at runtime
//
// XML FORMAT (ArduPilot apm.pdef.xml)
//   <paramfile>
//     <vehicles>
//       <parameters name="ArduCopter">
//         <param name="ATC_RAT_RLL_P" humanName="Rate Roll kP"
//                documentation="Roll rate P gain. ...">
//           <field name="Range">0.01 0.5</field>
//           <field name="Increment">0.005</field>
//           <field name="Units">Hz</field>
//         </param>
//         <param name="FLTMODE1" humanName="Flight Mode 1" documentation="...">
//           <values>
//             <value code="0">Stabilize</value>
//             <value code="2">AltHold</value>
//           </values>
//         </param>
//       </parameters>
//     </vehicles>
//     <libraries>
//       <parameters name="AHRS"> ... </parameters>
//     </libraries>
//   </paramfile>

using System.Collections.Frozen;
using System.Xml.Linq;
using BlazorApp3.Models;

namespace BlazorApp3.Services;

/// <summary>
/// Metadata record as parsed from the ArduPilot XML dictionary.
/// </summary>

public sealed record ParameterMeta(
    string HumanName,
    string Description,
    string Units,
    float? RangeMin,
    float? RangeMax,
    float? Increment,
    IReadOnlyDictionary<int, string>? AllowedValues
);

/// <summary>
/// Vehicle firmware type — controls which parameter dictionary is fetched.
/// </summary>
public enum ArduPilotVehicleType
{
    ArduCopter,
    ArduPlane,
    Rover,
    ArduSub,
    Blimp
}

public sealed class ParameterMetadataService
{
    // ── Constants ─────────────────────────────────────────────────────────

    private const string BaseUrl   = "https://autotest.ardupilot.org/Parameters";
    private const string XmlFile   = "apm.pdef.xml";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    // ── State ─────────────────────────────────────────────────────────────

    private readonly HttpClient _http;
    private readonly ILogger<ParameterMetadataService> _log;
    private readonly string _cacheDir;

    // Vehicle type → metadata dictionary (populated lazily on first Load call)
    private readonly Dictionary<ArduPilotVehicleType, FrozenDictionary<string, ParameterMeta>> _cache = [];
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public string MetadataStatus { get; private set; } = "Not loaded";
    public bool MetadataLoaded  { get; private set; } = false;

    // ── Constructor ───────────────────────────────────────────────────────

    public ParameterMetadataService(
        HttpClient http,
        ILogger<ParameterMetadataService> log)
    {
        _http = http;
        _log  = log;

        // Local cache directory — works on Windows, Linux, macOS
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DivyaLink", "param_metadata");

        Directory.CreateDirectory(_cacheDir);
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the metadata record for a parameter name, or null if unknown.
    /// Loads the dictionary for <paramref name="vehicleType"/> on first call.
    /// </summary>
    public async Task<ParameterMeta?> GetMetaAsync(
        string paramName,
        ArduPilotVehicleType vehicleType = ArduPilotVehicleType.ArduCopter,
        CancellationToken ct = default)
    {
        var dict = await EnsureLoadedAsync(vehicleType, ct);
        dict.TryGetValue(paramName.ToUpperInvariant(), out var meta);
        return meta;
    }

    /// <summary>
    /// Annotates a list of <see cref="ParameterEntry"/> objects with their metadata
    /// in a single pass — more efficient than calling GetMetaAsync per parameter.
    /// </summary>
    public async Task AnnotateAsync(
        IEnumerable<ParameterEntry> entries,
        ArduPilotVehicleType vehicleType = ArduPilotVehicleType.ArduCopter,
        CancellationToken ct = default)
    {
        var dict = await EnsureLoadedAsync(vehicleType, ct);

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

    /// <summary>
    /// Forces a fresh download, bypassing the local file cache.
    /// Useful from a "Refresh Dictionary" button in Developer Mode.
    /// </summary>
    public async Task RefreshAsync(
        ArduPilotVehicleType vehicleType = ArduPilotVehicleType.ArduCopter,
        CancellationToken ct = default)
    {
        var cachePath = CachePath(vehicleType);
        if (File.Exists(cachePath)) File.Delete(cachePath);

        await _loadLock.WaitAsync(ct);
        try
        {
            _cache.Remove(vehicleType);
        }
        finally
        {
            _loadLock.Release();
        }

        await EnsureLoadedAsync(vehicleType, ct);
    }

    // ── Internal ──────────────────────────────────────────────────────────

    private async Task<FrozenDictionary<string, ParameterMeta>> EnsureLoadedAsync(
    ArduPilotVehicleType vehicleType,
    CancellationToken ct)
{
    await _loadLock.WaitAsync(ct);
    try
    {
        if (_cache.TryGetValue(vehicleType, out var cached)) return cached;

        MetadataStatus = $"Downloading {vehicleType} dictionary...";
        var xml    = await GetXmlAsync(vehicleType, ct);
        var parsed = Parse(xml);
        _cache[vehicleType] = parsed;

        MetadataLoaded = parsed.Count > 0;
        MetadataStatus = parsed.Count > 0
            ? $"Loaded {parsed.Count:N0} parameter definitions"
            : "Dictionary unavailable — descriptions hidden. Check internet connection.";

        return parsed;
    }
    finally
    {
        _loadLock.Release();
    }
}

    private async Task<string> GetXmlAsync(ArduPilotVehicleType vehicleType, CancellationToken ct)
    {
        var cachePath = CachePath(vehicleType);

        // Use local cache if it exists and is within TTL
        if (File.Exists(cachePath))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath);
            if (age < CacheTtl)
            {
                _log.LogInformation("Loaded parameter metadata from cache ({Vehicle}, age {Age:hh\\:mm})",
                    vehicleType, age);
                return await File.ReadAllTextAsync(cachePath, ct);
            }
            _log.LogInformation("Cache expired ({Vehicle}), refreshing from ArduPilot server", vehicleType);
        }

        // Download from ArduPilot autotest server
        var url = $"{BaseUrl}/{VehicleFolderName(vehicleType)}/{XmlFile}";
        _log.LogInformation("Downloading parameter dictionary: {Url}", url);

        try
        {
            var xml = await _http.GetStringAsync(url, ct);
            await File.WriteAllTextAsync(cachePath, xml, ct);
            _log.LogInformation("Parameter dictionary cached ({Bytes} bytes)", xml.Length);
            return xml;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Failed to download parameter dictionary: {Error}. " +
                            "Trying stale cache...", ex.Message);

            // Graceful degradation — use stale cache for field use without internet
            if (File.Exists(cachePath))
                return await File.ReadAllTextAsync(cachePath, ct);

            _log.LogWarning("No parameter metadata available — descriptions will be empty");
            return "<paramfile/>";
        }
    }

    /// <summary>
    /// Parses the apm.pdef.xml content into an immutable lookup dictionary.
    /// Merges both &lt;vehicles&gt; and &lt;libraries&gt; sections.
    /// </summary>
    private static FrozenDictionary<string, ParameterMeta> Parse(string xml)
    {
        var result = new Dictionary<string, ParameterMeta>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var doc = XDocument.Parse(xml);

            // Both <vehicles><parameters> and <libraries><parameters> use the same schema
            var paramSections = doc.Descendants("parameters");

            foreach (var section in paramSections)
            {
                foreach (var param in section.Elements("param"))
                {
                    var name = (string?)param.Attribute("name");
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var humanName   = (string?)param.Attribute("humanName") ?? name;
                    var description = (string?)param.Attribute("documentation") ?? string.Empty;

                    // <field name="Range">0.01 0.5</field>
                    float? rangeMin = null, rangeMax = null;
                    var rangeField = param.Elements("field")
                        .FirstOrDefault(f => (string?)f.Attribute("name") == "Range");
                    if (rangeField != null)
                        ParseRange((string?)rangeField, out rangeMin, out rangeMax);

                    // <field name="Increment">0.005</field>
                    float? increment = null;
                    var incrField = param.Elements("field")
                        .FirstOrDefault(f => (string?)f.Attribute("name") == "Increment");
                    if (incrField != null && float.TryParse((string?)incrField,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var inc))
                    {
                        increment = inc;
                    }

                    // <field name="Units">Hz</field>
                    var units = (string?)param.Elements("field")
                        .FirstOrDefault(f => (string?)f.Attribute("name") == "Units") ?? string.Empty;

                    // <values><value code="0">Stabilize</value></values>
                    IReadOnlyDictionary<int, string>? allowedValues = null;
                    var valuesEl = param.Element("values");
                    if (valuesEl != null)
                    {
                        var dict = new Dictionary<int, string>();
                        foreach (var v in valuesEl.Elements("value"))
                        {
                            if (int.TryParse((string?)v.Attribute("code"), out var code))
                                dict[code] = (string?)v ?? string.Empty;
                        }
                        if (dict.Count > 0) allowedValues = dict;
                    }

                    result[name] = new ParameterMeta(
                        HumanName:     humanName,
                        Description:   description.Trim(),
                        Units:         units,
                        RangeMin:      rangeMin,
                        RangeMax:      rangeMax,
                        Increment:     increment,
                        AllowedValues: allowedValues
                    );
                }
            }
        }
        catch (Exception ex)
        {
            // Malformed XML — return empty dictionary rather than crashing
            Console.Error.WriteLine($"[ParameterMetadataService] XML parse error: {ex.Message}");
        }

        return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static void ParseRange(string? raw, out float? min, out float? max)
    {
        min = max = null;
        if (string.IsNullOrWhiteSpace(raw)) return;

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lo)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var hi))
        {
            min = lo;
            max = hi;
        }
    }

    private static string VehicleFolderName(ArduPilotVehicleType v) => v switch
    {
        ArduPilotVehicleType.ArduCopter => "ArduCopter",
        ArduPilotVehicleType.ArduPlane  => "ArduPlane",
        ArduPilotVehicleType.Rover      => "Rover",
        ArduPilotVehicleType.ArduSub    => "ArduSub",
        ArduPilotVehicleType.Blimp      => "Blimp",
        _ => throw new ArgumentOutOfRangeException(nameof(v))
    };

    private string CachePath(ArduPilotVehicleType v) =>
        Path.Combine(_cacheDir, $"apm.pdef.{v}.xml");

    public void PurgeCache()
    {
        try
        {
            if (Directory.Exists(_cacheDir))
            {
                foreach (var file in Directory.GetFiles(_cacheDir))
                {
                    try { File.Delete(file); } catch { }
                }
                _log.LogInformation("[Metadata] Parameter metadata cache directory purged.");
            }
            _loadLock.Wait();
            try
            {
                _cache.Clear();
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
            _log.LogError(ex, "[Metadata] Failed to purge parameter metadata cache.");
        }
    }
}
