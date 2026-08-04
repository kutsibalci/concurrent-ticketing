using SeatReservation.Application.Services;
using SeatReservation.Infrastructure;
using SeatReservation.Worker;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// Shares the API's infrastructure registration: the same DbContext, the same RabbitMQ
// connection and topology. Declaring topology in one place and consuming it in another is
// how a queue ends up bound differently depending on which process started first.
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddScoped<NotificationHandler>();
builder.Services.AddHostedService<NotificationConsumer>();

var host = builder.Build();
host.Run();
