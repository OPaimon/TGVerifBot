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
        pull_policy: always # 确保每次启动时都拉取最新的镜像
        restart: unless-stopped
        environment:
          - TelegramSettings__BotToken=${TELEGRAM_BOT_TOKEN}
          - TelegramSettings__ApiId=${TELEGRAM_API_ID}
          - TelegramSettings__ApiHash=${TELEGRAM_API_HASH}
          - ConnectionStrings__Redis=redis:6379
        volumes:
          - ./data:/app/data
        depends_on:
          - redis

      redis:
        image: "docker.io/library/redis:alpine"
        restart: unless-stopped
        volumes:
          - ./redis-data:/data
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

### 方案二：部署预构建产物 (不使用 Docker)

此方法适用于在不使用 Docker 的 Linux 服务器上进行部署，我们将直接使用 CI 工作流生成的预编译可执行文件。

#### 1. 下载构建产物

1.  导航到本 GitHub 仓库的 **Actions** 标签页。
2.  从 `main` 分支中选择最新一次成功的工作流运行。
3.  在“Artifacts”区域下载您需要的构建产物。

    **如何选择构建产物:**

    -   **`linux-x64-trimmed-artifact.zip` (为体积优化):**
        这是我们用于构建官方 Docker 镜像的版本。它使用了 .NET 的裁剪 (Trimming) 和 ReadyToRun 技术以获得最小的体积。尽管裁剪功能通常是可靠的，但理论上存在移除边缘场景所需代码的微小风险（尤其是在第三方库中）。这个版本是大多数容器化或无服务器部署场景的理想选择。

    -   **`linux-x64-full-artifact.zip` (为最大化兼容性):**
        这是一个体积较大、未经裁剪的版本。它可作为调试的基准；如果您在使用裁剪版本时遇到任何意外问题，或者您的运维策略要求绝对的兼容性而非更小的部署体积，应选择此版本。

    -   **`framework-dependent-artifact.zip`:**
        需要目标服务器上预先安装 .NET 9.0 运行时。

#### 2. 部署文件

1.  解压下载的 `zip` 文件。
2.  将解压后文件夹中的**内容**复制到您服务器上的指定位置，例如 `/opt/tgverifbot`。

    ```bash
    # 确保目标文件夹存在
    sudo mkdir -p /opt/tgverifbot
    
    # 直接解压到目标位置
    unzip linux-x64-*.zip -d /opt/tgverifbot
    ```
3.  您还需要一同拷贝数据文件，例如 `data/quizzes.json`。
    ```bash
    sudo mkdir -p /opt/tgverifbot/data
    sudo cp ./data/quizzes.json /opt/tgverifbot/data/quizzes.json
    ```

#### 3. 创建环境配置文件

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
ConnectionStrings__Sqlite=Data Source=/opt/tgverifbot/data/TelegramVerificationBot.sqlite
QuizFilePath=/opt/tgverifbot/data/quizzes.json
```

#### 4. 设置 Systemd 服务

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

#### 5. 管理服务

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