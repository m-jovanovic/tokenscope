namespace TokenScope.Api.Infrastructure.Http;

public sealed class RequestBoundaryMiddleware(RequestDelegate next, IWebHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var origin = context.Request.Headers.Origin;
        if (HttpMethods.IsPost(context.Request.Method) && origin.Count > 0 &&
            !RequestOrigins.IsAllowed(origin.ToString(), context.Request.Host, environment.IsDevelopment()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Browser requests must come from this application's origin.",
            });
            return;
        }

        await next(context);
    }
}
