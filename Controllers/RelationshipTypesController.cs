using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;

namespace I3X4Influx.Controllers
{
    [ApiController]
    [Route("v1/relationshiptypes")]
    public sealed class RelationshipTypesController : ControllerBase
    {
        private readonly InfluxDataService _influx;

        // Well-known OPC UA reference types exposed through this adapter
        private static readonly List<RelationshipType> KnownRelationshipTypes =
        [
            new("HasComponent", "HasComponent", "http://opcfoundation.org/UA/", "HasComponent", "ComponentOf"),
            new("Organizes", "Organizes", "http://opcfoundation.org/UA/", "Organizes", "OrganizedBy"),
            new("HasProperty", "HasProperty", "http://opcfoundation.org/UA/", "HasProperty", "PropertyOf"),
            new("HasSubtype", "HasSubtype", "http://opcfoundation.org/UA/", "HasSubtype", "SubtypeOf"),
            new("HasTypeDefinition", "HasTypeDefinition", "http://opcfoundation.org/UA/", "HasTypeDefinition", "TypeDefinitionOf"),
            new("HasModellingRule", "HasModellingRule", "http://opcfoundation.org/UA/", "HasModellingRule", "ModellingRuleOf"),
            new("HasEncoding", "HasEncoding", "http://opcfoundation.org/UA/", "HasEncoding", "EncodingOf"),
            new("HasDescription", "HasDescription", "http://opcfoundation.org/UA/", "HasDescription", "DescriptionOf"),
            new("GeneratesEvent", "GeneratesEvent", "http://opcfoundation.org/UA/", "GeneratesEvent", "GeneratedBy"),
            new("AlwaysGeneratesEvent", "AlwaysGeneratesEvent", "http://opcfoundation.org/UA/", "AlwaysGeneratesEvent", "AlwaysGeneratedBy"),
            new("HasNotifier", "HasNotifier", "http://opcfoundation.org/UA/", "HasNotifier", "NotifierOf"),
            new("HasEventSource", "HasEventSource", "http://opcfoundation.org/UA/", "HasEventSource", "EventSourceOf"),
            new("HasCondition", "HasCondition", "http://opcfoundation.org/UA/", "HasCondition", "IsConditionOf"),
            new("HasOrderedComponent", "HasOrderedComponent", "http://opcfoundation.org/UA/", "HasOrderedComponent", "OrderedComponentOf"),
            new("FromState", "FromState", "http://opcfoundation.org/UA/", "FromState", "ToTransition"),
            new("ToState", "ToState", "http://opcfoundation.org/UA/", "ToState", "FromTransition"),
            new("HasCause", "HasCause", "http://opcfoundation.org/UA/", "HasCause", "MayBeCausedBy"),
            new("HasEffect", "HasEffect", "http://opcfoundation.org/UA/", "HasEffect", "MayBeAffectedBy"),
            new("HasGuard", "HasGuard", "http://opcfoundation.org/UA/", "HasGuard", "GuardOf"),

            // Reverse reference types. The i3X spec requires every reverseOf to also be a registered
            // relationship type, so each forward type above has its inverse registered here pointing back.
            new("ComponentOf", "ComponentOf", "http://opcfoundation.org/UA/", "ComponentOf", "HasComponent"),
            new("OrganizedBy", "OrganizedBy", "http://opcfoundation.org/UA/", "OrganizedBy", "Organizes"),
            new("PropertyOf", "PropertyOf", "http://opcfoundation.org/UA/", "PropertyOf", "HasProperty"),
            new("SubtypeOf", "SubtypeOf", "http://opcfoundation.org/UA/", "SubtypeOf", "HasSubtype"),
            new("TypeDefinitionOf", "TypeDefinitionOf", "http://opcfoundation.org/UA/", "TypeDefinitionOf", "HasTypeDefinition"),
            new("ModellingRuleOf", "ModellingRuleOf", "http://opcfoundation.org/UA/", "ModellingRuleOf", "HasModellingRule"),
            new("EncodingOf", "EncodingOf", "http://opcfoundation.org/UA/", "EncodingOf", "HasEncoding"),
            new("DescriptionOf", "DescriptionOf", "http://opcfoundation.org/UA/", "DescriptionOf", "HasDescription"),
            new("GeneratedBy", "GeneratedBy", "http://opcfoundation.org/UA/", "GeneratedBy", "GeneratesEvent"),
            new("AlwaysGeneratedBy", "AlwaysGeneratedBy", "http://opcfoundation.org/UA/", "AlwaysGeneratedBy", "AlwaysGeneratesEvent"),
            new("NotifierOf", "NotifierOf", "http://opcfoundation.org/UA/", "NotifierOf", "HasNotifier"),
            new("EventSourceOf", "EventSourceOf", "http://opcfoundation.org/UA/", "EventSourceOf", "HasEventSource"),
            new("IsConditionOf", "IsConditionOf", "http://opcfoundation.org/UA/", "IsConditionOf", "HasCondition"),
            new("OrderedComponentOf", "OrderedComponentOf", "http://opcfoundation.org/UA/", "OrderedComponentOf", "HasOrderedComponent"),
            new("ToTransition", "ToTransition", "http://opcfoundation.org/UA/", "ToTransition", "FromState"),
            new("FromTransition", "FromTransition", "http://opcfoundation.org/UA/", "FromTransition", "ToState"),
            new("MayBeCausedBy", "MayBeCausedBy", "http://opcfoundation.org/UA/", "MayBeCausedBy", "HasCause"),
            new("MayBeAffectedBy", "MayBeAffectedBy", "http://opcfoundation.org/UA/", "MayBeAffectedBy", "HasEffect"),
            new("GuardOf", "GuardOf", "http://opcfoundation.org/UA/", "GuardOf", "HasGuard")
        ];

        public RelationshipTypesController(InfluxDataService influx)
        {
            _influx = influx;
            _influx.Connect();
        }

        [HttpGet]
        public ActionResult<SuccessResponse<IReadOnlyList<RelationshipType>>> GetRelationshipTypes(
            [FromQuery] string namespaceUri = null)
        {
            // No explicit relationship measurement in InfluxDB – return well-known OPC UA reference types
            IReadOnlyList<RelationshipType> results = string.IsNullOrEmpty(namespaceUri)
                ? KnownRelationshipTypes
                : KnownRelationshipTypes.Where(rt => rt.NamespaceUri == namespaceUri).ToList();

            return Ok(new SuccessResponse<IReadOnlyList<RelationshipType>>(true, results));
        }

        [HttpPost("query")]
        public ActionResult<BulkResponse<RelationshipType>> QueryByElementId([FromBody] GetRelationshipTypesRequest request)
        {
            var byId = KnownRelationshipTypes.ToDictionary(rt => rt.ElementId);

            var items = request.ElementIds.Select(id => byId.TryGetValue(id, out var rt)
                ? BulkResultItem<RelationshipType>.Ok(id, rt)
                : BulkResultItem<RelationshipType>.NotFound(id, "Relationship type not found")).ToList();

            return Ok(new BulkResponse<RelationshipType>(items.All(i => i.Success), items));
        }
    }
}
