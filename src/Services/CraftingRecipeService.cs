using SVSAP.Content;
using SVSAP.Models;
using StardewModdingAPI;
using StardewValley;

namespace SVSAP.Services;

internal sealed class CraftingRecipeService
{
    private readonly InventoryTransactionService transactionService;
    private readonly Func<ModConfig> getConfig;
    private readonly IMonitor monitor;

    public CraftingRecipeService(InventoryTransactionService transactionService, Func<ModConfig> getConfig, IMonitor monitor)
    {
        this.transactionService = transactionService;
        this.getConfig = getConfig;
        this.monitor = monitor;
    }

    public List<NetworkCraftingRecipe> GetKnownRecipes()
    {
        return this.GetKnownRecipesForPlayer(Game1.player);
    }

    public List<NetworkCraftingRecipe> GetKnownRecipesForPlayer(Farmer player)
    {
        var rawRecipes = Game1.content.Load<Dictionary<string, string>>("Data/CraftingRecipes");
        var recipes = new List<NetworkCraftingRecipe>();

        foreach (var pair in rawRecipes)
        {
            // Recognise the whole SVSAP family (Koizumi.SVSAP*) via the unique-id root without the
            // trailing dot, so SVSAPME addon recipes are also surfaced in the terminal — including the
            // few whose ingredient lists reference only vanilla items (e.g. CarbonRod, CopperCoil) and
            // therefore would not otherwise match the dotted prefix. Non-family keys never contain this
            // root, so there are no false positives. This does not use ModItemCatalog.Prefix, which the
            // trailing dot is still required for elsewhere.
            var isSVSAPRecipe = pair.Key.StartsWith(ModItemCatalog.UniqueId, StringComparison.Ordinal)
                || pair.Value.Contains(ModItemCatalog.UniqueId, StringComparison.Ordinal);
            if (isSVSAPRecipe && !this.MeetsSVSAPUnlockRequirement(player, pair.Key))
                continue;

            var isKnown = player.craftingRecipes.ContainsKey(pair.Key) || isSVSAPRecipe;

            if (!isKnown)
                continue;

            if (this.TryParseRecipe(pair.Key, pair.Value, out var recipe))
                recipes.Add(recipe);
        }

        return recipes
            .OrderBy(recipe => recipe.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public CraftingAvailability GetAvailability(NetworkData network, NetworkCraftingRecipe recipe, int batches, MaterialQualityStrategy qualityStrategy = MaterialQualityStrategy.LowQualityFirst)
    {
        var requests = Scale(recipe.Ingredients, batches);
        var ingredientLines = new List<string>();
        var ingredients = new List<CraftingIngredientAvailability>();
        var missing = new List<CraftingMissingIngredient>();
        foreach (var request in requests)
        {
            var available = this.transactionService.GetUnreservedCount(
                network,
                request,
                qualityStrategy: qualityStrategy,
                autoConsumableOnly: true);
            ingredientLines.Add(ItemDisplayService.FormatIngredientLine(request, available, request.Count));
            ingredients.Add(new CraftingIngredientAvailability
            {
                Request = request,
                AvailableCount = available,
                RequiredCount = request.Count
            });
            if (available < request.Count)
            {
                missing.Add(new CraftingMissingIngredient
                {
                    Request = request,
                    AvailableCount = available,
                    RequiredCount = request.Count
                });
            }
        }

        return new CraftingAvailability
        {
            CanCraft = missing.Count == 0,
            Ingredients = ingredients,
            IngredientLines = ingredientLines,
            MissingIngredients = missing,
            MissingLines = missing.Select(line => ItemDisplayService.FormatIngredientLine(line.Request, line.AvailableCount, line.RequiredCount)).ToList()
        };
    }

    public bool TryCraft(NetworkData network, NetworkCraftingRecipe recipe, int batches, MaterialQualityStrategy qualityStrategy, out string message)
    {
        return this.TryCraftForPlayer(network, Game1.player, recipe, batches, qualityStrategy, out message);
    }

    public bool TryCraftForPlayer(NetworkData network, Farmer player, NetworkCraftingRecipe recipe, int batches, MaterialQualityStrategy qualityStrategy, out string message)
    {
        var output = recipe.OutputPrototype.getOne();
        output.Stack = recipe.OutputCount * batches;

        if (!this.transactionService.CanAcceptNetworkItem(network, output, output.Stack))
        {
            message = ModText.Get("craftingTerminal.noOutputSpace");
            this.LogGameplay($"action=crafting_terminal_action result=fail player={DescribePlayer(player)} network={ShortId(network.NetworkId)} recipe={Quote(recipe.Name)} output={Quote(recipe.DisplayName)} batches={batches:N0} quality={qualityStrategy} reason={Quote(message)}");
            return false;
        }

        var requests = Scale(recipe.Ingredients, batches);
        if (!this.transactionService.TryConsumeIngredients(network, requests, out message, qualityStrategy: qualityStrategy))
        {
            this.LogGameplay($"action=crafting_terminal_action result=fail player={DescribePlayer(player)} network={ShortId(network.NetworkId)} recipe={Quote(recipe.Name)} output={Quote(recipe.DisplayName)} batches={batches:N0} quality={qualityStrategy} reason={Quote(message)}");
            return false;
        }

        if (this.transactionService.TryDepositItem(network, output, out var moved) && output.Stack <= 0)
        {
            this.transactionService.SaveNetworkState();
            message = ModText.Format("craftingTerminal.craftedToNetwork", recipe.DisplayName, moved);
            this.LogGameplay($"action=crafting_terminal_action result=success player={DescribePlayer(player)} network={ShortId(network.NetworkId)} recipe={Quote(recipe.Name)} output={Quote(recipe.DisplayName)} batches={batches:N0} crafted={moved:N0} destination=network");
            return true;
        }

        var dropped = output.Stack;
        Game1.createItemDebris(output, player.Position, player.FacingDirection, player.currentLocation);
        this.transactionService.SaveNetworkState();
        message = ModText.Format("craftingTerminal.craftedDropped", recipe.DisplayName, dropped);
        this.LogGameplay($"action=crafting_terminal_action result=success player={DescribePlayer(player)} network={ShortId(network.NetworkId)} recipe={Quote(recipe.Name)} output={Quote(recipe.DisplayName)} batches={batches:N0} crafted={dropped:N0} destination=debris reason=\"storage_changed\"");
        return true;
    }

    private bool MeetsSVSAPUnlockRequirement(Farmer player, string recipeName)
    {
        if (this.getConfig().IsDebugRecipeCostMode())
            return true;

        var requiredMiningLevel = ModItemCatalog.GetRequiredMiningLevel(recipeName);
        return requiredMiningLevel <= 0 || player.MiningLevel >= requiredMiningLevel;
    }

    private bool TryParseRecipe(string name, string raw, out NetworkCraftingRecipe recipe)
    {
        recipe = new NetworkCraftingRecipe();
        var parts = raw.Split('/');
        if (parts.Length < 4)
            return false;

        if (!bool.TryParse(parts[3], out var isBigCraftable))
            isBigCraftable = false;

        var ingredients = ParseIngredients(parts[0]);
        if (ingredients.Count == 0 && !this.getConfig().IsDebugRecipeCostMode())
            return false;

        if (!TryParseOutput(parts[2], isBigCraftable, out var outputId, out var outputCount))
            return false;

        Item output;
        try
        {
            output = ItemRegistry.Create(outputId);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Skipping unresolvable crafting recipe '{name}' output '{outputId}': {ex.Message}", LogLevel.Trace);
            return false;
        }

        output.Stack = outputCount;
        recipe = new NetworkCraftingRecipe
        {
            Name = name,
            DisplayName = output.DisplayName,
            OutputPrototype = output,
            OutputCount = outputCount,
            IsBigCraftable = isBigCraftable,
            Ingredients = ingredients
        };
        return true;
    }

    private static List<NetworkItemRequest> ParseIngredients(string raw)
    {
        var result = new List<NetworkItemRequest>();
        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < tokens.Length; i += 2)
        {
            if (!int.TryParse(tokens[i + 1], out var count) || count <= 0)
                continue;

            if (TryParseIngredientToken(tokens[i], out var request))
            {
                request.Count = count;
                result.Add(request);
            }
        }

        return result;
    }

    private static bool TryParseIngredientToken(string token, out NetworkItemRequest request)
    {
        request = new NetworkItemRequest();
        if (token.StartsWith("(", StringComparison.Ordinal))
        {
            request.QualifiedItemId = token;
            return true;
        }

        if (!int.TryParse(token, out var id))
            return false;

        if (id < 0)
            request.Category = id;
        else
            request.QualifiedItemId = "(O)" + id;

        return true;
    }

    private static bool TryParseOutput(string raw, bool isBigCraftable, out string outputId, out int outputCount)
    {
        outputId = string.Empty;
        outputCount = 1;

        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        outputId = NormalizeOutputId(tokens[0], isBigCraftable);
        if (tokens.Length > 1 && int.TryParse(tokens[1], out var parsedCount) && parsedCount > 0)
            outputCount = parsedCount;

        return !string.IsNullOrWhiteSpace(outputId);
    }

    private static string NormalizeOutputId(string token, bool isBigCraftable)
    {
        if (token.StartsWith("(", StringComparison.Ordinal))
            return token;

        if (int.TryParse(token, out var id))
            return (isBigCraftable ? "(BC)" : "(O)") + id;

        return token;
    }

    private static List<NetworkItemRequest> Scale(IEnumerable<NetworkItemRequest> ingredients, int batches)
    {
        return ingredients
            .Select(ingredient => new NetworkItemRequest
            {
                QualifiedItemId = ingredient.QualifiedItemId,
                Category = ingredient.Category,
                SerializedItemPrototype = ingredient.SerializedItemPrototype,
                PreservedParentQualifiedItemId = ingredient.PreservedParentQualifiedItemId,
                Count = ingredient.Count * batches
            })
            .ToList();
    }

    private void LogGameplay(string message)
    {
        if (this.getConfig().DetailedGameplayLogs)
            this.monitor.Log("SVSAP_GAMELOG " + message, LogLevel.Info);
    }

    private static string DescribePlayer(Farmer player)
    {
        return $"{Quote(player.Name)}#{player.UniqueMultiplayerID}";
    }

    private static string ShortId(Guid id)
    {
        var raw = id.ToString("N");
        return raw.Length <= 8 ? raw : raw[..8];
    }

    private static string Quote(string? value)
    {
        return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
