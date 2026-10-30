using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

string Env(string key, string fallback) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

var connString = new NpgsqlConnectionStringBuilder
{
    Host = Env("DB_HOST", "postgres"),
    Port = int.Parse(Env("DB_PORT", "5432")),
    Database = Env("DB_NAME", "worldclock"),
    Username = Env("DB_USER", "postgres"),
    Password = Env("DB_PASSWORD", "postgres"),
    Pooling = true
}.ConnectionString;

async Task<NpgsqlConnection> OpenAsync()
{
    for (var attempt = 0; attempt < 20; attempt++)
    {
        try
        {
            var c = new NpgsqlConnection(connString);
            await c.OpenAsync();
            return c;
        }
        catch when (attempt < 19)
        {
            await Task.Delay(2000);
        }
    }
    var fail = new NpgsqlConnection(connString);
    await fail.OpenAsync();
    return fail;
}

string CurrentTime(string tz)
{
    try
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(tz);
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone)
            .ToString("yyyy-MM-dd HH:mm:ss");
    }
    catch
    {
        return "unknown";
    }
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "dotnet" }));

app.MapGet("/api/clocks", async () =>
{
    await using var conn = await OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT id, country, timezone, created_at FROM world_clocks ORDER BY id", conn);
    await using var reader = await cmd.ExecuteReaderAsync();

    var list = new List<object>();
    while (await reader.ReadAsync())
    {
        var tz = reader.GetString(2);
        list.Add(new
        {
            id = reader.GetInt32(0),
            country = reader.GetString(1),
            timezone = tz,
            created_at = reader.GetDateTime(3),
            current_time = CurrentTime(tz)
        });
    }
    return Results.Ok(new { service = "dotnet", data = list });
});

app.MapPost("/api/clocks", async (ClockInput input) =>
{
    try
    {
        TimeZoneInfo.FindSystemTimeZoneById(input.Timezone);
    }
    catch
    {
        return Results.BadRequest(new { error = "invalid timezone" });
    }

    await using var conn = await OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "INSERT INTO world_clocks (country, timezone) VALUES (@c, @t) " +
        "RETURNING id, country, timezone, created_at", conn);
    cmd.Parameters.AddWithValue("c", input.Country);
    cmd.Parameters.AddWithValue("t", input.Timezone);

    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    var result = new
    {
        id = reader.GetInt32(0),
        country = reader.GetString(1),
        timezone = reader.GetString(2),
        created_at = reader.GetDateTime(3),
        current_time = CurrentTime(input.Timezone)
    };
    return Results.Created("/api/clocks", new { service = "dotnet", data = result });
});

app.Run();

record ClockInput(string Country, string Timezone);
