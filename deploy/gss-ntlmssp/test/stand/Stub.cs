namespace ClaudeHomeServer.WebDav;
internal static class WebDavHandler { internal static string BuildAuthChallenge(bool ntlmAvailable) => "Basic realm=\"x\""; internal static bool OfferNegotiate(HttpContext c) => true; }
