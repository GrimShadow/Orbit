namespace Dam.Domain.Common;

public enum ErrorType { Validation, Unauthorized, Forbidden, NotFound, Conflict, Failure, TooManyRequests }

/// <param name="Extensions">Extra members for the problem+json answer (a count, a list of reasons), with their own JSON types. Used by Conflict and Forbidden.</param>
public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Failure,
    IReadOnlyDictionary<string, string[]>? FieldErrors = null, IReadOnlyDictionary<string, object?>? Extensions = null)
{
    public static Error Validation(IReadOnlyDictionary<string, string[]> fields) =>
        new("validation_failed", "One or more validation errors occurred.", ErrorType.Validation, fields);
    public static Error Forbidden(string message = "Not permitted.") => new("forbidden", message, ErrorType.Forbidden);
    public static Error Unauthorized(string message = "Authentication required.") => new("unauthorized", message, ErrorType.Unauthorized);
    public static Error NotFound(string message = "Not found.") => new("not_found", message, ErrorType.NotFound);
    public static Error Conflict(string message) => new("conflict", message, ErrorType.Conflict);
    /// <summary>The caller must wait before asking again; <paramref name="retryAfterSeconds"/> says how long (answered as 429 with a Retry-After header).</summary>
    public static Error TooManyRequests(string message, int retryAfterSeconds) =>
        new("too_many_requests", message, ErrorType.TooManyRequests, Extensions: new Dictionary<string, object?> { ["retryAfterSeconds"] = retryAfterSeconds });
}

public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    private Result(T? value, Error? error) { _value = value; _error = error; }

    public bool IsSuccess => _error is null;
    public bool IsFailure => !IsSuccess;
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Result is a failure.");
    public Error Error => _error ?? throw new InvalidOperationException("Result is a success.");

    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Failure(Error error) => new(default, error);
    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(Error error) => Failure(error);
}
