using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Multiplayer;
using Dalamud.Bindings.ImGui;

namespace AnoMech.Windows;

// Session-only fields: never persist credentials or infer a character identity.
internal sealed class MultiplayerPanel(Plugin plugin)
{
    private static readonly string[] Roles = ["MT", "ST", "H1", "H2", "D1", "D2", "D3", "D4"];
    private static readonly Vector4 Ok = new(0.4f, 1f, 0.6f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.8f, 0.2f, 1f);
    private static readonly Vector4 Muted = new(0.7f, 0.7f, 0.7f, 1f);
    private const string OutboundScope =
        "外送範圍：自填別名、房間／場次識別、版本與場景摘要、分工、模擬位置及 HP／狀態／世界特效；不傳錄影檔、聊天、裝備或帳號資料。";
    private string alias = "匿名";
    private string invitationText = "";
    private RoomInvitation? parsedInvitation;
    private string invitationError = "";
    private string hostingProfile = plugin.Configuration.MultiplayerHostingProfile;
    private bool invitationCopied;
    // 匯入中的開房授權只存在於這個工作緩衝區，成功保存或離開後立即清空；保存後一律只留密文。
    private string hostGrantImport = "";
    private string hostGrantStatus = "";
    private HostInvitation? hostGrant;
    private bool hostGrantLoaded;

    // Status/action first, details opt-in: the common path is "貼上邀請加入"，授權與外網設定屬少數人一次性設定。
    public void Draw()
    {
        if (!ImGui.CollapsingHeader("多人同步（TC Relay）"))
            return;
        var manager = plugin.Multiplayer;
        var session = manager.Session;
        DrawStatus(manager, session);
        ImGui.Spacing();
        if (manager.IsConnecting)
        {
            ImGui.TextDisabled("正在準備或清理連線，請稍候…");
            if (ImGui.Button("取消連線"))
                manager.Disconnect();
        }
        else if (!manager.HasSession)
        {
            invitationCopied = false;
            DrawConnectionEntry(manager);
        }
        else if (session != null)
            DrawRoom(manager, session);
        if (manager.ConnectionStatus.Length != 0)
            ImGui.TextWrapped(manager.ConnectionStatus);
        if (manager.LastError != MpError.None)
            WrappedColored(Warn, ErrorText(manager.LastError));
        ImGui.Separator();
    }

    private static void DrawStatus(MultiplayerManager manager, MultiplayerSession? session)
    {
        var (label, color) = StatusOf(manager, session);
        WrappedColored(color, $"狀態：{label}");
        if (!manager.HasSession || session == null)
            return;
        var role = session.IsHost ? "房主" : "成員";
        ImGui.TextWrapped(session.Identity is { } identity
            ? $"房間：{identity.RoomCode}　你的身分：{role}"
            : $"你的身分：{role}");
    }

    // 繁中狀態字樣；enum 名稱只在診斷區出現。
    private static (string Label, Vector4 Color) StatusOf(MultiplayerManager manager, MultiplayerSession? session)
    {
        if (manager.IsConnecting)
            return ("連線處理中", Warn);
        if (!manager.HasSession || session == null)
            return ("未連線", Muted);
        return session.Phase switch
        {
            MultiplayerPhase.Connecting => ("連線中", Warn),
            MultiplayerPhase.Lobby => ("房間等待中", Ok),
            MultiplayerPhase.Checking => ("檢查設定一致性", Warn),
            MultiplayerPhase.Preparing => ("場次準備中", Warn),
            MultiplayerPhase.Running => ("場次進行中", Ok),
            MultiplayerPhase.Restoring => ("場次收尾還原中", Warn),
            _ => ("房間已關閉", Muted),
        };
    }

