# TGVerifBot

This is a Telegram verification bot built with .NET, designed to verify new members joining a group.

## Features

- Verifies new members through a quiz.
- Rate limits user actions to prevent abuse.
- Uses Redis for data storage and rate limiting.

## Deployment Guide

The recommended way to deploy this project is by using Docker and Docker Compose.

### Prerequisites

- [Docker](https://www.docker.com/get-started)
- [Docker Compose](https://docs.docker.com/compose/install/)

### Deploying with Docker Compose

1.  **Clone or download the project**

2.  **Create a configuration file**

    In the project's root directory (the same directory as `docker-compose.yml`), create a file named `.env`. This file will store your sensitive configuration information.

3.  **Edit the `.env` file**

    Add the following content to the `.env` file and replace the values with your Telegram Bot information:

    ```env
    # Your Telegram Bot Token
    TELEGRAM_BOT_TOKEN=123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11
    
    # Your ApiId from my.telegram.org
    TELEGRAM_API_ID=12345678
    
    # Your ApiHash from my.telegram.org
    TELEGRAM_API_HASH=0123456789abcdef0123456789abcdef
    ```

4.  **Start the services**

    Open a terminal in the project root directory and run the following command to build and start the containers:

    ```bash
    docker-compose up -d
    ```

    This will start the Telegram Bot and Redis services in detached mode.

5.  **View logs**

    If you need to check the bot's running logs, you can run:

    ```bash
    docker-compose logs -f tgverifbot
    ```

6.  **Stop the services**

    To stop the services, you can run:

    ```bash
    docker-compose down
    ```

## Manual Build

If you prefer to build and run the application without Docker, you can follow these steps.

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (or the version specified in `TelegramVerificationBot.csproj`)
- [Redis](https://redis.io/docs/getting-started/installation/) running on a reachable host.

### Configuration

1.  **Edit `appsettings.json`:**
    Open the `appsettings.json` file and fill in your Telegram bot details under `TelegramSettings`:

    ```json
    "TelegramSettings": {
      "BotToken": "YOUR_BOT_TOKEN_HERE",
      "ApiId": "YOUR_API_ID_HERE",
      "ApiHash": "YOUR_API_HASH_HERE"
    },
    ```

2.  **Configure Connection Strings:**
    Ensure the `ConnectionStrings.Redis` value in `appsettings.json` points to your Redis instance. The default is `localhost:6379`.

    ```json
    "ConnectionStrings": {
      "Redis": "localhost:6379",
      "Sqlite": "Data Source=./data/TelegramVerificationBot.sqlite"
    },
    ```

3.  **Update Storage Paths:**
    **Important:** The default paths for the SQLite database (`ConnectionStrings.Sqlite`) and quiz file (`QuizFilePath`) are set for Docker (`/app/data/...`). You **must** update these in `appsettings.json` to a valid path on your system, for example:
    ```json
    "ConnectionStrings": {
      "Redis": "localhost:6379",
      "Sqlite": "Data Source=./data/TelegramVerificationBot.sqlite"
    },
    "QuizFilePath": "./data/quizzes.json"
    ```

### Running the Application

1.  **Restore Dependencies:**
    Open a terminal in the project root and run:
    ```bash
    dotnet restore
    ```

2.  **Run the project:**
    ```bash
    dotnet run --project TelegramVerificationBot.csproj
    ```

The bot should now be running and connected to Telegram.

## Production Deployment (without Docker)

This guide explains how to run the bot as a systemd service on a Linux server.

### 1. Release Build

First, publish the application in `Release` mode. This command compiles the application and its dependencies into a self-contained directory.

```bash
# This will create a publish/ directory in the project root
dotnet publish -c Release -o ./publish
```

### 2. Deploy Files

Copy the contents of the `./publish` directory to a location on your server, for example, `/opt/tgverifbot`.

```bash
# Make sure the destination directory exists
sudo mkdir -p /opt/tgverifbot

sudo cp -r ./publish/* /opt/tgverifbot/
```

You also need to copy over any data files, like your quizzes.json and the data directory.

```bash
sudo cp ./data/quizzes.json /opt/tgverifbot/data/quizzes.json
```

### 3. Create Environment Configuration

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

### 4. Setup Systemd Service

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

### 5. Manage the Service

-   **Check the status:**

    ```bash
    sudo systemctl status tgverifbot.service
    ```

-   **View logs in real-time:**

    ```bash
    sudo journalctl -u tgverifbot.service -f
    ```

-   **Stop the service:**

    ```bash
    sudo systemctl stop tgverifbot.service
    ```

-   **Restart the service:**

    ```bash
    sudo systemctl restart tgverifbot.service
    ```
