using Dam.Domain.Common;

namespace Dam.Domain.Assets;

/// <summary>
/// How long an asset's file bytes are kept once it leaves active use (spec A4.1). The retention job (Step 1.6b) moves an
/// asset from expired to archived after <see cref="ArchiveAfterDays"/>, then from archived to deleted (purged, bytes
/// removed) after <see cref="DeleteAfterDays"/> — skipping any asset under legal hold unless <see cref="LegalHoldExempt"/>.
/// A policy with a null day count simply never drives that step for the assets it applies to; nothing happens until an
/// asset is actually assigned the policy (<c>Asset.RetentionPolicyId</c>).
/// </summary>
public sealed class RetentionPolicy : TenantEntity
{
    private RetentionPolicy() { }

    public string Name { get; private set; } = "";
    public int? ArchiveAfterDays { get; private set; }
    public int? DeleteAfterDays { get; private set; }
    /// <summary>When true, this policy purges an asset on schedule even if it is under legal hold. Default false: a hold
    /// always wins unless a policy explicitly says otherwise.</summary>
    public bool LegalHoldExempt { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static RetentionPolicy Create(Guid tenantId, string name, int? archiveAfterDays, int? deleteAfterDays, bool legalHoldExempt, Guid? createdBy, DateTimeOffset now) => new()
    {
        TenantId = tenantId, Name = name.Trim(), ArchiveAfterDays = archiveAfterDays, DeleteAfterDays = deleteAfterDays,
        LegalHoldExempt = legalHoldExempt, CreatedBy = createdBy, CreatedAt = now,
    };

    public void Update(string name, int? archiveAfterDays, int? deleteAfterDays, bool legalHoldExempt)
    {
        Name = name.Trim();
        ArchiveAfterDays = archiveAfterDays;
        DeleteAfterDays = deleteAfterDays;
        LegalHoldExempt = legalHoldExempt;
    }
}
