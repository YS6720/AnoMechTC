using AnoMech.Pointers;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using Lumina.Excel.Sheets;
using System;
using System.Linq;

namespace AnoMech.Helpers;

internal static unsafe class InstanceContentDirectorHelper
{
    public static void ProcessDirectorUpdate(uint category, uint arg1 = 0, uint arg2 = 0, uint arg3 = 0, uint arg4 = 0)
    {
        // native 相容閘（擋 crash）。閘為 false 時一律 no-op——
        // 呼叫錯的函式會讓 client 當場崩潰，靜默跳過至少還能跑機制。
        if (!Core.Map.NativeCompatGate.ProcessDirectorUpdateVerified)
        {
            Core.CrashTrace.Log($"[閘] 跳過 ProcessDirectorUpdate(0x{category:X}) — 台服 sig 未驗證");
            return;
        }

        var eventFramework = EventFramework.Instance();

        if (eventFramework == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.Commence] EventFramework.Instance() was null");
            return;
        }

        var director = eventFramework->GetInstanceContentDirector();

        if (director == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.Commence] eventFramework->GetInstanceContentDirector() was null");
            return;
        }

        var eventId = director->GetEventId();
        // 台服 CS 無此成員 ⇒ 走自行 signature-scan 的函式指標（見 EventFrameworkPointers）
        EventFrameworkPointers.ProcessDirectorUpdate(
            eventFramework, eventId, category, arg1, arg2, arg3, arg4);
    }

    public static void SetDirectorData(byte sequence, byte unknown, byte* unionData, ulong length = 12)
    {
        var eventFramework = EventFramework.Instance();

        if (eventFramework == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.SetDirectorData (ptr)] EventFramework.Instance() was null");
            return;
        }

        var director = eventFramework->GetInstanceContentDirector();

        if (director == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.SetDirectorData (ptr)] eventFramework->GetInstanceContentDirector() was null");
            return;
        }

        var eventId = director->GetEventId();
        EventFrameworkPointers.SetDirectorData(eventFramework, eventId, sequence, unknown, unionData, length);
    }

    public static void SetDirectorData(byte sequence, byte unk, byte[] unionData, bool fillExtraData = true)
    {
        if (unionData.Length < 12 && fillExtraData)
        {
            var extraBytes = 12 - unionData.Length;
            Plugin.Log.Debug($"[EventFrameworkHelper.SetDirectorData (array)] Adding {extraBytes} to unionData.");
            Array.Resize(ref unionData, 12);
        }
        else if (unionData.Length > 12)
        {
            var extraBytes = unionData.Length - 12;
            Plugin.Log.Debug($"[EventFrameworkHelper.SetDirectorData (array)] Removing {extraBytes} from unionData, as the maximum sane size is 12.");
            unionData = unionData.Take(12).ToArray();
        }

        fixed (byte* unionDataPtr = unionData)
        {
            SetDirectorData(sequence, unk, unionDataPtr, (ulong)unionData.Length);
        }
    }

    public static void SetDutyData(ContentFinderCondition contentFinderCondition)
    {
        var eventFramework = EventFramework.Instance();

        if (eventFramework == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.SetDutyData] EventFramework.Instance() was null");
            return;
        }

        var director = eventFramework->GetInstanceContentDirector();

        if (director == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.SetDutyData] eventFramework->GetInstanceContentDirector() was null");
            return;
        }

        var uiState = UIState.Instance();

        if (uiState == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.SetDutyData] UIState.Instance() was null");
            return;
        }

        var contentTypeRef = contentFinderCondition.ContentType;

        if (contentTypeRef.IsValid)
        {
            director->ContentTypeRowId = (byte)contentFinderCondition.ContentType.RowId;
            director->IconId = contentTypeRef.Value.IconDutyFinder;
        }
        else
        {
            Plugin.Log.Debug("[EventFrameworkHelper.SetDutyData] contentFinderCondition.ContentType was not valid, skipping director->ContentTypeRowId and director->IconId.");
        }

        using var str = new Utf8String(contentFinderCondition.Name);

        director->Title.SetString(str);
        uiState->DirectorTodo.Title.SetString(str);
        uiState->DirectorTodo.IsShown = true;
    }

    // Fire the three DirectorUpdate events the server sends at instance Commence
    // Note: these IDs/params are TOP-specific — other content fires different values
    // (e.g. T=1045 uses 40000007 / 40000001-E10 / 80000004-E0F). Future work to parameterize.
    public static void Commence()
    {
        ProcessDirectorUpdate(0x4000000C);
        ProcessDirectorUpdate(0x40000001, 0x1C20);
        ProcessDirectorUpdate(0x80000004, 0x1C1F);
    }
}
