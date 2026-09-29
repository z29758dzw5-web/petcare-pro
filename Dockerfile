FROM node:20-alpine AS frontend
RUN apk add --no-cache unzip
WORKDIR /src
COPY petcare-source.zip /tmp/petcare-source.zip
RUN unzip -q /tmp/petcare-source.zip -d /src
WORKDIR /src/petcare_slim/ClientApp
RUN npm ci
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend
WORKDIR /src/petcare_slim
COPY --from=frontend /src/petcare_slim ./
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=backend /app/publish ./
RUN mkdir -p /app/data
ENV USE_SQLITE=true
ENV SQLITE_PATH=/app/data/petshop.db
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "ConsoleApp24.dll"]
