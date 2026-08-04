using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;

namespace I3X4Influx.Controllers
{
    [ApiController]
    [Route("v1/objects")]
    public sealed class ObjectsController : ControllerBase
    {
        private readonly InfluxDataService _influx;

        public ObjectsController(InfluxDataService influx)
        {
            _influx = influx;
            _influx.Connect();
        }

        /// <summary>Synthetic type id used for ISA-95 container objects that have no OPC UA type.</summary>
        private const string ContainerTypePrefix = "ISA95:";

        /// <summary>
        /// Builds the ISA-95 containment tree from the current metadata. The tree synthesizes the
        /// intermediate container levels (Enterprise/Site/Area/Line/Workcell) that are not materialized as
        /// their own rows, and attaches each leaf asset under its deepest populated level.
        /// </summary>
        private Isa95Hierarchy BuildHierarchy() => new(_influx.GetIsa95LeafAssets());

        /// <summary>Maps an ISA-95 tree node (container/asset or variable) to an I3X object response.</summary>
        private static ObjectInstanceResponse MapNode(Isa95Hierarchy.Node node, bool includeMetadata)
        {
            if (node.Kind == Isa95Hierarchy.NodeKind.Variable)
            {
                // Variable leaf: the value-bearing node. Its type is the OPC UA DataType from the metadata,
                // namespace-qualified so it matches the ids returned by ObjectTypesController and stays unique
                // across namespaces (enabling namespace -> type -> object navigation).
                string typeToken = node.TypeToken;
                string variableType = ObjectTypeId.Build(node.NamespaceUri, typeToken);
                return new ObjectInstanceResponse(
                    node.ElementId,
                    node.DisplayName,
                    variableType,
                    false,
                    node.ParentId,
                    false,
                    includeMetadata ? BuildMetadata(node.NamespaceUri, variableType) : null);
            }

            // Container level (ISA-95 level or the asset/station at the deepest level). These are structural
            // ISA-95 nodes, not OPC UA objects, so they always get an ISA-95 type derived from their level
            // (e.g. "ISA95:Site", "ISA95:Workcell"). Both are compositions.
            string typeId = ContainerTypePrefix + node.Level;

            return new ObjectInstanceResponse(
                node.ElementId,
                node.DisplayName,
                typeId,
                true,
                node.ParentId,
                false,
                includeMetadata ? BuildMetadata(node.NamespaceUri, typeId) : null);
        }

        [HttpGet]
        public ActionResult<SuccessResponse<IReadOnlyList<ObjectInstanceResponse>>> GetObjects(
            [FromQuery] string typeElementId = null,
            [FromQuery] bool includeMetadata = false,
            [FromQuery] bool? root = null)
        {
            var hierarchy = BuildHierarchy();

            IEnumerable<Isa95Hierarchy.Node> nodes;

            if (!string.IsNullOrEmpty(typeElementId))
            {
                if (typeElementId.StartsWith(ContainerTypePrefix, StringComparison.Ordinal))
                {
                    // ISA-95 container level type (e.g. "ISA95:Site"): return the container objects at that
                    // level so a namespace -> type -> object drilldown works for structural nodes too.
                    nodes = hierarchy.ById.Values.Where(n =>
                        n.Kind == Isa95Hierarchy.NodeKind.Container &&
                        string.Equals(ContainerTypePrefix + n.Level, typeElementId, StringComparison.Ordinal));
                }
                else
                {
                    // A type is a namespace-qualified OPC UA DataType ("<namespaceUri>#<dataType>"), matching the
                    // ids returned by ObjectTypesController. Filtering by it returns the variables of that type -
                    // this is how the I3X namespace view drills from a namespace's types to their objects.
                    var parsed = ObjectTypeId.Parse(typeElementId);
                    nodes = hierarchy.ById.Values.Where(n =>
                        n.Kind == Isa95Hierarchy.NodeKind.Variable &&
                        (parsed is { } p
                            ? string.Equals(n.NamespaceUri, p.NamespaceUri, StringComparison.Ordinal) &&
                              string.Equals(n.TypeToken, p.Name, StringComparison.Ordinal)
                            : string.Equals(n.TypeToken, typeElementId, StringComparison.Ordinal)));
                }
            }
            else if (root == true)
            {
                // Roots are the top-most ISA-95 container levels (or root assets when a server provides no
                // ISA-95 context at all).
                nodes = hierarchy.Roots;
            }
            else
            {
                nodes = hierarchy.ById.Values;
            }

            var results = nodes.Select(n => MapNode(n, includeMetadata)).ToList();

            return Ok(new SuccessResponse<IReadOnlyList<ObjectInstanceResponse>>(true, results));
        }

