# TGVerifBot

这是一个基于 .NET 构建的 Telegram 验证机器人，用于验证加入群组的新成员。

## 功能

- 通过问答（Quiz）验证新成员。
- 限制用户操作频率以防止滥用。
- 使用 Redis 进行数据存储和速率限制。

## 部署指南

### 方案一：使用 Docker Compose 部署 (推荐)

这是部署此机器人最简单的方式，因为它**不需要**克隆本项目的源代码。

1.  **创建项目目录**

    在您的服务器上创建一个用于存放配置和数据的文件夹。
    ```bash
    mkdir tgverifbot
    cd tgverifbot
    ```

2.  **创建 `docker-compose.yml` 文件**

    创建一个名为 `docker-compose.yml` 的文件，并填入以下内容：
    ```yaml
    version: '3.8'

    services:
      tgverifbot:
        image: ghcr.io/opaimon/tgverifbot:main
        restart: unless-stopped
        user: "${UID}:${GID}"
        environment:
          - TelegramSettings__BotToken=${TELEGRAM_BOT_TOKEN}
          - TelegramSettings__ApiId=${TELEGRAM_API_ID}
          - TelegramSettings__ApiHash=${TELEGRAM_API_HASH}
          - ConnectionStrings__Redis=redis:6379
        volumes:
          - ./data:/app/data:z
        depends_on:
          - redis

      redis:
        image: "docker.io/library/redis:alpine"
        restart: unless-stopped
        volumes:
          - ./redis-data:/data:z
    ```

3.  **创建 `.env` 配置文件**

    在同一个目录下，创建一个名为 `.env` 的文件，用于存放您的密钥：

    ```env
    # 你的 Telegram Bot Token
    TELEGRAM_BOT_TOKEN=123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11
    
    # 从 my.telegram.org 获取的 ApiId
    TELEGRAM_API_ID=12345678
    
    # 从 my.telegram.org 获取的 ApiHash
    TELEGRAM_API_HASH=0123456789abcdef0123456789abcdef
    ```

4.  **启动服务**

    运行以下命令来下载镜像并启动容器：
    ```bash
    docker-compose up -d
    ```

#### 管理服务
-   **查看日志:** `docker-compose logs -f tgverifbot`
-   **停止服务:** `docker-compose down`
-   **更新机器人:** `docker-compose pull` 然后 `docker-compose up -d` (如果您没有使用 `pull_policy`)。

---

### 方案二：从源码构建和部署 (不使用 Docker)

此方法适用于在不使用 Docker 的 Linux 服务器上进行部署。

#### 1. 克隆仓库并安装 .NET SDK

首先，克隆本仓库到您的服务器上，并确保您已经安装了 [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)。

```bash
git clone https://github.com/opaimon/tgverifbot.git
cd tgverifbot
```

#### 2. 从源码构建

您可以根据需求选择不同的构建方式。构建产物将位于 `publish` 目录中。

-   **为体积优化 (推荐):**
    这将生成一个经过裁剪和 ReadyToRun 优化的独立可执行文件，体积最小。
    ```bash
    dotnet publish TelegramVerificationBot.csproj -r linux-x64 --self-contained /p:PublishTrimmed=true /p:PublishReadyToRun=true -o ./publish
    ```

-   **为最大化兼容性:**
    这将生成一个未经裁剪的独立可执行文件，体积较大，但兼容性最好。
    ```bash
    dotnet publish TelegramVerificationBot.csproj -r linux-x64 --self-contained /p:PublishReadyToRun=true -o ./publish
    ```

-   **框架依赖型:**
    这将生成一个依赖于服务器上已安装的 .NET 9.0 运行时的版本。
    ```bash
    dotnet publish -o ./publish
    ```

#### 3. 部署文件

1.  将构建好的文件复制到您服务器上的指定位置，例如 `/opt/tgverifbot`。

    ```bash
    # 确保目标文件夹存在
    sudo mkdir -p /opt/tgverifbot
    
    # 复制构建产物
    sudo cp -r ./publish/* /opt/tgverifbot/
    ```
2.  您还需要一同拷贝数据文件。
    ```bash
    sudo mkdir -p /opt/tgverifbot/data
    sudo cp ./data/quizzes.json /opt/tgverifbot/data/quizzes.json
    ```

#### 4. 创建环境配置文件

systemd 服务将从一个环境文件中加载配置。为其创建一个目录和文件：

```bash
sudo mkdir -p /etc/tgverifbot
sudo nano /etc/tgverifbot/config.env
```

将您的配置添加到此文件中。请注意，变量名与 `appsettings.json` 中的结构相匹配，但使用双下划线 `__` 作为分隔符。

```ini
TelegramSettings__BotToken=YOUR_BOT_TOKEN_HERE
TelegramSettings__ApiId=YOUR_API_ID_HERE
TelegramSettings__ApiHash=YOUR_API_HASH_HERE
ConnectionStrings__Redis=localhost:6379

# 重要：请将这些路径更新为您的实际部署路径
QuizFilePath=/opt/tgverifbot/data/quizzes.json
```

#### 5. 设置 Systemd 服务

1.  将项目中的 `tgverifbot.service` 示例文件复制到 systemd 目录：

    ```bash
    sudo cp ./tgverifbot.service /etc/systemd/system/tgverifbot.service
    ```

2.  重新加载 systemd 守护进程以识别新服务：

    ```bash
    sudo systemctl daemon-reload
    ```

3.  设置服务开机自启，并立即启动它：

    ```bash
    sudo systemctl enable --now tgverifbot.service
    ```

#### 6. 管理服务

-   **检查状态:** `sudo systemctl status tgverifbot.service`
-   **实时查看日志:** `sudo journalctl -u tgverifbot.service -f`
-   **停止服务:** `sudo systemctl stop tgverifbot.service`
-   **重启服务:** `sudo systemctl restart tgverifbot.service`

---

## 面向开发者：从源码构建

此部分面向希望为项目贡献代码，或在本地开发环境中运行应用的开发者。

### 先决条件

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Redis](https://redis.io/docs/getting-started/installation/) 正在运行并可以访问。

### 配置

1.  **使用用户机密:** 对于本地开发，最好使用 .NET 的用户机密功能来存储您的 API 密钥。在项目根目录运行此命令来初始化：
    ```bash
    dotnet user-secrets init
    ```
2.  **设置你的机密:**
    ```bash
    dotnet user-secrets set "TelegramSettings:BotToken" "YOUR_BOT_TOKEN_HERE"
    dotnet user-secrets set "TelegramSettings:ApiId" "YOUR_API_ID_HERE"
    dotnet user-secrets set "TelegramSettings:ApiHash" "YOUR_API_HASH_HERE"
    ```
3.  **确保 `appsettings.Development.json` 已配置**为使用本地路径和您的 Redis 实例（默认为 `localhost:6379`）。

### 运行应用

1.  **还原依赖:** `dotnet restore`
2.  **运行项目:** `dotnet run`