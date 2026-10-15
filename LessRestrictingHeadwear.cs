using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

[assembly: AssemblyVersion("4.0.0.0")]
[assembly: AssemblyFileVersion("4.0.0.0")]
[assembly: AssemblyInformationalVersion("4.0.0")]
[assembly: AssemblyCompany("MusicManiac")]
[assembly: AssemblyProduct("Less Restricting Headwear")]
[assembly: AssemblyCopyright("Copyright (c) 2023-2026 MusicManiac")]

namespace LessRestrictingHeadwear;

[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public sealed class LessRestrictingHeadwear : IOnLoad
{
    private static readonly EquipmentRole[] AtomicRoles =
    [
        EquipmentRole.Headwear,
        EquipmentRole.Earpiece,
        EquipmentRole.FaceCover,
        EquipmentRole.Eyewear,
    ];

    private static readonly IReadOnlyDictionary<string, EquipmentRole> EquipmentSlotRoles =
        new Dictionary<string, EquipmentRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["Headwear"] = EquipmentRole.Headwear,
            ["Earpiece"] = EquipmentRole.Earpiece,
            ["FaceCover"] = EquipmentRole.FaceCover,
            ["Eyewear"] = EquipmentRole.Eyewear,
        };

    private readonly ISptLogger<LessRestrictingHeadwear> _logger;
    private readonly TemplateTable _templateTable;
    private readonly LessRestrictingHeadwearConfig _config;

    public LessRestrictingHeadwear(
        ISptLogger<LessRestrictingHeadwear> logger,
        ModHelper modHelper,
        JsonUtil jsonUtil,
        TemplateTable templateTable
    )
    {
        _logger = logger;
        _templateTable = templateTable;

        string modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        string configPath = System.IO.Path.Combine(modPath, "config.jsonc");
        if (!File.Exists(configPath))
        {
            File.WriteAllText(
                configPath,
                jsonUtil.Serialize(new LessRestrictingHeadwearConfig(), true) ?? string.Empty,
                new UTF8Encoding(false)
            );
        }

        _config = modHelper.GetJsonDataFromFile<LessRestrictingHeadwearConfig>(modPath, "config.jsonc");
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        ItemGraph graph = new(_templateTable.Items);
        DiscoveryResult discovery = DiscoverRoles(graph, cancellationToken);
        PatchResult result = ApplyCompatibility(discovery.Roles, cancellationToken);

        _logger.Success(
            $"[LessRestrictingHeadwear] Changed {result.ChangedItems} items: "
                + $"{result.BlockFlagsCleared} block flags cleared and "
                + $"{result.ConflictEntriesRemoved} conflict entries removed."
        );

        if (_config.Debug)
        {
            _logger.Info(
                $"[LessRestrictingHeadwear] Discovered "
                    + $"{discovery.DirectHeadwearCount} headwear, "
                    + $"{discovery.DirectEarpieceCount} earpieces, "
                    + $"{discovery.DirectFaceCoverCount} face covers, "
                    + $"{discovery.DirectEyewearCount} eyewear, and "
                    + $"{discovery.HelmetAttachmentCount} reachable helmet attachments."
            );
            _logger.Info(
                $"[LessRestrictingHeadwear] Preserved "
                    + $"{result.ClassifiedConflictsPreserved} same-role or disabled-pair conflicts."
            );
        }

        return Task.CompletedTask;
    }

    private DiscoveryResult DiscoverRoles(ItemGraph graph, CancellationToken cancellationToken)
    {
        TemplateItem inventory = FindEquipmentInventory();
        Dictionary<MongoId, EquipmentRole> roles = [];
        Dictionary<EquipmentRole, HashSet<MongoId>> directMembers = [];

        foreach ((string slotName, EquipmentRole role) in EquipmentSlotRoles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Slot slot = inventory.Properties!.Slots!.First(candidate =>
                string.Equals(candidate.Name, slotName, StringComparison.OrdinalIgnoreCase)
            );

            HashSet<MongoId> filterSeeds = GetFilterSeeds(slot);
            HashSet<MongoId> members = [];
            Dictionary<MongoId, int> itemSeedNodeFrequency = [];

            foreach (MongoId seed in filterSeeds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                members.UnionWith(graph.ExpandFilterSeed(seed));

                if (
                    graph.TryGetItem(seed, out TemplateItem seedItem)
                    && IsConcreteItem(seedItem)
                    && graph.TryGetNearestNodeAncestor(seed, out MongoId nodeId)
                )
                {
                    itemSeedNodeFrequency[nodeId] = itemSeedNodeFrequency.GetValueOrDefault(nodeId) + 1;
                }
            }

            // A node explicitly used by a slot filter is authoritative. When a slot instead
            // lists concrete items (as Earpiece does), expand a common item-type node only
            // when at least two listed items share it. A one-off custom item remains a direct
            // member without accidentally classifying its entire unrelated parent category.
            foreach (MongoId seed in filterSeeds)
            {
                if (graph.TryGetItem(seed, out TemplateItem seedItem) && IsNode(seedItem))
                {
                    members.UnionWith(graph.GetConcreteDescendants(seed));
                }
            }

            foreach ((MongoId nodeId, int frequency) in itemSeedNodeFrequency)
            {
                if (frequency >= 2)
                {
                    members.UnionWith(graph.GetConcreteDescendants(nodeId));
                }
            }

            directMembers[role] = members;
            foreach (MongoId member in members)
            {
                AddRole(roles, member, role);
            }
        }

        // Every possible child reachable through a headwear item's slot filters is part of
        // the headwear assembly. Traversal includes nested mounts and mod-added attachments.
        // Only compatibility properties are changed later, so unrelated lights/NVGs remain
        // untouched unless they actually declare a relevant wearable conflict.
        HashSet<MongoId> headwearRoots = directMembers[EquipmentRole.Headwear];
        HashSet<MongoId> helmetAttachments = [];
        Queue<MongoId> pending = new(headwearRoots);
        HashSet<MongoId> traversed = [];

        while (pending.TryDequeue(out MongoId current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!traversed.Add(current) || !graph.TryGetItem(current, out TemplateItem item))
            {
                continue;
            }

            foreach (MongoId child in GetFilteredItems(item, graph))
            {
                if (headwearRoots.Contains(child) || !helmetAttachments.Add(child))
                {
                    continue;
                }

                // Preserve an attachment's more specific wearable role. For example, a
                // helmet-mounted headset is still an Earpiece; treating it as both Headwear
                // and Earpiece could incorrectly remove same-earpiece conflicts. Attachments
                // without a wearable role inherit Headwear from their containing assembly.
                if (!roles.ContainsKey(child))
                {
                    AddRole(roles, child, EquipmentRole.Headwear);
                }
                pending.Enqueue(child);
            }
        }

        return new DiscoveryResult(
            roles,
            directMembers[EquipmentRole.Headwear].Count,
            directMembers[EquipmentRole.Earpiece].Count,
            directMembers[EquipmentRole.FaceCover].Count,
            directMembers[EquipmentRole.Eyewear].Count,
            helmetAttachments.Count
        );
    }

    private TemplateItem FindEquipmentInventory()
    {
        foreach (TemplateItem item in _templateTable.Items.Values)
        {
            IEnumerable<Slot> slots = item.Properties?.Slots ?? [];
            HashSet<string> slotNames = new(
                slots.Select(slot => slot.Name).Where(name => !string.IsNullOrWhiteSpace(name))!,
                StringComparer.OrdinalIgnoreCase
            );

            if (EquipmentSlotRoles.Keys.All(slotNames.Contains))
            {
                return item;
            }
        }

        throw new InvalidOperationException(
            "Could not find an inventory template containing Headwear, Earpiece, FaceCover, and Eyewear slots."
        );
    }

    private PatchResult ApplyCompatibility(
        IReadOnlyDictionary<MongoId, EquipmentRole> roles,
        CancellationToken cancellationToken
    )
    {
        int blockFlagsCleared = 0;
        int conflictEntriesRemoved = 0;
        int classifiedConflictsPreserved = 0;
        HashSet<MongoId> changedItems = [];

        foreach ((MongoId itemId, EquipmentRole sourceRoles) in roles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_templateTable.Items.TryGetValue(itemId, out TemplateItem? item) || item.Properties is null)
            {
                continue;
            }

            TemplateItemProperties properties = item.Properties;
            int itemBlockChanges = 0;
            itemBlockChanges += ClearBlockIfAllowed(
                sourceRoles,
                EquipmentRole.Headwear,
                properties.BlocksHeadwear,
                value => properties.BlocksHeadwear = value
            );
            itemBlockChanges += ClearBlockIfAllowed(
                sourceRoles,
                EquipmentRole.Earpiece,
                properties.BlocksEarpiece,
                value => properties.BlocksEarpiece = value
            );
            itemBlockChanges += ClearBlockIfAllowed(
                sourceRoles,
                EquipmentRole.FaceCover,
                properties.BlocksFaceCover,
                value => properties.BlocksFaceCover = value
            );
            itemBlockChanges += ClearBlockIfAllowed(
                sourceRoles,
                EquipmentRole.Eyewear,
                properties.BlocksEyewear,
                value => properties.BlocksEyewear = value
            );

            int itemConflictChanges = 0;
            if (properties.ConflictingItems is not null)
            {
                foreach (MongoId targetId in properties.ConflictingItems.ToArray())
                {
                    if (!roles.TryGetValue(targetId, out EquipmentRole targetRoles))
                    {
                        continue;
                    }

                    if (RolesAreCompatible(sourceRoles, targetRoles))
                    {
                        properties.ConflictingItems.Remove(targetId);
                        itemConflictChanges++;
                    }
                    else
                    {
                        classifiedConflictsPreserved++;
                    }
                }
            }

            if (itemBlockChanges + itemConflictChanges == 0)
            {
                continue;
            }

            blockFlagsCleared += itemBlockChanges;
            conflictEntriesRemoved += itemConflictChanges;
            changedItems.Add(itemId);

            if (_config.Debug)
            {
                _logger.Info(
                    $"[LessRestrictingHeadwear] Patched {item.Name} ({itemId}): "
                        + $"{itemBlockChanges} flags, {itemConflictChanges} conflicts."
                );
            }
        }

        return new PatchResult(
            changedItems.Count,
            blockFlagsCleared,
            conflictEntriesRemoved,
            classifiedConflictsPreserved
        );
    }

    private int ClearBlockIfAllowed(
        EquipmentRole sourceRoles,
        EquipmentRole targetRole,
        bool? currentValue,
        Action<bool?> setter
    )
    {
        if (currentValue is not true || !RolesAreCompatible(sourceRoles, targetRole))
        {
            return 0;
        }

        setter(false);
        return 1;
    }

    private bool RolesAreCompatible(EquipmentRole sourceRoles, EquipmentRole targetRoles)
    {
        // A shared role represents a same-slot/same-kind conflict and must survive even if
        // one of the items has another role as well.
        if ((sourceRoles & targetRoles) != EquipmentRole.None)
        {
            return false;
        }

        foreach (EquipmentRole source in AtomicRoles)
        {
            if (!sourceRoles.HasFlag(source))
            {
                continue;
            }

            foreach (EquipmentRole target in AtomicRoles)
            {
                if (targetRoles.HasFlag(target) && _config.Compatibility.Allows(source, target))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static HashSet<MongoId> GetFilterSeeds(Slot slot)
    {
        HashSet<MongoId> result = [];
        foreach (SlotFilter filter in slot.Properties?.Filters ?? [])
        {
            if (filter.Filter is not null)
            {
                result.UnionWith(filter.Filter);
            }
        }

        return result;
    }

    private static IEnumerable<MongoId> GetFilteredItems(TemplateItem item, ItemGraph graph)
    {
        foreach (Slot slot in item.Properties?.Slots ?? [])
        {
            foreach (MongoId seed in GetFilterSeeds(slot))
            {
                foreach (MongoId candidate in graph.ExpandFilterSeed(seed))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static void AddRole(
        IDictionary<MongoId, EquipmentRole> roles,
        MongoId itemId,
        EquipmentRole role
    )
    {
        roles.TryGetValue(itemId, out EquipmentRole currentRoles);
        roles[itemId] = currentRoles | role;
    }

    private static bool IsNode(TemplateItem item) =>
        string.Equals(item.Type, "Node", StringComparison.OrdinalIgnoreCase);

    private static bool IsConcreteItem(TemplateItem item) =>
        string.Equals(item.Type, "Item", StringComparison.OrdinalIgnoreCase);
}

internal sealed class ItemGraph
{
    private readonly IReadOnlyDictionary<MongoId, TemplateItem> _items;
    private readonly Dictionary<MongoId, List<MongoId>> _childrenByParent = [];
    private readonly Dictionary<MongoId, HashSet<MongoId>> _concreteDescendantCache = [];

    public ItemGraph(IReadOnlyDictionary<MongoId, TemplateItem> items)
    {
        _items = items;
        foreach ((MongoId itemId, TemplateItem item) in items)
        {
            if (!_childrenByParent.TryGetValue(item.Parent, out List<MongoId>? children))
            {
                children = [];
                _childrenByParent[item.Parent] = children;
            }

            children.Add(itemId);
        }
    }

    public bool TryGetItem(MongoId itemId, out TemplateItem item)
    {
        if (_items.TryGetValue(itemId, out TemplateItem? found))
        {
            item = found;
            return true;
        }

        item = null!;
        return false;
    }

    public IReadOnlySet<MongoId> ExpandFilterSeed(MongoId seed)
    {
        if (!_items.TryGetValue(seed, out TemplateItem? item))
        {
            return new HashSet<MongoId>();
        }

        if (string.Equals(item.Type, "Node", StringComparison.OrdinalIgnoreCase))
        {
            return GetConcreteDescendants(seed);
        }

        return new HashSet<MongoId> { seed };
    }

    public HashSet<MongoId> GetConcreteDescendants(MongoId root)
    {
        if (_concreteDescendantCache.TryGetValue(root, out HashSet<MongoId>? cached))
        {
            return cached;
        }

        HashSet<MongoId> result = [];
        Queue<MongoId> pending = new();
        HashSet<MongoId> visited = [];
        pending.Enqueue(root);

        while (pending.TryDequeue(out MongoId current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (!_childrenByParent.TryGetValue(current, out List<MongoId>? children))
            {
                continue;
            }

            foreach (MongoId child in children)
            {
                if (!_items.TryGetValue(child, out TemplateItem? childItem))
                {
                    continue;
                }

                if (string.Equals(childItem.Type, "Item", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(child);
                }

                pending.Enqueue(child);
            }
        }

        _concreteDescendantCache[root] = result;
        return result;
    }

    public bool TryGetNearestNodeAncestor(MongoId itemId, out MongoId nodeId)
    {
        HashSet<MongoId> visited = [];
        MongoId current = itemId;

        while (_items.TryGetValue(current, out TemplateItem? item) && visited.Add(current))
        {
            MongoId parentId = item.Parent;
            if (!_items.TryGetValue(parentId, out TemplateItem? parent))
            {
                break;
            }

            if (string.Equals(parent.Type, "Node", StringComparison.OrdinalIgnoreCase))
            {
                nodeId = parentId;
                return true;
            }

            current = parentId;
        }

        nodeId = default;
        return false;
    }
}

[Flags]
internal enum EquipmentRole
{
    None = 0,
    Headwear = 1 << 0,
    Earpiece = 1 << 1,
    FaceCover = 1 << 2,
    Eyewear = 1 << 3,
}

public sealed record LessRestrictingHeadwearConfig
{
    [JsonPropertyName("debug")]
    public bool Debug { get; init; }

    [JsonPropertyName("compatibility")]
    public CompatibilityRules Compatibility { get; init; } = new();
}

public sealed record CompatibilityRules
{
    [JsonPropertyName("headwearWithEarpiece")]
    public bool HeadwearWithEarpiece { get; init; } = true;

    [JsonPropertyName("headwearWithFaceCover")]
    public bool HeadwearWithFaceCover { get; init; } = true;

    [JsonPropertyName("headwearWithEyewear")]
    public bool HeadwearWithEyewear { get; init; } = true;

    [JsonPropertyName("earpieceWithFaceCover")]
    public bool EarpieceWithFaceCover { get; init; } = true;

    [JsonPropertyName("earpieceWithEyewear")]
    public bool EarpieceWithEyewear { get; init; } = true;

    [JsonPropertyName("faceCoverWithEyewear")]
    public bool FaceCoverWithEyewear { get; init; } = true;

    internal bool Allows(EquipmentRole left, EquipmentRole right)
    {
        if (left == right || left == EquipmentRole.None || right == EquipmentRole.None)
        {
            return false;
        }

        return (left, right) switch
        {
            (EquipmentRole.Headwear, EquipmentRole.Earpiece)
            or (EquipmentRole.Earpiece, EquipmentRole.Headwear) => HeadwearWithEarpiece,
            (EquipmentRole.Headwear, EquipmentRole.FaceCover)
            or (EquipmentRole.FaceCover, EquipmentRole.Headwear) => HeadwearWithFaceCover,
            (EquipmentRole.Headwear, EquipmentRole.Eyewear)
            or (EquipmentRole.Eyewear, EquipmentRole.Headwear) => HeadwearWithEyewear,
            (EquipmentRole.Earpiece, EquipmentRole.FaceCover)
            or (EquipmentRole.FaceCover, EquipmentRole.Earpiece) => EarpieceWithFaceCover,
            (EquipmentRole.Earpiece, EquipmentRole.Eyewear)
            or (EquipmentRole.Eyewear, EquipmentRole.Earpiece) => EarpieceWithEyewear,
            (EquipmentRole.FaceCover, EquipmentRole.Eyewear)
            or (EquipmentRole.Eyewear, EquipmentRole.FaceCover) => FaceCoverWithEyewear,
            _ => false,
        };
    }
}

internal sealed record DiscoveryResult(
    IReadOnlyDictionary<MongoId, EquipmentRole> Roles,
    int DirectHeadwearCount,
    int DirectEarpieceCount,
    int DirectFaceCoverCount,
    int DirectEyewearCount,
    int HelmetAttachmentCount
);

internal sealed record PatchResult(
    int ChangedItems,
    int BlockFlagsCleared,
    int ConflictEntriesRemoved,
    int ClassifiedConflictsPreserved
);

public sealed record LessRestrictingHeadwearModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.musicmaniac.lessrestrictingheadwear";
    public string Name { get; init; } = "Less Restricting Headwear";
    public string Author { get; init; } = "MusicManiac";
    public List<string>? Contributors { get; init; } = ["MusicManiac"];
    public SemanticVersioning.Version Version { get; init; } = new("4.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; } = [];
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = [];
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}