    private void DrawRoom(MultiplayerManager manager, MultiplayerSession session)
    {
        var lobby = session.Phase == MultiplayerPhase.Lobby;
        if (session.IsHost)
        {
            ImGui.BeginDisabled(manager.Invitation.Length == 0 || !lobby);
            if (ImGui.Button("複製邀請", new Vector2(-1, 0)))
            {
                ImGui.SetClipboardText(manager.Invitation);
                invitationCopied = true;
            }
            ImGui.EndDisabled();
            if (invitationCopied)
                ImGui.TextDisabled("邀請已複製，傳給要加入的朋友即可。");
            ImGui.TextWrapped("持有邀請即可加入，請勿公開；朋友必須在場次開始前加入。");
            if (manager.Destination.StartsWith("ws:", System.StringComparison.Ordinal))
                WrappedColored(Warn, "這是同機測試房間，不能用這份邀請跨電腦連線。");
            ImGui.Spacing();
        }
        DrawMembers(session);
        ImGui.Spacing();
        ImGui.TextUnformatted("選擇你的分工");
        DrawRoleButtons(manager, session, lobby);
        ImGui.TextDisabled("所有連線成員選好不同分工後，由房主使用下方「開始」。未占用的位置由房主的 AI 負責。");
        ImGui.Spacing();
        ImGui.BeginDisabled(session.Phase != MultiplayerPhase.Running);
        if (ImGui.Button("請求本人的無敵狀態", new Vector2(-1, 0)))
            manager.GiveInvulnerability();
        ImGui.EndDisabled();
        ImGui.Spacing();
        if (ImGui.TreeNode("連線目的地與外送範圍"))
        {
            ImGui.TextWrapped($"入口：{manager.Destination}");
            ImGui.TextWrapped(OutboundScope);
            ImGui.TreePop();
        }
        ImGui.Separator();
        if (ImGui.Button("離開房間"))
            manager.Disconnect();
    }

