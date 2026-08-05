using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace InventorySlotLock;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public HashSet<LockedSlot> LockedSlots { get; set; } = [];

    public List<FakeItem> FakeItems { get; set; } = [];

    public void Save(IDalamudPluginInterface pluginInterface)
        => pluginInterface.SavePluginConfig(this);
}

public readonly record struct LockedSlot(int Container, ushort Slot);

public readonly record struct FakeItem(int DisplayIndex, uint ItemId, uint Quantity);
