using System.Text.Json;

namespace TokenScope.Api.Shared.Usage;

public static class UsageTokens
{
    public static int Read(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
        {
            throw new InvalidDataException("Invalid token count.");
        }

        return count;
    }
}