    private static void DrawMembers(MultiplayerSession session)
    {
        ImGui.TextUnformatted($"房內成員（{session.Members.Count}）");
        if (!ImGui.BeginTable("##mpmembers", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("成員", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("分工", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        foreach (var member in session.Members)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            var self = member.PeerId == session.Identity?.PeerId;
            ImGui.TextWrapped(self ? $"{member.Alias}（你）" : member.Alias);
            ImGui.TableSetColumnIndex(1);
            if (member.Role is { } role)
                ImGui.TextUnformatted(Roles[(int)role]);
            else
                ImGui.TextDisabled("尚未選擇");
        }
        ImGui.EndTable();
    }

    // 4×2 表格：窄視窗下按鈕仍各佔一格寬度，不會被 SameLine 擠到截斷。
    private void DrawRoleButtons(MultiplayerManager manager, MultiplayerSession session, bool lobby)
    {
        var occupied = 0;
        foreach (var member in session.Members)
            if (member.Role is { } role && member.PeerId != session.Identity?.PeerId)
                occupied |= 1 << (int)role;
        if (!ImGui.BeginTable("##mproles", 4, ImGuiTableFlags.SizingStretchSame))
            return;
        for (var index = 0; index < Roles.Length; index++)
        {
            if (index % 4 == 0)
                ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(index % 4);
            ImGui.BeginDisabled(!lobby || (occupied & (1 << index)) != 0);
            if (ImGui.Button(Roles[index], new Vector2(-1, 0)))
                manager.ClaimRole((PartyRole)index);
            ImGui.EndDisabled();
        }
        ImGui.EndTable();
    }

    private void DrawConnectionEntry(MultiplayerManager manager)
    {
        EnsureHostGrantLoaded();
        ImGui.InputText("顯示別名", ref alias, MpLimits.AliasCharacters);
        var aliasOk = MpValidation.Alias(alias);
        if (!aliasOk)
            WrappedColored(Warn, $"別名無效：請填寫非空白、不含控制字元，且不超過 {MpValidation.AliasUtf8Bytes} 個 UTF-8 位元組的名稱。超過長度會直接拒絕，不會自動截斷。");
        if (ImGui.TreeNode("別名與資料外送說明"))
        {
            ImGui.TextWrapped($"別名自行填寫，不會傳送角色名稱；上限 {MpValidation.AliasUtf8Bytes} 個 UTF-8 位元組（中文約 21 字）。");
            ImGui.TextWrapped(OutboundScope);
            ImGui.TextWrapped("斷線不會自動續局，房主離線即關房。");
            ImGui.TreePop();
        }
        ImGui.Spacing();
        if (!ImGui.BeginTabBar("##mpentry"))
            return;
        if (ImGui.BeginTabItem("加入房間"))
        {
            DrawJoin(manager, aliasOk);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("共用 Relay 開房"))
        {
            DrawSharedHosting(manager, aliasOk);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("本機開房"))
        {
            DrawLocalHosting(manager, aliasOk);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawJoin(MultiplayerManager manager, bool aliasOk)
    {
        ImGui.TextWrapped("貼上房主給你的邀請即可加入；加入者不需要任何額外設定。");
        if (ImGui.Button("從剪貼簿貼上邀請"))
        {
            var clipboard = ImGui.GetClipboardText();
            if (clipboard.Length <= RoomInvitation.MaxTextLength)
            {
                invitationText = clipboard;
                ParseInvitation();
            }
            else
            {
                invitationText = "";
                parsedInvitation = null;
                invitationError = "邀請資訊過長，請重新複製房主提供的邀請。";
            }
        }
        if (ImGui.InputText("邀請資訊", ref invitationText, RoomInvitation.MaxTextLength, ImGuiInputTextFlags.Password))
            ParseInvitation();
        if (parsedInvitation is { } invitation)
        {
            ImGui.TextWrapped($"將連線至：{invitation.Endpoint.GetLeftPart(System.UriPartial.Authority)}");
            ImGui.TextWrapped($"房間：{invitation.RoomCode}");
            if (invitation.LocalOnly)
                WrappedColored(Warn, "這份邀請僅供同一台電腦測試，不能跨電腦使用。");
            else
                ImGui.TextWrapped("連線資料會交給此入口的營運者；請只加入你信任的房主。");
        }
        if (invitationError.Length != 0)
            WrappedColored(Warn, invitationError);
        ImGui.Spacing();
        ImGui.BeginDisabled(parsedInvitation == null || !aliasOk);
        if (ImGui.Button("加入房間", new Vector2(-1, 0)))
        {
            manager.Join(invitationText, alias);
            invitationText = "";
            parsedInvitation = null;
            invitationError = "";
        }
        ImGui.EndDisabled();
    }

    private void DrawLocalHosting(MultiplayerManager manager, bool aliasOk)
    {
        ImGui.TextWrapped("由這台電腦啟動自己的連線服務與外網入口；只有這種開房會由插件負責關閉。需要先完成一次外網入口設定。");
        ImGui.BeginDisabled(!aliasOk || string.IsNullOrWhiteSpace(hostingProfile));
        if (ImGui.Button("以本機開房", new Vector2(-1, 0)))
            manager.Host(hostingProfile, alias);
        ImGui.EndDisabled();
        ImGui.Spacing();
        if (ImGui.TreeNode("房主設定（只需一次）"))
        {
            ImGui.TextWrapped("只有自行管理本機服務的人需要填入此電腦的設定檔。共用 Relay 開房及加入房間都不需要；請勿把自己的設定檔交給其他人。");
            ImGui.InputText("房主設定檔", ref hostingProfile, 1024, ImGuiInputTextFlags.Password);
            if (ImGui.Button("儲存設定檔路徑"))
            {
                plugin.Configuration.MultiplayerHostingProfile = hostingProfile;
                plugin.Configuration.Save();
            }
            ImGui.TextWrapped("設定檔只存外網網址與本機程式路徑；每次房間憑證自動產生，不必手填，也不寫入設定。");
            ImGui.TreePop();
        }
    }

    private void DrawSharedHosting(MultiplayerManager manager, bool aliasOk)
    {
        ImGui.TextWrapped("連到別人長期開著的共用 Relay 建立房間；遊戲機制仍由你這台電腦運算，本機不會啟動任何連線服務或外網入口。需要管理者核發的授權。");
        if (hostGrant is { } grant)
        {
            ImGui.TextWrapped($"已匯入授權：{grant.Endpoint.GetLeftPart(System.UriPartial.Authority)}（編號 {grant.GrantId[..8]}…）");
            if (grant.LocalOnly)
                WrappedColored(Warn, "這份授權指向同機測試入口，只能在這台電腦驗證，不能跨電腦開房。");
            ImGui.BeginDisabled(!aliasOk);
            if (ImGui.Button("以共用 Relay 開房", new Vector2(-1, 0)))
                manager.HostShared(grant, alias);
            ImGui.EndDisabled();
        }
        else
            ImGui.TextDisabled("尚未匯入開房授權；沒有授權無法在共用 Relay 開房。");
        ImGui.Spacing();
        if (ImGui.TreeNode(hostGrant == null ? "匯入開房授權" : "重新匯入開房授權"))
        {
            ImGui.TextWrapped("貼上管理者核發給你的授權內容（ANOMECH-HOST 開頭的一行）。這是機密：只會用你的 Windows 帳號加密後存在本機設定，插件不會顯示、不會自動複製，也不會寫入日誌。");
            ImGui.InputText("開房授權", ref hostGrantImport, HostInvitation.MaxTextLength, ImGuiInputTextFlags.Password);
            if (ImGui.Button("從剪貼簿貼上授權"))
            {
                var clipboard = ImGui.GetClipboardText();
                if (clipboard.Length <= HostInvitation.MaxTextLength)
                {
                    hostGrantImport = clipboard;
                    hostGrantStatus = "";
                }
                else
                {
                    hostGrantImport = "";
                    hostGrantStatus = "授權內容過長；請重新複製管理者核發的授權檔內容。";
                }
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(hostGrantImport.Length == 0);
            if (ImGui.Button("匯入並保存"))
                ImportHostGrant();
            ImGui.EndDisabled();
            ImGui.TreePop();
        }
        if (hostGrantStatus.Length != 0)
            ImGui.TextWrapped(hostGrantStatus);
        if (hostGrant == null)
            return;
        ImGui.Separator();
        if (ImGui.Button("清除已保存的授權"))
        {
            plugin.Configuration.MultiplayerHostGrantProtected = "";
            plugin.Configuration.Save();
            hostGrant = null;
            hostGrantImport = "";
            hostGrantStatus = "已清除本機保存的開房授權。";
        }
    }

    // TextColored 不換行；長的安全提示一律走這條以免窄視窗截斷。
    private static void WrappedColored(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    // 只有使用者主動匯入才會保存，且只保存 DPAPI 密文；解密不成功一律要求重匯，不回落明文。
    private void ImportHostGrant()
    {
        if (!HostInvitation.TryParse(hostGrantImport, out var parsed) || parsed is null)
        {
            hostGrantStatus = "授權內容格式不正確；請向管理者確認，並貼上完整的一行內容。";
            return;
        }
        if (!HostCredentialProtection.TryProtect(parsed, out var ciphertext) || ciphertext is null)
        {
            hostGrantStatus = HostCredentialProtection.IsSupported
                ? "無法用這個 Windows 帳號加密保存授權；未保存任何內容。"
                : "此平台不支援 Windows DPAPI；拒絕以明文保存開房授權。";
            return;
        }
        plugin.Configuration.MultiplayerHostGrantProtected = ciphertext;
        plugin.Configuration.Save();
        hostGrant = parsed;
        hostGrantImport = "";
        hostGrantStatus = "開房授權已加密保存在本機；現在可以使用共用 Relay 開房。";
    }

    private void EnsureHostGrantLoaded()
    {
        if (hostGrantLoaded)
            return;
        hostGrantLoaded = true;
        var stored = plugin.Configuration.MultiplayerHostGrantProtected;
        if (stored.Length == 0)
            return;
        if (HostCredentialProtection.TryUnprotect(stored, out hostGrant))
            return;
        hostGrant = null;
        hostGrantStatus = HostCredentialProtection.IsSupported
            ? "已保存的開房授權無法解密（可能換了 Windows 帳號或電腦）；請重新匯入，插件不會嘗試任何明文來源。"
            : "此平台不支援 Windows DPAPI，無法使用已保存的開房授權。";
    }

    private void ParseInvitation()
    {
        var valid = RoomInvitation.TryParse(invitationText, out parsedInvitation);
        invitationError = invitationText.Length == 0 || valid
            ? "" : "邀請格式不正確，請貼上房主複製的完整邀請資訊。";
    }

    private static string ErrorText(MpError error) => error switch
    {
        MpError.RoleOccupied => "這個分工已有人選擇，請改選其他分工。",
        MpError.RoleRequired => "請所有成員先選擇不同分工，再由房主開始。",
        MpError.BuildMismatch => "建置內容不一致，請使用同一批套件，並確認遊戲及插件版本一致後重新加入。",
        MpError.ProtocolMismatch => "插件版本不一致，請使用相同版本後重新加入。",
        // 版本內容差異與資料不一致走同一個 error：成員的建置可能沒有該副本。
        // 否則使用者看到「資料不一致」會去找檔案，而真正的原因是版本不同。
        MpError.SceneMismatch or MpError.ResourceMismatch =>
            "有成員的版本沒有這個副本，或雙方場景資料不一致；請確認全員使用相同版本的插件。",
        MpError.Busy => "目前仍在連線、清理或模擬中，請完成當前操作後再試。",
        MpError.HostDisconnected => "房主已離線，房間已關閉；請向房主取得新邀請。",
        MpError.PeerDisconnected => "有成員離線，當前場次已結束；整理成員後可重新開始。",
        MpError.PrepareFailed or MpError.NativeFailure => "全隊：模擬準備或執行失敗；已停止當前場次，不會自動續接。",
        MpError.TransportFailure or MpError.InvalidEndpoint => "連線未完成；請查看上方的設定或邀請提示。",
        _ => "同步已停止或操作未完成；請確認網路與邀請內容後重新連線。",
    };
}
