using System.Text.Json;
using Dam.Application.Content;
using Dam.Application.Messaging;
using Dam.Shared.Content;
using Microsoft.AspNetCore.Mvc;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

public sealed record CreateFolderRequest(Guid? ParentId, string Name, string? Label, JsonElement? InheritedMetadata);
public sealed record UpdateFolderRequest(string? Name, JsonElement? InheritedMetadata, Guid? DefaultWorkflowId, bool ClearDefaultWorkflow = false);
public sealed record MoveRequest(Guid? NewParentId);
public sealed record CreateVocabularyRequest(string Key, string Name, string? Description, bool Hierarchical, IReadOnlyList<string>? Levels);
public sealed record UpdateVocabularyRequest(string? Name, string? Description, IReadOnlyList<string>? Levels);
public sealed record CreateTermRequest(Guid? ParentId, string Code, Dictionary<string, string>? Labels, int? SortOrder, JsonElement? Attributes);
public sealed record UpdateTermRequest(Dictionary<string, string>? Labels, int? SortOrder, string? Status, JsonElement? Attributes);
public sealed record UpdateSchemaRequest(IReadOnlyList<FieldDto> Fields, string? ChangeNote);
public sealed record ValidateMetadataRequest(JsonElement Metadata, int? Version);

public static class ContentEndpoints
{
    public static void MapContentEndpoints(this RouteGroupBuilder v1)
    {
        // ---- folders
        var folders = v1.MapGroup("/folders").WithTags("Folders");
        folders.MapGet("", (IDispatcher d, CancellationToken ct, bool? tree) => tree == true
                ? Run(d, new GetFolderTreeQuery(), v => Task.FromResult(Results.Ok(v)), ct)
                : Run(d, new ListFoldersQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListFolders").WithSummary("All folders, flat (path order) or nested with ?tree=true").Produces<IReadOnlyList<FolderDto>>();
        folders.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new GetFolderQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetFolder").WithSummary("A folder with its ancestors and the metadata it inherits").Produces<FolderDetailDto>();
        folders.MapPost("", (CreateFolderRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new CreateFolderCommand(r.ParentId, r.Name, r.Label, r.InheritedMetadata), id => new GetFolderQuery(id), ct, "/api/v1/folders"))
            .WithName("CreateFolder").Produces<FolderDetailDto>(StatusCodes.Status201Created);
        folders.MapPatch("/{id:guid}", (Guid id, UpdateFolderRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new UpdateFolderCommand(id, r.Name, r.InheritedMetadata, r.DefaultWorkflowId, r.ClearDefaultWorkflow), fid => new GetFolderQuery(fid), ct))
            .WithName("UpdateFolder").Produces<FolderDetailDto>();
        folders.MapPost("/{id:guid}/move", (Guid id, MoveRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new MoveFolderCommand(id, r.NewParentId), fid => new GetFolderQuery(fid), ct))
            .WithName("MoveFolder").WithSummary("Re-parent a folder; the paths of everything beneath it are rewritten").Produces<FolderDetailDto>();
        folders.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteFolderCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteFolder");

        // ---- vocabularies and terms
        var vocabs = v1.MapGroup("/vocabularies").WithTags("Vocabularies");
        vocabs.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new ListVocabulariesQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListVocabularies").Produces<IReadOnlyList<VocabularyDto>>();
        vocabs.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetVocabularyQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetVocabulary").Produces<VocabularyDto>();
        vocabs.MapPost("", (CreateVocabularyRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new CreateVocabularyCommand(r.Key, r.Name, r.Description, r.Hierarchical, r.Levels), id => new GetVocabularyQuery(id), ct, "/api/v1/vocabularies"))
            .WithName("CreateVocabulary").Produces<VocabularyDto>(StatusCodes.Status201Created);
        vocabs.MapPatch("/{id:guid}", (Guid id, UpdateVocabularyRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new UpdateVocabularyCommand(id, r.Name, r.Description, r.Levels), vid => new GetVocabularyQuery(vid), ct))
            .WithName("UpdateVocabulary").Produces<VocabularyDto>();
        vocabs.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteVocabularyCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteVocabulary");
        vocabs.MapGet("/{id:guid}/terms", (Guid id, IDispatcher d, CancellationToken ct, bool? tree, bool? includeDeprecated) =>
                Run(d, new ListTermsQuery(id, tree == true, includeDeprecated ?? true),
                    v => Task.FromResult(v.Tree is not null ? Results.Ok(v.Tree) : Results.Ok(v.Flat)), ct))
            .WithName("ListTerms").WithSummary("Terms of a vocabulary, flat or nested with ?tree=true")
            .Produces<IReadOnlyList<TermNodeDto>>();
        vocabs.MapPost("/{id:guid}/terms", (Guid id, CreateTermRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new CreateTermCommand(id, r.ParentId, r.Code, r.Labels, r.SortOrder, r.Attributes), tid => new GetTermQuery(tid), ct, "/api/v1/terms"))
            .WithName("CreateTerm").Produces<TermDto>(StatusCodes.Status201Created);

        var terms = v1.MapGroup("/terms").WithTags("Vocabularies");
        terms.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetTermQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetTerm").Produces<TermDto>();
        terms.MapPatch("/{id:guid}", (Guid id, UpdateTermRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new UpdateTermCommand(id, r.Labels, r.SortOrder, r.Status, r.Attributes), tid => new GetTermQuery(tid), ct))
            .WithName("UpdateTerm").Produces<TermDto>();
        terms.MapPost("/{id:guid}/move", (Guid id, MoveRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new MoveTermCommand(id, r.NewParentId), tid => new GetTermQuery(tid), ct))
            .WithName("MoveTerm").Produces<TermDto>();
        terms.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteTermCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteTerm");

        // ---- metadata schemas
        var schemas = v1.MapGroup("/schemas").WithTags("Metadata schemas");
        schemas.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new ListSchemasQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListSchemas").WithSummary("Current schema version per asset type").Produces<IReadOnlyList<SchemaSummaryDto>>();
        schemas.MapGet("/{assetType}", (string assetType, int? version, IDispatcher d, CancellationToken ct) =>
                Run(d, new GetSchemaQuery(assetType, version), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetSchema").WithSummary("The current schema, or a specific version with ?version=").Produces<SchemaDto>();
        schemas.MapPut("/{assetType}", (string assetType, UpdateSchemaRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new UpdateSchemaCommand(assetType, r.Fields, r.ChangeNote), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("UpdateSchema").WithSummary("Save the fields; a change creates the next version and old assets stay valid").Produces<SchemaUpdateResultDto>();
        schemas.MapGet("/{assetType}/versions", (string assetType, IDispatcher d, CancellationToken ct) =>
                Run(d, new ListSchemaVersionsQuery(assetType), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListSchemaVersions").Produces<IReadOnlyList<SchemaDto>>();
        schemas.MapPost("/{assetType}/validate", (string assetType, ValidateMetadataRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new ValidateMetadataQuery(assetType, r.Metadata, r.Version), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ValidateMetadata").WithSummary("Check metadata against a schema; invalid metadata is a 422 with field-level errors")
            .Produces<ValidationResultDto>();

        // ---- template packs
        var templates = v1.MapGroup("/templates").WithTags("Templates");
        templates.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new ListTemplatesQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListTemplates").WithSummary("Starter packs: vocabularies, metadata schemas and folders").Produces<IReadOnlyList<TemplateDto>>();
        templates.MapPost("/{id}/apply", (string id, IDispatcher d, CancellationToken ct) =>
                Run(d, new ApplyTemplateCommand(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ApplyTemplate").WithSummary("Add a pack's content. Additive and repeatable: nothing existing is changed or removed").Produces<TemplateApplyResultDto>();
    }
}
