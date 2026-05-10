FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先复制 Shared 项目（依赖项）
COPY src/AmGatewayCloud.Shared/AmGatewayCloud.Shared.csproj ./AmGatewayCloud.Shared/
RUN dotnet restore ./AmGatewayCloud.Shared/AmGatewayCloud.Shared.csproj

# 复制 AlarmService 项目
COPY src/AmGatewayCloud.AlarmService/AmGatewayCloud.AlarmService.csproj ./AmGatewayCloud.AlarmService/
RUN dotnet restore ./AmGatewayCloud.AlarmService/AmGatewayCloud.AlarmService.csproj

# 复制源码并构建
COPY src/AmGatewayCloud.Shared/ ./AmGatewayCloud.Shared/
COPY src/AmGatewayCloud.AlarmService/ ./AmGatewayCloud.AlarmService/
RUN dotnet publish ./AmGatewayCloud.AlarmService/AmGatewayCloud.AlarmService.csproj \
    -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5001
ENTRYPOINT ["dotnet", "AmGatewayCloud.AlarmService.dll"]
