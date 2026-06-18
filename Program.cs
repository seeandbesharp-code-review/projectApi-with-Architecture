using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using projectApiAngular.Configurations;
using projectApiAngular.Data;
using projectApiAngular.Middleware;
using projectApiAngular.Repositories;
using projectApiAngular.Services;
using Serilog;
using StackExchange.Redis;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);


builder.Services.Configure<RedisSettings>(builder.Configuration.GetSection("Redis"));
// 1. נסי לקחת מהכתובת המלאה ב-Environment, אם אין - קחי מהמבנה ההיררכי, אם אין - ברירת מחדל
var redisConnectionString = builder.Configuration["Redis__ConnectionString"]
                         ?? builder.Configuration["Redis:ConnectionString"]
                         ?? "redis:6379,password=Your_Redis_Password_123";

// 2. עדכון הרישום של ה-Redis עם הגדרות עמידות
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var configuration = ConfigurationOptions.Parse(redisConnectionString, true);
    configuration.AbortOnConnectFail = false; // חשוב מאוד! מונע קריסה אם החיבור איטי
    return ConnectionMultiplexer.Connect(configuration);
});

builder.Services.AddSingleton<IRedisCacheService, RedisCacheService>();
//add cors
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowLocalhost",
        policy =>
        {
            policy.WithOrigins("http://localhost:4200")  // ����� ����� (�-Frontend)
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
});

builder.Services.AddSwaggerGen(options =>
{
    // ����� ������ ������ (Security Definition)
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "�� ����� �� ����� ���� (��� ����� Bearer)"
    });

    // ���� ������ �� �� ������ (Security Requirement)
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("SpecificPolicy", context =>
    {
        return RateLimitPartition.GetSlidingWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 8,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });
});

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        "Logs/app-.log",
        rollingInterval: RollingInterval.Day
    )
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container.
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("jwtSettings"));
JwtSettings? JwtSettings = builder.Configuration.GetSection("jwtSettings").Get<JwtSettings>();
if (JwtSettings is null || string.IsNullOrWhiteSpace(JwtSettings.SecretKey))
{
    throw new InvalidOperationException("JWT settings are not configured properly.");
}

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();


builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<IDonnerRepository, DonnerRepository>();
builder.Services.AddScoped<IDonnerService, DonnerService>();
builder.Services.AddScoped<ICategoryRepository, CategoryRepository>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<IGiftRepository, GiftRepository>();
builder.Services.AddScoped<IGiftService, GiftService>();
builder.Services.AddScoped<IPurchaseRepository, PurchaseRepository>();
builder.Services.AddScoped<IPurchaseService, PurcheseServicecs>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IBasketRepository, BasketRepository>();
builder.Services.AddScoped<IBasketService, BasketService>();
builder.Services.AddScoped<ILotteryService, LotteryService>();
builder.Services.AddScoped<IZipService, ZipService>();
builder.Services.AddSingleton<IKafkaProducerService, KafkaProducerService>();
builder.Services.AddHttpContextAccessor();



builder.Services.AddDbContext<Chinese_SalesDbContext>(options =>
            options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})

.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = true;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = !string.IsNullOrEmpty(JwtSettings?.Issuer),
        ValidateAudience = !string.IsNullOrEmpty(JwtSettings?.Audience),
        ValidateIssuerSigningKey = true,
        ValidIssuer = JwtSettings?.Issuer,
        ValidAudience = JwtSettings?.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSettings!.SecretKey)),
        ClockSkew = TimeSpan.Zero
    };
});
builder.Services.AddAuthorization(options =>
{

    options.AddPolicy("requireAdmin", policy => policy.RequireRole("admin"));
});

var app = builder.Build();
app.UseMiddleware<RequestLog>();

app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        var exceptionHandlerPathFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        var exception = exceptionHandlerPathFeature?.Error;

        if (exception is GiftAlreadyAssignedException ex)
        {
            context.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict;
            context.Response.ContentType = "application/json";
            var payload = System.Text.Json.JsonSerializer.Serialize(new { winnerName = ex.WinnerName });
            await context.Response.WriteAsync(payload);
        }
        else if (exception != null)
        {
            context.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            var payload = System.Text.Json.JsonSerializer.Serialize(new { error = "An unexpected error occurred." });
            await context.Response.WriteAsync(payload);
        }
    });
});


// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseCors("AllowLocalhost");

app.UseRouting();

app.UseRateLimiter();

app.UseAuthentication();

app.UseHttpsRedirection();

app.UseAuthorization();

app.UseStaticFiles();

app.MapControllers();
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<Chinese_SalesDbContext>(); // תחליף בשם ה-Context שלך
    context.Database.EnsureCreated();
}

app.Run();
public partial class Program { }
