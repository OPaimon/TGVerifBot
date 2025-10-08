# TGVerifBot

这是一个基于 .NET 构建的 Telegram 验证机器人，用于验证加入群组的新成员。

## 功能

- 通过问答（Quiz）验证新成员。
- 限制用户操作频率以防止滥用。
- 使用 Redis 进行数据存储和速率限制。

## 部署指南

本项目推荐使用 Docker 和 Docker Compose 进行部署。

### 先决条件

- [Docker](https://www.docker.com/get-started)
- [Docker Compose](https://docs.docker.com/compose/install/)

### 使用 Docker Compose 部署

1.  **克隆或下载项目**

2.  **创建配置文件**

    在项目的根目录（与 `docker-compose.yml` 文件同级）创建一个名为 `.env` 的文件。该文件用于存放敏感的配置信息。

3.  **编辑 `.env` 文件**

    在 `.env` 文件中添加以下内容，并将其替换为你的 Telegram Bot 信息：

    ```env
    # 你的 Telegram Bot Token
    TELEGRAM_BOT_TOKEN=123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11
    
    # 从 my.telegram.org 获取的 ApiId
    TELEGRAM_API_ID=12345678
    
    # 从 my.telegram.org 获取的 ApiHash
    TELEGRAM_API_HASH=0123456789abcdef0123456789abcdef
    ```

4.  **启动服务**

    在项目根目录打开终端，运行以下命令来构建和启动容器：

    ```bash
    docker-compose up -d
    ```

    这将会以后台模式启动 Telegram Bot 和 Redis 服务。

5.  **查看日志**

    如果你需要查看机器人的运行日志，可以运行：

    ```bash
    docker-compose logs -f tgverifbot
    ```

6.  **停止服务**

    如果需要停止服务，可以运行：

    ```bash
    docker-compose down
    ```

## 手动构建

如果你不想使用 Docker，也可以按照以下步骤手动构建和运行此应用。

### 先决条件

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (或 `TelegramVerificationBot.csproj` 文件中指定的版本)
- [Redis](https://redis.io/docs/getting-started/installation/) 正在运行并可以访问。

### 配置

1.  **编辑 `appsettings.json` 文件:**
    打开 `appsettings.json` 文件，在 `TelegramSettings` 部分填入你的 Telegram Bot 信息：

    ```json
    "TelegramSettings": {
      "BotToken": "YOUR_BOT_TOKEN_HERE",
      "ApiId": "YOUR_API_ID_HERE",
      "ApiHash": "YOUR_API_HASH_HERE"
    },
    ```

2.  **配置连接字符串:**
    确保 `appsettings.json` 文件中的 `ConnectionStrings.Redis` 指向你的 Redis 实例。默认值为 `localhost:6379`。

    ```json
    "ConnectionStrings": {
      "Redis": "localhost:6379",
      "Sqlite": "Data Source=./data/TelegramVerificationBot.sqlite"
    },
    ```

3.  **更新存储路径:**
    **重要：** SQLite数据库 (`ConnectionStrings.Sqlite`) 和测验文件 (`QuizFilePath`) 的默认路径是为 Docker (`/app/data/...`) 设置的。您**必须**在 `appsettings.json` 中将它们更新为您系统上的有效路径，例如：
    ```json
    "ConnectionStrings": {
      "Redis": "localhost:6379",
      "Sqlite": "Data Source=./data/TelegramVerificationBot.sqlite"
    },
    "QuizFilePath": "./data/quizzes.json"
    ```

### 运行应用

1.  **还原依赖:**
    在项目根目录打开终端，并运行：
    ```bash
    dotnet restore
    ```

2.  **运行项目:**
    ```bash
    dotnet run --project TelegramVerificationBot.csproj
    ```

现在，机器人应该已经成功运行并连接到 Telegram。

## 生产环境部署 (不使用 Docker)

本指南说明了如何在 Linux 服务器上将机器人作为 systemd 服务运行。

### 1. 发布构建

首先，以 `Release` 模式发布应用。此命令会将应用程序及其依赖项编译到一个独立的文件夹中。

```bash
# 这会在项目根目录下创建一个 publish/ 文件夹
dotnet publish -c Release -o ./publish
```

### 2. 部署文件

将 `./publish` 文件夹的内容复制到您服务器上的指定位置，例如 `/opt/tgverifbot`。

```bash
# 确保目标文件夹存在
sudo mkdir -p /opt/tgverifbot

sudo cp -r ./publish/* /opt/tgverifbot/
```

你还需要一同拷贝例如 `quizzes.json` 和 `data` 文件夹这样的数据文件。

```bash
sudo cp ./data/quizzes.json /opt/tgverifbot/data/quizzes.json
```

### 3. 创建环境配置文件

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

### 4. 设置 Systemd 服务

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

### 5. 管理服务

-   **检查状态:**

    ```bash
    sudo systemctl status tgverifbot.service
    ```

-   **实时查看日志:**

    ```bash
    sudo journalctl -u tgverifbot.service -f
    ```

-   **停止服务:**

    ```bash
    sudo systemctl stop tgverifbot.service
    ```

-   **重启服务:**

    ```bash
    sudo systemctl restart tgverifbot.service
    ```
