using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TokenScope.Api.Shared.Usage;

public static class UsageIdentity
{
    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Keep this serialization stable: existing databases already contain these replay keys.
    public static string Key(ApiRequestRow request) =>
        request.EventKey ?? Hash(JsonSerializer.Serialize(request with
        {
            EventKey = null
        }));
}
