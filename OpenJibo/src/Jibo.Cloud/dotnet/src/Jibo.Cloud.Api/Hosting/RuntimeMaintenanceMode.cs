namespace Jibo.Cloud.Api.Hosting;

/// <summary>Startup-only closed profile for installation and maintenance.</summary>
internal static class RuntimeMaintenanceMode
{
    internal const string ConfigurationKey = "OpenJibo:Maintenance:Enabled";

    internal static bool IsEnabled(IConfiguration configuration)
    {
        var value = configuration[ConfigurationKey];
        if (value is null) return false;
        if (bool.TryParse(value, out var enabled)) return enabled;
        throw new InvalidOperationException("Runtime maintenance must be explicitly true or false.");
    }

    internal static Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var healthMethod = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        if (healthMethod && context.Request.Path == "/health/live")
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            if (HttpMethods.IsHead(context.Request.Method)) return Task.CompletedTask;
            return context.Response.WriteAsJsonAsync(new { status = "alive", maintenance = true }, context.RequestAborted);
        }

        context.Response.StatusCode = healthMethod && context.Request.Path == "/health/ready"
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
