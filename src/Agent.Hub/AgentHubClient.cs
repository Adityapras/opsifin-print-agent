using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Agent.Hub;

public sealed class AgentHubClient(HttpClient httpClient)
{
    public async Task<EnrollResponse> EnrollAsync(
        Uri baseAddress,
        EnrollRequest payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, "enroll"))
        {
            Content = JsonContent.Create(payload, AgentJsonContext.Default.EnrollRequest),
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadAsync(response, AgentJsonContext.Default.EnrollResponse, cancellationToken);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(
        Uri baseAddress,
        string bearerToken,
        string agentVersion,
        HeartbeatRequest payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, "heartbeat"))
        {
            Content = JsonContent.Create(payload, AgentJsonContext.Default.HeartbeatRequest),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Add("X-Agent-Version", agentVersion);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadAsync(response, AgentJsonContext.Default.HeartbeatResponse, cancellationToken);
    }

    public async Task<IReadOnlyList<ClaimedJob>> ClaimJobsAsync(
        Uri baseAddress,
        string bearerToken,
        string agentVersion,
        IReadOnlyList<Guid> printerIds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(printerIds.Count);

        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            new Uri(baseAddress, "jobs/claim"),
            bearerToken,
            agentVersion);
        request.Content = JsonContent.Create(
            new ClaimJobsRequest(printerIds),
            AgentJsonContext.Default.ClaimJobsRequest);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return [];
        }

        var payload = await ReadAsync(response, AgentJsonContext.Default.ClaimJobsResponse, cancellationToken);
        return payload.Jobs;
    }

    public async Task<JobEventsResponse> SendJobEventsAsync(
        Uri baseAddress,
        string bearerToken,
        string agentVersion,
        Guid jobId,
        IReadOnlyList<JobEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(events.Count);

        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            new Uri(baseAddress, $"jobs/{jobId:D}/events"),
            bearerToken,
            agentVersion);
        request.Content = JsonContent.Create(
            new JobEventsRequest(events),
            AgentJsonContext.Default.JobEventsRequest);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadAsync(response, AgentJsonContext.Default.JobEventsResponse, cancellationToken);
    }

    public async Task SendCommandResultAsync(
        Uri baseAddress,
        string bearerToken,
        string agentVersion,
        Guid commandId,
        string status,
        string? message,
        CancellationToken cancellationToken) => await SendCommandResultAsync(
            baseAddress,
            bearerToken,
            agentVersion,
            commandId,
            status,
            message,
            null,
            cancellationToken);

    public async Task SendCommandResultAsync(
        Uri baseAddress,
        string bearerToken,
        string agentVersion,
        Guid commandId,
        string status,
        string? message,
        CommandResultAttachment? attachment,
        CancellationToken cancellationToken)
    {
        if (status is not ("succeeded" or "failed"))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        using var request = CreateAuthenticatedRequest(
            HttpMethod.Post,
            new Uri(baseAddress, $"commands/{commandId:D}/result"),
            bearerToken,
            agentVersion);
        using var content = new MultipartFormDataContent
        {
            { new StringContent(status), "status" },
        };
        if (!string.IsNullOrWhiteSpace(message))
        {
            content.Add(new StringContent(message), "message");
        }

        if (attachment is not null)
        {
            var attachmentContent = new StreamContent(attachment.Content);
            attachmentContent.Headers.ContentType = new MediaTypeHeaderValue(attachment.MediaType);
            content.Add(attachmentContent, "attachment", attachment.FileName);
        }

        request.Content = content;
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiException(response, cancellationToken);
        }
    }

    public static Uri NormalizeBaseAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("URL Hub harus berupa URL absolut.", nameof(value));
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback))
        {
            throw new ArgumentException("URL Hub wajib HTTPS; HTTP hanya boleh untuk loopback saat pengembangan.", nameof(value));
        }

        var builder = new UriBuilder(uri)
        {
            Query = "",
            Fragment = "",
        };
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/agent/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = $"{path}/agent/v1";
        }

        builder.Path = $"{path.TrimStart('/')}/";
        return builder.Uri;
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiException(response, cancellationToken);
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync(body, typeInfo, cancellationToken);
        return result ?? throw new InvalidDataException("Hub mengembalikan body JSON kosong.");
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(
        HttpMethod method,
        Uri uri,
        string bearerToken,
        string agentVersion)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Add("X-Agent-Version", agentVersion);
        return request;
    }

    private static async Task ThrowApiException(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ErrorEnvelope? envelope = null;
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            envelope = await JsonSerializer.DeserializeAsync(
                body,
                AgentJsonContext.Default.ErrorEnvelope,
                cancellationToken);
        }
        catch (JsonException)
        {
            // Respons proxy atau server yang rusak bisa bukan JSON. Status HTTP tetap cukup
            // untuk menentukan retry tanpa memasukkan body tak tepercaya ke log.
        }

        var message = envelope?.Error.Message ?? $"Hub mengembalikan HTTP {(int)response.StatusCode}.";
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
        {
            retryAfter = retryDate - DateTimeOffset.UtcNow;
        }

        throw new AgentApiException(
            response.StatusCode,
            envelope?.Error.Code,
            message,
            retryAfter is { } delay && delay > TimeSpan.Zero ? delay : null);
    }
}
