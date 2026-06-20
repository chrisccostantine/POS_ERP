FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props Scalora.sln ./
COPY src/Scalora.Domain/Scalora.Domain.csproj src/Scalora.Domain/
COPY src/Scalora.Application/Scalora.Application.csproj src/Scalora.Application/
COPY src/Scalora.Infrastructure/Scalora.Infrastructure.csproj src/Scalora.Infrastructure/
COPY src/Scalora.Api/Scalora.Api.csproj src/Scalora.Api/
RUN dotnet restore src/Scalora.Api/Scalora.Api.csproj

COPY src/ src/
RUN dotnet publish src/Scalora.Api/Scalora.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "Scalora.Api.dll"]
