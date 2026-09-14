using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using RoomLedger.Application;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.API.Services;
using RoomLedger.Infrastructure;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ───────────── Controllers ─────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ───────────── Swagger + JWT (OpenApi v2 syntax) ─────────────
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste your JWT here"
    });

    c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference("Bearer"),
            new List<string>()
        }
    });
});

// ───────────── CORS (Angular host & Vercel) ─────────────
builder.Services.AddCors(o => o.AddPolicy("app", p => p
    .SetIsOriginAllowed(origin =>
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        try
        {
            var host = new Uri(origin).Host;
            return host == "localhost"
                || host.EndsWith("vercel.app", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("runasp.net", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    })
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// ───────────── Layers ─────────────
builder.Services.AddInfrastructure(builder.Configuration);   // DbContext + services + JWT
builder.Services.AddApplication();                           // App services (AuthService, etc.)

// ───────────── Current user (reads JWT claims) ─────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

// ───────────── JWT Authentication ─────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"]!))
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// ───────────── Pipeline ─────────────
app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("app");          // ← must come BEFORE UseAuthentication
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
