FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY PetCarePro.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/data
ENV DATA_DIR=/app/data
EXPOSE 8080
ENTRYPOINT ["dotnet","PetCarePro.dll"]
