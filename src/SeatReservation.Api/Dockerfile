# Build stage. The SDK image is ~800 MB and has no business in production; only the
# published output crosses into the runtime image below.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, restore, then the rest. Restore is the slow step, and this way it
# is only re-run when a .csproj actually changes rather than on every source edit.
COPY ["src/SeatReservation.Api/SeatReservation.Api.csproj", "src/SeatReservation.Api/"]
COPY ["src/SeatReservation.Application/SeatReservation.Application.csproj", "src/SeatReservation.Application/"]
COPY ["src/SeatReservation.Domain/SeatReservation.Domain.csproj", "src/SeatReservation.Domain/"]
COPY ["src/SeatReservation.Infrastructure/SeatReservation.Infrastructure.csproj", "src/SeatReservation.Infrastructure/"]
RUN dotnet restore "src/SeatReservation.Api/SeatReservation.Api.csproj"

COPY src/ src/
RUN dotnet publish "src/SeatReservation.Api/SeatReservation.Api.csproj" \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# The runtime images are minimal and ship neither curl nor wget, so a compose healthcheck
# written against either silently fails forever — the container reports unhealthy while the
# application is running perfectly well.
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

# Runs as a non-root user. The base image ships one; using it means a process
# compromise does not start with write access to the whole filesystem.
USER $APP_UID

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "SeatReservation.Api.dll"]
