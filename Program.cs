using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;

namespace I3X4Influx
{
    public class Program
    {
        private const string CorsPolicyName = "i3xCors";

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();

            // The i3X spec requires servers to honor Accept-Encoding: gzip. Enable gzip response
            // compression (including over HTTPS) for the JSON payloads this API returns.
            builder.Services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
                options.Providers.Add<GzipCompressionProvider>();
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
            });

            builder.Services.Configure<GzipCompressionProviderOptions>(options =>
            {
                options.Level = CompressionLevel.Fastest;
            });

            builder.Services.AddOpenApi();

            builder.Services.AddSwaggerGen(options =>
            {
                // Enable HTTP Basic auth in the Swagger UI so the "Authorize" button lets users
                // supply credentials that are sent as the "Authorization: Basic" header on API calls.
                options.AddSecurityDefinition("basic", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "basic",
                    Description = "Enter the I3X_BASIC_AUTH_USERNAME and I3X_BASIC_AUTH_PASSWORD credentials."
                });

                // OAuth2 / OpenID Connect bearer tokens are accepted as a second authentication method
                // when I3X_OAUTH2_AUTHORITY is configured.
                options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Enter an OAuth2 / OpenID Connect access token (JWT)."
                });

                options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecuritySchemeReference("basic", document),
                        new List<string>()
                    },
                    {
                        new OpenApiSecuritySchemeReference("bearer", document),
                        new List<string>()
                    }
                });
            });

            builder.Services.AddSingleton<InfluxDataService>();

            // OAuth2 bearer-token validator (second authentication method alongside HTTP Basic).
            builder.Services.AddSingleton<OAuth2TokenValidator>();

            builder.Services.AddSingleton<SubscriptionStore>();

            // CORS: the browser-based CESMII i3X client calls this API cross-origin, so the API must
            // return the appropriate Access-Control-* headers (including for preflight OPTIONS) or the
            // browser blocks the requests. Allow any origin/header/method by default; restrict the
            // origins via the I3X_CORS_ORIGINS env var (comma-separated) in production if needed.
            string[] allowedOrigins = (Environment.GetEnvironmentVariable("I3X_CORS_ORIGINS") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            builder.Services.AddCors(options =>
            {
                options.AddPolicy(CorsPolicyName, policy =>
                {
                    if (allowedOrigins.Length > 0)
                    {
                        policy.WithOrigins(allowedOrigins)
                              .AllowAnyHeader()
                              .AllowAnyMethod();
                    }
                    else
                    {
                        policy.AllowAnyOrigin()
                              .AllowAnyHeader()
                              .AllowAnyMethod();
                    }
                });
            });

            var app = builder.Build();

            // Response compression must run early so downstream endpoint output is compressed.
            app.UseResponseCompression();

            // Configure middleware pipeline
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "i3X InfluxDB Adapter v1");
            });

            app.UseHttpsRedirection();

            // CORS must run before the endpoints so preflight requests are handled.
            app.UseCors(CorsPolicyName);

            // Authentication (HTTP Basic and/or OAuth2 bearer tokens).
            // Runs after CORS so preflight OPTIONS requests are not challenged.
            app.UseMiddleware<AuthMiddleware>();

            app.MapControllers();

            app.Run();
        }
    }
}
