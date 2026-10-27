using System.Net;
using System.Text;

namespace KatanaAirWinHost.Bridge;

/// <summary>Proxy result: status code, Content-Type and body.</summary>
internal readonly record struct ProxyResponse(int Status, string ContentType, byte[] Body);

/// <summary>Server-side forwarding for <c>/__proxy?url=&lt;percent-encoded URL&gt;</c>.</summary>
internal static class CloudProxy
{
    // Schemes: http/https. Redirects: max 3 hops, each target checked against this list. Timeout 10 s.
    // KAWH_PROXY_OFF=1 returns 403 for every request.
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.roland.com",
        "bosstonecentral.com",
        "www.bosstonecentral.com",
        "api.soundcloud.com",
        "w.soundcloud.com",
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // One HttpClient per process; the server runs one thread per request.
    private static readonly HttpClient Client = CreateClient();

    public static bool IsEnabled =>
        !string.Equals(Environment.GetEnvironmentVariable("KAWH_PROXY_OFF"), "1", StringComparison.Ordinal);

    public static async Task<ProxyResponse> FetchAsync(string rawUrl)
    {
        if (!IsEnabled)
            return Text(403, "proxy disabled (KAWH_PROXY_OFF=1)");

        if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            return Text(400, "invalid url");

        for (int hop = 0; hop <= 3; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return Text(403, $"scheme not allowed: {uri.Scheme}");

            if (!AllowedHosts.Contains(uri.Host))
                return Text(403, $"host not allowed: {uri.Host}");

            try
            {
                using var response = await Client
                    .GetAsync(uri, HttpCompletionOption.ResponseContentRead)
                    .ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    Uri? location = response.Headers.Location;
                    if (location is null) return Text(502, "redirect without location");

                    // Relative Location headers resolve against the current request URI.
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }

                byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                string contentType = response.Content.Headers.ContentType?.ToString()
                                     ?? "application/octet-stream";
                return new ProxyResponse((int)response.StatusCode, contentType, body);
            }
            catch (TaskCanceledException)
            {
                return Text(504, "upstream timeout (10s)");
            }
            catch (HttpRequestException ex)
            {
                return Text(502, "upstream error: " + ex.Message);
            }
        }

        return Text(502, "too many redirects");
    }

    private static bool IsRedirect(HttpStatusCode status) => status switch
    {
        HttpStatusCode.MovedPermanently => true,
        HttpStatusCode.Found => true,
        HttpStatusCode.SeeOther => true,
        HttpStatusCode.TemporaryRedirect => true,
        HttpStatusCode.PermanentRedirect => true,
        _ => false,
    };

    private static ProxyResponse Text(int status, string message) =>
        new(status, "text/plain; charset=utf-8", Utf8NoBom.GetBytes(message));

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Redirects are followed manually; each hop is checked against the allowlist.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }
}
