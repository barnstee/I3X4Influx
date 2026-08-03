namespace I3X4Influx
{
    /// <summary>
    /// Encodes and decodes I3X object type element ids. In this adapter a "type" is a distinct OPC UA variable
    /// (field Name) within a Namespace, so the type elementId is the namespace-qualified name
    /// "&lt;namespaceUri&gt;#&lt;Name&gt;". This keeps type ids globally unique (the same variable Name can
    /// appear in several namespaces) while letting a type be resolved back to its Namespace and Name.
    /// </summary>
    public static class ObjectTypeId
    {
        private const char Separator = '#';

        /// <summary>Builds the namespace-qualified type elementId for a variable Name.</summary>
        public static string Build(string namespaceUri, string name) =>
            (namespaceUri ?? string.Empty) + Separator + (name ?? string.Empty);

        /// <summary>
        /// Parses a type elementId back into its NamespaceUri and Name. Returns null when the id is not a
        /// namespace-qualified type id.
        /// </summary>
        public static (string NamespaceUri, string Name)? Parse(string elementId)
        {
            if (string.IsNullOrEmpty(elementId))
            {
                return null;
            }

            int idx = elementId.LastIndexOf(Separator);

            if (idx < 0)
            {
                return null;
            }

            return (elementId.Substring(0, idx), elementId.Substring(idx + 1));
        }
    }
}
