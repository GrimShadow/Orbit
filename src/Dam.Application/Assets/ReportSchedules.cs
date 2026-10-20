using Dam.Application.Abstractions;
using Dam.Application.Content;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Analytics;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Identity;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Assets;

internal static class ReportScheduleMapping
{
    public static ReportScheduleDto ToDto(ReportSchedule s) =>
        new(s.Id, s.Report, s.Days, s.Frequency, s.HourUtc, s.DayOfWeek, s.DayOfMonth, s.RecipientUserIds, s.Enabled, s.LastSentAt, s.LastError, s.NextRunAt, s.CreatedAt);
}

/// <summary>The rules for a schedule's settings, shared by create and update.</summary>
internal static class ReportScheduleRules
{
    public static void Apply<T>(AbstractValidator<T> v, Func<T, ReportScheduleInput> input) =>
        v.RuleFor(x => x).Custom((cmd, ctx) =>
        {
            var i = input(cmd);
            void Fail(string field, string message) => ctx.AddFailure(field, message);
            var def = ReportCatalogue.Find(i.Report);
            if (def is null) Fail("report", $"Must be one of: {string.Join(", ", ReportCatalogue.All.Select(r => r.Name))}.");
            if (i.Days < 1 || i.Days > (def?.MaxDays ?? KpiPeriod.MaxDays)) Fail("days", $"Must be between 1 and {def?.MaxDays ?? KpiPeriod.MaxDays}.");
            if (!ScheduleFrequencies.IsKnown(i.Frequency)) Fail("frequency", $"Must be one of: {string.Join(", ", ScheduleFrequencies.All)}.");
            if (i.HourUtc is < 0 or > 23) Fail("hourUtc", "Must be between 0 and 23.");
            if (i.Frequency == ScheduleFrequencies.Weekly && i.DayOfWeek is not (>= 1 and <= 7)) Fail("dayOfWeek", "A weekly report needs a day of the week, 1 (Monday) to 7 (Sunday).");
            if (i.Frequency == ScheduleFrequencies.Monthly && i.DayOfMonth is not (>= 1 and <= 31)) Fail("dayOfMonth", "A monthly report needs a day of the month, 1 to 31.");
            if (i.RecipientUserIds is not { Count: >= 1 }) Fail("recipientUserIds", "Choose at least one recipient.");
            else if (i.RecipientUserIds.Count > ScheduleRules.MaxRecipients) Fail("recipientUserIds", $"At most {ScheduleRules.MaxRecipients} recipients.");
            else if (i.RecipientUserIds.Any(id => id == Guid.Empty)) Fail("recipientUserIds", "A recipient is not a valid user id.");
        });

    /// <summary>Recipients are users of this tenant, active, never arbitrary addresses: a schedule cannot be used to mail an outsider.</summary>
    public static async Task<Error?> CheckRecipientsAsync(IRepository<User> users, IQueryExecutor q, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        var found = (await q.ToListAsync(users.Query().Where(u => wanted.Contains(u.Id) && u.Status == UserStatus.Active).Select(u => u.Id), ct)).ToHashSet();
        var missing = wanted.Where(id => !found.Contains(id)).ToList();
        return missing.Count == 0 ? null
            : Error.Validation(ContentMapping.Fail("recipientUserIds", $"Not active users of this tenant: {string.Join(", ", missing)}."));
    }
}

// ------------------------------------------------------------------------------------------------------------ read

public sealed record ListReportSchedulesQuery : IQuery<IReadOnlyList<ReportScheduleDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.AnalyticsManage;
}

public sealed class ListReportSchedulesHandler(IRepository<ReportSchedule> schedules, IQueryExecutor q) : IRequestHandler<ListReportSchedulesQuery, IReadOnlyList<ReportScheduleDto>>
{
    public async Task<Result<IReadOnlyList<ReportScheduleDto>>> Handle(ListReportSchedulesQuery r, CancellationToken ct) =>
        (await q.ToListAsync(schedules.Query().OrderBy(s => s.CreatedAt).ThenBy(s => s.Id), ct)).Select(ReportScheduleMapping.ToDto).ToList();
}

// ---------------------------------------------------------------------------------------------------------- create

public sealed record CreateReportScheduleCommand(ReportScheduleInput Input) : ICommand<ReportScheduleDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AnalyticsManage;
}

public sealed class CreateReportScheduleValidator : AbstractValidator<CreateReportScheduleCommand>
{
    public CreateReportScheduleValidator() => ReportScheduleRules.Apply(this, c => c.Input);
}

