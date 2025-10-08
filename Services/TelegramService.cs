using Microsoft.Extensions.Options;
using Telegram.Bot.Extensions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramVerificationBot.Dispatcher;
using TelegramVerificationBot.Models;
using TelegramVerificationBot.Tasks;
using TL;
using WTelegram;

namespace TelegramVerificationBot;

public class TelegramService : IDisposable
{
    // private Client _client;
    private Bot? _bot;
    private Microsoft.Data.Sqlite.SqliteConnection? _dbConnection;
    private readonly FunctionalTaskDispatcher _dispatcher;
    private readonly ILogger<TelegramService> _logger;
    private readonly IOptions<TelegramSettings> _settings;
    private readonly IConfiguration _configuration;

    public TelegramService(
        ILogger<TelegramService> logger,
        FunctionalTaskDispatcher dispatcher,
        IOptions<TelegramSettings> settings,
        IConfiguration configuration)
    {
        _logger = logger;
        _dispatcher = dispatcher;
        _settings = settings; // 保存配置
        _configuration = configuration;
    }
    private void InitializeBot()
    {
        if (_bot != null) return; // 防止重复 init

        var sqliteConnectionString = _configuration.GetConnectionString("Sqlite");
        _dbConnection = new Microsoft.Data.Sqlite.SqliteConnection(sqliteConnectionString);
        _dbConnection.Open();
        _bot = new Bot(botToken: _settings.Value.BotToken, apiId: int.Parse(_settings.Value.ApiId), apiHash: _settings.Value.ApiHash, dbConnection: _dbConnection);
        _bot.OnMessage += OnMessage;
        _bot.OnError += async (e, s) =>
        {
            _logger.LogError(e, "WTelegram error: {State}", s);
            await Task.CompletedTask;
        };
        // Console.Error.WriteLineAsync(e.ToString());
        _bot.OnUpdate += OnUpdate;
    }

    private async Task OnUpdate(WTelegram.Types.Update update)
    {
        if (update.ChatJoinRequest is not null)
        {
            _logger.LogInformation("New chat join request: {Info}", update.ChatJoinRequest);
            await _dispatcher.DispatchAsync(new StartVerificationJob(update.ChatJoinRequest));
            return;
        }
        else if (update.CallbackQuery is not null)
        {
            _logger.LogInformation("New callback query: {Info}", update.CallbackQuery.Data);
            await _dispatcher.DispatchAsync(new ProcessQuizCallbackJob(update.CallbackQuery.Data, update.CallbackQuery.From, update.CallbackQuery.Message));
            // if (data.StartsWith("verify_"))
            // {
            //     string[] parts = data.Substring("verify_".Length).Split('_');
            //     if (parts is [var user, var chat, var quizId, var indexStr]
            //         && int.TryParse(indexStr, out var index)
            //         && int.TryParse(quizId, out var quizIdInt)
            //         && long.TryParse(user, out var userId)
            //         && long.TryParse(chat, out var chatId))
            //     {
            //         var _user = update.CallbackQuery.From;
            //         await _dispatcher.DispatchAsync(new ProcessQuizCallbackJob(userId, chatId, quizIdInt, index, _user, message));
            //     }
            // }

            return;
        }
    }

    private async Task OnMessage(WTelegram.Types.Message msg, UpdateType type)
    {
        switch (msg)
        {
            case WTelegram.Types.Message { Text: "Ping" } when msg.Type == MessageType.Text:
                _logger.LogInformation("Received 'Ping' from chat {ChatId}", msg.Chat.Id);
                // 这里可以处理 Ping 消息
                var pingJob = new RespondToPingJob(msg.Chat);
                await _dispatcher.DispatchAsync(pingJob);
                break;
            default:
                _logger.LogInformation("Received message of type {MessageType} from chat {ChatId}", msg.Type, msg.Chat.Id);
                break;
        }
    }

    public void Dispose()
    {
        _bot?.Dispose();
        _dbConnection?.Dispose();
    }

    internal async Task ConnectAndListenAsync(CancellationToken stoppingToken)
    {
        InitializeBot();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var me = await _bot.GetMe();
                _logger.LogInformation("Logged in as {User} (id {Id})", me.FirstName, me.Id);

                await Task.Delay(-1, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            // catch (Exception ex)
            // {
            //     _logger.LogError(ex, "An error occurred: {Message}", ex.Message);
            //     _logger.LogInformation("Reconnecting in 10 seconds...");
            //     await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            // }
        }

        _logger.LogInformation("Disconnecting from Telegram...");
    }


    public async Task RespondToPingAsync(RespondToPingJob job)
    {
        var chat = job.Chat;
        _logger.LogInformation("Responding to Ping from chat {ChatId}", chat.Id);
        await _bot.SendMessage(chat, "Pong");
    }

    public async Task SendQuizAsync(SendQuizJob job)
    {
        var chat = job.Chat;
        var question = job.Question;
        var user = job.User;
        var userChat = job.UserChatId;
        var optionsWithTokens = job.OptionsWithTokens;
        _logger.LogInformation("Sending quiz to chat {ChatId}: {QuizQuestion}", chat.Id, question);

        var buttons = optionsWithTokens
            .Select((item, index) => new InlineKeyboardButton(item.Option)
            {
                CallbackData = chat.Id.ToString()+ "_" + item.Token
            })
            .ToArray();

        var inlineKeyboard = new InlineKeyboardMarkup(buttons);
        var chatTitle = string.IsNullOrEmpty(chat.Title) ? chat.Id.ToString() : chat.Title;
        var replyMessage = $"*欢迎 {MentionMarkdownV2(user.FirstName, user.Id)} 来到 {chatTitle} ！* \n问题: {question}";

        await _bot.SendMessage(userChat, replyMessage, parseMode: ParseMode.MarkdownV2, replyMarkup: inlineKeyboard);
    }

    public async Task HandleChatJoinRequestAsync(ChatJoinRequestJob job)
    {
        var chat = await _bot.GetChat(job.Chat);
        var user = job.User;
        var result = await _bot.Client.Messages_HideChatJoinRequest(chat, user.TLUser(), job.Approve);
        _logger.LogInformation("Handled chat join request for user {UserId} in chat {ChatId}, approved: {Approve}", user.Id, chat.Id, job.Approve);
    }

    public async Task HandleEditMessageAsync(EditMessageJob job)
    {
        var message = job.Message;
        var chat = message.Chat;
        var messageId = message.MessageId;
        var newText = job.NewText;
        var result = await _bot.EditMessageText(chat, messageId, newText);
        _logger.LogInformation("Edited message {MessageId} in chat {ChatId}", messageId, chat.Id);
    }

    /*
    let mentionMarkdownV2 (username: string, userid: int64) =
        sprintf "[%s](tg://user?id=%d)" username userid
    */
    public static String MentionMarkdownV2(string username, long userid) =>
        $"[{username}](tg://user?id={userid})";
}