using Microsoft.EntityFrameworkCore;
using SeatReservation.Application.Abstractions;
using SeatReservation.Domain.Entities;
using SeatReservation.Infrastructure.Persistence;

namespace SeatReservation.Api;

public static class DatabaseInitializer
{
    /// <summary>
    /// Applies pending migrations before the host starts serving.
    ///
    /// Without this a fresh <c>docker compose up</c> produces an API with no tables: the
    /// first request fails, and the outbox dispatcher crash-loops on
    /// <c>relation "outbox_messages" does not exist</c>. Running here rather than from a
    /// background service also means the schema exists before any hosted service touches it.
    ///
    /// Migrating on startup suits a single-instance deployment. Several instances starting
    /// together would race, and a real deployment runs migrations as their own step —
    /// noted rather than solved, because this project deploys as one API container.
    /// </summary>
    public static async Task InitializeAsync(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseInitializer));

        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("{Count} migration uygulaniyor: {Migrations}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync();
        }

        if (app.Environment.IsDevelopment())
            await SeedDemoDataAsync(db, logger);
    }

    /// <summary>
    /// Puts one event with a seat map in the database so a fresh clone has something to
    /// look at. Development only, and skipped entirely once any event exists.
    /// </summary>
    private static async Task SeedDemoDataAsync(IApplicationDbContext db, ILogger logger)
    {
        if (await db.Events.AnyAsync())
            return;

        var @event = Event.Create(
            "Final Maçı", "Atatürk Olimpiyat Stadyumu", DateTimeOffset.UtcNow.AddDays(30));

        @event.AddSeatBlock("A", 1, 20, 1500m);
        @event.AddSeatBlock("B", 1, 20, 1200m);
        @event.AddSeatBlock("C", 1, 30, 750m);

        db.Events.Add(@event);
        await db.SaveChangesAsync();

        logger.LogInformation("Ornek etkinlik olusturuldu: {Name} ({Seats} koltuk).", @event.Name, @event.Seats.Count);
    }
}
