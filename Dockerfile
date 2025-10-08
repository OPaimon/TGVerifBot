# 使用官方 .NET SDK 镜像作为构建环境
    FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
    WORKDIR /src

    # 复制 .csproj 文件并还原依赖
    COPY ["TelegramVerificationBot.csproj", "./"]
    RUN dotnet restore "./TelegramVerificationBot.csproj"

    # 复制所有源代码并构建发布
    COPY . .
    WORKDIR "/src/."
    RUN dotnet build "TelegramVerificationBot.csproj" -c Release -o /app/build

    FROM build AS publish
    RUN dotnet publish "TelegramVerificationBot.csproj" -c Release -o /app/publish /p:UseAppHost=false

    # 使用官方 .NET 运行时镜像作为最终环境
    FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
    WORKDIR /app
    COPY --from=publish /app/publish .
    ENTRYPOINT ["dotnet", "TelegramVerificationBot.dll"]
    