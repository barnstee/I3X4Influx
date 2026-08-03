using System.Collections.Generic;

namespace I3X4Influx
{
    /// <summary>
    /// Lookup table for OPC UA built-in type ids to their display names, per the OPC UA specification
    /// (Part 6, Table "BuiltInType" - the DataEncoding built-in type identifiers 0..25). Used to present a
    /// human-readable type name (e.g. "String", "Int32", "Double") for a variable instead of the raw numeric
    /// BuiltInType id or an opaque DataType NodeId.
    /// </summary>
    public static class OpcUaBuiltInType
    {
        private static readonly Dictionary<int, string> Names = new()
        {
            [0] = "Null",
            [1] = "Boolean",
            [2] = "SByte",
            [3] = "Byte",
            [4] = "Int16",
            [5] = "UInt16",
            [6] = "Int32",
            [7] = "UInt32",
            [8] = "Int64",
            [9] = "UInt64",
            [10] = "Float",
            [11] = "Double",
            [12] = "String",
            [13] = "DateTime",
            [14] = "Guid",
            [15] = "ByteString",
            [16] = "XmlElement",
            [17] = "NodeId",
            [18] = "ExpandedNodeId",
            [19] = "StatusCode",
            [20] = "QualifiedName",
            [21] = "LocalizedText",
            [22] = "ExtensionObject",
            [23] = "DataValue",
            [24] = "Variant",
            [25] = "DiagnosticInfo"
        };

        /// <summary>
        /// Standard OPC UA DataType NodeIds in the base namespace (namespace 0). These are the numeric node
        /// identifiers used by the DataSetMetaData "DataType" field (e.g. "i=27" = Number, "i=6" = Int32).
        /// Covers the built-in scalar types plus the common abstract super-types (Number, Integer, UInteger,
        /// Enumeration) and frequently used concrete types. Per OPC UA Part 6 Annex A / the standard NodeSet.
        /// </summary>
        private static readonly Dictionary<int, string> DataTypeNodeIds = new()
        {
            [1] = "Boolean",
            [2] = "SByte",
            [3] = "Byte",
            [4] = "Int16",
            [5] = "UInt16",
            [6] = "Int32",
            [7] = "UInt32",
            [8] = "Int64",
            [9] = "UInt64",
            [10] = "Float",
            [11] = "Double",
            [12] = "String",
            [13] = "DateTime",
            [14] = "Guid",
            [15] = "ByteString",
            [16] = "XmlElement",
            [17] = "NodeId",
            [18] = "ExpandedNodeId",
            [19] = "StatusCode",
            [20] = "QualifiedName",
            [21] = "LocalizedText",
            [22] = "Structure",
            [23] = "DataValue",
            [24] = "BaseDataType",
            [25] = "DiagnosticInfo",
            [26] = "Number",
            [27] = "Integer",
            [28] = "UInteger",
            [29] = "Enumeration",
            [30] = "Image",
            [50] = "Decimal",
            [120] = "NamingRuleType",
            [121] = "Decimal128",
            [290] = "Duration",
            [294] = "UtcTime",
            [295] = "LocaleId",
            [862] = "TimeZoneDataType",
            [12756] = "Union"
        };

        /// <summary>
        /// Returns the display name for a numeric OPC UA BuiltInType id, or null when the value is not a known
        /// built-in type id. Accepts the id as a string (as carried in the metadata).
        /// </summary>
        public static string GetDisplayName(string builtInType)
        {
            if (int.TryParse(builtInType, out int id) && Names.TryGetValue(id, out var name))
            {
                return name;
            }

            return null;
        }

        /// <summary>
        /// Returns the display name for a standard OPC UA DataType NodeId in the base namespace, or null when
        /// unknown. Accepts the metadata "DataType" value in the common forms: "i=27", "ns=0;i=27",
        /// "nsu=http://opcfoundation.org/UA/;i=27", or a bare "27". Only base-namespace (ns=0) ids resolve to a
        /// standard name.
        /// </summary>
        public static string GetDataTypeName(string dataType)
        {
            if (string.IsNullOrEmpty(dataType))
            {
                return null;
            }

            // Only the OPC UA base namespace (namespace index 0) carries standard DataType ids. If a non-zero
            // namespace index is present, this is a server-specific type, not a standard built-in one.
            int nsIndex = ExtractNamespaceIndex(dataType);
            if (nsIndex != 0)
            {
                return null;
            }

            int idStart = dataType.LastIndexOf("i=", System.StringComparison.Ordinal);
            string idPart = idStart >= 0 ? dataType.Substring(idStart + 2) : dataType;

            return int.TryParse(idPart.Trim(), out int id) && DataTypeNodeIds.TryGetValue(id, out var name)
                ? name
                : null;
        }

        // Returns the ns index encoded in a NodeId string ("ns=2;i=..."), or 0 when none is specified
        // (a bare "i=27" / "27" implies namespace 0). A nsu= form other than the base UA namespace is treated
        // as non-zero so it is not mistaken for a standard type.
        private static int ExtractNamespaceIndex(string nodeId)
        {
            int nsStart = nodeId.IndexOf("ns=", System.StringComparison.Ordinal);

            if (nsStart >= 0)
            {
                int semi = nodeId.IndexOf(';', nsStart);
                string nsPart = semi > nsStart
                    ? nodeId.Substring(nsStart + 3, semi - (nsStart + 3))
                    : nodeId.Substring(nsStart + 3);

                return int.TryParse(nsPart.Trim(), out int ns) ? ns : -1;
            }

            int nsuStart = nodeId.IndexOf("nsu=", System.StringComparison.Ordinal);

            if (nsuStart >= 0)
            {
                int semi = nodeId.IndexOf(';', nsuStart);
                string uri = semi > nsuStart
                    ? nodeId.Substring(nsuStart + 4, semi - (nsuStart + 4))
                    : nodeId.Substring(nsuStart + 4);

                // The base OPC UA namespace maps to index 0; any other namespace URI is server-specific.
                return string.Equals(uri.Trim(), "http://opcfoundation.org/UA/", System.StringComparison.Ordinal)
                    ? 0
                    : 1;
            }

            return 0;
        }
    }
}
