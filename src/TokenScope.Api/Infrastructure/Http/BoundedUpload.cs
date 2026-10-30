using System.Text;

namespace TokenScope.Api.Infrastructure.Http;

public static class BoundedUpload
{
    public const int MaxBytes = 10 * 1024 * 1024;

    public static async Task<string> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBytes)
        {
            throw new BadHttpRequestException("Upload exceeds 10 MiB.", StatusCodes.Status413PayloadTooLarge);
        }

        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int bytesRead;

        while ((bytesRead = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            // The length header may be absent, and compressed requests can expand past the limit.
            if (output.Length + bytesRead > MaxBytes)
            {
                throw new BadHttpRequestException("Upload exceeds 10 MiB.", StatusCodes.Status413PayloadTooLarge);
            }

            output.Write(buffer, 0, bytesRead);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(output.ToArray());
    }
}
