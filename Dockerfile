FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/VoiceBot/VoiceBot.csproj src/VoiceBot/
RUN dotnet restore src/VoiceBot/VoiceBot.csproj
COPY src/ src/
RUN dotnet publish src/VoiceBot/VoiceBot.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data \
    && chown app:app /data
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "VoiceBot.dll"]
