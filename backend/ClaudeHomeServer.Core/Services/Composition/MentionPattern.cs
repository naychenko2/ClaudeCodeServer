using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Composition;

// @handle-паттерн для MentionTriggerSource: regex вынесен в Core — TriggerSources
// отдельная сборка, не видит GroupChatRouter (Main).
// internal в Main переиспользуется GroupChatRouter (тот же паттерн).
public static class MentionPattern
{
    // @handle по границе слова: не срабатывает внутри email (a@b) и на «слипшихся» токенах.
    public static readonly Regex Regex =
        new(@"(?<![\p{L}\p{N}_@-])@([\p{L}\p{N}_-]+)", RegexOptions.Compiled);
}
