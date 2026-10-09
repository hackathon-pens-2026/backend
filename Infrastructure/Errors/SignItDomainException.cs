namespace SignIt.Infrastructure.Errors;

public enum DomainErrorKind
{
    Validation = 400,
    Unauthorized = 401,
    Forbidden = 403,
    NotFound = 404,
    Conflict = 409,
    ProcessingFailed = 500
}

public class SignItDomainException : Exception
{
    public DomainErrorKind Kind { get; }
    public string Code { get; }

    public SignItDomainException(DomainErrorKind kind, string code, string message) : base(message)
    {
        Kind = kind;
        Code = code;
    }
}
