using SVSAP.Content;
using SVSAP.Models;
using StardewValley;

namespace SVSAP.Services;

internal sealed class PatternEncodingService
{
    private const int FishCategory = -4;
    private const int FruitCategory = -79;
    private const int VegetableCategory = -75;
    private const string KegQualifiedItemId = "(BC)12";
    private const string PreservesJarQualifiedItemId = "(BC)15";
    private const string CheesePressQualifiedItemId = "(BC)16";
    private const string LoomQualifiedItemId = "(BC)17";
    private const string OilMakerQualifiedItemId = "(BC)19";
    private const string MayonnaiseMachineQualifiedItemId = "(BC)24";
    private const string DehydratorQualifiedItemId = "(BC)Dehydrator";
    private const string FishSmokerQualifiedItemId = "(BC)FishSmoker";
    private const string OilQualifiedItemId = "(O)247";
    private const string WineQualifiedItemId = "(O)348";
    private const string PaleAleQualifiedItemId = "(O)303";
    private const string BeerQualifiedItemId = "(O)346";
    private const string CoffeeQualifiedItemId = "(O)395";
    private const string MeadQualifiedItemId = "(O)459";
    private const string GreenTeaQualifiedItemId = "(O)614";
    private const string JellyQualifiedItemId = "(O)344";
    private const string JuiceQualifiedItemId = "(O)350";
    private const string PicklesQualifiedItemId = "(O)342";
    private const string MayonnaiseQualifiedItemId = "(O)306";
    private const string DuckMayonnaiseQualifiedItemId = "(O)307";
    private const string VoidMayonnaiseQualifiedItemId = "(O)308";
    private const string CheeseQualifiedItemId = "(O)424";
    private const string GoatCheeseQualifiedItemId = "(O)426";
    private const string ClothQualifiedItemId = "(O)428";
    private const string TruffleOilQualifiedItemId = "(O)432";
    private const string DriedFruitQualifiedItemId = "(O)DriedFruit";
    private const string DriedMushroomsQualifiedItemId = "(O)DriedMushrooms";
    private const string RaisinsQualifiedItemId = "(O)Raisins";
    private const string SmokedFishQualifiedItemId = "(O)SmokedFish";
    private const string DinosaurMayonnaiseQualifiedItemId = "(O)807";
    private const string CoalQualifiedItemId = "(O)382";
    private const string HopsQualifiedItemId = "(O)304";
    private const string WheatQualifiedItemId = "(O)262";
    private const string CoffeeBeanQualifiedItemId = "(O)433";
    private const string HoneyQualifiedItemId = "(O)340";
    private const string TeaLeavesQualifiedItemId = "(O)815";
    private const string GrapeQualifiedItemId = "(O)398";
    private const string CornQualifiedItemId = "(O)270";
    private const string SunflowerQualifiedItemId = "(O)421";
    private const string SunflowerSeedsQualifiedItemId = "(O)431";
    private const string TruffleQualifiedItemId = "(O)430";

    private static readonly HashSet<string> MushroomQualifiedItemIds = new(StringComparer.Ordinal)
    {
        "(O)257",
        "(O)281",
        "(O)404",
        "(O)420",
        "(O)422",
        "(O)851"
    };

    private readonly CraftingRecipeService craftingRecipeService;
    private readonly Func<ModConfig> getConfig;

    public PatternEncodingService(CraftingRecipeService craftingRecipeService, Func<ModConfig> getConfig)
    {
        this.craftingRecipeService = craftingRecipeService;
        this.getConfig = getConfig;
    }

    public List<PatternData> GetCraftingPatterns()
    {
        return this.craftingRecipeService.GetKnownRecipes()
            .Select(recipe => new PatternData
            {
                Kind = PatternKind.Crafting,
                DisplayName = recipe.DisplayName,
                Inputs = recipe.Ingredients,
                Outputs = new List<NetworkItemRequest>
                {
                    new()
                    {
                        QualifiedItemId = recipe.OutputPrototype.QualifiedItemId,
                        Count = recipe.OutputCount
                    }
                },
                ProcessingMinutes = 0,
                SpeedClass = ProcessingSpeedClass.Fast
            })
            .ToList();
    }

    public List<PatternData> GetProcessingPatterns()
    {
        if (!this.getConfig().EnableProcessingPatterns)
            return new List<PatternData>();

        var patterns = ProcessingPatternCatalog.CreateDefaults().ToList();
        var held = Game1.player.CurrentItem;
        if (held is StardewValley.Object obj && obj.Category == FishCategory)
        {
            var heldFishPattern = new PatternData
            {
                Kind = PatternKind.Processing,
                DisplayName = $"熏{held.DisplayName}",
                MachineQualifiedItemId = FishSmokerQualifiedItemId,
                ProcessingMinutes = 50,
                SpeedClass = ProcessingSpeedClass.Medium,
                Inputs = new List<NetworkItemRequest>
                {
                    new() { QualifiedItemId = held.QualifiedItemId, Count = 1 },
                    new() { QualifiedItemId = CoalQualifiedItemId, Count = 1 }
                },
                Outputs = new List<NetworkItemRequest>
                {
                    new() { QualifiedItemId = SmokedFishQualifiedItemId, Count = 1 }
                }
            };

            patterns.RemoveAll(pattern => pattern.MachineQualifiedItemId == FishSmokerQualifiedItemId
                && pattern.Inputs.Any(input => input.QualifiedItemId == held.QualifiedItemId));
            patterns.Insert(0, heldFishPattern);
        }

        if (held is StardewValley.Object heldObject)
            this.AddHeldItemProcessingPatterns(patterns, heldObject);

        return patterns;
    }

