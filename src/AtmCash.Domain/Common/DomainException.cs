namespace AtmCash.Domain.Common;

public enum ErrorKind
{
    /// <summary>The request itself is invalid (400).</summary>
    Validation,

    /// <summary>A referenced resource does not exist (404).</summary>
    NotFound,

    /// <summary>The request is valid but conflicts with the current state (409).</summary>
    Conflict,

    /// <summary>The caller is not allowed to perform this action, e.g. approving their own work (403).</summary>
    Forbidden,
}

/// <summary>A business rule violation. <see cref="Code"/> is a stable, machine-readable error code.</summary>
public sealed class DomainException : Exception
{
    public DomainException(ErrorKind kind, string code, string message)
        : base(message)
    {
        Kind = kind;
        Code = code;
    }

    public ErrorKind Kind { get; }

    public string Code { get; }

    public static DomainException Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static DomainException NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static DomainException Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static DomainException Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
}
