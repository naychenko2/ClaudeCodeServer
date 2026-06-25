namespace ClaudeHomeServer.Models;

public class User
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "user";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    // NT-хэш для NTLM WebDAV: MD4(UTF-16LE(password)); null — если пользователь ещё не логинился после обновления
    public byte[]? NtHash { get; set; }
}
