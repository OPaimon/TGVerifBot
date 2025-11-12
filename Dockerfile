ARG DOTNET_VERSION=10.0

# --- Build Stage ---
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /source

COPY --link ["src/TelegramVerificationBot/TelegramVerificationBot.csproj", "./src/TelegramVerificationBot/"]

RUN dotnet restore "src/TelegramVerificationBot/TelegramVerificationBot.csproj" -r linux-x64 /p:PublishReadyToRun=true

COPY --link ["src/TelegramVerificationBot/", "./src/TelegramVerificationBot/"]

WORKDIR "/source/src/TelegramVerificationBot"
RUN dotnet publish "TelegramVerificationBot.csproj" -r linux-x64 --self-contained --no-restore /p:PublishTrimmed=true /p:PublishReadyToRun=true -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}
WORKDIR /app

RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser

COPY --link --from=build /app .

RUN chown -R appuser:appgroup /app

USER appuser
ENTRYPOINT ["./TelegramVerificationBot"]