        [HttpPost("list")]
        public ActionResult<BulkResponse<ObjectInstanceResponse>> ListObjects([FromBody] GetObjectsRequest request)
        {
            var hierarchy = BuildHierarchy();

            var items = request.ElementIds.Select(id => hierarchy.TryGet(id, out var node)
                ? BulkResultItem<ObjectInstanceResponse>.Ok(id, MapNode(node, request.IncludeMetadata))
                : BulkResultItem<ObjectInstanceResponse>.NotFound(id, "Object not found")).ToList();

            return Ok(new BulkResponse<ObjectInstanceResponse>(items.All(i => i.Success), items));
        }

        [HttpPost("related")]
        public ActionResult<BulkResponse<List<RelatedObjectResult>>> QueryRelatedObjects([FromBody] GetRelatedObjectsRequest request)
        {
            // Resolve the requested relationship type (default: all edges). i3X relationships are
            // directional: a "forward" type (e.g. HasComponent, Organizes) walks from a parent to its
            // children, and its reverse (e.g. ComponentOf, OrganizedBy) walks from a child to its parent.
            // Relationships MUST be traversable in both directions, so when no relationshipType filter is
            // supplied we return BOTH the child (HasComponent) and parent (ComponentOf) edges.
            string requestedType = request.RelationshipType;
            bool allDirections = string.IsNullOrEmpty(requestedType);
            RelationshipDirection direction = allDirections ? RelationshipDirection.Unsupported : ResolveDirection(requestedType);

            var hierarchy = BuildHierarchy();

            var items = request.ElementIds.Select(id =>
            {
                if (!hierarchy.TryGet(id, out _))
                {
                    return BulkResultItem<List<RelatedObjectResult>>.NotFound(id, "Object not found");
                }

                var related = new List<RelatedObjectResult>();

                // Forward edges (parent -> children) are labelled HasComponent.
                if (allDirections || direction == RelationshipDirection.Forward)
                {
                    related.AddRange(hierarchy.ChildrenOf(id)
                        .Select(n => new RelatedObjectResult(
                            allDirections ? "HasComponent" : requestedType,
                            MapNode(n, request.IncludeMetadata))));
                }

                // Reverse edge (child -> parent) is labelled ComponentOf. Including this makes the
                // hierarchy reachable through /objects/related and stores relationships bidirectionally.
                if (allDirections || direction == RelationshipDirection.Reverse)
                {
                    if (hierarchy.ParentOf(id) is { } parent)
                    {
                        related.Add(new RelatedObjectResult(
                            allDirections ? "ComponentOf" : requestedType,
                            MapNode(parent, request.IncludeMetadata)));
                    }
                }

                return BulkResultItem<List<RelatedObjectResult>>.Ok(id, related);
            }).ToList();

            return Ok(new BulkResponse<List<RelatedObjectResult>>(items.All(i => i.Success), items));
        }

        /// <summary>Direction of an i3X relationship relative to the ISA-95 containment hierarchy.</summary>
        private enum RelationshipDirection
        {
            Forward,     // parent -> children (HasComponent, Organizes, ...)
            Reverse,     // child -> parent   (ComponentOf, OrganizedBy, ...)
            Unsupported
        }

        // Forward containment relationship types and their reverses, matching RelationshipTypesController.
        private static readonly HashSet<string> ForwardContainmentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "HasComponent", "HasOrderedComponent", "Organizes", "HasProperty"
        };

