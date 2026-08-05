namespace FinApp.Server.Infrastructure;

/// <summary>An error that maps to a specific HTTP status code. Thrown by services, translated to a response by the pipeline.</summary>
public class ApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class BadRequestException(string message) : ApiException(StatusCodes.Status400BadRequest, message);
public sealed class UnauthorizedException(string message) : ApiException(StatusCodes.Status401Unauthorized, message);
public sealed class ForbiddenException(string message) : ApiException(StatusCodes.Status403Forbidden, message);
public sealed class NotFoundException(string message) : ApiException(StatusCodes.Status404NotFound, message);
public sealed class ConflictException(string message) : ApiException(StatusCodes.Status409Conflict, message);

/// <summary>A Pro-only capability was reached on a Free plan (OPEN-BETA P4). Maps to HTTP 402 and carries the
/// blocked <see cref="FeatureKey"/> so the client can raise the same upgrade prompt a local gate would.</summary>
public sealed class PaymentRequiredException(string featureKey, string message)
    : ApiException(StatusCodes.Status402PaymentRequired, message)
{
    public string FeatureKey { get; } = featureKey;
}

public static class ExceptionMessageExtensions
{
    /// <summary><see cref="ArgumentException"/>.Message appends " (Parameter 'name')" — a raw parameter-name leak
    /// that shouldn't reach clients. Strip it so domain validation messages read cleanly in the UI.</summary>
    public static string CleanMessage(this Exception ex)
    {
        var i = ex.Message.IndexOf(" (Parameter '", StringComparison.Ordinal);
        return i >= 0 ? ex.Message[..i] : ex.Message;
    }
}
