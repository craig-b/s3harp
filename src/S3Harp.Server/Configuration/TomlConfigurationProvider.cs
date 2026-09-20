using System.Diagnostics;
using System.Globalization;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace S3Harp.Server.Configuration;

/// <summary>
/// Reads a TOML file into configuration keys the way the framework's JSON
/// provider does: a table or dotted key becomes a section, an array an indexed
/// one, and every value its invariant text. The file is walked as syntax, so
/// nothing is bound by reflection. A malformed file, a redefined key included,
/// raises an <see cref="InvalidDataException"/> saying where it went wrong.
/// </summary>
internal sealed class TomlConfigurationProvider(TomlConfigurationSource source)
    : FileConfigurationProvider(source)
{
    public override void Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream);
        var document = SyntaxParser.Parse(reader.ReadToEnd(), sourceName: null, validate: true);
        if (document.HasErrors)
        {
            throw new InvalidDataException(
                string.Join(
                    "; ",
                    document.Diagnostics.Where(d => d.Kind == DiagnosticMessageKind.Error)
                )
            );
        }

        Data = new Walk().Document(document);
    }

    /// <summary>
    /// One pass over a validated document. Array-of-table headers count their
    /// elements so that a later header under that array lands in its newest one.
    /// </summary>
    private sealed class Walk
    {
        private readonly Dictionary<string, string?> data = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> tableArrayLengths = new(
            StringComparer.OrdinalIgnoreCase
        );

        public Dictionary<string, string?> Document(DocumentSyntax document)
        {
            KeyValues(document.KeyValues, prefix: null);
            foreach (var table in document.Tables)
            {
                KeyValues(table.Items, TablePrefix(table));
            }

            return data;
        }

        private string TablePrefix(TableSyntaxBase table)
        {
            var parts = Parts(Present(table.Name));
            if (table is not TableArraySyntax)
            {
                return Section(parts);
            }

            var array =
                parts.Length == 1 ? parts[0] : Combine(Section(parts.AsSpan(..^1)), parts[^1]);
            var length = tableArrayLengths.GetValueOrDefault(array) + 1;
            tableArrayLengths[array] = length;
            return ConfigurationPath.Combine(array, Index(length - 1));
        }

        /// <summary>
        /// The section a header names, entering the newest element of every
        /// array of tables the path passes through.
        /// </summary>
        private string Section(ReadOnlySpan<string> parts)
        {
            var section = Element(parts[0]);
            foreach (var part in parts[1..])
            {
                section = Element(ConfigurationPath.Combine(section, part));
            }

            return section;
        }

        /// <summary>The newest element when the section is an array of tables, else the section itself.</summary>
        private string Element(string section) =>
            tableArrayLengths.TryGetValue(section, out var length)
                ? ConfigurationPath.Combine(section, Index(length - 1))
                : section;

        private void KeyValues(SyntaxList<KeyValueSyntax> keyValues, string? prefix)
        {
            foreach (var keyValue in keyValues)
            {
                KeyValue(keyValue, prefix);
            }
        }

        private void KeyValue(KeyValueSyntax keyValue, string? prefix)
        {
            var key = prefix;
            foreach (var part in Parts(Present(keyValue.Key)))
            {
                key = Combine(key, part);
            }

            Value(Present(key), Present(keyValue.Value));
        }

        private void Value(string key, ValueSyntax value)
        {
            switch (value)
            {
                case StringValueSyntax text:
                    data[key] = text.Value;
                    break;
                case IntegerValueSyntax integer:
                    data[key] = integer.Value.ToString(CultureInfo.InvariantCulture);
                    break;
                case FloatValueSyntax number:
                    data[key] = number.Value.ToString(CultureInfo.InvariantCulture);
                    break;
                case BooleanValueSyntax boolean:
                    data[key] = boolean.Value ? "true" : "false";
                    break;
                case DateTimeValueSyntax dateTime:
                    data[key] = dateTime.Value.ToString();
                    break;
                case ArraySyntax array:
                    var index = 0;
                    foreach (var item in array.Items)
                    {
                        Value(ConfigurationPath.Combine(key, Index(index++)), Present(item.Value));
                    }

                    break;
                case InlineTableSyntax table:
                    foreach (var item in table.Items)
                    {
                        KeyValue(Present(item.KeyValue), key);
                    }

                    break;
                default:
                    throw new UnreachableException();
            }
        }

        private static string[] Parts(KeySyntax key) =>
            [Text(Present(key.Key)), .. key.DotKeys.Select(dotted => Text(Present(dotted.Key)))];

        private static string Text(BareKeyOrStringValueSyntax key) =>
            key switch
            {
                BareKeySyntax bare => Present(Present(bare.Key).Text),
                StringValueSyntax quoted => Present(quoted.Value),
                _ => throw new UnreachableException(),
            };

        private static string Combine(string? prefix, string part) =>
            prefix is null ? part : ConfigurationPath.Combine(prefix, part);

        private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);

        /// <summary>A child the parser always fills in a document that validated.</summary>
        private static T Present<T>(T? node)
            where T : class => node ?? throw new UnreachableException();
    }
}
