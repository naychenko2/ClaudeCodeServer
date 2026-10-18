using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace ClaudeHomeServer.Controllers;

// ADR-030 (2026-10-18), fail-closed: не-админ без непустого claim sub — для него
// медиа-функции «нет» (404, а не «все записи» и не падение). Единственная проверка для
// всех трёх ручек (/history, /history/{id}/file, /stats/media): админ не ограничен,
// не-админ обязан иметь непустой sub. «Непустой» здесь = IsNullOrEmpty (только "");
// пробельный sub (" ") пускаем дальше — там его ловит строже защита строк/индекса
// (IsNullOrWhiteSpace), чтобы whitespace-владелец тоже не расширял выдачу.
internal static class MediaSelfScope
{
    public static bool HasOwner(ClaimsPrincipal user) =>
        user.IsInRole("admin") || !string.IsNullOrEmpty(user.FindFirstValue(JwtRegisteredClaimNames.Sub));
}
