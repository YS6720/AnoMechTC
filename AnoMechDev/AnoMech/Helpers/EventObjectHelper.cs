using AnoMech.Pointers;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace AnoMech.Helpers;

internal static unsafe class EventObjectHelper
{
    public static bool Create(SpawnObjectPacket* packet, out int slot, out GameObject* eventObject, bool useSyntheticEntityId = false)
    {
        var manager = EventObjectManager.Instance();

        // Since the Packet Handle method does not return the ID, we do a manual check beforehand, and test if that ID was created.
        if (packet->ObjectIndex == 0xFF) // -1
        {
            var freeId = -1;

            for (int i = 0; i < 40; i++)
            {
                if (manager->EventObjects[i] == null)
                {
                    freeId = i;
                    break;
                }
            }

            if (freeId == -1)
            {
                slot = -1;
                eventObject = null;
                return false;
            }

            packet->ObjectIndex = (byte)freeId;
        }

        // EventObject ObjectIndex is local to the 40-slot pool; use the
        // corresponding global GameObject slot for opt-in synthetic IDs.
        if (useSyntheticEntityId)
        {
            packet->EntityId = 0xE0000000u + 449u + packet->ObjectIndex;
        }

        // 台服 CS 無此函式 ⇒ 走自行 signature-scan 的函式指標（已離線驗證唯一命中）
        PacketDispatcherPointers.HandleSpawnObjectPacket(0, packet);
        slot = packet->ObjectIndex;
        eventObject = EventObjectManagerPointers.GetEventObjectByIndex(manager, (uint)slot);
        return eventObject != null;
    }

    // Writes the EObj state field (actor[0x1B2]) and notifies the attached
    // SharedGroupLayoutInstance to switch sub-instances. Different SGs use
    // different state values to gate which sub-instances are visible —
    // simulator code finds the right value empirically per EObj row.
    public static void SetState(GameObject* obj, ushort state)
    {
        if (obj == null)
        {
            Plugin.Log.Warning("[EventObjectHelper.SetState] obj was null.");
            return;
        }

        EventObjectPointers.SetEventObjectState((EventObject*)obj, state, 1, 0);
    }
}
