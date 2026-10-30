namespace TokenScope.Api.Infrastructure.Http;

public static class RequestOrigins
{
    public static bool IsAllowed(string origin, HostString host, bool development)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return uri.Authority.Equals(host.Value, StringComparison.OrdinalIgnoreCase) ||
            development && uri.IsLoopback && uri.Port == 5199;
    }
}
