using System.Net;

namespace Agent.Hub;

public sealed class AgentApiException(
    HttpStatusCode statusCode,
    string? errorCode,
    string message,
    TimeSpan? retryAfter = null) : HttpRequestException(message, null, statusCode)
{
    public string? ErrorCode { get; } = errorCode;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
