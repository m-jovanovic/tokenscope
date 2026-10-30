using System.Text;
using System.Text.Json;
using Npgsql;

namespace TokenScope.Api.Infrastructure.Http;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted && IsExpectedFailure(exception))
        {
            if (exception is NpgsqlException databaseError)
            {
                // Database messages can contain connection details or user data. Log only the category.
                logger.LogWarning("Database operation failed for {Path}; SQLSTATE {SqlState}",
                    context.Request.Path.Value,
                    databaseError is PostgresException postgres ? postgres.SqlState : "connection");
            }

            context.Response.StatusCode = exception switch
            {
                BadHttpRequestException badRequest => badRequest.StatusCode,
                NpgsqlException => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status400BadRequest,
            };

            await context.Response.WriteAsJsonAsync(new
            {
                error = GetMessage(exception)
            });
        }
    }

    private static bool IsExpectedFailure(Exception exception) =>
        exception is InvalidDataException or JsonException or OverflowException or
            DecoderFallbackException or BadHttpRequestException or NpgsqlException;

    private static string GetMessage(Exception exception) => exception switch
    {
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
            "Upload exceeds 10 MiB.",
        BadHttpRequestException { StatusCode: StatusCodes.Status429TooManyRequests } =>
            "Preview capacity reached; retry after previews expire.",
        NpgsqlException => "Database operation failed. Check database readiness and retry.",
        _ => "Invalid request data. Check the documented format and bounds.",
    };
}
