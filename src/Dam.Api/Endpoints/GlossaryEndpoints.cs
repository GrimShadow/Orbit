using Dam.Application.Ai;
using Dam.Application.Messaging;
using Dam.Shared.Ai;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

/// <summary>The tenant's translation glossary: terms that are never translated, or always rendered one way (ADR-0036).</summary>
public static class GlossaryEndpoints
{
    public static void MapGlossaryEndpoints(this RouteGroupBuilder v1)
    {
        var glossary = v1.MapGroup("/glossary").WithTags("Glossary");

        glossary.MapGet("", (string? q, string? cursor, int? limit, IDispatcher d, CancellationToken ct) =>
                Run(d, new ListGlossaryQuery(q, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListGlossary")
            .WithSummary("The glossary, alphabetical, paged by the term itself (nextCursor is the last term). q keeps terms containing the text, ignoring case. Needs assets.read")
            .Produces<Dam.Shared.Identity.Page<GlossaryTermDto>>();
        glossary.MapPost("", (GlossaryTermInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new CreateGlossaryTermCommand(r), v => Task.FromResult(Results.Created($"/api/v1/glossary/{v.Id}", v)), ct))
            .WithName("CreateGlossaryTerm")
            .WithSummary("Adds a term. mode is locked (kept exactly as written in every language) or mapped (a fixed rendering per language; needs at least one translation). Terms are matched exactly, case included, longest first; a term is unique in the tenant and at most 5,000 are kept (422 on term). Needs glossary.manage")
            .Produces<GlossaryTermDto>(StatusCodes.Status201Created);
        glossary.MapPut("/{id:guid}", (Guid id, GlossaryTermInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new UpdateGlossaryTermCommand(id, r), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("UpdateGlossaryTerm")
            .WithSummary("Replaces a term. Text already translated, and translations waiting for review, are not changed. Needs glossary.manage")
            .Produces<GlossaryTermDto>();
        glossary.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new DeleteGlossaryTermCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteGlossaryTerm").WithSummary("Removes a term. Needs glossary.manage");
    }
}
