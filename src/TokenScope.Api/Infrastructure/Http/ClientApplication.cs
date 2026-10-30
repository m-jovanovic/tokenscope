namespace TokenScope.Api.Infrastructure.Http;

public static class ClientApplication
{
    public static void UseClientFiles(this WebApplication app)
    {
        app.UseDefaultFiles();

        app.UseStaticFiles();
    }

    public static void MapClientFallback(this WebApplication app)
    {
        app.MapFallback(async context =>
        {
            if (!IsClientRoute(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
            var indexPath = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexPath))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html";
            await context.Response.SendFileAsync(indexPath);
        });
    }

    private static bool IsClientRoute(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) &&
        !Path.HasExtension(request.Path.Value) &&
        !request.Path.StartsWithSegments("/api") &&
        !request.Path.StartsWithSegments("/v1") &&
        !request.Path.StartsWithSegments("/mcp");
}
