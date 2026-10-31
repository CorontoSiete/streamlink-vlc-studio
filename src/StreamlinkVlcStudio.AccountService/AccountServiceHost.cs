using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;

namespace StreamlinkVlcStudio.AccountService;

public static class AccountServiceHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var clientId = builder.Configuration["Kick:ClientId"]?.Trim();
        var clientSecret = builder.Configuration["Kick:ClientSecret"]?.Trim();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Configure Kick:ClientId and Kick:ClientSecret in the service's secret store.");

        builder.Services.Configure<FormOptions>(options =>
        {
            options.ValueCountLimit = 8;
            options.KeyLengthLimit = 64;
            options.ValueLengthLimit = 4096;
        });
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16_384);
        builder.Services.AddHttpClient("kick", client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CheckCertificateRevocationList = true
            });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("tokens", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next();
        });
        app.UseRateLimiter();
        KickTokenEndpoint.Map(app, clientId, clientSecret);
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        return app;
    }
}
