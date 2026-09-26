using System.Reflection;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Content;

// A template pack is DATA: vocabularies, metadata schemas and sample folders that a tenant can adopt. Nothing about any
// particular customer lives in code; each customer's taxonomy is a pack (or is created through the API).

public sealed record PackTerm(string Code, Dictionary<string, string> Labels, Dictionary<string, JsonElement>? Attributes = null,
    int? SortOrder = null, List<PackTerm>? Children = null);

public sealed record PackVocabulary(string Key, string Name, string? Description, bool Hierarchical, List<string>? Levels, List<PackTerm> Terms);

public sealed record PackSchema(string AssetType, List<FieldDefinition> Fields);

public sealed record PackFolder(string Name, string? Label = null, Dictionary<string, JsonElement>? InheritedMetadata = null, List<PackFolder>? Children = null);

public sealed record TemplatePack(
    string Id, string Name, string Description, List<string>? Requires, List<PackVocabulary>? Vocabularies, List<PackSchema>? Schemas, List<PackFolder>? Folders)
{
    public int TermCount => (Vocabularies ?? []).Sum(v => CountTerms(v.Terms));
    public int FolderCount => CountFolders(Folders ?? []);
    private static int CountTerms(IEnumerable<PackTerm> ts) => ts.Sum(t => 1 + CountTerms(t.Children ?? []));
    private static int CountFolders(IEnumerable<PackFolder> fs) => fs.Sum(f => 1 + CountFolders(f.Children ?? []));
}

/// <summary>The packs shipped with the product, read from embedded JSON. Add a customer pack by adding a file, no code.</summary>
public static class TemplateCatalog
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };
    private static readonly Lazy<IReadOnlyDictionary<string, TemplatePack>> Loaded = new(Load);

    public static IReadOnlyDictionary<string, TemplatePack> All => Loaded.Value;

    private static Dictionary<string, TemplatePack> Load()
    {
        var asm = typeof(TemplateCatalog).Assembly;
        var packs = new Dictionary<string, TemplatePack>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Contains(".Templates.") && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            var pack = JsonSerializer.Deserialize<TemplatePack>(stream, Options) ?? throw new InvalidOperationException($"Empty template {name}");
            packs[pack.Id] = pack;
        }
        return packs;
    }

    /// <summary>The pack and everything it requires, dependencies first.</summary>
    public static IReadOnlyList<TemplatePack> Resolve(string id)
    {
        var order = new List<TemplatePack>();
        void Visit(string packId, HashSet<string> path)
        {
            if (order.Any(p => p.Id == packId)) return;
            if (!All.TryGetValue(packId, out var pack)) throw new KeyNotFoundException($"Unknown template '{packId}'.");
            if (!path.Add(packId)) throw new InvalidOperationException($"Template dependency cycle at '{packId}'.");
            foreach (var r in pack.Requires ?? []) Visit(r, path);
            path.Remove(packId);
            order.Add(pack);
        }
        Visit(id, []);
        return order;
    }
}

/// <summary>What one application of packs did.</summary>
public sealed class TemplateApplication
{
    public List<string> Applied { get; } = [];
    public int VocabulariesCreated { get; set; }
    public int TermsCreated { get; set; }
    public int SchemasCreated { get; set; }
    public int SchemasExtended { get; set; }
    public int FoldersCreated { get; set; }
}

