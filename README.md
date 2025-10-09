# TGVerifBot

This is a Telegram verification bot built with .NET, designed to verify new members joining a group.

## Features

- Verifies new members through a quiz.
- Rate limits user actions to prevent abuse.
- Uses Redis for data storage and rate limiting.

## Deployment Guide

### Option 1: Deploying with Docker Compose (Recommended)

This is the easiest way to deploy the bot, as it does not require cloning the source code.

1.  **Create a Project Directory**

    Create a directory on your server where you will store the configuration and data.
    ```bash
    mkdir tgverifbot
    cd tgverifbot
    ```

2.  **Create a `docker-compose.yml` File**

    Create a file named `docker-compose.yml` with the following content:
    ```yaml
    version: '3.8'

    services:
      tgverifbot:
        image: ghcr.io/opaimon/tgverifbot:main
        pull_policy: always # Ensures you get the latest image every time you start
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

3.  **Create a `.env` Configuration File**

    In the same directory, create a file named `.env` to store your secrets:

    ```env
    # Your Telegram Bot Token
    TELEGRAM_BOT_TOKEN=123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11
    
    # Your ApiId from my.telegram.org
    TELEGRAM_API_ID=12345678
    
    # Your ApiHash from my.telegram.org
    TELEGRAM_API_HASH=0123456789abcdef0123456789abcdef
    ```

4.  **Start the Services**

    Run the following command to download the images and start the containers:
    ```bash
    docker-compose up -d
    ```

#### Managing the Service
-   **View logs:** `docker-compose logs -f tgverifbot`
-   **Stop the services:** `docker-compose down`
-   **Update the bot:** `docker-compose pull` followed by `docker-compose up -d` (if you choose not to use `pull_policy`).

---

### Option 2: Deploying Pre-built Artifacts (without Docker)

This method is for deploying on a Linux server without Docker, using pre-compiled executables from our CI workflow.

#### 1. Download an Artifact

1.  Navigate to the **Actions** tab of this GitHub repository.
2.  Select the latest successful workflow run from the `main` branch.
3.  Download an artifact from the "Artifacts" section.

    **Choosing an Artifact:**

    -   **`linux-x64-trimmed-artifact.zip` (Optimized for Size):**
        This is the version we use to build the official Docker image. It is optimized for a small footprint using .NET's trimming and ReadyToRun features. While trimming is generally reliable, it can theoretically remove necessary code in edge cases, especially with third-party dependencies. This version is ideal for most containerized or serverless deployments.

    -   **`linux-x64-full-artifact.zip` (Maximum Compatibility):**
        This is a larger, non-trimmed version. It provides a baseline for debugging and should be used if you encounter any unexpected issues with the trimmed version, or if your operational policy prioritizes guaranteed compatibility over deployment size.

    -   **`framework-dependent-artifact.zip`:**
        Requires the .NET 9.0 runtime to be installed on the server.

#### 2. Deploy Files

1.  Unzip the downloaded artifact.
2.  Copy the contents of the unzipped directory to a location on your server, for example, `/opt/tgverifbot`.

    ```bash
    # Make sure the destination directory exists
    sudo mkdir -p /opt/tgverifbot
    
    # Unzip directly to the target location
    unzip linux-x64-*.zip -d /opt/tgverifbot
    ```
3.  You also need to copy over your data files, for example `data/quizzes.json`.
    ```bash
    sudo mkdir -p /opt/tgverifbot/data
    sudo cp ./data/quizzes.json /opt/tgverifbot/data/quizzes.json
    ```

#### 3. Create Environment Configuration

The systemd service will load configuration from an environment file. Create a directory and a file for it:

```bash
sudo mkdir -p /etc/tgverifbot
sudo nano /etc/tgverifbot/config.env
```

Add your configuration to this file. Note that the variable names match the structure in `appsettings.json` but use a double underscore `__` as a separator.

```ini
TelegramSettings__BotToken=YOUR_BOT_TOKEN_HERE
TelegramSettings__ApiId=YOUR_API_ID_HERE
TelegramSettings__ApiHash=YOUR_API_HASH_HERE
ConnectionStrings__Redis=localhost:6379

# Important: Update these paths to match your deployment location
ConnectionStrings__Sqlite=Data Source=/opt/tgverifbot/data/TelegramVerificationBot.sqlite
QuizFilePath=/opt/tgverifbot/data/quizzes.json
```

#### 4. Setup Systemd Service

1.  Copy the example `tgverifbot.service` file (included in this repository) to the systemd directory:

    ```bash
    sudo cp ./tgverifbot.service /etc/systemd/system/tgverifbot.service
    ```

2.  Reload the systemd daemon to recognize the new service:

    ```bash
    sudo systemctl daemon-reload
    ```

3.  Enable the service to start on boot and start it now:

    ```bash
    sudo systemctl enable --now tgverifbot.service
    ```

#### 5. Manage the Service

-   **Check the status:** `sudo systemctl status tgverifbot.service`
-   **View logs in real-time:** `sudo journalctl -u tgverifbot.service -f`
-   **Stop the service:** `sudo systemctl stop tgverifbot.service`
-   **Restart the service:** `sudo systemctl restart tgverifbot.service`

---

## For Developers: Building from Source

This section is for those who wish to contribute or run the application in a local development environment.

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Redis](https://redis.io/docs/getting-started/installation/) running on a reachable host.

### Configuration

1.  **Use User Secrets:** For local development, it's best to use .NET's User Secrets to store your API keys. Initialize it by running this in the project root:
    ```bash
    dotnet user-secrets init
    ```
2.  **Set your secrets:**
    ```bash
    dotnet user-secrets set "TelegramSettings:BotToken" "YOUR_BOT_TOKEN_HERE"
    dotnet user-secrets set "TelegramSettings:ApiId" "YOUR_API_ID_HERE"
    dotnet user-secrets set "TelegramSettings:ApiHash" "YOUR_API_HASH_HERE"
    ```
3.  **Ensure `appsettings.Development.json` is configured** for local paths and your Redis instance (default is `localhost:6379`).

### Running the Application

1.  **Restore Dependencies:** `dotnet restore`
2.  **Run the project:** `dotnet run`