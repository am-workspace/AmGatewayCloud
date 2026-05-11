FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/AmGatewayCloud.Shared/AmGatewayCloud.Shared.csproj ./AmGatewayCloud.Shared/
RUN dotnet restore ./AmGatewayCloud.Shared/AmGatewayCloud.Shared.csproj
COPY src/AmGatewayCloud.WorkOrderService/AmGatewayCloud.WorkOrderService.csproj ./AmGatewayCloud.WorkOrderService/
RUN dotnet restore ./AmGatewayCloud.WorkOrderService/AmGatewayCloud.WorkOrderService.csproj
COPY src/AmGatewayCloud.Shared/ ./AmGatewayCloud.Shared/
COPY src/AmGatewayCloud.WorkOrderService/ ./AmGatewayCloud.WorkOrderService/
RUN dotnet publish ./AmGatewayCloud.WorkOrderService/AmGatewayCloud.WorkOrderService.csproj \
    -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5002
ENTRYPOINT ["dotnet", "AmGatewayCloud.WorkOrderService.dll"]
