# MiniFactory migrator：一次性執行 DbUp 遷移後退出（architecture.md §16、ADR-012）
# 建置 context 為 repo 根目錄：docker build -f src/MiniFactory.Migrator/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只複製專案檔與 props 還原套件，利用 layer cache
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/MiniFactory.Domain/MiniFactory.Domain.csproj src/MiniFactory.Domain/
COPY src/MiniFactory.Application/MiniFactory.Application.csproj src/MiniFactory.Application/
COPY src/MiniFactory.Infrastructure/MiniFactory.Infrastructure.csproj src/MiniFactory.Infrastructure/
COPY src/MiniFactory.Migrator/MiniFactory.Migrator.csproj src/MiniFactory.Migrator/
RUN dotnet restore src/MiniFactory.Migrator/MiniFactory.Migrator.csproj

COPY src/MiniFactory.Domain/ src/MiniFactory.Domain/
COPY src/MiniFactory.Application/ src/MiniFactory.Application/
COPY src/MiniFactory.Infrastructure/ src/MiniFactory.Infrastructure/
COPY src/MiniFactory.Migrator/ src/MiniFactory.Migrator/
RUN dotnet publish src/MiniFactory.Migrator/MiniFactory.Migrator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
# 非 root 執行（.NET 映像內建 APP_UID）
USER $APP_UID
ENTRYPOINT ["dotnet", "MiniFactory.Migrator.dll"]
