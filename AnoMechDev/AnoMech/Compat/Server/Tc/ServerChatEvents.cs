using System;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace AnoMech.Compat.Server;

/// <summary>
/// TC 伺服器層（Dalamud API 13）：聊天訊息事件收斂成 (類型, 發話者, 內容)；只讀不改、不攔截。
/// API 13 的 IChatGui.ChatMessage 是 ref 參數版委派。
/// </summary>
internal sealed class ServerChatEvents : IDisposable
{
    private readonly IChatGui chatGui;
    private readonly Action<XivChatType, SeString, SeString> received;

    public ServerChatEvents(IChatGui chatGui, Action<XivChatType, SeString, SeString> received)
    {
        this.chatGui = chatGui;
        this.received = received;
        chatGui.ChatMessage += OnChatMessage;
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message,
        ref bool isHandled) => received(type, sender, message);

    public void Dispose() => chatGui.ChatMessage -= OnChatMessage;
}
