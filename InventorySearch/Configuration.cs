using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace InventorySearch;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public List<CharacterSnapshot> Characters { get; set; } = [];

    public void Save(IDalamudPluginInterface pluginInterface)
        => pluginInterface.SavePluginConfig(this);
}

public sealed class CharacterSnapshot
{
    public ulong ContentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
    public List<StorageSnapshot> Storages { get; set; } = [];
}

public sealed class StorageSnapshot
{
    public string Key { get; set; } = string.Empty;
    public StorageKind Kind { get; set; }
    public ulong OwnerId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public List<StoredItem> Items { get; set; } = [];
}

public sealed class StoredItem
{
    public uint ItemId { get; set; }
    public uint Quantity { get; set; }
    public bool IsHq { get; set; }
    public uint IconId { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint Container { get; set; }
    public int Slot { get; set; }
}

public enum StorageKind
{
    Inventory,
    Saddlebag,
    PremiumSaddlebag,
    Retainer,
    GlamourDresser,
    Armoire,
}
