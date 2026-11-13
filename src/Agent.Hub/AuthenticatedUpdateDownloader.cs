using System.Net.Http.Headers;

namespace Agent.Hub;

public sealed class AuthenticatedUpdateDownloader(HttpClient httpClient)
{
    public async Task<HttpResponseMessage> DownloadAsync(
        Uri hubBaseAddress,
        Uri packageAddress,
        string bearerToken,
        string agentVersion,
        CancellationToken cancellationToken)
    {
        ValidatePackageAddress(hubBaseAddress, packageAddress);

        using var request = new HttpRequestMessage(HttpMethod.Get, packageAddress);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Add("X-Agent-Version", agentVersion);

        return await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private static void ValidatePackageAddress(Uri hubBaseAddress, Uri packageAddress)
    {
        if (!hubBaseAddress.IsAbsoluteUri || !packageAddress.IsAbsoluteUri)
        {
            throw new InvalidDataException("The Hub and update package URLs must be absolute.");
        }

        var sameServer = Uri.Compare(
            hubBaseAddress,
            packageAddress,
            UriComponents.SchemeAndServer,
            UriFormat.Unescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
        var updatePath = $"{hubBaseAddress.AbsolutePath.TrimEnd('/')}/updates/";
        if (!sameServer
            || !string.IsNullOrEmpty(packageAddress.UserInfo)
            || !packageAddress.AbsolutePath.StartsWith(updatePath, StringComparison.Ordinal))
        {
            // Bearer token tidak boleh mengikuti URL arbitrer dari command karena token memberi
            // akses penuh sebagai Agent, bukan hanya ke file update yang sedang diunduh.
            throw new InvalidDataException("The update package URL does not belong to the paired Print Hub.");
        }
    }
}
