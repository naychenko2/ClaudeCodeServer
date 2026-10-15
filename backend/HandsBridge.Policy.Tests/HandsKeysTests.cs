using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;
using Vk = ClaudeHomeServer.HandsBridge.Policy.HandsPolicy.VirtualKeys;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Узкий запрет системных сочетаний — по виртуальным кодам ПОСЛЕ сопоставления имени (синонимы
/// «meta»/«super» маппятся в VK_LWIN и обхода не дают). Запрещены: Win сама по себе, Win+R/X/Q/L,
/// Win+S (без Shift), Ctrl+Esc, Ctrl+Shift+Esc. Всё остальное — разрешено.
/// </summary>
public class HandsKeysTests
{
    private const int KeyA = 0x41;
    private const int KeyD = 0x44;
    private const int KeyE = 0x45;
    private const int KeyQ = 0x51;
    private const int KeyR = 0x52;
    private const int KeyS = 0x53;
    private const int KeyV = 0x56;
    private const int KeyX = 0x58;
    private const int KeyL = 0x4C;
    private const int Key1 = 0x31;
    private const int LeftArrow = 0x25;
    private const int F4 = 0x73;

    // ---------- запрещённые ----------

    [Theory]
    // Win сама по себе (без другой клавиши) — «Пуск»; VK_LWIN/VK_RWIN — в том числе синонимы meta/super
    [InlineData(Vk.LeftWin, false, false, false, false)]
    [InlineData(Vk.RightWin, false, false, false, false)]
    // Win + R/X/Q/L — «Выполнить», меню, поиск, блокировка
    [InlineData(KeyR, false, false, false, true)]
    [InlineData(KeyX, false, false, false, true)]
    [InlineData(KeyQ, false, false, false, true)]
    [InlineData(KeyL, false, false, false, true)]
    // Win + S — поиск (Win+Shift+S — сниппинг, разрешён)
    [InlineData(KeyS, false, false, false, true)]
    // Ctrl+Esc — «Пуск», Ctrl+Shift+Esc — диспетчер задач
    [InlineData(Vk.Escape, true, false, false, false)]
    [InlineData(Vk.Escape, true, false, true, false)]
    public void Shell_combination_is_denied(int key, bool ctrl, bool alt, bool shift, bool win)
    {
        var decision = HandsPolicy.CheckKeys(key, ctrl, alt, shift, win);

        Assert.False(decision.Allowed, $"vk={key:X2} ctrl={ctrl} alt={alt} shift={shift} win={win}");
    }

    // ---------- разрешённые ----------

    [Theory]
    [InlineData(Vk.Tab, false, true, false, false)]        // Alt+Tab
    [InlineData(Vk.Escape, false, true, false, false)]     // Alt+Esc
    [InlineData(KeyD, false, false, false, true)]          // Win+D
    [InlineData(LeftArrow, false, false, false, true)]     // Win+стрелка
    [InlineData(KeyQ, true, true, false, false)]           // Ctrl+Alt+Q (AltGr)
    [InlineData(F4, false, true, false, false)]            // Alt+F4
    [InlineData(Key1, false, false, false, true)]          // Win+1
    [InlineData(KeyS, false, false, true, true)]           // Win+Shift+S — сниппинг
    [InlineData(KeyV, false, false, false, true)]          // Win+V
    [InlineData(KeyE, false, false, false, true)]          // Win+E
    [InlineData(KeyA, true, false, false, false)]          // Ctrl+A
    [InlineData(KeyA, false, false, false, false)]         // обычная клавиша
    public void Allowed_combination_passes(int key, bool ctrl, bool alt, bool shift, bool win)
    {
        Assert.True(HandsPolicy.CheckKeys(key, ctrl, alt, shift, win).Allowed);
    }

    // ---------- удержание (CheckKeyDown) ----------

    [Theory]
    // Удержание одной Win — запрещено: иначе Win «залипнет» и откроет «Пуск»
    [InlineData(Vk.LeftWin, false, false, false, false)]
    [InlineData(Vk.RightWin, false, false, false, false)]
    // Удержанный Win + R в одном вызове — то же Win+R
    [InlineData(KeyR, false, false, false, true)]
    public void Held_win_combination_is_denied(int key, bool ctrl, bool alt, bool shift, bool win)
    {
        Assert.False(HandsPolicy.CheckKeyDown(key, ctrl, alt, shift, win).Allowed);
    }

    [Theory]
    // Удержание любой обычной клавиши (включая Ctrl и Alt) — разрешено
    [InlineData(KeyA)]
    [InlineData(Vk.Control)]
    [InlineData(Vk.RightControl)]
    [InlineData(Vk.Menu)]
    [InlineData(Vk.LeftMenu)]
    public void Holding_a_key_is_allowed(int key)
    {
        Assert.True(HandsPolicy.CheckKeyDown(key).Allowed);
    }
}
