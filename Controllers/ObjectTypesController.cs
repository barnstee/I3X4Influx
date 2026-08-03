using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;

namespace I3X4Influx.Controllers
{
    [ApiController]
    [Route("v1/objecttypes")]
    public sealed class ObjectTypesController : ControllerBase
    {
        private readonly InfluxDataService _influx;

        // Synthetic ISA-95 container level types. Container objects (Enterprise/Site/Area/Line/Workcell,
        // plus the Namespace/Asset grouping levels synthesized for aggregating servers that expose no ISA-95
        // path) carry a typeElementId of "ISA95:<Level>", so those types must be registered here for every
        // object's typeElementId to resolve.
        private static readonly string[] Isa95Levels =
            { "Enterprise", "Site", "Area", "Line", "Workcell", "Namespace", "Asset", "Folder" };

        private static IEnumerable<ObjectTypeResponse> Isa95ContainerTypes() =>
            Isa95Levels.Select(level => new ObjectTypeResponse(
                "ISA95:" + level,
                level,
                Isa95Hierarchy.Isa95NamespaceUri,
                "ISA95:" + level,
                new Dictionary<string, object>()));

        public ObjectTypesController(InfluxDataService influx)
        {
            _influx = influx;
            _influx.Connect();
        }

        [HttpGet]
        public ActionResult<SuccessResponse<IReadOnlyList<ObjectTypeResponse>>> GetObjectTypes(
            [FromQuery] string namespaceUri = null)
        {
            // Build the object types from the same variable data (and TypeToken logic) the objects use, so the
            // type ids returned here always match each object's typeElementId. A type is a distinct OPC UA
            // DataType within a Namespace; its elementId is namespace-qualified to stay globally unique.
            var hierarchy = new Isa95Hierarchy(_influx.GetIsa95LeafAssets());

            var types = hierarchy.ById.Values
                .Where(n => n.Kind == Isa95Hierarchy.NodeKind.Variable &&
                            (string.IsNullOrEmpty(namespaceUri) ||
                             string.Equals(n.NamespaceUri, namespaceUri, System.StringComparison.Ordinal)))
                .Select(n => (n.NamespaceUri, Token: n.TypeToken))
                .Distinct()
                .Select(t => MapObjectType(t.NamespaceUri, t.Token))
                .ToList();

            // Include the synthetic ISA-95 container level types (filtered by namespace when requested).
            types.AddRange(Isa95ContainerTypes()
                .Where(t => string.IsNullOrEmpty(namespaceUri) ||
                            string.Equals(t.NamespaceUri, namespaceUri, System.StringComparison.Ordinal)));

            return Ok(new SuccessResponse<IReadOnlyList<ObjectTypeResponse>>(true, types));
        }

        [HttpPost("query")]
        public ActionResult<BulkResponse<ObjectTypeResponse>> QueryByElementId([FromBody] GetObjectTypesRequest request)
        {
            // Type elementIds are namespace-qualified OPC UA DataTypes ("<namespaceUri>#<token>"). Resolve each
            // by rebuilding the type set and matching on elementId.
            var hierarchy = new Isa95Hierarchy(_influx.GetIsa95LeafAssets());

            var byId = hierarchy.ById.Values
                .Where(n => n.Kind == Isa95Hierarchy.NodeKind.Variable)
                .Select(n => (n.NamespaceUri, Token: n.TypeToken))
                .Distinct()
                .Select(t => MapObjectType(t.NamespaceUri, t.Token))
                .GroupBy(t => t.ElementId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var containerType in Isa95ContainerTypes())
            {
                byId[containerType.ElementId] = containerType;
            }

            var items = request.ElementIds.Select(id => byId.TryGetValue(id, out var ot)
                ? BulkResultItem<ObjectTypeResponse>.Ok(id, ot)
                : BulkResultItem<ObjectTypeResponse>.NotFound(id, "Object type not found")).ToList();

            return Ok(new BulkResponse<ObjectTypeResponse>(items.All(i => i.Success), items));
        }

        private static ObjectTypeResponse MapObjectType(string namespaceUri, string token)
        {
            var elementId = ObjectTypeId.Build(namespaceUri, token);
            return new ObjectTypeResponse(
                elementId,
                token,
                namespaceUri,
                token,
                new Dictionary<string, object>());
        }
    }
}
