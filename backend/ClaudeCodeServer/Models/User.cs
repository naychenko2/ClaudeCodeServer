namespace ClaudeCodeServer.Models;

public class User
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "user";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
