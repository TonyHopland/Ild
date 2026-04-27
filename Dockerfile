FROM node:22-alpine AS frontend-build
WORKDIR /app/frontend
COPY frontend/package.json frontend/package-lock.json* ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ILD.sln ./
COPY ILD.Core/ILD.Core.csproj ILD.Core/
COPY ILD.Api/ILD.Api.csproj ILD.Api/
RUN dotnet restore
COPY . .
WORKDIR /src/ILD.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish ./
COPY --from=frontend-build /app/frontend/dist ./wwwroot
ENV ILD_DATA_PATH=/data
ENV ILD_WORKTREES_PATH=/worktrees
RUN mkdir -p /data /worktrees
EXPOSE 8080
ENTRYPOINT ["dotnet", "ILD.Api.dll"]
