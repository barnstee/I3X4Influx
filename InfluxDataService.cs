using InfluxDB.Client;
using InfluxDB.Client.Core.Flux.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace I3X4Influx
{
    /// <summary>
    /// InfluxDB (Flux) backed data access for the I3X adapter. It is the counterpart of the
    /// Azure Data Explorer <c>ADXDataService</c> in the I3X4Kusto adapter and returns the exact same
    /// row shape, so the controllers and <see cref="Isa95Hierarchy"/> are backend agnostic.
    ///
    /// Schema mapping (OPC UA PubSub payloads ingested into InfluxDB, e.g. by Telegraf):
    ///   ADX <c>opcua_telemetry</c>       -> the telemetry measurement (INFLUX_MEASUREMENT).
    ///   ADX <c>Subject</c>               -> the <c>datasetWriterId</c> tag.
    ///   ADX <c>Name</c>                  -> the Influx field key (<c>_field</c>).
    ///   ADX <c>Timestamp</c> / <c>Value</c> -> <c>_time</c> / <c>_value</c>.
    ///   ADX <c>opcua_metadata_lkv</c>    -> the metadata measurement (INFLUX_METADATA_MEASUREMENT),
    ///                                       whose <c>metaName</c> tag carries the OPC UA DataSetName.
    /// </summary>
    public class InfluxDataService
    {
        // ISA-95 levels, from the top of the containment hierarchy to the bottom. They are optional tags
        // on the metadata measurement: producers that do not emit them simply yield an empty path, which
        // Isa95Hierarchy handles by grouping the assets under a synthetic namespace container instead.
        private static readonly string[] Isa95Levels = { "Enterprise", "Site", "Area", "Line", "Workcell" };

        private readonly object _clientLock = new();
        private InfluxDBClient _client;

        // Cache for the ISA-95 leaf-asset metadata. Building it costs several Flux round trips (the writer
        // list, the metadata rows and one field-key query per writer) and is needed by nearly every
        // /objects, /objecttypes, /namespaces and /subscriptions request; OPC UA metadata changes rarely,
        // so caching it for a short TTL drastically cuts the query volume against InfluxDB. The TTL is
        // configurable via I3X_METADATA_CACHE_SECONDS (default 60, minimum 0 = disabled).
        private readonly object _metadataCacheLock = new();
        private List<Dictionary<string, object>> _metadataCache;
        private DateTime _metadataCacheUtc = DateTime.MinValue;

        // Cache for the namespace URIs, which are read straight from the metadata measurement (one Flux
        // round trip) and share the same TTL as the leaf-asset cache.
        private readonly object _namespaceCacheLock = new();
        private List<string> _namespaceCache;
        private DateTime _namespaceCacheUtc = DateTime.MinValue;

        /// <summary>InfluxDB endpoint. Defaults to a local server.</summary>
        private static string Url => Environment.GetEnvironmentVariable("INFLUX_URL") ?? "http://localhost:8086";

        /// <summary>InfluxDB organization.</summary>
        private static string Org => Environment.GetEnvironmentVariable("INFLUX_ORG") ?? "iot";

        /// <summary>InfluxDB bucket holding the OPC UA telemetry and metadata.</summary>
        private static string Bucket => Environment.GetEnvironmentVariable("INFLUX_BUCKET") ?? "mqtt";

        /// <summary>Measurement holding the OPC UA telemetry (one field per variable).</summary>
        private static string Measurement => Environment.GetEnvironmentVariable("INFLUX_MEASUREMENT") ?? "opcua_pubsub";

        /// <summary>Measurement holding the OPC UA DataSet metadata.</summary>
        private static string MetadataMeasurement => Environment.GetEnvironmentVariable("INFLUX_METADATA_MEASUREMENT") ?? "opcua_metadata";

        /// <summary>
        /// Lookback window used to discover the available series while browsing. Shorter windows are
        /// significantly faster; writers/variables that have not reported within the window are not listed.
        /// Configurable via INFLUX_BROWSE_RANGE (defaults to "-24h").
        /// </summary>
        private static string BrowseRange => Environment.GetEnvironmentVariable("INFLUX_BROWSE_RANGE") ?? "-24h";

        /// <summary>
        /// Lookback window for current-value ("last known value") queries. This mirrors the ADX adapter's
        /// <c>where Timestamp &gt; now(-1h)</c>. Configurable via INFLUX_LATEST_RANGE.
        /// </summary>
        private static string LatestRange => Environment.GetEnvironmentVariable("INFLUX_LATEST_RANGE") ?? "-1h";

        /// <summary>
        /// Creates the InfluxDB client if it does not exist yet. Called by the controllers on construction,
        /// mirroring the ADX adapter's <c>Connect</c>; the client is a singleton and is safe to reuse.
        /// </summary>
        public void Connect()
        {
            if (_client != null)
            {
                return;
            }

            lock (_clientLock)
            {
                if (_client != null)
                {
                    return;
                }

                string token = Environment.GetEnvironmentVariable("INFLUX_TOKEN");
                if (string.IsNullOrEmpty(token))
                {
                    Console.WriteLine("InfluxDB connection not configured (INFLUX_TOKEN missing).");
                    return;
                }

                // The client default HTTP timeout is only 10 seconds, which browse-style queries that scan
                // many days of data regularly exceed (surfacing as a TaskCanceledException), so use a
                // longer, configurable timeout instead.
                int timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("INFLUX_TIMEOUT_SECONDS"), out int parsed) && parsed > 0
                    ? parsed
                    : 120;

                InfluxDBClientOptions options = new InfluxDBClientOptions.Builder()
                    .Url(Url)
                    .AuthenticateToken(token)
                    .TimeOut(TimeSpan.FromSeconds(timeoutSeconds))
                    .Build();

                _client = new InfluxDBClient(options);
            }
        }

        public void Dispose()
        {
            lock (_clientLock)
            {
                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }
            }
        }

        /// <summary>
        /// Escapes a value for safe interpolation into a double-quoted Flux string literal.
        /// </summary>
        public static string EscapeFlux(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// Renders a set of values as a Flux array literal for use with <c>contains(value:, set:)</c>.
        /// This is the Flux counterpart of the ADX adapter's KQL <c>in()</c> list.
        /// </summary>
        public static string ToFluxStringSet(IEnumerable<string> values)
        {
            return string.Join(", ", values.Select(v => "\"" + EscapeFlux(v) + "\""));
        }

        /// <summary>
        /// Executes a Flux query and returns every record, or an empty list when the query fails.
        /// A failing query must not take down the whole request: the caller then simply surfaces no data.
        /// </summary>
        public List<FluxRecord> RunQuery(string flux)
        {
            var records = new List<FluxRecord>();

            if (_client == null)
            {
                return records;
            }

            try
            {
                List<FluxTable> tables = _client.GetQueryApi().QueryAsync(flux, Org).GetAwaiter().GetResult();
                foreach (FluxTable table in tables)
                {
                    records.AddRange(table.Records);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("RunInfluxQuery: " + ex.Message);
                records.Clear();
            }

            return records;
        }

        /// <summary>
        /// Returns one row per OPC UA variable (Subject + Name) with its owning asset's ISA-95 path
        /// (Enterprise/Site/Area/Line/Workcell), identity (Subject/DataSetName/DisplayName/Name) and the
        /// resolved NamespaceUri. This is the raw input the ISA-95 hierarchy builder uses to synthesize the
        /// container tree, the per-Subject asset object and the per-variable leaf nodes.
        /// </summary>
        public List<Dictionary<string, object>> GetIsa95LeafAssets()
        {
            int ttlSeconds = GetMetadataCacheSeconds();
            if (ttlSeconds > 0)
            {
                lock (_metadataCacheLock)
                {
                    if (_metadataCache != null &&
                        (DateTime.UtcNow - _metadataCacheUtc).TotalSeconds < ttlSeconds)
                    {
                        return _metadataCache;
                    }
                }
            }

            var rows = QueryIsa95LeafAssets();

            if (ttlSeconds > 0)
            {
                lock (_metadataCacheLock)
                {
                    _metadataCache = rows;
                    _metadataCacheUtc = DateTime.UtcNow;
                }
            }

            return rows;
        }

        private List<Dictionary<string, object>> QueryIsa95LeafAssets()
        {
            var rows = new List<Dictionary<string, object>>();

            // Per-writer metadata (DataSetName and, when the producer emits them, the ISA-95 levels).
            Dictionary<string, WriterMetadata> metadataByWriter = QueryWriterMetadata();

            // Discovering the available (datasetWriterId, _field) pairs by scanning telemetry would force
            // the storage engine to open one series per pair, which on a high-cardinality bucket regularly
            // outlives the HTTP timeout. The schema package answers the same question from the index only,
            // so enumerate the writers first and then the field keys per writer: no point data is read.
            List<string> writers = QueryTagValues("datasetWriterId", writerFilter: null);
            if (writers.Count == 0)
            {
                // No writer tag indexed in the browse window: fall back to the writers the metadata knows
                // about, so a bucket whose telemetry is idle still exposes its structure.
                writers = metadataByWriter.Keys.ToList();
            }

            foreach (string writer in writers)
            {
                metadataByWriter.TryGetValue(writer, out WriterMetadata metadata);

                string dataSetName = metadata?.DataSetName ?? string.Empty;
                string namespaceUri = NamespaceUriFromDataSetName(dataSetName) ?? string.Empty;

                // The OPC UA metadata rarely carries explicit ISA-95 tags in InfluxDB; UA Cloud Publisher
                // instead encodes the hierarchy in the ApplicationUri of the DataSetName. Fall back to it so
                // the same Enterprise/Site/Area/Line/Workcell tree is built as in the ADX adapter, where the
                // metadata table has those columns.
                Dictionary<string, string> isa95 = Isa95FromDataSetName(dataSetName);

                foreach (string field in QueryTagValues("_field", writer))
                {
                    var row = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["Subject"] = writer,
                        ["DataSetName"] = dataSetName,
                        ["Name"] = field,
                        // Mirror what UA Cloud Action returns when browsing InfluxDB: the browse name is the
                        // telemetry field, the display name is the expanded node id of that variable, and the
                        // node id is the dataset's own ExpandedNodeId as recorded in the metadata.
                        ["DisplayName"] = string.IsNullOrEmpty(namespaceUri)
                            ? field
                            : BuildExpandedNodeId(field, namespaceUri),
                        // InfluxDB carries no OPC UA type information alongside the telemetry, so the type
                        // columns stay empty; the hierarchy then falls back to its generic variable type.
                        ["Type"] = string.Empty,
                        ["DataType"] = string.Empty,
                        ["BuiltInType"] = string.Empty,
                        ["NodeId"] = string.IsNullOrEmpty(dataSetName) ? field : dataSetName,
                        ["NamespaceUri"] = namespaceUri
                    };

                    foreach (string level in Isa95Levels)
                    {
                        if (metadata != null && metadata.Isa95.TryGetValue(level, out string value))
                        {
                            row[level] = value;
                        }
                        else
                        {
                            row[level] = isa95.TryGetValue(level, out string derived) ? derived : string.Empty;
                        }
                    }

                    rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// Returns the distinct OPC UA namespace URIs known to this InfluxDB bucket, derived from the
        /// DataSetName recorded per dataset writer.
        /// </summary>
        public List<string> GetNamespaceUris()
        {
            int ttlSeconds = GetMetadataCacheSeconds();
            if (ttlSeconds > 0)
            {
                lock (_namespaceCacheLock)
                {
                    if (_namespaceCache != null &&
                        (DateTime.UtcNow - _namespaceCacheUtc).TotalSeconds < ttlSeconds)
                    {
                        return _namespaceCache;
                    }
                }
            }

            // Read the DataSetName of every writer the metadata measurement knows about rather than only
            // those that surfaced in the leaf-asset scan: a writer whose telemetry is outside the browse
            // window, or that currently exposes no field keys, still declares a valid namespace.
            var uris = QueryWriterMetadata().Values
                .Select(m => NamespaceUriFromDataSetName(m.DataSetName))
                .Where(uri => !string.IsNullOrEmpty(uri))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (ttlSeconds > 0)
            {
                lock (_namespaceCacheLock)
                {
                    _namespaceCache = uris;
                    _namespaceCacheUtc = DateTime.UtcNow;
                }
            }

            return uris;
        }

        /// <summary>
        /// Returns the latest reading per (Subject, Name) for the given subjects, as rows carrying
        /// <c>Subject</c>, <c>Name</c>, <c>Timestamp</c> and <c>Value</c>. This is the Flux equivalent of the
        /// ADX <c>summarize arg_max(Timestamp, Value) by Subject, Name</c>: the telemetry series key is
        /// (datasetWriterId, _field), so <c>last()</c> yields exactly one row per pair, and applying it
        /// straight after range+filter lets the storage engine answer it without a full scan.
        /// </summary>
        public List<Dictionary<string, object>> GetLatestValues(IReadOnlyCollection<string> subjects)
        {
            if (subjects == null || subjects.Count == 0)
            {
                return new List<Dictionary<string, object>>();
            }

            string flux = BuildTelemetryQuery(subjects, LatestRange, "now()") + " |> last()";

            return ToTelemetryRows(RunQuery(flux));
        }

        /// <summary>
        /// Returns every reading for the given subjects between <paramref name="startTime"/> and
        /// <paramref name="endTime"/> (RFC 3339), as rows carrying <c>Subject</c>, <c>Name</c>,
        /// <c>Timestamp</c> and <c>Value</c>.
        /// </summary>
        public List<Dictionary<string, object>> GetHistory(IReadOnlyCollection<string> subjects, string startTime, string endTime)
        {
            if (subjects == null || subjects.Count == 0)
            {
                return new List<Dictionary<string, object>>();
            }

            // Absolute RFC 3339 bounds keep the scan proportional to the requested window; an open-ended
            // start would make InfluxDB read every shard in the bucket before returning anything. Flux's
            // range() takes a time, not a string, so the RFC 3339 bounds are converted with time(v: "...").
            string flux = BuildTelemetryQuery(subjects, ToFluxTime(startTime), ToFluxTime(endTime));

            return ToTelemetryRows(RunQuery(flux));
        }

        // Renders an RFC 3339 timestamp as a Flux time value. range() rejects a bare string literal with
        // "error calling function "range": value is not a time, got string".
        private static string ToFluxTime(string rfc3339) => "time(v: \"" + EscapeFlux(rfc3339) + "\")";

        private static string BuildTelemetryQuery(IEnumerable<string> subjects, string rangeStart, string rangeStop)
        {
            string set = ToFluxStringSet(subjects);

            return "from(bucket: \"" + EscapeFlux(Bucket) + "\")"
                 + " |> range(start: " + rangeStart + ", stop: " + rangeStop + ")"
                 + " |> filter(fn: (r) => r._measurement == \"" + EscapeFlux(Measurement) + "\")"
                 + " |> filter(fn: (r) => contains(value: r.datasetWriterId, set: [" + set + "]))";
        }

        /// <summary>
        /// Projects Flux telemetry records onto the (Subject, Name, Timestamp, Value) row shape the
        /// controllers expect, matching the ADX adapter's <c>project Subject, Name, Timestamp, Value</c>.
        /// </summary>
        private static List<Dictionary<string, object>> ToTelemetryRows(IEnumerable<FluxRecord> records)
        {
            var rows = new List<Dictionary<string, object>>();

            foreach (FluxRecord record in records)
            {
                string subject = record.GetValueByKey("datasetWriterId")?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(subject))
                {
                    continue;
                }

                rows.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Subject"] = subject,
                    ["Name"] = record.GetField() ?? string.Empty,
                    ["Timestamp"] = record.GetTime()?.ToDateTimeUtc() ?? DateTime.MinValue,
                    ["Value"] = FormatValue(record.GetValue())
                });
            }

            return rows;
        }

        // The I3X payload carries values as strings, mirroring the ADX adapter's "Value = tostring(Value)".
        // Format invariantly so a decimal point is never rendered as a comma on non-English hosts.
        private static string FormatValue(object value)
        {
            return value switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };
        }

        /// <summary>
        /// Returns the distinct values of <paramref name="tag"/> recorded for the telemetry measurement
        /// within <see cref="BrowseRange"/>, optionally restricted to a single datasetWriterId. This uses
        /// the schema package, which is answered from metadata rather than by scanning series data, and is
        /// therefore orders of magnitude cheaper than a "filter + last()" browse query.
        /// </summary>
        private List<string> QueryTagValues(string tag, string writerFilter)
        {
            string predicate = string.IsNullOrEmpty(writerFilter)
                ? "(r) => r._measurement == \"" + EscapeFlux(Measurement) + "\""
                : "(r) => r._measurement == \"" + EscapeFlux(Measurement) + "\" and r.datasetWriterId == \"" + EscapeFlux(writerFilter) + "\"";

            string flux = "import \"influxdata/influxdb/schema\"\n"
                        + "schema.tagValues(bucket: \"" + EscapeFlux(Bucket) + "\", tag: \"" + EscapeFlux(tag) + "\", "
                        + "predicate: " + predicate + ", start: " + BrowseRange + ")";

            return RunQuery(flux)
                .Select(r => r.GetValueByKey("_value")?.ToString())
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();
        }

        /// <summary>
        /// Maps each datasetWriterId to the DataSetName (and any ISA-95 tags) recorded for it in the
        /// metadata measurement. Filtering to a single field and taking <c>last()</c> yields the current row
        /// per series, which is the Flux equivalent of the ADX <c>opcua_metadata_lkv</c> ("last known
        /// value") materialized view.
        /// </summary>
        private Dictionary<string, WriterMetadata> QueryWriterMetadata()
        {
            var result = new Dictionary<string, WriterMetadata>(StringComparer.Ordinal);
            var newestByWriter = new Dictionary<string, DateTime>(StringComparer.Ordinal);

            string flux = "from(bucket: \"" + EscapeFlux(Bucket) + "\")"
                        + " |> range(start: " + BrowseRange + ")"
                        + " |> filter(fn: (r) => r._measurement == \"" + EscapeFlux(MetadataMeasurement) + "\")"
                        + " |> last()";

            foreach (FluxRecord record in RunQuery(flux))
            {
                string writer = record.GetValueByKey("datasetWriterId")?.ToString();
                if (string.IsNullOrEmpty(writer))
                {
                    continue;
                }

                // A writer can report several metadata series; keep the most recent one. Without a group()
                // the query returns one row per series, but the result set is tiny (one row per field).
                DateTime time = record.GetTime()?.ToDateTimeUtc() ?? DateTime.MinValue;
                if (newestByWriter.TryGetValue(writer, out DateTime existing) && time < existing)
                {
                    continue;
                }

                var metadata = new WriterMetadata
                {
                    DataSetName = record.GetValueByKey("metaName")?.ToString() ?? string.Empty
                };

                foreach (string level in Isa95Levels)
                {
                    // Producers name the ISA-95 tags either exactly as the OPC UA metadata does
                    // ("Enterprise") or in the lower-case style Telegraf tends to emit ("enterprise").
                    string value = record.GetValueByKey(level)?.ToString()
                                ?? record.GetValueByKey(level.ToLowerInvariant())?.ToString();

                    if (!string.IsNullOrEmpty(value))
                    {
                        metadata.Isa95[level] = value;
                    }
                }

                newestByWriter[writer] = time;
                result[writer] = metadata;
            }

            return result;
        }

        /// <summary>
        /// Extracts the OPC UA namespace URI from a metadata DataSetName. InfluxDB stores it as a full
        /// ExpandedNodeId, e.g. <c>urn:&lt;applicationUri&gt;;nsu=&lt;namespaceUri&gt;;s=&lt;identifier&gt;</c>,
        /// where the leading <c>urn:</c> segment is the ApplicationUri rather than the namespace. The value
        /// may also arrive as a JSON array of such values, in which case the first entry is used. This
        /// mirrors <c>OpcUaNodeId.NamespaceUriFromDataSetName</c> in UA-CloudAction.
        /// </summary>
        public static string NamespaceUriFromDataSetName(string dataSetName)
        {
            string value = FirstNamespaceUri(dataSetName);
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            string[] segments = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Prefer an explicit "nsu=<uri>" component.
            foreach (string segment in segments)
            {
                if (segment.StartsWith("nsu=", StringComparison.OrdinalIgnoreCase))
                {
                    string uri = segment.Substring(4).Trim();
                    if (!string.IsNullOrEmpty(uri))
                    {
                        return uri;
                    }
                }
            }

            // Otherwise fall back to the first segment that looks like a URI. A prefix test rather than
            // Uri.IsWellFormedUriString: namespace URIs regularly carry characters that would have to be
            // percent-encoded to pass the stricter check, and dropping them would lose real namespaces.
            foreach (string segment in segments)
            {
                if (LooksLikeUri(segment))
                {
                    return segment;
                }
            }

            return null;
        }

        /// <summary>
        /// Normalizes a namespace value read from the OPC UA metadata. The metadata may store a single
        /// value, or a JSON array of them (e.g. <c>["http://.../a/","http://.../b/"]</c>); in the latter
        /// case the first non-empty entry is used.
        /// </summary>
        public static string FirstNamespaceUri(string namespaceValue)
        {
            if (string.IsNullOrWhiteSpace(namespaceValue))
            {
                return null;
            }

            string trimmed = namespaceValue.Trim();
            if (trimmed.StartsWith('['))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(trimmed);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement element in document.RootElement.EnumerateArray())
                        {
                            string uri = element.GetString();
                            if (!string.IsNullOrWhiteSpace(uri))
                            {
                                return uri;
                            }
                        }
                    }

                    return null;
                }
                catch (JsonException)
                {
                    // Not valid JSON; fall through and treat it as a plain string.
                }
            }

            return trimmed;
        }

        private static bool LooksLikeUri(string value) =>
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("urn:", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Builds an expanded string NodeId (<c>nsu=&lt;namespaceUri&gt;;s=&lt;tag&gt;</c>) for a telemetry
        /// field, matching the display names UA Cloud Action returns when browsing InfluxDB.
        /// </summary>
        public static string BuildExpandedNodeId(string tag, string namespaceUri) => "nsu=" + namespaceUri + ";s=" + tag;

        /// <summary>
        /// Extracts the ApplicationUri from a metadata DataSetName, i.e. the leading <c>urn:</c> component of
        /// an ExpandedNodeId such as <c>urn:&lt;applicationUri&gt;;nsu=&lt;namespaceUri&gt;;i=385</c>. This is
        /// what distinguishes individual servers publishing under a shared namespace URI.
        /// </summary>
        public static string ApplicationUriFromDataSetName(string dataSetName)
        {
            string value = FirstNamespaceUri(dataSetName);
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            foreach (string segment in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (segment.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
                {
                    return segment;
                }
            }

            return null;
        }

        /// <summary>
        /// Derives the ISA-95 hierarchy from the ApplicationUri of a metadata DataSetName. OPC UA
        /// ApplicationUris are dotted names ordered from the most specific part to the least specific one,
        /// e.g. <c>urn:assembly.line1.building1.munich.contoso</c>, which reversed is the ISA-95 path
        /// Enterprise=contoso, Site=munich, Area=building1, Line=line1, Workcell=assembly. This is the
        /// InfluxDB equivalent of the Enterprise/Site/Area/Line/Workcell columns the ADX metadata table
        /// exposes to the I3X4Kusto adapter. Deeper names than the five ISA-95 levels are kept together in
        /// the Workcell so no part of the path is lost.
        /// </summary>
        public static Dictionary<string, string> Isa95FromDataSetName(string dataSetName)
        {
            var levels = new Dictionary<string, string>(StringComparer.Ordinal);

            string applicationUri = ApplicationUriFromDataSetName(dataSetName);
            if (string.IsNullOrEmpty(applicationUri))
            {
                return levels;
            }

            // Strip the "urn:" scheme; what remains is the dotted, most-specific-first name.
            string[] parts = applicationUri.Substring(4)
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 0)
            {
                return levels;
            }

            Array.Reverse(parts);

            for (int i = 0; i < Isa95Levels.Length && i < parts.Length; i++)
            {
                bool isLastLevel = i == Isa95Levels.Length - 1;
                levels[Isa95Levels[i]] = isLastLevel && parts.Length > Isa95Levels.Length
                    ? string.Join(".", parts.Skip(i).Reverse())
                    : parts[i];
            }

            return levels;
        }

        // TTL for the metadata cache, in seconds. Default 60, minimum 0 (disables the cache).
        private static int GetMetadataCacheSeconds()
        {
            string raw = Environment.GetEnvironmentVariable("I3X_METADATA_CACHE_SECONDS");
            if (int.TryParse(raw, out int seconds) && seconds >= 0)
            {
                return seconds;
            }

            return 60;
        }

        private sealed class WriterMetadata
        {
            public string DataSetName { get; set; } = string.Empty;

            public Dictionary<string, string> Isa95 { get; } = new(StringComparer.Ordinal);
        }
    }
}
