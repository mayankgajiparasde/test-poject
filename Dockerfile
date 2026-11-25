# One image for both roles: Role=api or Role=worker (see compose.yaml).
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /source
COPY src/ src/
RUN dotnet publish src/HotelUnderwriting.Api/HotelUnderwriting.Api.csproj -c Release -f net10.0 -o /app -warnaserror -p:NuGetAudit=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "HotelUnderwriting.Api.dll"]