/// <summary>
/// Applies packs additively and idempotently: it creates what is missing and never renames, overwrites or deletes what a
/// tenant already has. Applying the same pack twice changes nothing.
/// </summary>
public sealed class TemplateApplier(
    IRepository<Vocabulary> vocabularies, IRepository<Term> terms, IRepository<MetadataSchema> schemas, IRepository<Folder> folders,
    IQueryExecutor q, ITenantContext tenant, ICurrentUser me, IClock clock)
{
    public Task<TemplateApplication> ApplyAsync(string packId, CancellationToken ct) => ApplyAsync([packId], ct);

    /// <summary>
    /// Applies several packs in one pass (state is read once, so packs that share a dependency do not collide).
    /// Each pack is applied after the packs it requires, and never more than once.
    /// </summary>
    public async Task<TemplateApplication> ApplyAsync(IEnumerable<string> packIds, CancellationToken ct)
    {
        var chain = new List<TemplatePack>();
        foreach (var id in packIds)
            foreach (var pack in TemplateCatalog.Resolve(id))
                if (chain.All(p => p.Id != pack.Id)) chain.Add(pack);

        var result = new TemplateApplication();
        // State is read once and kept in memory: rows added in this request are not yet visible to queries.
        var vocabs = (await q.ToListAsync(vocabularies.Query(), ct)).ToDictionary(v => v.Key);
        var termsByVocab = (await q.ToListAsync(terms.Query(), ct)).GroupBy(t => t.VocabularyId).ToDictionary(g => g.Key, g => g.ToList());
        var currentSchemas = (await q.ToListAsync(schemas.Query().Where(s => s.IsCurrent), ct)).ToDictionary(s => s.AssetType);
        var existingFolders = await q.ToListAsync(folders.Query(), ct);
        var now = clock.UtcNow;

        foreach (var pack in chain)
        {
            result.Applied.Add(pack.Id);

            foreach (var pv in pack.Vocabularies ?? [])
            {
                if (!vocabs.TryGetValue(pv.Key, out var vocab))
                {
                    vocab = Vocabulary.Create(tenant.TenantId, pv.Key, pv.Name, pv.Description, pv.Hierarchical, pv.Levels, now);
                    vocabularies.Add(vocab);
                    vocabs[pv.Key] = vocab;
                    termsByVocab[vocab.Id] = [];
                    result.VocabulariesCreated++;
                }
                AddTerms(vocab, null, pv.Terms, termsByVocab[vocab.Id], result, now);
            }

            foreach (var ps in pack.Schemas ?? [])
            {
                if (!currentSchemas.TryGetValue(ps.AssetType, out var current))
                {
                    var created = MetadataSchema.Create(tenant.TenantId, ps.AssetType, 1, ps.Fields, $"Template '{pack.Id}'", me.UserId, now);
                    schemas.Add(created);
                    currentSchemas[ps.AssetType] = created;
                    result.SchemasCreated++;
                    continue;
                }
                var missing = ps.Fields.Where(f => current.Fields.All(c => c.Name != f.Name)).ToList();
                if (missing.Count == 0) continue;
                var next = MetadataSchema.Create(tenant.TenantId, ps.AssetType, current.Version + 1, current.Fields.Concat(missing), $"Template '{pack.Id}' added {string.Join(", ", missing.Select(m => m.Name))}", me.UserId, now);
                current.Retire();
                schemas.Add(next);
                currentSchemas[ps.AssetType] = next;
                result.SchemasExtended++;
            }

            AddFolders(null, pack.Folders ?? [], existingFolders, result, now);
        }
        return result;
    }

    private void AddTerms(Vocabulary vocab, Term? parent, List<PackTerm> pack, List<Term> existing, TemplateApplication result, DateTimeOffset now)
    {
        var order = 0;
        foreach (var pt in pack)
        {
            var term = existing.FirstOrDefault(t => t.Code == pt.Code);
            if (term is null)
            {
                term = Term.Create(tenant.TenantId, vocab.Id, parent, pt.Code, pt.Labels, pt.SortOrder ?? order, pt.Attributes, now);
                terms.Add(term);
                existing.Add(term);
                result.TermsCreated++;
            }
            order++;
            if (pt.Children is { Count: > 0 }) AddTerms(vocab, term, pt.Children, existing, result, now);
        }
    }

    private void AddFolders(Folder? parent, List<PackFolder> pack, List<Folder> existing, TemplateApplication result, DateTimeOffset now)
    {
        foreach (var pf in pack)
        {
            var label = pf.Label ?? Paths.Slugify(pf.Name, "folder");
            var folder = existing.FirstOrDefault(f => f.ParentId == parent?.Id && f.Label == label);
            if (folder is null)
            {
                folder = Folder.Create(tenant.TenantId, parent, pf.Name, label, pf.InheritedMetadata, me.UserId, now);
                folders.Add(folder);
                existing.Add(folder);
                result.FoldersCreated++;
            }
            if (pf.Children is { Count: > 0 }) AddFolders(folder, pf.Children, existing, result, now);
        }
    }
}

public sealed record ListTemplatesQuery : IQuery<IReadOnlyList<TemplateDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.TenantsManage;
}

public sealed class ListTemplatesHandler : IRequestHandler<ListTemplatesQuery, IReadOnlyList<TemplateDto>>
{
    public Task<Result<IReadOnlyList<TemplateDto>>> Handle(ListTemplatesQuery r, CancellationToken ct) =>
        Task.FromResult<Result<IReadOnlyList<TemplateDto>>>(TemplateCatalog.All.Values.OrderBy(p => p.Id)
            .Select(p => new TemplateDto(p.Id, p.Name, p.Description, p.Requires ?? [], (p.Vocabularies ?? []).Count, p.TermCount, (p.Schemas ?? []).Count, p.FolderCount))
            .ToList());
}

public sealed record ApplyTemplateCommand(string TemplateId) : ICommand<TemplateApplyResultDto>, IAuthorizedRequest
{
    public string Permission => Permissions.TenantsManage;
}

public sealed class ApplyTemplateHandler(TemplateApplier applier, IEventCollector events) : IRequestHandler<ApplyTemplateCommand, TemplateApplyResultDto>
{
    public async Task<Result<TemplateApplyResultDto>> Handle(ApplyTemplateCommand c, CancellationToken ct)
    {
        if (!TemplateCatalog.All.ContainsKey(c.TemplateId)) return Error.NotFound($"Unknown template '{c.TemplateId}'.");
        var r = await applier.ApplyAsync(c.TemplateId, ct);
        events.Raise(Events.Changed("template.applied", "template", Guid.Empty));
        return new TemplateApplyResultDto(r.Applied, r.VocabulariesCreated, r.TermsCreated, r.SchemasCreated, r.SchemasExtended, r.FoldersCreated);
    }
}
