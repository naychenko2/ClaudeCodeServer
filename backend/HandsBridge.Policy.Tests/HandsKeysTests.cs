using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;
using Vk = ClaudeHomeServer.HandsBridge.Policy.HandsPolicy.VirtualKeys;

namespace HandsBridge.Policy.Tests;

/// <summary>Системные сочетания клавиш — по виртуальным кодам после сопоставления имени.</summary>
public class HandsKeysTests
{
    private const int KeyA = 0x41;
    private const int KeyO = 0x4F;
    private const int KeyS = 0x53;
    private const int KeyR = 0x52;
    private const int Delete = 0x2E;
    private const int Return = 0x0D;
    private const int F4 = 0x73;

    [Theory]
    [InlineData(Vk.LeftWin, false, false, false, false)]
    [InlineData(Vk.RightWin, false, false, false, false)]
    [InlineData(KeyR, false, false, false, true)]
    [InlineData(Vk.Tab, false, true, false, false)]
    [InlineData(Vk.Escape, false, true, false, false)]
    [InlineData(Vk.Escape, true, false, false, false)]
    [InlineData(Vk.Escape, true, false, true, false)]
    [InlineData(Delete, true, true, false, false)]
    [InlineData(Vk.Menu, true, false, false, false)]
    [InlineData(Vk.LeftControl, false, true, false, false)]
    public void System_combination_is_denied(int key, bool ctrl, bool alt, bool shift, bool win)
    {
        var decision = HandsPolicy.CheckKeys(key, ctrl, alt, shift, win);

        Assert.False(decision.Allowed, $"vk={key:X2} ctrl={ctrl} alt={alt} shift={shift} win={win}");
    }

    [Theory]
    [InlineData(KeyA, true, false, false)]
    [InlineData(KeyS, true, false, false)]
    [InlineData(KeyO, true, false, false)]
    [InlineData(Delete, false, false, false)]
    [InlineData(Return, false, false, false)]
    [InlineData(Vk.Escape, false, false, false)]
    [InlineData(F4, false, true, false)]
    [InlineData(KeyA, true, false, true)]
    [InlineData(Vk.Tab, false, false, false)]
    public void Keys_the_bridge_presses_itself_pass(int key, bool ctrl, bool alt, bool shift)
    {
        Assert.True(HandsPolicy.CheckKeys(key, ctrl, alt, shift, win: false).Allowed);
    }

    [Theory]
    [InlineData(Vk.LeftWin)]
    [InlineData(Vk.RightWin)]
    [InlineData(Vk.Control)]
    [InlineData(Vk.RightControl)]
    [InlineData(Vk.Menu)]
    [InlineData(Vk.LeftMenu)]
    public void Holding_a_modifier_is_denied(int key)
    {
        Assert.False(HandsPolicy.CheckKeyDown(key).Allowed);
        Assert.True(HandsPolicy.CheckKeyDown(KeyA).Allowed);
    }
}
