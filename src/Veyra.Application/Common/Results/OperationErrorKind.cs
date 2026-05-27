namespace Veyra.Application.Common.Results;

public enum OperationErrorKind
{
    None = 0,

    // Generic / unclassified
    General = 1,

    // Connectivity
    NetworkUnavailable = 10,
    CloudUnavailable = 11,
    Timeout = 12,

    // Authentication
    AuthenticationRequired = 20,
    TokenExpired = 21,
    Unauthorized = 22,

    // Data
    NotFound = 30,
    Conflict = 31,
    ValidationFailed = 32,

    // Local
    FilesystemError = 40,

    // Unrecoverable
    Fatal = 99,
}
