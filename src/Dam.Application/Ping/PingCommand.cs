using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Common;
using FluentValidation;

namespace Dam.Application.Ping;

public sealed record PingCommand(string Message) : ICommand<string>, IAuthorizedRequest
{
    public string Permission => "system.ping";
}

public sealed record SystemPingEvent(string Message) : DomainEvent
{
    public override string Name => "system.ping";
}

public sealed class PingValidator : AbstractValidator<PingCommand>
{
    public PingValidator() => RuleFor(x => x.Message).NotEmpty().MaximumLength(100);
}

public sealed class PingHandler(IEventCollector events) : IRequestHandler<PingCommand, string>
{
    public Task<Result<string>> Handle(PingCommand request, CancellationToken ct)
    {
        events.Raise(new SystemPingEvent(request.Message));
        return Task.FromResult<Result<string>>($"pong: {request.Message}");
    }
}
