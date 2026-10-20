using System.Text;
using System.Text.Json;
using Dam.Application.Knowledge;
using Dam.Application.Messaging;
using Dam.Shared.Knowledge;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

/// <summary>Knowledge documents (manuals, FAQs, service documents) on top of assets, VIN rules, and resolution from a VIN (ADR-0039).</summary>
public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this RouteGroupBuilder v1)
    {
        var knowledge = v1.MapGroup("/knowledge").WithTags("Knowledge");

        knowledge.MapGet("/resolve", (string? vin, string? type, string? lang, IDispatcher d, CancellationToken ct) =>
                Run(d, new ResolveKnowledgeQuery(vin, type, lang), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ResolveKnowledge")
            .WithSummary("The document that describes a vehicle: validates the VIN (422 when it is not 17 letters and digits without I, O or Q), chooses the VIN rule (highest priority, then most specific; 404 when none matches), then the best document of the rule's model for type (default owner_manual) among those the caller may read, exactly as for assets (RBAC, ABAC, status). Published documents come before ones in review (the Ask Assist role sees both); then an exact variant over an empty one, a narrower model-year range over a wider one, the most recently updated. The language is lang, else the tenant's default, else en, else any; otherLanguages lists the rest. 404 with code knowledge.no_document (and the matched vehicle) when there is no readable document, 404 with code knowledge.no_rule when no rule matches, 422 with code knowledge.invalid_vin for a malformed VIN. Never reveals documents the caller cannot read. Needs assets.read")
            .Produces<ResolveDto>();

        var docs = knowledge.MapGroup("/documents");
        docs.MapGet("", (string? type, string? model, string? variant, int? year, string? lang, string? q, string? assetId, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                Run(d, new ListKnowledgeDocumentsQuery(type, model, variant, year, lang, q, assetId, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListKnowledgeDocuments")
            .WithSummary("Knowledge documents whose assets the caller can read, newest first, paged by id (limit 50, at most 200). Filters: assetId (the mapping of one asset), type, model and variant codes, lang, year (documents for that model year or for no year in particular), q (title, model or variant contains the text). Needs assets.read")
            .Produces<Dam.Shared.Identity.Page<KnowledgeDocumentDto>>();
        docs.MapPost("", (CreateKnowledgeDocumentInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new CreateKnowledgeDocumentCommand(r), v => Task.FromResult(Results.Created($"/api/v1/knowledge/documents/{v.Id}", v)), ct))
            .WithName("CreateKnowledgeDocument")
            .WithSummary("Maps a manual, document or html5 asset as a knowledge document (one per asset; 409 if it already has one). docType is owner_manual, faq, service, training or product_info; model, variant and theme are lower-case codes; modelYearFrom and modelYearTo come together; language defaults to the tenant's default language. The worker then reads the chapters. Needs knowledge.manage and assets.update on the asset")
            .Produces<KnowledgeDocumentDetailDto>(StatusCodes.Status201Created);
        docs.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetKnowledgeDocumentQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetKnowledgeDocument").WithSummary("One knowledge document with its chapters. 404 when it does not exist or its asset cannot be read. Needs assets.read on the asset")
            .Produces<KnowledgeDocumentDetailDto>();
        docs.MapPut("/{id:guid}", (Guid id, UpdateKnowledgeDocumentInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new UpdateKnowledgeDocumentCommand(id, r), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("UpdateKnowledgeDocument")
            .WithSummary("Replaces the document's mapping (the asset cannot change). Needs knowledge.manage and assets.update on the asset")
            .Produces<KnowledgeDocumentDetailDto>();
        docs.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new DeleteKnowledgeDocumentCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteKnowledgeDocument").WithSummary("Removes the mapping; the asset and its versions are untouched. Needs knowledge.manage and assets.update on the asset");
        docs.MapGet("/{id:guid}/chapters", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetKnowledgeChaptersQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetKnowledgeChapters")
            .WithSummary("The document's table of contents: source (none, pdf, html or manual), when it was indexed, and the chapters. Needs assets.read on the asset")
            .Produces<ChaptersDto>();
        docs.MapPut("/{id:guid}/chapters", (Guid id, ChaptersInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new SetKnowledgeChaptersCommand(id, r), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("SetKnowledgeChapters")
            .WithSummary("Enters or corrects the chapters by hand (source manual; at most 500; titles up to 300 characters). An empty list clears them and hands the document back to automatic indexing. A later re-index leaves manual chapters alone unless forced. Needs knowledge.manage")
            .Produces<ChaptersDto>();
        docs.MapPost("/{id:guid}/reindex", (Guid id, bool? force, IDispatcher d, CancellationToken ct) =>
                Run(d, new ReindexKnowledgeDocumentCommand(id, force ?? false), v => Task.FromResult(Results.Accepted($"/api/v1/knowledge/documents/{id}", v)), ct))
            .WithName("ReindexKnowledgeDocument")
            .WithSummary("Asks the worker to read the chapters again from the asset's current version (202). Manual chapters are kept unless force=true. Needs knowledge.manage")
            .Produces<KnowledgeDocumentDetailDto>(StatusCodes.Status202Accepted);

        var rules = knowledge.MapGroup("/vin-rules");
        rules.MapGet("", (string? q, string? model, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                Run(d, new ListVinRulesQuery(q, model, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListVinRules")
            .WithSummary("VIN rules by name, paged by the lower-cased name (limit 100, at most 500; nextCursor is the last name, null at the end). q keeps rules whose name, pattern or model contains the text; model keeps one model. Needs knowledge.manage or assets.update")
            .Produces<Dam.Shared.Identity.Page<VinRuleDto>>();
        rules.MapPost("", (VinRuleInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new CreateVinRuleCommand(r), v => Task.FromResult(Results.Created($"/api/v1/knowledge/vin-rules/{v.Id}", v)), ct))
            .WithName("CreateVinRule")
            .WithSummary("Adds a rule: a pattern (letters, digits, ? for any character, [ABC] or [A-C] sets, a final * for anything after; without it all 17 characters), the model (and optionally variant, fixed model year or the year read from the VIN's tenth character, theme), a priority (higher wins) and an enabled flag. The name is unique in the tenant, ignoring case; at most 5,000 rules (422). Needs knowledge.manage")
            .Produces<VinRuleDto>(StatusCodes.Status201Created);
        rules.MapPut("/{id:guid}", (Guid id, VinRuleInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new UpdateVinRuleCommand(id, r), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("UpdateVinRule").WithSummary("Replaces a rule. Needs knowledge.manage").Produces<VinRuleDto>();
        rules.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new DeleteVinRuleCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteVinRule").WithSummary("Removes a rule. Needs knowledge.manage");
        rules.MapPost("/test", (VinTestInput r, IDispatcher d, CancellationToken ct) => Run(d, new TestVinQuery(r.Vin), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("TestVinRules")
            .WithSummary("Weighs every rule against a VIN without changing anything: the chosen rule and vehicle, and for each rule whether it matched and why (or the first character that disagrees). A malformed VIN answers 200 with valid=false and a reason. Needs knowledge.manage")
            .Produces<VinTestDto>();
        rules.MapPost("/import", async (HttpRequest request, bool? dryRun, bool? partial, IDispatcher d, CancellationToken ct) =>
            {
                var csv = await ReadCsvAsync(request, ct);
                return csv.Error is not null
                    ? Results.ValidationProblem(new Dictionary<string, string[]> { ["csv"] = [csv.Error] }, title: "One or more validation errors occurred.", statusCode: 422)
                    : await Run(d, new ImportVinRulesCommand(csv.Text, dryRun ?? false, partial ?? false), v => Task.FromResult(Results.Ok(v)), ct);
            })
            .WithName("ImportVinRules")
            .WithSummary("Creates or updates rules from CSV (columns name,pattern,model,variant,year,theme,priority,enabled; a first row starting with 'name' is a header and may reorder or omit optional columns; year is 'vin', empty or a number; enabled defaults to true), matched by name, in one transaction. Send the CSV as the request body (text/csv, or JSON {\"csv\": \"...\"}), at most 2 MB. With any invalid line nothing is changed unless partial=true; dryRun=true changes nothing and reports what would happen. The answer lists created, updated, skipped (identical to the stored rule) and per-line errors (200 even with line errors; 422 when the file as a whole is unusable). Needs knowledge.manage")
            .Accepts<string>("text/csv", "text/plain", "application/json")
            .Produces<VinImportResultDto>();
    }

    private static async Task<(string? Text, string? Error)> ReadCsvAsync(HttpRequest request, CancellationToken ct)
    {
        var limit = Dam.Domain.Knowledge.KnowledgeLimits.MaxImportBytes;
        if (request.ContentLength > limit + 1024) return (null, $"The file is larger than {limit / (1024 * 1024)} MB.");
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > limit + 1024) return (null, $"The file is larger than {limit / (1024 * 1024)} MB.");
            ms.Write(buffer, 0, read);
        }
        var text = Encoding.UTF8.GetString(ms.ToArray());
        if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("csv", out var c) && c.ValueKind == JsonValueKind.String
                    ? (c.GetString(), null)
                    : (null, "Send a JSON object like {\"csv\": \"name,pattern,...\"}, or the CSV itself as text/csv.");
            }
            catch (JsonException) { return (null, "The body is not valid JSON."); }
        }
        return (text, null);
    }
}
