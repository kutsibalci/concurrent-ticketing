FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/SeatReservation.Worker/SeatReservation.Worker.csproj", "src/SeatReservation.Worker/"]
COPY ["src/SeatReservation.Application/SeatReservation.Application.csproj", "src/SeatReservation.Application/"]
COPY ["src/SeatReservation.Domain/SeatReservation.Domain.csproj", "src/SeatReservation.Domain/"]
COPY ["src/SeatReservation.Infrastructure/SeatReservation.Infrastructure.csproj", "src/SeatReservation.Infrastructure/"]
RUN dotnet restore "src/SeatReservation.Worker/SeatReservation.Worker.csproj"

COPY src/ src/
RUN dotnet publish "src/SeatReservation.Worker/SeatReservation.Worker.csproj" \
    -c Release -o /app/publish /p:UseAppHost=false

# The runtime image, not the ASP.NET one: this process has no web server. Keeping it that
# way means no ASP.NET package may be referenced from here or from anything it depends on —
# Serilog.AspNetCore instead of Serilog.Extensions.Hosting was enough to break it.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Npgsql probes for GSSAPI at startup. Without the library it writes a two-line error to
# stdout and then carries on with password authentication — harmless, but an error-level
# line that is not an error is how people learn to ignore logs.
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*

USER $APP_UID

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "SeatReservation.Worker.dll"]
