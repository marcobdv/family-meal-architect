using Api.Models;
using System.Text.Json;

namespace Api.Services;

/// <summary>
/// Orchestrates meal-plan generation as a multi-stage AI pipeline:
///   1. Selection      - pick meals that fit the family's constraints
///   2. Adaptation     - per-meal toddler version + adult booster
///   3. Orchestration  - equipment coordination / cascade cooking plan
/// Set OpenAI:MultiStage=false to collapse this into a single selection call
/// (cheaper, used for quick experimentation).
/// </summary>
public class AiMealPlanningService(IAiChatClient ai, IConfiguration config, ILogger<AiMealPlanningService> logger)
{
    private readonly IAiChatClient _ai = ai;
    private readonly IConfiguration _config = config;
    private readonly ILogger<AiMealPlanningService> _logger = logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private bool MultiStage => _config.GetValue("OpenAI:MultiStage", true);

    public async Task<MealPlanResponse> GenerateMealPlanAsync(MealPlanRequest request)
    {
        try
        {
            _logger.LogInformation("Generating {NumberOfMeals} meals (multi-stage={MultiStage})",
                request.NumberOfMeals, MultiStage);

            var input = 0;
            var output = 0;

            // Stage 1: selection
            var selectionPrompt = CreateSelectionPrompt(request.Family, request.NumberOfMeals, request.RecentMeals, request.RequestedMeals, includeAdaptations: !MultiStage);
            var selection = await _ai.CompleteAsync(selectionPrompt, jsonMode: true);
            input += selection.InputTokens; output += selection.OutputTokens;

            var meals = ParseMeals(selection.Text);
            if (meals.Count == 0)
            {
                return Fail("AI did not generate valid meal suggestions. Please try again.");
            }

            if (MultiStage)
            {
                // Stage 2: family adaptation
                var adaptPrompt = CreateAdaptationPrompt(request.Family, meals);
                var adapt = await _ai.CompleteAsync(adaptPrompt, jsonMode: true);
                input += adapt.InputTokens; output += adapt.OutputTokens;
                ApplyAdaptations(meals, adapt.Text);
            }

            // Stage 3: preparation options (2-3 methods per meal, recommended preselected)
            var prep = await _ai.CompleteAsync(CreatePreparationOptionsPrompt(request.Family, meals), jsonMode: true);
            input += prep.InputTokens; output += prep.OutputTokens;
            ApplyPreparationOptions(meals, prep.Text);

            // Stage 4: parallel/cascading cooking plan based on each meal's selected method
            var cook = await RunCookingPlanStageAsync(request.Family, meals);
            input += cook.Input; output += cook.Output;

            var plan = new MealPlan
            {
                Meals = meals,
                EquipmentCoordination = cook.Coordination,
                FamilyAdaptations = cook.FamilyAdaptations,
                EstimatedPrepTime = meals.Sum(m => m.SelectedPreparation?.PrepTimeMinutes ?? m.PrepTimeMinutes)
            };

            return new MealPlanResponse
            {
                Plan = plan,
                Success = true,
                TokensUsed = input + output,
                EstimatedCost = CostFor(input, output)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating meal plan");
            return Fail($"Failed to generate meal plan: {ex.Message}");
        }
    }

    /// <summary>Regenerate a single meal, avoiding the names of the other meals in the plan.</summary>
    public async Task<(Meal? Meal, int Tokens, decimal Cost, string? Error)> RegenerateMealAsync(
        FamilyProfile family, Meal current, IEnumerable<string> otherMealNames)
    {
        try
        {
            var prompt = CreateRegeneratePrompt(family, current, otherMealNames);
            var result = await _ai.CompleteAsync(prompt, jsonMode: true);
            var meal = ParseSingleMeal(result.Text);
            if (meal is null)
            {
                return (null, result.TotalTokens, CostFor(result.InputTokens, result.OutputTokens), "AI did not return a valid meal.");
            }
            return (meal, result.TotalTokens, CostFor(result.InputTokens, result.OutputTokens), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error regenerating meal");
            return (null, 0, 0m, ex.Message);
        }
    }

    /// <summary>Generate a consolidated, categorised shopping list for a plan.</summary>
    public async Task<(List<ShoppingItem>? Items, int Tokens, decimal Cost, string? Error)> GenerateShoppingListAsync(MealPlan plan)
    {
        try
        {
            var prompt = CreateShoppingListPrompt(plan);
            var result = await _ai.CompleteAsync(prompt, jsonMode: true);
            var items = ParseShoppingList(result.Text);
            if (items.Count == 0)
            {
                return (null, result.TotalTokens, CostFor(result.InputTokens, result.OutputTokens), "AI did not return a valid shopping list.");
            }
            return (items, result.TotalTokens, CostFor(result.InputTokens, result.OutputTokens), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating shopping list");
            return (null, 0, 0m, ex.Message);
        }
    }

    /// <summary>
    /// Suggest lightweight dinner IDEAS (name + blurb only) so the user can pick
    /// which ones to turn into full recipes — the interactive step before generation.
    /// </summary>
    public async Task<(List<MealIdea> Ideas, int Tokens, decimal Cost, string? Error)> SuggestMealsAsync(
        FamilyProfile family, int count, List<string> recentMeals)
    {
        try
        {
            var result = await _ai.CompleteAsync(CreateSuggestionsPrompt(family, count, recentMeals), jsonMode: true);
            var json = Deserialize<SuggestionsJsonResponse>(result.Text);
            var ideas = json?.Ideas?
                .Where(i => !string.IsNullOrWhiteSpace(i.Name))
                .Select(i => new MealIdea
                {
                    Name = i.Name,
                    Blurb = i.Blurb ?? "",
                    Cuisine = i.Cuisine ?? "",
                    MainIngredient = i.MainIngredient ?? ""
                }).ToList() ?? [];

            return ideas.Count == 0
                ? (ideas, result.TotalTokens, CostFor(result.InputTokens, result.OutputTokens), "AI did not return any ideas.")
                : (ideas, result.TotalTokens, CostFor(result.InputTokens, result.OutputTokens), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error suggesting meals");
            return ([], 0, 0m, ex.Message);
        }
    }

    /// <summary>
    /// Generate preparation options (2-3 methods, one recommended) for the given
    /// meals and set each meal's selected method to the recommended one. Used to
    /// backfill options for a freshly regenerated meal.
    /// </summary>
    public async Task<(int Tokens, decimal Cost, string? Error)> GeneratePreparationsAsync(FamilyProfile family, List<Meal> meals)
    {
        try
        {
            var result = await _ai.CompleteAsync(CreatePreparationOptionsPrompt(family, meals), jsonMode: true);
            ApplyPreparationOptions(meals, result.Text);
            return (result.TotalTokens, CostFor(result.InputTokens, result.OutputTokens), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating preparation options");
            foreach (var meal in meals)
            {
                EnsureFallbackPreparation(meal);
            }
            return (0, 0m, ex.Message);
        }
    }

    /// <summary>
    /// Build a parallel/cascading cooking plan for the meals using each meal's
    /// currently selected preparation method. Used both during generation and when
    /// the user changes their method choices.
    /// </summary>
    public async Task<(string Coordination, string FamilyAdaptations, int Tokens, decimal Cost, string? Error)> GenerateCookingPlanAsync(
        FamilyProfile family, List<Meal> meals)
    {
        try
        {
            var (coordination, familyAdaptations, input, output) = await RunCookingPlanStageAsync(family, meals);
            return (coordination, familyAdaptations, input + output, CostFor(input, output), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating cooking plan");
            return (string.Empty, string.Empty, 0, 0m, ex.Message);
        }
    }

    private async Task<(string Coordination, string FamilyAdaptations, int Input, int Output)> RunCookingPlanStageAsync(
        FamilyProfile family, List<Meal> meals)
    {
        var result = await _ai.CompleteAsync(CreateCookingPlanPrompt(family, meals), jsonMode: true);
        var parsed = Deserialize<OrchestrationJsonResponse>(result.Text);
        var coordination = !string.IsNullOrWhiteSpace(parsed?.EquipmentCoordination)
            ? parsed!.EquipmentCoordination
            : GenerateBasicEquipmentCoordination(meals);
        return (coordination, parsed?.FamilyAdaptations ?? string.Empty, result.InputTokens, result.OutputTokens);
    }

    public async Task<string> TestConnectionAsync()
    {
        try
        {
            var result = await _ai.CompleteAsync("Say 'OpenAI connection successful!' if you can read this.", jsonMode: false);
            return result.Text;
        }
        catch (Exception ex)
        {
            return $"Connection failed: {ex.Message}";
        }
    }

    // --- Prompts ---

    private static string CreateSuggestionsPrompt(FamilyProfile family, int count, List<string> recentMeals)
    {
        var activeBudget = family.WeekdayActiveCookingMinutes > 0 ? family.WeekdayActiveCookingMinutes : 15;
        return $@"You are a meal-prep chef brainstorming dinner ideas for a family. Suggest {count} varied dinner IDEAS only — NO full recipes yet. Each idea fits a ""big prep day, then a short (<= {activeBudget} min hands-on) weekday finish"" model and respects the family's tastes, equipment and dietary needs. Offer variety across cuisines and main ingredients.

Family Profile:
- Adult preferences: {Join(family.AdultPreferences)}
- Toddler preferences: {Join(family.ToddlerPreferences)}
- Dietary restrictions: {Join(family.DietaryRestrictions)}
- Available equipment: {Join(family.AvailableEquipment)}
- Recent meals to avoid repeating: {Join(recentMeals)}

Respond with ONLY a valid JSON object in this exact format:
{{
  ""ideas"": [
    {{
      ""name"": ""Dish name"",
      ""blurb"": ""One enticing sentence describing it and its prep-day/eat-day angle"",
      ""cuisine"": ""e.g. Italian"",
      ""mainIngredient"": ""e.g. Chicken""
    }}
  ]
}}

Rules:
- Return ONLY valid JSON.
- Exactly {count} distinct ideas, varied in cuisine and main ingredient.
- Respect all dietary restrictions and only assume the available equipment.
- Do NOT include ingredients lists or steps — ideas only.";
    }

    private static string CreateSelectionPrompt(FamilyProfile family, int numberOfMeals, List<string> recentMeals, List<string> requestedMeals, bool includeAdaptations)
    {
        var adaptationFields = includeAdaptations
            ? @",
      ""toddlerVersion"": ""How to adapt this meal for a toddler"",
      ""adultBooster"": ""How to enhance this meal for adults"""
            : "";

        var activeBudget = family.WeekdayActiveCookingMinutes > 0 ? family.WeekdayActiveCookingMinutes : 15;

        var chosen = requestedMeals is { Count: > 0 }
            ? $"\nThe user has CHOSEN these specific dishes — build one full recipe for EACH, keeping the name close to what they asked, in this order:\n{string.Join("\n", requestedMeals.Select(m => $"- {m}"))}\n"
            : "";

        return $@"You are a meal-prep chef. Design {numberOfMeals} RICH, high-quality dinner recipes built around a ""prep day does the heavy lifting, weekdays are quick"" model.{chosen}

The big effort happens once on PREP DAY (chopping, marinating, making sauces, browning, par-cooking, assembling, batch-cooking components) and is then stored. On each EAT DAY (a busy weekday) the meal must need only a SHORT FINISH of at most {activeBudget} minutes of ACTIVE hands-on cooking. Passive, unattended time does NOT count against that budget — e.g. ""bake at 200C for 30 min"" or ""simmer 20 min"" is fine as long as the hands-on part is tiny. The eat-day finish should add real quality (crisping in the oven, searing/frying, finishing a sauce, baking), NOT just microwaving.

Favour dishes that suit this split (components that store well then get a fresh finish: traybakes, braises, marinated proteins seared to order, par-cooked then roasted veg, assemble-then-bake gratins/pasta, curries/stews whose flavour improves, dumplings/patties cooked to order). Respect all dietary restrictions.

Family Profile:
- Adult preferences: {Join(family.AdultPreferences)}
- Toddler preferences: {Join(family.ToddlerPreferences)}
- Dietary restrictions: {Join(family.DietaryRestrictions)}
- Available equipment: {Join(family.AvailableEquipment)}
- Hands-on time available on PREP DAY: {family.PrepTimeMinutes} minutes
- Max ACTIVE hands-on cooking per EAT DAY: {activeBudget} minutes
- Recent meals to avoid repeating: {Join(recentMeals)}

Respond with ONLY a valid JSON object in this exact format:
{{
  ""meals"": [
    {{
      ""name"": ""Meal name here"",
      ""description"": ""Brief description (2-3 sentences)"",
      ""prepTimeMinutes"": 30,
      ""servings"": 4,
      ""requiredEquipment"": [""stovetop"", ""oven""],
      ""ingredients"": [
        {{ ""name"": ""Chicken thighs"", ""quantity"": ""800 g"" }},
        {{ ""name"": ""Olive oil"", ""quantity"": ""2 tbsp"" }}
      ],
      ""prepAhead"": ""What to do on prep day for this recipe: the make-ahead components, par-cooking, sauces and assembly (2-4 sentences)."",
      ""storage"": ""How to store the prepped components, e.g. Fridge up to 4 days; freezes well up to 2 months"",
      ""eatDay"": ""The short weekday finish (e.g. Bake at 200C for 25 min; while it bakes, wilt the spinach 3 min). Make clear which part is hands-on vs unattended."",
      ""eatDayActiveMinutes"": 10,
      ""eatDayTotalMinutes"": 30{adaptationFields}
    }}
  ]
}}

Rules:
- Return ONLY valid JSON, no markdown, no commentary.
- Generate exactly {numberOfMeals} recipes that fit the prep-day / quick-eat-day model.
- ""eatDayActiveMinutes"" MUST be <= {activeBudget}. ""eatDayTotalMinutes"" may be larger (it includes passive oven/simmer time).
- Put as much work as possible into ""prepAhead""; keep ""eatDay"" minimal but quality-adding.
- Set ""servings"" to the batch size (aim for 4-6) and size ingredient quantities to it.
- List ALL ingredients with realistic quantities and clear units (g, ml, tbsp, tsp, cups, cloves, whole items).
- Only reference equipment from the available list.
- Avoid repeating any of the recent meals.";
    }

    private static string CreateAdaptationPrompt(FamilyProfile family, List<Meal> meals)
    {
        var mealList = string.Join("\n", meals.Select(m => $"- {m.Name}: {m.Description}"));
        return $@"You are a family meal planning expert. For each meal below, describe how to adapt it for a toddler and how to enhance (""boost"") it for adults.

Family Profile:
- Adult preferences: {Join(family.AdultPreferences)}
- Toddler preferences: {Join(family.ToddlerPreferences)}
- Dietary restrictions: {Join(family.DietaryRestrictions)}

Meals:
{mealList}

Respond with ONLY a valid JSON object in this exact format:
{{
  ""adaptations"": [
    {{
      ""name"": ""Exact meal name from the list"",
      ""toddlerVersion"": ""1-2 sentences on adapting for a toddler"",
      ""adultBooster"": ""1-2 sentences on enhancing for adults""
    }}
  ]
}}

Rules:
- Return ONLY valid JSON.
- Include one entry per meal, using the exact meal names provided.
- Respect all dietary restrictions.";
    }

    private static string CreateCookingPlanPrompt(FamilyProfile family, List<Meal> meals)
    {
        var mealList = string.Join("\n\n", meals.Select(m =>
        {
            var sel = m.SelectedPreparation;
            var header = sel is null
                ? $"{m.Name} — makes {m.Servings} portions (equipment: {Join(m.RequiredEquipment)})"
                : $"{m.Name} — makes {m.Servings} portions — method: {sel.Name} (equipment: {Join(sel.Equipment)})";
            var ingredients = m.Ingredients is { Count: > 0 }
                ? "\n  ingredients: " + string.Join(", ", m.Ingredients.Select(i => $"{i.Quantity} {i.Name}".Trim()))
                : "";
            var prepAhead = string.IsNullOrWhiteSpace(m.PrepAhead) ? "" : $"\n  prep-ahead: {m.PrepAhead}";
            var storage = string.IsNullOrWhiteSpace(m.StorageInstructions) ? "" : $"\n  store as: {m.StorageInstructions}";
            var eatDay = string.IsNullOrWhiteSpace(m.EatDayInstructions) ? "" : $"\n  eat-day finish (do NOT do now): {m.EatDayInstructions}";
            return $"- {header}{ingredients}{prepAhead}{storage}{eatDay}";
        }));

        return $@"You are a meal-prep coach. Write a DETAILED, TIME-SEQUENCED plan for ONE PREP-DAY SESSION that does the HEAVY LIFTING for ALL the recipes below at once, then portions and stores the components. This is the big up-front cook so that on busy weekdays only a short finish remains. Do ONLY the prep-ahead work here — do NOT perform each recipe's eat-day finish (that happens later on the day it's eaten).

Available equipment: {Join(family.AvailableEquipment)}

Recipes to prep (with their prep-ahead work, ingredients, storage, and the eat-day finish that is LEFT FOR LATER):
{mealList}

Write the session as an elapsed-time timeline. Requirements:
- Every step starts with an elapsed-time marker like ""0:00"", ""0:15"", ""0:40"".
- Maximise overlap: start long passive cooks (oven roasts, simmering stocks/sauces, grains) FIRST, then do hands-on prep for other recipes while they cook. When things run at the same time say ""meanwhile"" / ""in parallel"" and on which equipment, so nothing is double-booked.
- Batch shared tasks across recipes (e.g. chop all onions together, roast trays together) and say so.
- Each step says exactly what to do, WITH ingredient quantities and on WHICH equipment.
- Be specific and detailed (aim for 10-16 steps); do not just summarise.
- END with explicit steps to COOL, PORTION into containers (note portions per recipe), LABEL, and STORE (fridge/freezer) per the storage notes.
- Finish with a final line ""Total prep-day session: ~N min"".

Respond with ONLY a valid JSON object in this exact format:
{{
  ""equipmentCoordination"": ""The full prep-day session timeline, one step per line, using \n between lines, following ALL the requirements above (including cool/portion/label/store at the end)."",
  ""familyAdaptations"": ""A short note on portioning toddler vs adult servings and a reminder of what short finish each recipe needs on its eat day.""
}}

Rules:
- Return ONLY valid JSON.
- Only do PREP-DAY work; leave each recipe's eat-day finish for later.
- Only reference equipment from the available list.";
    }

    private static string CreatePreparationOptionsPrompt(FamilyProfile family, List<Meal> meals)
    {
        var mealList = string.Join("\n", meals.Select(m => $"- {m.Name}: {m.Description}"));
        return $@"You are a cooking-methods expert. For EACH meal below, propose 2-3 distinct ways to prepare it (for example: stovetop, oven-baked, air-fryer, slow-cooker, grill/BBQ, sheet-pan), using ONLY the family's available equipment. Mark EXACTLY ONE method per meal as the recommended choice (best balance of ease and quality for this family).

Available equipment: {Join(family.AvailableEquipment)}
Total prep time available: {family.PrepTimeMinutes} minutes

Meals:
{mealList}

Respond with ONLY a valid JSON object in this exact format:
{{
  ""meals"": [
    {{
      ""name"": ""Exact meal name from the list"",
      ""methods"": [
        {{
          ""name"": ""Short method name (e.g. One-pan stovetop)"",
          ""description"": ""1-2 sentences on how this method works for this meal"",
          ""equipment"": [""stovetop""],
          ""prepTimeMinutes"": 30,
          ""recommended"": true
        }}
      ]
    }}
  ]
}}

Rules:
- Return ONLY valid JSON.
- Provide 2-3 methods per meal, with EXACTLY ONE recommended=true per meal.
- Only reference equipment from the available list.
- Use the exact meal names provided.
- Keep each method's prep time within the available prep time.";
    }

    private static string CreateRegeneratePrompt(FamilyProfile family, Meal current, IEnumerable<string> otherMealNames)
    {
        var activeBudget = family.WeekdayActiveCookingMinutes > 0 ? family.WeekdayActiveCookingMinutes : 15;

        return $@"You are a meal-prep chef. Suggest ONE new dinner to replace ""{current.Name}"" that fits a ""prep day does the heavy lifting, weekday finish is quick"" model: the bulk of the work is done ahead and stored, and the eat-day finish needs at most {activeBudget} minutes of ACTIVE hands-on cooking (passive oven/simmer time does not count). It must be different from these meals: {Join(otherMealNames.ToList())}.

Family Profile:
- Adult preferences: {Join(family.AdultPreferences)}
- Toddler preferences: {Join(family.ToddlerPreferences)}
- Dietary restrictions: {Join(family.DietaryRestrictions)}
- Available equipment: {Join(family.AvailableEquipment)}
- Hands-on time available on PREP DAY: {family.PrepTimeMinutes} minutes
- Max ACTIVE hands-on cooking per EAT DAY: {activeBudget} minutes

Respond with ONLY a valid JSON object in this exact format:
{{
  ""name"": ""Meal name"",
  ""description"": ""Brief description (2-3 sentences)"",
  ""prepTimeMinutes"": 30,
  ""servings"": 4,
  ""requiredEquipment"": [""stovetop""],
  ""ingredients"": [ {{ ""name"": ""Chicken breast"", ""quantity"": ""500 g"" }} ],
  ""prepAhead"": ""The make-ahead heavy lifting done on prep day."",
  ""storage"": ""How to store the prepped components, e.g. Fridge up to 4 days; freezes well"",
  ""eatDay"": ""The short weekday finish, noting hands-on vs unattended time."",
  ""eatDayActiveMinutes"": 10,
  ""eatDayTotalMinutes"": 30,
  ""toddlerVersion"": ""How to adapt for a toddler"",
  ""adultBooster"": ""How to enhance for adults""
}}

Rules:
- Return ONLY valid JSON.
- ""eatDayActiveMinutes"" MUST be <= {activeBudget}; ""eatDayTotalMinutes"" may be larger.
- Set ""servings"" to the batch size (4-6) and size ingredient quantities to it.
- Respect all dietary restrictions and only use available equipment.";
    }

    private static string CreateShoppingListPrompt(MealPlan plan)
    {
        var mealList = string.Join("\n", plan.Meals.Select(m =>
        {
            var ingredients = m.Ingredients is { Count: > 0 }
                ? string.Join(", ", m.Ingredients.Select(i => $"{i.Quantity} {i.Name}".Trim()))
                : m.Description;
            return $"- {m.Name}: {ingredients}";
        }));
        return $@"You are a grocery shopping assistant. Produce a consolidated shopping list covering all ingredients needed for these meals. Combine duplicate ingredients into a single line, ADDING UP their quantities into a sensible total, and group items by category.

Meals (with per-meal ingredient quantities):
{mealList}

Respond with ONLY a valid JSON object in this exact format:
{{
  ""items"": [
    {{ ""name"": ""Ingredient"", ""category"": ""Produce"", ""quantity"": ""2 lbs"" }}
  ]
}}

Rules:
- Return ONLY valid JSON.
- Use categories like Produce, Meat & Fish, Dairy, Pantry, Frozen, Bakery, Other.
- Merge duplicate ingredients across meals.";
    }

    // --- Parsing ---

    private List<Meal> ParseMeals(string aiResponse)
    {
        var json = Deserialize<MealPlanJsonResponse>(aiResponse);
        if (json?.Meals is not { Count: > 0 })
        {
            _logger.LogWarning("Selection response had no meals");
            return [];
        }

        return json.Meals.Select(MapMeal).ToList();
    }

    private static Meal MapMeal(MealJson m) => new()
    {
        Name = Coalesce(m.Name, "AI Suggested Meal"),
        Description = Coalesce(m.Description, "No description provided"),
        PrepTimeMinutes = m.PrepTimeMinutes > 0 ? m.PrepTimeMinutes : 30,
        ToddlerVersion = Coalesce(m.ToddlerVersion, "Suitable for toddler"),
        AdultBooster = Coalesce(m.AdultBooster, "No adult enhancements"),
        RequiredEquipment = m.RequiredEquipment?.ToList() ?? [],
        Ingredients = m.Ingredients?
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new Ingredient { Name = i.Name, Quantity = i.Quantity ?? "" })
            .ToList() ?? [],
        Servings = m.Servings > 0 ? m.Servings : 4,
        PrepAhead = Coalesce(m.PrepAhead, "Prepare and cook the components ahead, then store."),
        StorageInstructions = Coalesce(m.Storage, "Refrigerate the prepped components in airtight containers."),
        EatDayInstructions = Coalesce(m.EatDay, "Reheat until piping hot and serve."),
        EatDayActiveMinutes = m.EatDayActiveMinutes > 0 ? m.EatDayActiveMinutes : 10,
        EatDayTotalMinutes = m.EatDayTotalMinutes > 0 ? m.EatDayTotalMinutes : (m.EatDayActiveMinutes > 0 ? m.EatDayActiveMinutes : 10)
    };

    private void ApplyAdaptations(List<Meal> meals, string aiResponse)
    {
        var json = Deserialize<AdaptationJsonResponse>(aiResponse);
        if (json?.Adaptations is not { Count: > 0 })
        {
            _logger.LogWarning("Adaptation response had no entries; keeping defaults");
            return;
        }

        foreach (var adaptation in json.Adaptations)
        {
            var meal = meals.FirstOrDefault(m =>
                string.Equals(m.Name, adaptation.Name, StringComparison.OrdinalIgnoreCase));
            if (meal is null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(adaptation.ToddlerVersion))
            {
                meal.ToddlerVersion = adaptation.ToddlerVersion;
            }
            if (!string.IsNullOrWhiteSpace(adaptation.AdultBooster))
            {
                meal.AdultBooster = adaptation.AdultBooster;
            }
        }
    }

    private void ApplyPreparationOptions(List<Meal> meals, string aiResponse)
    {
        var json = Deserialize<PreparationOptionsJsonResponse>(aiResponse);
        if (json?.Meals is not { Count: > 0 })
        {
            _logger.LogWarning("Preparation response had no entries; using fallback methods");
            foreach (var meal in meals)
            {
                EnsureFallbackPreparation(meal);
            }
            return;
        }

        foreach (var meal in meals)
        {
            var match = json.Meals.FirstOrDefault(x =>
                string.Equals(x.Name, meal.Name, StringComparison.OrdinalIgnoreCase));

            if (match?.Methods is not { Count: > 0 })
            {
                EnsureFallbackPreparation(meal);
                continue;
            }

            meal.PreparationMethods = match.Methods.Select(mm => new PreparationMethod
            {
                Name = Coalesce(mm.Name, "Preparation"),
                Description = mm.Description ?? string.Empty,
                Equipment = mm.Equipment?.ToList() ?? [],
                PrepTimeMinutes = mm.PrepTimeMinutes > 0 ? mm.PrepTimeMinutes : meal.PrepTimeMinutes,
                Recommended = mm.Recommended
            }).ToList();

            // Guarantee exactly one recommended method and select it by default.
            var recommended = meal.PreparationMethods.FirstOrDefault(p => p.Recommended)
                              ?? meal.PreparationMethods[0];
            foreach (var p in meal.PreparationMethods)
            {
                p.Recommended = ReferenceEquals(p, recommended);
            }
            meal.SelectedPreparationId = recommended.Id;
        }
    }

    private static void EnsureFallbackPreparation(Meal meal)
    {
        if (meal.PreparationMethods.Count > 0)
        {
            if (string.IsNullOrEmpty(meal.SelectedPreparationId))
            {
                meal.SelectedPreparationId = meal.PreparationMethods[0].Id;
            }
            return;
        }

        var method = new PreparationMethod
        {
            Name = "Standard preparation",
            Description = meal.Description,
            Equipment = meal.RequiredEquipment.ToList(),
            PrepTimeMinutes = meal.PrepTimeMinutes,
            Recommended = true
        };
        meal.PreparationMethods = [method];
        meal.SelectedPreparationId = method.Id;
    }

    private Meal? ParseSingleMeal(string aiResponse)
    {
        var m = Deserialize<MealJson>(aiResponse);
        return m is null || string.IsNullOrWhiteSpace(m.Name) ? null : MapMeal(m);
    }

    private List<ShoppingItem> ParseShoppingList(string aiResponse)
    {
        var json = Deserialize<ShoppingListJsonResponse>(aiResponse);
        if (json?.Items is not { Count: > 0 })
        {
            return [];
        }
        return json.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new ShoppingItem
            {
                Name = i.Name,
                Category = Coalesce(i.Category, "Other"),
                Quantity = i.Quantity ?? ""
            }).ToList();
    }

    private T? Deserialize<T>(string aiResponse) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(StripCodeFences(aiResponse), _jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse AI JSON response of type {Type}", typeof(T).Name);
            return null;
        }
    }

    // --- Helpers ---

    private static string StripCodeFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```"))
        {
            return t;
        }
        var firstNewline = t.IndexOf('\n');
        if (firstNewline >= 0)
        {
            t = t[(firstNewline + 1)..];
        }
        if (t.EndsWith("```"))
        {
            t = t[..^3];
        }
        return t.Trim();
    }

    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value!;

    private static string Join(List<string>? values) =>
        values is { Count: > 0 } ? string.Join(", ", values) : "none";

    private static string GenerateBasicEquipmentCoordination(List<Meal> meals)
    {
        var equipment = meals.SelectMany(m => m.RequiredEquipment).Distinct().ToList();
        return equipment.Count == 0
            ? "Equipment coordination plan:\n• Use standard kitchen equipment as needed"
            : "Equipment coordination plan:\n" +
              $"• Required equipment: {string.Join(", ", equipment)}\n" +
              "• Plan equipment usage to avoid conflicts during meal prep";
    }

    private decimal CostFor(int inputTokens, int outputTokens)
    {
        // Pricing defaults to gpt-4o-mini rates (USD per 1M tokens). Input and output
        // are priced separately using the real per-call token counts.
        var inputCostPer1M = _config.GetValue<decimal?>("OpenAI:InputCostPer1MTokens") ?? 0.15m;
        var outputCostPer1M = _config.GetValue<decimal?>("OpenAI:OutputCostPer1MTokens") ?? 0.60m;
        return (inputTokens / 1_000_000m * inputCostPer1M) + (outputTokens / 1_000_000m * outputCostPer1M);
    }

    private static MealPlanResponse Fail(string message) => new()
    {
        Success = false,
        ErrorMessage = message
    };
}
