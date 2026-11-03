FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY tools/GraphSimulator/GraphSimulator.csproj tools/GraphSimulator/
RUN dotnet restore tools/GraphSimulator/GraphSimulator.csproj
COPY tools/ tools/
RUN dotnet publish tools/GraphSimulator/GraphSimulator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "GraphSimulator.dll"]
