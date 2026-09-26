using Dam.Application.Content;
using Dam.Domain.Content;

namespace Dam.UnitTests;

/// <summary>Every shipped pack must be internally consistent, so a new customer pack cannot break tenant setup.</summary>
public sealed class TemplatePackTests
{
    public static IEnumerable<object[]> PackIds() => TemplateCatalog.All.Keys.Order().Select(id => new object[] { id });

    [Fact]
    public void The_product_ships_a_core_pack_and_no_pack_names_a_real_customer()
    {
        Assert.Contains("core", TemplateCatalog.All.Keys);
        var text = string.Join(' ', TemplateCatalog.All.Values.Select(p => System.Text.Json.JsonSerializer.Serialize(p)));
        foreach (var forbidden in new[] { "mahindra", "thar", "xuv", "scorpio", "pads4" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(PackIds))]
    public void Requirements_resolve_without_cycles_and_dependencies_come_first(string id)
    {
        var order = TemplateCatalog.Resolve(id);
        Assert.Equal(id, order[^1].Id);
        foreach (var (pack, i) in order.Select((p, i) => (p, i)))
            Assert.All(pack.Requires ?? [], r => Assert.True(order.ToList().FindIndex(p => p.Id == r) < i, $"{pack.Id} requires {r}"));
    }

    [Theory]
    [MemberData(nameof(PackIds))]
    public void Vocabularies_and_terms_are_well_formed(string id)
    {
        foreach (var pack in TemplateCatalog.Resolve(id))
        {
            var codes = new Dictionary<string, HashSet<string>>();
            foreach (var v in pack.Vocabularies ?? [])
            {
                Assert.True(Paths.IsValidLabel(v.Key) && char.IsAsciiLetterLower(v.Key[0]), $"vocabulary key {v.Key}");
                Assert.False(v.Levels is { Count: > 0 } && !v.Hierarchical, $"{v.Key}: levels on a flat vocabulary");
                var seen = codes[v.Key] = [];
                void Walk(IEnumerable<PackTerm> ts, int depth)
                {
                    foreach (var t in ts)
                    {
                        Assert.True(Paths.IsValidLabel(t.Code), $"{v.Key}/{t.Code}: invalid code");
                        Assert.True(seen.Add(t.Code), $"{v.Key}/{t.Code}: duplicate code");
                        Assert.True(t.Labels.Count > 0 && t.Labels.Values.All(l => l.Length is > 0 and <= 200), $"{v.Key}/{t.Code}: labels");
                        Assert.True(depth <= Paths.MaxDepth, $"{v.Key}/{t.Code}: too deep");
                        Assert.False(t.Children is { Count: > 0 } && !v.Hierarchical, $"{v.Key}/{t.Code}: children in a flat vocabulary");
                        Walk(t.Children ?? [], depth + 1);
                    }
                }
                Walk(v.Terms, 1);
            }
        }
    }

    [Theory]
    [MemberData(nameof(PackIds))]
    public void Schemas_are_valid_and_only_reference_vocabularies_the_pack_chain_defines(string id)
    {
        var chain = TemplateCatalog.Resolve(id);
        var hierarchical = new Dictionary<string, bool>();
        foreach (var v in chain.SelectMany(p => p.Vocabularies ?? [])) hierarchical[v.Key] = hierarchical.GetValueOrDefault(v.Key) || v.Hierarchical;

        // Apply schemas in order, the way the applier does: create, then add missing fields.
        var merged = new Dictionary<string, List<FieldDefinition>>();
        foreach (var s in chain.SelectMany(p => p.Schemas ?? []))
        {
            Assert.True(AssetTypes.IsKnown(s.AssetType), $"unknown asset type {s.AssetType}");
            var fields = merged.GetValueOrDefault(s.AssetType) ?? [];
            fields.AddRange(s.Fields.Where(f => fields.All(x => x.Name != f.Name)));
            merged[s.AssetType] = fields;
        }
        foreach (var (type, fields) in merged)
        {
            var errors = SchemaDefinitionValidator.Validate(fields);
            Assert.True(errors.Count == 0, $"{type}: {string.Join("; ", errors.Select(e => $"{e.Key} {string.Join(",", e.Value)}"))}");
            foreach (var f in fields.Where(f => f.Vocabulary is not null))
            {
                Assert.True(hierarchical.ContainsKey(f.Vocabulary!), $"{type}.{f.Name} references unknown vocabulary '{f.Vocabulary}'");
                if (f.Type == FieldType.TermTree) Assert.True(hierarchical[f.Vocabulary!], $"{type}.{f.Name} is term_tree on a flat vocabulary");
            }
        }
    }

    [Theory]
    [MemberData(nameof(PackIds))]
    public void Folders_have_unique_sibling_labels_and_valid_depth(string id)
    {
        foreach (var pack in TemplateCatalog.Resolve(id))
        {
            void Walk(IEnumerable<PackFolder> fs, int depth)
            {
                var labels = new HashSet<string>();
                foreach (var f in fs)
                {
                    var label = f.Label ?? Paths.Slugify(f.Name, "folder");
                    Assert.True(Paths.IsValidLabel(label), $"{f.Name}: label");
                    Assert.True(labels.Add(label), $"{f.Name}: duplicate sibling label {label}");
                    Assert.True(depth <= Paths.MaxDepth);
                    Walk(f.Children ?? [], depth + 1);
                }
            }
            Walk(pack.Folders ?? [], 1);
        }
    }

    [Fact]
    public void Asking_for_an_unknown_pack_is_an_error() => Assert.Throws<KeyNotFoundException>(() => TemplateCatalog.Resolve("nope"));
}
