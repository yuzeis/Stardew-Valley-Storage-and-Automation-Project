using SVSAP.Models;

namespace SVSAP.Services;

internal static class ProcessingPatternCatalog
{
    public static IReadOnlyList<PatternData> CreateDefaults()
    {
        return new List<PatternData>
        {
            Machine("pattern.default.copperBar", "(BC)13", 30, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)378", 5), Item("(O)382", 1) }, new List<NetworkItemRequest> { Item("(O)334", 1) }),
            Machine("pattern.default.ironBar", "(BC)13", 120, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)380", 5), Item("(O)382", 1) }, new List<NetworkItemRequest> { Item("(O)335", 1) }),
            Machine("pattern.default.goldBar", "(BC)13", 300, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)384", 5), Item("(O)382", 1) }, new List<NetworkItemRequest> { Item("(O)336", 1) }),
            Machine("pattern.default.iridiumBar", "(BC)13", 480, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)386", 5), Item("(O)382", 1) }, new List<NetworkItemRequest> { Item("(O)337", 1) }),
            Machine("pattern.default.radioactiveBar", "(BC)13", 1000, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)909", 5), Item("(O)382", 1) }, new List<NetworkItemRequest> { Item("(O)910", 1) }),
            Machine("pattern.default.coal", "(BC)114", 30, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)388", 10) }, new List<NetworkItemRequest> { Item("(O)382", 1) }),
            Machine("pattern.name.cheese", "(BC)16", 200, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)184", 1) }, new List<NetworkItemRequest> { Item("(O)424", 1) }),
            Machine("pattern.name.goatCheese", "(BC)16", 200, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)436", 1) }, new List<NetworkItemRequest> { Item("(O)426", 1) }),
            Machine("pattern.name.mayonnaise", "(BC)24", 180, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)176", 1) }, new List<NetworkItemRequest> { Item("(O)306", 1) }),
            Machine("pattern.name.duckMayonnaise", "(BC)24", 180, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)442", 1) }, new List<NetworkItemRequest> { Item("(O)307", 1) }),
            Machine("pattern.name.voidMayonnaise", "(BC)24", 180, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)305", 1) }, new List<NetworkItemRequest> { Item("(O)308", 1) }),
            Machine("pattern.name.cloth", "(BC)17", 240, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)440", 1) }, new List<NetworkItemRequest> { Item("(O)428", 1) }),
            Machine("pattern.name.truffleOil", "(BC)19", 360, ProcessingSpeedClass.Fast, new List<NetworkItemRequest> { Item("(O)430", 1) }, new List<NetworkItemRequest> { Item("(O)432", 1) }),
            Machine("pattern.name.raisins", "(BC)Dehydrator", 1440, ProcessingSpeedClass.Medium, new List<NetworkItemRequest> { Item("(O)398", 5) }, new List<NetworkItemRequest> { Item("(O)Raisins", 1) }),
            Machine("pattern.default.driedFruit", "(BC)Dehydrator", 1440, ProcessingSpeedClass.Medium, new List<NetworkItemRequest> { Item("(O)613", 5) }, new List<NetworkItemRequest> { Item("(O)DriedFruit", 1) }),
            Machine("pattern.default.driedMushrooms", "(BC)Dehydrator", 1440, ProcessingSpeedClass.Medium, new List<NetworkItemRequest> { Item("(O)404", 5) }, new List<NetworkItemRequest> { Item("(O)DriedMushrooms", 1) }),
            Machine("pattern.default.smokedSardine", "(BC)FishSmoker", 50, ProcessingSpeedClass.Medium, new List<NetworkItemRequest> { Item("(O)131", 1), Item("(O)382", 1) }, new List<NetworkItemRequest> { Item("(O)SmokedFish", 1) }),
            Machine("pattern.default.pumpkinPickles", "(BC)15", 5200, ProcessingSpeedClass.Slow, new List<NetworkItemRequest> { Item("(O)276", 1) }, new List<NetworkItemRequest> { Item("(O)342", 1) }),
            Machine("pattern.default.ancientFruitWine", "(BC)12", 10000, ProcessingSpeedClass.Slow, new List<NetworkItemRequest> { Item("(O)454", 1) }, new List<NetworkItemRequest> { Item("(O)348", 1) }),
            Machine("pattern.default.starfruitWine", "(BC)12", 10000, ProcessingSpeedClass.Slow, new List<NetworkItemRequest> { Item("(O)268", 1) }, new List<NetworkItemRequest> { Item("(O)348", 1) })
        };
    }

    private static PatternData Machine(string nameKey, string machineQualifiedItemId, int minutes, ProcessingSpeedClass speedClass, List<NetworkItemRequest> inputs, List<NetworkItemRequest> outputs)
    {
        return PatternDisplayNames.Apply(new PatternData
        {
            Kind = PatternKind.Processing,
            MachineQualifiedItemId = machineQualifiedItemId,
            ProcessingMinutes = minutes,
            SpeedClass = speedClass,
            Inputs = inputs,
            Outputs = outputs
        }, nameKey);
    }

    private static NetworkItemRequest Item(string qualifiedItemId, int count)
    {
        return new NetworkItemRequest
        {
            QualifiedItemId = qualifiedItemId,
            Count = count
        };
    }

}
