using Dam.Application.Messaging;
using Dam.Domain.Common;
using FluentValidation;

namespace Dam.Application.Ping;

public sealed record PingCommand(string Message) : ICommand<string>, IAuthorizedRequest
{
    public string Permission => "system.ping";
}

public sealed class PingValidator : AbstractValidator<PingCommand>
{
    public PingValidator() => RuleFor(x => x.Message).NotEmpty().MaximumLength(100);
}

public sealed class PingHandler : IRequestHandler<PingCommand, string>
{
    public Task<Result<string>> Handle(PingCommand request, CancellationToken ct) =>
        Task.FromResult<Result<string>>($"pong: {request.Message}");
}