    private void AddHeldItemProcessingPatterns(List<PatternData> patterns, StardewValley.Object held)
    {
        if (held.Stack <= 0)
            return;

        if (this.TryAddAnimalProductMachinePattern(patterns, held))
            return;

        this.TryAddOilMakerPattern(patterns, held);

        if (this.TryAddSpecialKegPattern(patterns, held))
            return;

        if (held.Category == FruitCategory)
        {
            this.InsertHeldPattern(
                patterns,
                $"{held.DisplayName}酒",
                KegQualifiedItemId,
                10000,
                ProcessingSpeedClass.Slow,
                held.QualifiedItemId,
                1,
                WineQualifiedItemId);
            this.InsertHeldPattern(
                patterns,
                $"{held.DisplayName}果酱",
                PreservesJarQualifiedItemId,
                4000,
                ProcessingSpeedClass.Slow,
                held.QualifiedItemId,
                1,
                JellyQualifiedItemId);
            this.InsertHeldPattern(
                patterns,
                held.QualifiedItemId == GrapeQualifiedItemId ? "葡萄干" : $"{held.DisplayName}干",
                DehydratorQualifiedItemId,
                1440,
                ProcessingSpeedClass.Medium,
                held.QualifiedItemId,
                5,
                held.QualifiedItemId == GrapeQualifiedItemId ? RaisinsQualifiedItemId : DriedFruitQualifiedItemId);
            return;
        }

        if (held.Category == VegetableCategory)
        {
            this.InsertHeldPattern(
                patterns,
                $"{held.DisplayName}汁",
                KegQualifiedItemId,
                6000,
                ProcessingSpeedClass.Slow,
                held.QualifiedItemId,
                1,
                JuiceQualifiedItemId);
            this.InsertHeldPattern(
                patterns,
                $"{held.DisplayName}腌菜",
                PreservesJarQualifiedItemId,
                4000,
                ProcessingSpeedClass.Slow,
                held.QualifiedItemId,
                1,
                PicklesQualifiedItemId);
            return;
        }

        if (MushroomQualifiedItemIds.Contains(held.QualifiedItemId))
        {
            this.InsertHeldPattern(
                patterns,
                $"{held.DisplayName}干",
                DehydratorQualifiedItemId,
                1440,
                ProcessingSpeedClass.Medium,
                held.QualifiedItemId,
                5,
                DriedMushroomsQualifiedItemId);
        }
    }

    private bool TryAddAnimalProductMachinePattern(List<PatternData> patterns, StardewValley.Object held)
    {
        return held.QualifiedItemId switch
        {
            "(O)184" => this.InsertHeldPatternAndReturn(patterns, "奶酪", CheesePressQualifiedItemId, 200, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, CheeseQualifiedItemId),
            "(O)186" => this.InsertHeldPatternAndReturn(patterns, "奶酪", CheesePressQualifiedItemId, 200, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, CheeseQualifiedItemId),
            "(O)436" => this.InsertHeldPatternAndReturn(patterns, "山羊奶酪", CheesePressQualifiedItemId, 200, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, GoatCheeseQualifiedItemId),
            "(O)438" => this.InsertHeldPatternAndReturn(patterns, "山羊奶酪", CheesePressQualifiedItemId, 200, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, GoatCheeseQualifiedItemId),
            "(O)176" => this.InsertHeldPatternAndReturn(patterns, "蛋黄酱", MayonnaiseMachineQualifiedItemId, 180, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, MayonnaiseQualifiedItemId),
            "(O)180" => this.InsertHeldPatternAndReturn(patterns, "蛋黄酱", MayonnaiseMachineQualifiedItemId, 180, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, MayonnaiseQualifiedItemId),
            "(O)174" => this.InsertHeldPatternAndReturn(patterns, "蛋黄酱", MayonnaiseMachineQualifiedItemId, 180, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, MayonnaiseQualifiedItemId),
            "(O)182" => this.InsertHeldPatternAndReturn(patterns, "蛋黄酱", MayonnaiseMachineQualifiedItemId, 180, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, MayonnaiseQualifiedItemId),
            "(O)442" => this.InsertHeldPatternAndReturn(patterns, "鸭蛋黄酱", MayonnaiseMachineQualifiedItemId, 180, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, DuckMayonnaiseQualifiedItemId),
            "(O)305" => this.InsertHeldPatternAndReturn(patterns, "虚空蛋黄酱", MayonnaiseMachineQualifiedItemId, 180, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, VoidMayonnaiseQualifiedItemId),
            "(O)107" => this.InsertHeldPatternAndReturn(patterns, "恐龙蛋黄酱", MayonnaiseMachineQualifiedItemId, 180, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, DinosaurMayonnaiseQualifiedItemId),
            "(O)440" => this.InsertHeldPatternAndReturn(patterns, "布料", LoomQualifiedItemId, 240, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, ClothQualifiedItemId),
            _ => false
        };
    }

