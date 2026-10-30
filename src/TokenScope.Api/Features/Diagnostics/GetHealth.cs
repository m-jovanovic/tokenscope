using Dapper;
using Npgsql;

namespace TokenScope.Api.Features.Diagnostics;

public static class GetHealth
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(NpgsqlDataSource database, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT 1",
                commandTimeout: 3,
                cancellationToken: cancellationToken));

            return Results.Ok(new Response("healthy"));
        }
        catch (NpgsqlException)
        {
            return Results.Json(new Response("database-unavailable"), statusCode: 503);
        }
    }

    public sealed record Response(string Status);
}
