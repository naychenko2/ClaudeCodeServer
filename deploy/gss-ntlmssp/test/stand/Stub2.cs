namespace ClaudeHomeServer.Services { internal static class UserStore { internal static string ResolveUsersFilePath(IConfiguration c) => Path.GetFullPath("users.json"); } }