public sealed class CreateReportScheduleHandler(
    IRepository<ReportSchedule> schedules, IRepository<User> users, IAuthorizationService authz, ITenantContext tenant, IEventCollector events, IQueryExecutor q, IClock clock)
    : IRequestHandler<CreateReportScheduleCommand, ReportScheduleDto>
{
    public async Task<Result<ReportScheduleDto>> Handle(CreateReportScheduleCommand c, CancellationToken ct)
    {
        var i = c.Input;
        if (await q.CountAsync(schedules.Query(), ct) >= ScheduleRules.MaxPerTenant)
            return Error.Validation(ContentMapping.Fail("report", $"At most {ScheduleRules.MaxPerTenant} scheduled reports."));
        if (await ReportScheduleRules.CheckRecipientsAsync(users, q, i.RecipientUserIds, ct) is { } bad) return bad;

        var schedule = ReportSchedule.Create(tenant.TenantId, ReportCatalogue.Find(i.Report)!.Name, i.Days, i.Frequency, i.HourUtc, i.DayOfWeek, i.DayOfMonth,
            i.RecipientUserIds, i.Enabled, (await authz.GetProfileAsync(ct)).UserId, clock.UtcNow);
        schedules.Add(schedule);
        events.Raise(Events.Changed("report_schedule.created", "report_schedule", schedule.Id));
        return ReportScheduleMapping.ToDto(schedule);
    }
}

// ---------------------------------------------------------------------------------------------------------- update

public sealed record UpdateReportScheduleCommand(Guid Id, ReportScheduleInput Input) : ICommand<ReportScheduleDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AnalyticsManage;
}

public sealed class UpdateReportScheduleValidator : AbstractValidator<UpdateReportScheduleCommand>
{
    public UpdateReportScheduleValidator() => ReportScheduleRules.Apply(this, c => c.Input);
}

public sealed class UpdateReportScheduleHandler(IRepository<ReportSchedule> schedules, IRepository<User> users, IEventCollector events, IQueryExecutor q, IClock clock)
    : IRequestHandler<UpdateReportScheduleCommand, ReportScheduleDto>
{
    public async Task<Result<ReportScheduleDto>> Handle(UpdateReportScheduleCommand c, CancellationToken ct)
    {
        var schedule = await q.FirstOrDefaultAsync(schedules.Query().Where(s => s.Id == c.Id), ct);
        if (schedule is null) return Error.NotFound("Report schedule not found.");
        var i = c.Input;
        if (await ReportScheduleRules.CheckRecipientsAsync(users, q, i.RecipientUserIds, ct) is { } bad) return bad;

        schedule.Update(ReportCatalogue.Find(i.Report)!.Name, i.Days, i.Frequency, i.HourUtc, i.DayOfWeek, i.DayOfMonth, i.RecipientUserIds, i.Enabled, clock.UtcNow);
        events.Raise(Events.Changed("report_schedule.updated", "report_schedule", schedule.Id));
        return ReportScheduleMapping.ToDto(schedule);
    }
}

// ---------------------------------------------------------------------------------------------------------- delete

public sealed record DeleteReportScheduleCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.AnalyticsManage;
}

public sealed class DeleteReportScheduleHandler(IRepository<ReportSchedule> schedules, IEventCollector events, IQueryExecutor q) : IRequestHandler<DeleteReportScheduleCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteReportScheduleCommand c, CancellationToken ct)
    {
        var schedule = await q.FirstOrDefaultAsync(schedules.Query().Where(s => s.Id == c.Id), ct);
        if (schedule is null) return Error.NotFound("Report schedule not found.");
        schedules.Remove(schedule);
        events.Raise(Events.Changed("report_schedule.deleted", "report_schedule", schedule.Id));
        return schedule.Id;
    }
}

// ------------------------------------------------------------------------------------------------------- send now

/// <param name="Sent">Recipients the email went to.</param>
/// <param name="Skipped">Recipients left out (no longer active, no email address, no analytics access).</param>
/// <param name="Failed">Recipients the mail server did not accept.</param>
public sealed record SendReportNowResultDto(int Sent, int Skipped, int Failed);

/// <summary>Sends the report to the schedule's recipients now. Does not change when the schedule runs next. Answers 409 when email is not configured or nothing could be sent.</summary>
public sealed record SendReportScheduleNowCommand(Guid Id) : ICommand<SendReportNowResultDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AnalyticsManage;
}

public sealed class SendReportScheduleNowHandler(IRepository<ReportSchedule> schedules, ReportMailer mailer, IQueryExecutor q, IClock clock)
    : IRequestHandler<SendReportScheduleNowCommand, SendReportNowResultDto>
{
    public async Task<Result<SendReportNowResultDto>> Handle(SendReportScheduleNowCommand c, CancellationToken ct)
    {
        var schedule = await q.FirstOrDefaultAsync(schedules.Query().Where(s => s.Id == c.Id), ct);
        if (schedule is null) return Error.NotFound("Report schedule not found.");

        // A manual send mails everyone: the ledger of a run in progress is not consulted and not changed.
        var outcome = await mailer.SendAsync(schedule, [], (_, _) => Task.CompletedTask, ct);
        if (outcome.Sent == 0) return Error.Conflict(outcome.Error ?? outcome.Note ?? "The report was not sent to anyone.");

        schedule.RecordManualSend(clock.UtcNow, outcome.Failed > 0 ? outcome.Error : outcome.Note);
        return new SendReportNowResultDto(outcome.Sent, outcome.Skipped, outcome.Failed);
    }
}
