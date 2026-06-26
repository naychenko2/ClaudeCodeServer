namespace ClaudeHomeServer.Models;

public class DifyOptions
{
    public const string Section = "Dify";
    public string ApiUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string IndexingTechnique { get; set; } = "high_quality";
}
