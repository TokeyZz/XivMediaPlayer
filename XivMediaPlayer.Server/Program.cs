using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Bind to all interfaces so external connections work via port forwarding
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Add services to the container.

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Data Source=XivMediaPlayer.db;Cache=Shared;";

builder.Services.AddDbContext<XivMediaPlayer.Server.Models.AppDbContext>(options =>
    options.UseSqlite(connectionString));

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Ensure database is created and apply migrations safely
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<XivMediaPlayer.Server.Models.AppDbContext>();
    
    // Check if the old pre-migrations table exists
    var tables = db.Database.SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' AND name='TvPlacements'").ToList();
    var history = db.Database.SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'").ToList();
    
    if (tables.Any() && !history.Any()) {
        // This is an existing database from before we added EF Core Migrations!
        // We need to fake the InitialCreate migration so data isn't destroyed.
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE ""__EFMigrationsHistory"" (
                ""MigrationId"" TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY,
                ""ProductVersion"" TEXT NOT NULL
            );
            INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
            VALUES ('20260606020813_InitialCreate', '10.0.8');
        ");
    }

    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