        private static readonly HashSet<string> ReverseContainmentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "ComponentOf", "OrderedComponentOf", "OrganizedBy", "PropertyOf"
        };

        private static RelationshipDirection ResolveDirection(string relationshipType)
        {
            if (ForwardContainmentTypes.Contains(relationshipType))
            {
                return RelationshipDirection.Forward;
            }

            if (ReverseContainmentTypes.Contains(relationshipType))
            {
                return RelationshipDirection.Reverse;
            }

            return RelationshipDirection.Unsupported;
        }

        [HttpPost("value")]
        public ActionResult<BulkResponse<CurrentValueResult>> QueryValue([FromBody] GetObjectValueRequest request)
        {
            var hierarchy = BuildHierarchy();

            // Map each requested ElementId to the underlying telemetry Subject(s) so both asset ids and
            // individual variable ids (Subject::Name) can be answered.
            var subjects = ResolveSubjects(hierarchy, request.ElementIds);

            var rows = _influx.GetLatestValues(subjects);

            // Index telemetry by Subject, and by Subject::Name for direct variable lookups.
            var bySubject = rows.GroupBy(r => Str(r, "Subject"))
                                .ToDictionary(g => g.Key, g => g.ToList());

            var items = request.ElementIds.Select(id =>
            {
                if (!hierarchy.TryGet(id, out var node))
                {
                    return BulkResultItem<CurrentValueResult>.NotFound(id, "Object not found");
                }

                // Variable leaf: return the single scalar value of this field.
                if (node.Kind == Isa95Hierarchy.NodeKind.Variable)
                {
                    var row = bySubject.TryGetValue(node.Subject, out var g)
                        ? g.FirstOrDefault(r => Str(r, "Name") == node.VariableName)
                        : null;
                    if (row == null)
                    {
                        return BulkResultItem<CurrentValueResult>.NotFound(id, "No current value available");
                    }

                    string ts = row.TryGetValue("Timestamp", out var t) && t is DateTime dt ? ToRfc3339(dt) : "";
                    var vr = new CurrentValueResult(false, row.GetValueOrDefault("Value"), "Good", ts);
                    return BulkResultItem<CurrentValueResult>.Ok(id, vr);
                }

                // Asset (or other composition): return the components map of all its variables. UA Cloud
                // Publisher emits one dataset writer per published node, so a single asset regularly spans
                // several Subjects; aggregate all of them instead of only the first one recorded on the node,
                // otherwise just that writer's tag would carry a value.
                if (node.IsAsset)
                {
                    var components = new Dictionary<string, VQT>();
                    DateTime latest = DateTime.MinValue;
                    foreach (var subj in CollectSubjects(hierarchy, node))
                    {
                        if (!bySubject.TryGetValue(subj, out var group)) continue;
                        foreach (var row in group)
                        {
                            string ts = "";
                            if (row.TryGetValue("Timestamp", out var t) && t is DateTime dt)
                            {
                                if (dt > latest) latest = dt;
                                ts = ToRfc3339(dt);
                            }
                            components[Isa95Hierarchy.VariableId(subj, Str(row, "Name"))] =
                                new VQT(row.GetValueOrDefault("Value"), "Good", ts);
                        }
                    }

                    var result = new CurrentValueResult(
                        true,
                        null,
                        "GoodNoData",
                        latest == DateTime.MinValue ? ToRfc3339(DateTime.UtcNow) : ToRfc3339(latest),
                        request.MaxDepth != 1 ? components : null);

                    return BulkResultItem<CurrentValueResult>.Ok(id, result);
                }

                // Containers (compositions such as ISA-95 levels, and assets without direct telemetry) do
                // not have a scalar value of their own. They are readable compositions: at maxDepth 1 they
                // return no components; deeper they aggregate their descendants' current values.
                if (node.IsContainer)
                {
                    Dictionary<string, VQT> components = null;
                    DateTime latest = DateTime.MinValue;
                    if (request.MaxDepth != 1)
                    {
                        components = new Dictionary<string, VQT>();
                        foreach (var subj in CollectSubjects(hierarchy, node))
                        {
                            if (!bySubject.TryGetValue(subj, out var descGroup)) continue;
                            foreach (var row in descGroup)
                            {
                                string ts = "";
                                if (row.TryGetValue("Timestamp", out var t) && t is DateTime dt)
                                {
                                    if (dt > latest) latest = dt;
                                    ts = ToRfc3339(dt);
                                }
                                components[Isa95Hierarchy.VariableId(subj, Str(row, "Name"))] =
                                    new VQT(row.GetValueOrDefault("Value"), "Good", ts);
                            }
                        }
                        if (components.Count == 0) components = null;
                    }

                    var composition = new CurrentValueResult(
                        true,
                        null,
                        "GoodNoData",
                        latest == DateTime.MinValue ? ToRfc3339(DateTime.UtcNow) : ToRfc3339(latest),
                        components);
                    return BulkResultItem<CurrentValueResult>.Ok(id, composition);
                }

                // Anything else has no current value.
                return BulkResultItem<CurrentValueResult>.NotFound(id, "No current value available");
            }).ToList();

            return Ok(new BulkResponse<CurrentValueResult>(items.All(i => i.Success), items));
        }

        [HttpPost("history")]
        public ActionResult<BulkResponse<HistoricalValueResult>> QueryHistory([FromBody] GetObjectHistoryRequest request)
        {
            var hierarchy = BuildHierarchy();
            var subjects = ResolveSubjects(hierarchy, request.ElementIds);

            // The i3X spec requires startTime and endTime in RFC 3339 format; reject requests missing either.
            if (string.IsNullOrWhiteSpace(request.StartTime) ||
                !DateTimeOffset.TryParse(request.StartTime, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out _))
            {
                return BadRequest(new ErrorResponse(new ErrorDetail("Bad Request", 400,
                    "startTime is required and must be an RFC 3339 timestamp.")));
            }

            if (string.IsNullOrWhiteSpace(request.EndTime) ||
                !DateTimeOffset.TryParse(request.EndTime, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out _))
            {
                return BadRequest(new ErrorResponse(new ErrorDetail("Bad Request", 400,
                    "endTime is required and must be an RFC 3339 timestamp.")));
            }

            string start = request.StartTime;
            string end = request.EndTime;

            var rows = _influx.GetHistory(subjects, start, end);

            var bySubject = rows.GroupBy(r => Str(r, "Subject"))
                                .ToDictionary(g => g.Key, g => g.ToList());

            var items = request.ElementIds.Select(id =>
            {
                if (!hierarchy.TryGet(id, out var node))
                {
                    return BulkResultItem<HistoricalValueResult>.NotFound(id, "Object not found");
                }

                // Variable leaf: return its own time series directly in "values".
                if (node.Kind == Isa95Hierarchy.NodeKind.Variable)
                {
                    var series = bySubject.TryGetValue(node.Subject, out var g)
                        ? g.Where(r => Str(r, "Name") == node.VariableName)
                        : Enumerable.Empty<Dictionary<string, object>>();

                    var values = series
                        .Select(r => new
                        {
                            Row = r,
                            Ts = r.TryGetValue("Timestamp", out var t) && t is DateTime dt ? dt : DateTime.MinValue
                        })
                        .OrderByDescending(x => x.Ts)
                        .Select(x => new VQT(
                            x.Row.GetValueOrDefault("Value"),
                            "Good",
                            x.Ts == DateTime.MinValue ? "" : ToRfc3339(x.Ts)))
                        .ToList();

                    return BulkResultItem<HistoricalValueResult>.Ok(id,
                        new HistoricalValueResult(false, values, null));
                }

                // Asset: return per-variable series in the components map, across every writer beneath it.
                if (node.IsAsset)
                {
                    var components = new Dictionary<string, object>();
                    foreach (var subj in CollectSubjects(hierarchy, node))
                    {
                        if (!bySubject.TryGetValue(subj, out var group)) continue;
                        foreach (var nameGroup in group.GroupBy(r => Str(r, "Name")))
                        {
                            var vqts = nameGroup
                                .Select(r => new
                                {
                                    Row = r,
                                    Ts = r.TryGetValue("Timestamp", out var t) && t is DateTime dt ? dt : DateTime.MinValue
                                })
                                .OrderByDescending(x => x.Ts)
                                .Select(x => new VQT(
                                    x.Row.GetValueOrDefault("Value"),
                                    "Good",
                                    x.Ts == DateTime.MinValue ? "" : ToRfc3339(x.Ts)))
                                .ToList();

                            components[Isa95Hierarchy.VariableId(subj, nameGroup.Key)] =
                                new HistoricalValueResult(false, vqts, null);
                        }
                    }

                    var result = new HistoricalValueResult(
                        true,
                        Array.Empty<VQT>(),
                        request.MaxDepth != 1 ? components : null);

                    return BulkResultItem<HistoricalValueResult>.Ok(id, result);
                }

                // Containers (compositions) aggregate their descendants' history under "components" when a
                // deeper maxDepth is requested; at maxDepth 1 they return an empty composition result.
                if (node.IsContainer)
                {
                    Dictionary<string, object> components = null;
                    if (request.MaxDepth != 1)
                    {
                        components = new Dictionary<string, object>();
                        foreach (var subj in CollectSubjects(hierarchy, node))
                        {
                            if (!bySubject.TryGetValue(subj, out var descGroup)) continue;
                            foreach (var nameGroup in descGroup.GroupBy(r => Str(r, "Name")))
                            {
                                var vqts = nameGroup
                                    .Select(r => new
                                    {
                                        Row = r,
                                        Ts = r.TryGetValue("Timestamp", out var t) && t is DateTime dt ? dt : DateTime.MinValue
                                    })
                                    .OrderByDescending(x => x.Ts)
                                    .Select(x => new VQT(
                                        x.Row.GetValueOrDefault("Value"),
                                        "Good",
                                        x.Ts == DateTime.MinValue ? "" : ToRfc3339(x.Ts)))
                                    .ToList();

                                components[Isa95Hierarchy.VariableId(subj, nameGroup.Key)] =
                                    new HistoricalValueResult(false, vqts, null);
                            }
                        }
                        if (components.Count == 0) components = null;
                    }

                    return BulkResultItem<HistoricalValueResult>.Ok(id,
                        new HistoricalValueResult(true, Array.Empty<VQT>(), components));
                }

                // Anything else has no historical values.
                return BulkResultItem<HistoricalValueResult>.NotFound(id, "No historical values available");
            }).ToList();

            return Ok(new BulkResponse<HistoricalValueResult>(items.All(i => i.Success), items));
        }

        /// <summary>
        /// Resolves the requested ElementIds (which may be container, asset or variable ids) to the distinct
        /// set of underlying telemetry Subjects to query.
        /// </summary>
        private static IReadOnlyCollection<string> ResolveSubjects(Isa95Hierarchy hierarchy, IEnumerable<string> elementIds)
        {
            var subjects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in elementIds)
            {
                if (hierarchy.TryGet(id, out var node))
                {
                    CollectSubjects(hierarchy, node, subjects);
                }
            }
            return subjects;
        }

        /// <summary>
        /// Collects the telemetry Subject of a node and, for container nodes, of all their descendants so a
        /// composition object can aggregate the values beneath it.
        /// </summary>
        private static void CollectSubjects(Isa95Hierarchy hierarchy, Isa95Hierarchy.Node node, HashSet<string> subjects)
        {
            if (!string.IsNullOrEmpty(node.Subject))
            {
                subjects.Add(node.Subject);
            }

            foreach (var child in hierarchy.ChildrenOf(node.ElementId))
            {
                CollectSubjects(hierarchy, child, subjects);
            }
        }

        private static IReadOnlyCollection<string> CollectSubjects(Isa95Hierarchy hierarchy, Isa95Hierarchy.Node node)
        {
            var subjects = new HashSet<string>(StringComparer.Ordinal);
            CollectSubjects(hierarchy, node, subjects);
            return subjects;
        }

        private static ObjectInstanceMetadata BuildMetadata(string namespaceUri, string sourceTypeId) =>
            new(TypeNamespaceUri: string.IsNullOrEmpty(namespaceUri) ? null : namespaceUri,
                SourceTypeId: string.IsNullOrEmpty(sourceTypeId) ? null : sourceTypeId);

        // i3X requires RFC 3339 UTC timestamps terminated by "Z", formatted culture-invariantly so a host
        // with a non-Gregorian calendar or different digits cannot render an unparsable value.
        private static string ToRfc3339(DateTime dt) =>
            DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

        private static string Str(Dictionary<string, object> row, string key) =>
            row.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
    }
}