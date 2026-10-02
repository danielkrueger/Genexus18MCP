using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// A Structure DSL line written <c>Attr : SomeDomain</c> is read back as
    /// <c>Attr : CHARACTER(10)</c>: the persisted rendering shows the physical type of a
    /// domain-based attribute, not the domain name. Post-save verification would
    /// report that as a mismatch although the attribute really is based on the
    /// domain.
    ///
    /// <see cref="Canonicalize"/> rewrites only the type token of a requested line
    /// whose domain the persisted attribute is verifiably based on, so a real
    /// difference (not domain-based, another domain, unknown name, any other line)
    /// still compares as different.
    /// </summary>
    internal static class StructureDomainEquivalence
    {
        // indent, name, optional key marker, separator, type token (no whitespace), rest of line
        private static readonly Regex DeclarationLine = new Regex(
            @"^(?<head>[ \t]*(?<name>[A-Za-z_][A-Za-z0-9_]*)[*+]?[ \t]*:[ \t]*)(?<type>[^\s]+)(?<rest>[^\r\n]*)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <param name="domainNameOfAttribute">Name of the domain the persisted attribute
        /// is based on, or null when it is unknown or not domain-based.</param>
        internal static string Canonicalize(string requested, string persisted, Func<string, string> domainNameOfAttribute)
        {
            if (string.IsNullOrEmpty(requested) || string.IsNullOrEmpty(persisted) || domainNameOfAttribute == null)
                return requested;

            var persistedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in DeclarationLine.Matches(persisted))
            {
                string name = m.Groups["name"].Value;
                if (!persistedTypes.ContainsKey(name)) persistedTypes[name] = m.Groups["type"].Value;
            }

            return DeclarationLine.Replace(requested, m =>
            {
                string name = m.Groups["name"].Value;
                string type = m.Groups["type"].Value;
                string persistedType;
                if (!persistedTypes.TryGetValue(name, out persistedType)) return m.Value;

                var spec = AttributeTypeApplier.Parse(type);
                if (!spec.Recognized
                    || !string.Equals(spec.CanonicalType, "DomainReference", StringComparison.Ordinal)
                    || string.IsNullOrEmpty(spec.DomainName))
                    return m.Value;

                string actualDomain = domainNameOfAttribute(name);
                if (!string.Equals(actualDomain, spec.DomainName, StringComparison.OrdinalIgnoreCase))
                    return m.Value;

                return m.Groups["head"].Value + persistedType + m.Groups["rest"].Value;
            });
        }
    }
}
