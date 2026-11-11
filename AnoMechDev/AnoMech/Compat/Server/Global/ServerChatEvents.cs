using System;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace AnoMech.Compat.Server;

/// <summary>
/// 國際服伺服器層（Dalamud API 15）：聊天訊息事件收斂成 (類型, 發話者, 內容)；只讀不改、不攔截。
/// API 15 的 IChatGui.ChatMessage 改帶 <see cref="IHandleableChatMessage"/>。
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

    private void OnChatMessage(IHandleableChatMessage message) => received(message.LogKind, message.Sender, message.Message);

    public void Dispose() => chatGui.ChatMessage -= OnChatMessage;
}
