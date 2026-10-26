using Microsoft.AspNetCore.DataProtection;
using DhanAPI.Filters;
using DhanService.Configuration;
using DhanService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

// Dhan multi-account futures trading module.
builder.Services.Configure<DhanTradingOptions>(builder.Configuration.GetSection("DhanTrading"));
builder.Services.AddDataProtection()
    .SetApplicationName("DhanAI.DhanTrading")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DhanTrading", "keys")));
builder.Services.AddSingleton<IDhanAccountStore, DhanAccountStore>();
builder.Services.AddSingleton<IDhanTokenStore, DhanTokenStore>();
builder.Services.AddHttpClient<IDhanAuthService, DhanAuthService>();
builder.Services.AddHttpClient<IDhanInstrumentService, DhanInstrumentService>();
builder.Services.AddHttpClient<IDhanOrderService, DhanOrderService>();
builder.Services.AddHttpClient<IDhanPositionService, DhanPositionService>();
builder.Services.AddHttpClient<IDhanMarketQuoteService, DhanMarketQuoteService>();
builder.Services.AddSingleton<ICopyTradingStore, CopyTradingStore>();
builder.Services.AddSingleton<CopyTradingRuntimeStatus>();
builder.Services.AddSingleton<ICopyTradingService, CopyTradingService>();
builder.Services.AddHostedService<DhanOrderUpdateWorker>();
builder.Services.AddScoped<DhanApiKeyGuardFilter>();

// CORS: the DhanClient is a static HTML/JS app served separately (e.g. file:// or a
// simple local static server), so allow any origin to call this API locally.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAllOrigins", p =>
        p.AllowAnyOrigin()
         .AllowAnyMethod()
         .AllowAnyHeader());
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "DhanAPI v1");
    options.RoutePrefix = "docs";
});

app.UseCors("AllowAllOrigins");
app.UseAuthorization();
app.MapControllers();

app.Run();
