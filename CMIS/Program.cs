using CMIS.Core.Extensions;
using CMIS.Core.Hubs;
using CMIS.Core.Jwt;
using CMIS.Core.Security;
using CMIS.Core.Middleware;
using CMIS.WH;
using CMIS.PIPE;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCoreInfrastructure(builder.Configuration);
builder.Services.AddCMISModules(builder.Configuration, modules =>
{
    modules.Add<WarehouseModule>();
    modules.Add<PipeModule>();
});

builder.Services.AddSignalR();

var jwtOpts = builder.Configuration.GetSection("Jwt").Get<JwtOptions>();
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        ValidIssuer = jwtOpts?.Issuer,
        ValidAudience = jwtOpts?.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOpts?.SecretKey ?? String.Empty)),
        ClockSkew = TimeSpan.Zero
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];

            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) &&
                path.StartsWithSegments("/hubs/session"))
            {
                context.Token = accessToken;
            }

            return Task.CompletedTask;
        }
    };
});


builder.Services.AddEndpointsApiExplorer();// required for minimal APIs
builder.Services.AddSwaggerGen(options =>
{
    // API metadata
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "CMIS Core API",
        Version = "v1",
        Description = "Authentication & session management (ADO.NET + stored procedures)."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Enter JWT Bearer token only. Example: eyJhbGciOi...",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    // Apply the scheme globally
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});

var app = builder.Build();

app.UseCors(c => {
    c.AllowAnyHeader()
    .AllowAnyMethod()
    .WithOrigins("http://localhost:5173", "http://192.168.136.75:5173")
    .AllowCredentials();
});

app.UseHttpsRedirection();

app.UseMiddleware<ApiExceptionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<SessionValidationMiddleware>();

// Swagger UI (only in Development, but can be left always‑on)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();// generates /swagger/v1/swagger.json
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "CMIS Core API v1");
        c.RoutePrefix = string.Empty; // serve UI at root (optional)
    });
}

app.MapControllers();

app.MapHub<SessionHub>("/hubs/session");

app.Run();
