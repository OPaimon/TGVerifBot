namespace TelegramVerificationBot.Configuration;
public class TelegramSettings
{
    public required string BotToken { get; set; }
    public required string ApiId { get; set; }
    public required string ApiHash { get; set; }

}

/*
api_id	int	Application identifier (see. App configuration)
api_hash	string	Application identifier hash (see. App configuration)
bot_auth_token	string	Bot token (see bots)
*/