FROM mcr.microsoft.com/dotnet/runtime-deps:9.0

RUN useradd --create-home appuser
USER appuser

WORKDIR /app

COPY --chown=appuser:appuser ./publish_output .

ENTRYPOINT ["./TelegramVerificationBot"]