    private bool TryAddOilMakerPattern(List<PatternData> patterns, StardewValley.Object held)
    {
        return held.QualifiedItemId switch
        {
            TruffleQualifiedItemId => this.InsertHeldPatternAndReturn(patterns, "松露油", OilMakerQualifiedItemId, 360, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, TruffleOilQualifiedItemId),
            CornQualifiedItemId => this.InsertHeldPatternAndReturn(patterns, "油", OilMakerQualifiedItemId, 1000, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, OilQualifiedItemId),
            SunflowerQualifiedItemId => this.InsertHeldPatternAndReturn(patterns, "油", OilMakerQualifiedItemId, 60, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, OilQualifiedItemId),
            SunflowerSeedsQualifiedItemId => this.InsertHeldPatternAndReturn(patterns, "油", OilMakerQualifiedItemId, 3200, ProcessingSpeedClass.Slow, held.QualifiedItemId, 1, OilQualifiedItemId),
            _ => false
        };
    }

    private bool TryAddSpecialKegPattern(List<PatternData> patterns, StardewValley.Object held)
    {
        return held.QualifiedItemId switch
        {
            HopsQualifiedItemId => this.InsertHeldPatternAndReturn(patterns, "淡啤酒", KegQualifiedItemId, 2250, ProcessingSpeedClass.Slow, held.QualifiedItemId, 1, PaleAleQualifiedItemId),
            WheatQualifiedItemId => this.InsertHeldPatternAndReturn(patterns, "啤酒", KegQualifiedItemId, 1750, ProcessingSpeedClass.Slow, held.QualifiedItemId, 1, BeerQualifiedItemId),
            CoffeeBeanQualifiedItemId => this.InsertHeldPatternAndReturn(patterns, "咖啡", KegQualifiedItemId, 120, ProcessingSpeedClass.Fast, held.QualifiedItemId, 5, CoffeeQualifiedItemId),
            HoneyQualifiedItemId => this.InsertHeldPatternAndReturn(patterns, "蜂蜜酒", KegQualifiedItemId, 600, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, MeadQualifiedItemId),
            TeaLeavesQualifiedItemId => this.InsertHeldPatternAndReturn(patterns, "绿茶", KegQualifiedItemId, 180, ProcessingSpeedClass.Fast, held.QualifiedItemId, 1, GreenTeaQualifiedItemId),
            _ => false
        };
    }

    private bool InsertHeldPatternAndReturn(
        List<PatternData> patterns,
        string displayName,
        string machineQualifiedItemId,
        int processingMinutes,
        ProcessingSpeedClass speedClass,
        string inputQualifiedItemId,
        int inputCount,
        string outputQualifiedItemId)
    {
        this.InsertHeldPattern(patterns, displayName, machineQualifiedItemId, processingMinutes, speedClass, inputQualifiedItemId, inputCount, outputQualifiedItemId);
        return true;
    }

    private void InsertHeldPattern(
        List<PatternData> patterns,
        string displayName,
        string machineQualifiedItemId,
        int processingMinutes,
        ProcessingSpeedClass speedClass,
        string inputQualifiedItemId,
        int inputCount,
        string outputQualifiedItemId)
    {
        patterns.RemoveAll(pattern => pattern.MachineQualifiedItemId == machineQualifiedItemId
            && pattern.Inputs.Any(input => input.QualifiedItemId == inputQualifiedItemId)
            && pattern.Outputs.Any(output => output.QualifiedItemId == outputQualifiedItemId));

        patterns.Insert(
            0,
            new PatternData
            {
                Kind = PatternKind.Processing,
                DisplayName = displayName,
                MachineQualifiedItemId = machineQualifiedItemId,
                ProcessingMinutes = processingMinutes,
                SpeedClass = speedClass,
                Inputs = new List<NetworkItemRequest>
                {
                    new() { QualifiedItemId = inputQualifiedItemId, Count = inputCount }
                },
                Outputs = new List<NetworkItemRequest>
                {
                    new() { QualifiedItemId = outputQualifiedItemId, Count = 1 }
                }
            });
    }

    public bool TryEncode(PatternData data, out string message)
    {
        var blank = Game1.player.Items.FirstOrDefault(item => item?.QualifiedItemId == "(O)" + ModItemCatalog.BlankPattern);
        if (blank is null)
        {
            message = "背包里没有空白样板。";
            return false;
        }

        var encoded = PatternCodec.CreatePatternItem(data);
        if (!Game1.player.couldInventoryAcceptThisItem(encoded))
        {
            message = "背包没有空间接收已编码样板。";
            return false;
        }

        blank.Stack -= 1;
        if (blank.Stack <= 0)
            Game1.player.removeItemFromInventory(blank);

        Game1.player.addItemToInventoryBool(encoded);
        message = $"已写入样板：{data.DisplayName}。";
        return true;
    }
}
