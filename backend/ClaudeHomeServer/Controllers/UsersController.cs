using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Roles = "admin")]
public class UsersController(UserStore users, SessionManager sessions,
    UserKnowledgeCascade knowledgeCascade, ILogger<UsersController> logger) : ControllerBase
{
    private static readonly Regex UsernameRegex = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    [HttpGet]
    public IActionResult GetAll()
    {
        var dtos = users.GetAll().Select(ToDto);
        return Ok(dtos);
    }

    [HttpPost]
    public IActionResult Create([FromBody] CreateUserRequest req)
    {
        var validationError = ValidateUsername(req.Username)
                           ?? ValidatePassword(req.Password)
                           ?? ValidateRole(req.Role)
                           ?? ValidateExecutionEnvironment(req.ExecutionEnvironment);
        if (validationError is not null) return BadRequest(new { error = validationError });

        try
        {
            var user = users.Add(req.Username, req.Password, req.Role,
                req.ExecutionEnvironment ?? ExecutionEnvironments.Local);
            return CreatedAtAction(nameof(GetAll), ToDto(user));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] UpdateUserRequest req)
    {
        if (req.Username is not null)
        {
            var err = ValidateUsername(req.Username);
            if (err is not null) return BadRequest(new { error = err });
        }

        if (req.Role is not null)
        {
            var err = ValidateRole(req.Role);
            if (err is not null) return BadRequest(new { error = err });
        }

        var envChange = req.ExecutionEnvironment;
        if (envChange is not null)
        {
            var err = ValidateExecutionEnvironment(envChange);
            if (err is not null) return BadRequest(new { error = err });

            // Среда фиксируется после появления чатов: корни проектов и профили сред различаются,
            // resume-транскрипты привязаны к путям старой среды (аналог guard'а смены провайдера)
            var existing = users.GetById(id);
            if (existing is not null && existing.ExecutionEnvironment != envChange
                && sessions.HasSessionsOwnedBy(id))
                return Conflict(new { error = "Нельзя сменить среду исполнения: у пользователя уже есть чаты. Удалите их и повторите." });
        }

        try
        {
            if (!users.Update(id, req.Username, req.Role, envChange))
                return NotFound();

            var user = users.GetById(id)!;
            return Ok(ToDto(user));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        // Нельзя удалить самого себя
        var currentUserId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (id == currentUserId)
            return BadRequest(new { error = "Нельзя удалить собственную учётную запись" });

        var user = users.GetById(id);
        try
        {
            if (!users.Delete(id))
                return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        // Каскад знаний: датасеты «{username}:…», локальные сторы, персоны. Без него всё это
        // сиротеет, а новый пользователь с тем же именем увидел бы чужие базы как свои.
        if (user is not null)
            try { await knowledgeCascade.CleanupAsync(user.Id, user.Username); }
            catch (Exception ex) { logger.LogWarning(ex, "Каскад знаний при удалении пользователя {User}", id); }

        return NoContent();
    }

    [HttpPut("{id}/password")]
    public IActionResult ResetPassword(string id, [FromBody] ResetPasswordRequest req)
    {
        var err = ValidatePassword(req.NewPassword);
        if (err is not null) return BadRequest(new { error = err });

        if (!users.ResetPassword(id, req.NewPassword))
            return NotFound();

        return NoContent();
    }

    // --- вспомогательные методы ---

    private static UserDto ToDto(User u) =>
        new(u.Id, u.Username, u.Role, u.CreatedAt, u.ExecutionEnvironment);

    private static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "Имя пользователя не может быть пустым";
        if (username.Length < 3 || username.Length > 32)
            return "Имя пользователя должно содержать от 3 до 32 символов";
        if (!UsernameRegex.IsMatch(username))
            return "Имя пользователя может содержать только буквы, цифры, _ и -";
        return null;
    }

    private static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return "Пароль должен содержать не менее 8 символов";
        return null;
    }

    private static string? ValidateRole(string? role)
    {
        if (role is not "admin" and not "user")
            return "Роль должна быть 'admin' или 'user'";
        return null;
    }

    private static string? ValidateExecutionEnvironment(string? env)
    {
        if (env is not null && !ExecutionEnvironments.IsValid(env))
            return "Среда исполнения должна быть 'local' или 'container'";
        return null;
    }
}

public record UserDto(string Id, string Username, string Role, DateTime CreatedAt, string ExecutionEnvironment);
public record CreateUserRequest(string Username, string Password, string Role, string? ExecutionEnvironment = null);
public record UpdateUserRequest(string? Username, string? Role, string? ExecutionEnvironment = null);
public record ResetPasswordRequest(string NewPassword);
