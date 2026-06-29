using Api.Models;
using Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests;

public class AiMealPlanningServiceTests
{
    private static IConfiguration Config(bool multiStage) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:MultiStage"] = multiStage ? "true" : "false"
            }).Build();

    private static AiMealPlanningService Service(bool multiStage, FakeAiChatClient ai) =>
        new(ai, Config(multiStage), NullLogger<AiMealPlanningService>.Instance);

    private static MealPlanRequest Request() => new()
    {
        Family = new FamilyProfile { AvailableEquipment = ["stovetop"], PrepTimeMinutes = 60 },
        NumberOfMeals = 1,
        RecentMeals = []
    };

    [Fact]
    public async Task MultiStage_AppliesAdaptationsAndOrchestration()
    {
        var ai = new FakeAiChatClient(
            """{"meals":[{"name":"Tacos","description":"Yum","prepTimeMinutes":25,"servings":6,"requiredEquipment":["stovetop"],"ingredients":[{"name":"Tortillas","quantity":"8"},{"name":"Ground beef","quantity":"500 g"}],"prepAhead":"Brown the beef and make the filling","storage":"Fridge 4 days","eatDay":"Warm filling and assemble","eatDayActiveMinutes":8,"eatDayTotalMinutes":12}]}""",
            """{"adaptations":[{"name":"Tacos","toddlerVersion":"Plain tortilla","adultBooster":"Add hot sauce"}]}""",
            """{"meals":[{"name":"Tacos","methods":[{"name":"Stovetop","description":"Pan-fry","equipment":["stovetop"],"prepTimeMinutes":25,"recommended":true},{"name":"Oven","description":"Bake","equipment":["oven"],"prepTimeMinutes":35,"recommended":false}]}]}""",
            """{"equipmentCoordination":"Cook on stovetop","familyAdaptations":"Mild base, spice up adult"}""");

        var result = await Service(true, ai).GenerateMealPlanAsync(Request());

        Assert.True(result.Success);
        Assert.Equal(4, ai.CallCount); // selection + adaptation + preparation + cooking plan
        var meal = Assert.Single(result.Plan.Meals);
        Assert.Equal("Tacos", meal.Name);
        Assert.Equal("Plain tortilla", meal.ToddlerVersion);
        Assert.Equal("Add hot sauce", meal.AdultBooster);
        // Ingredients with quantities parsed from the selection stage.
        Assert.Equal(2, meal.Ingredients.Count);
        Assert.Contains(meal.Ingredients, i => i.Name == "Ground beef" && i.Quantity == "500 g");
        // Meal-prep metadata parsed (prep-day vs eat-day split).
        Assert.Equal(6, meal.Servings);
        Assert.Equal("Brown the beef and make the filling", meal.PrepAhead);
        Assert.Equal("Fridge 4 days", meal.StorageInstructions);
        Assert.Equal("Warm filling and assemble", meal.EatDayInstructions);
        Assert.Equal(8, meal.EatDayActiveMinutes);
        Assert.Equal(12, meal.EatDayTotalMinutes);
        // Preparation options: 2 methods, exactly one recommended, recommended one selected.
        Assert.Equal(2, meal.PreparationMethods.Count);
        Assert.Single(meal.PreparationMethods, m => m.Recommended);
        Assert.Equal("Stovetop", meal.SelectedPreparation!.Name);
        // Cascading cooking plan built from the selected method.
        Assert.Equal("Cook on stovetop", result.Plan.EquipmentCoordination);
        Assert.Equal("Mild base, spice up adult", result.Plan.FamilyAdaptations);
        Assert.Equal(600, result.TokensUsed); // 4 calls * (100+50)
        // Cost uses real input/output split: 4*100 input @0.15/M + 4*50 output @0.60/M.
        var expected = (400 / 1_000_000m * 0.15m) + (200 / 1_000_000m * 0.60m);
        Assert.Equal(expected, result.EstimatedCost);
    }

    [Fact]
    public async Task SingleStage_SkipsAdaptationButStillPreparesAndPlans()
    {
        var ai = new FakeAiChatClient(
            """{"meals":[{"name":"Pasta","description":"Cheesy","prepTimeMinutes":20,"requiredEquipment":["stovetop"],"toddlerVersion":"No sauce","adultBooster":"Chili flakes"}]}""",
            """{"meals":[{"name":"Pasta","methods":[{"name":"Stovetop","description":"Boil","equipment":["stovetop"],"prepTimeMinutes":20,"recommended":true}]}]}""",
            """{"equipmentCoordination":"Boil water, cook pasta","familyAdaptations":"Reserve plain portion"}""");

        var result = await Service(false, ai).GenerateMealPlanAsync(Request());

        Assert.True(result.Success);
        Assert.Equal(3, ai.CallCount); // selection + preparation + cooking plan (no adaptation stage)
        var meal = Assert.Single(result.Plan.Meals);
        Assert.Equal("No sauce", meal.ToddlerVersion); // adaptation came from the selection call
        Assert.Equal("Chili flakes", meal.AdultBooster);
        Assert.Single(meal.PreparationMethods);
        Assert.Equal("Boil water, cook pasta", result.Plan.EquipmentCoordination);
    }

    [Fact]
    public async Task PreparationOptions_FallBackWhenMissing()
    {
        // Selection returns a meal, but the preparation stage returns nothing usable
        // and the cooking-plan stage returns nothing usable -> graceful fallbacks.
        var ai = new FakeAiChatClient(
            """{"meals":[{"name":"Curry","description":"Spiced","prepTimeMinutes":40,"requiredEquipment":["stovetop"],"toddlerVersion":"Mild","adultBooster":"Extra chili"}]}""",
            "{}",   // preparation: no meals -> fallback "Standard preparation"
            "{}");  // cooking plan: no coordination -> basic fallback

        var result = await Service(false, ai).GenerateMealPlanAsync(Request());

        Assert.True(result.Success);
        var meal = Assert.Single(result.Plan.Meals);
        var method = Assert.Single(meal.PreparationMethods);
        Assert.Equal("Standard preparation", method.Name);
        Assert.Equal(method.Id, meal.SelectedPreparationId);
        Assert.False(string.IsNullOrWhiteSpace(result.Plan.EquipmentCoordination));
    }

    [Fact]
    public async Task GenerateCookingPlan_UsesSelectedMethods()
    {
        var ai = new FakeAiChatClient(
            """{"equipmentCoordination":"Start rice cooker, then wok","familyAdaptations":"Hold chili for toddler"}""");
        var family = new FamilyProfile { AvailableEquipment = ["wok", "rice cooker"], PrepTimeMinutes = 45 };
        var meals = new List<Meal>
        {
            new()
            {
                Name = "Stir-fry",
                PreparationMethods = [new PreparationMethod { Name = "Wok", Equipment = ["wok"], PrepTimeMinutes = 20, Recommended = true }]
            }
        };
        meals[0].SelectedPreparationId = meals[0].PreparationMethods[0].Id;

        var (coordination, family2, _, _, error) = await Service(true, ai).GenerateCookingPlanAsync(family, meals);

        Assert.Null(error);
        Assert.Equal("Start rice cooker, then wok", coordination);
        Assert.Equal("Hold chili for toddler", family2);
    }

    [Fact]
    public async Task GeneratePreparations_SetsMethodsAndSelectsRecommended()
    {
        var ai = new FakeAiChatClient(
            """{"meals":[{"name":"Soup","methods":[{"name":"Stovetop","description":"Simmer","equipment":["stovetop"],"prepTimeMinutes":30,"recommended":false},{"name":"Slow cooker","description":"Low and slow","equipment":["slow cooker"],"prepTimeMinutes":240,"recommended":true}]}]}""");
        var meals = new List<Meal> { new() { Name = "Soup", PrepTimeMinutes = 30 } };

        var (_, _, error) = await Service(true, ai).GeneratePreparationsAsync(new FamilyProfile(), meals);

        Assert.Null(error);
        Assert.Equal(2, meals[0].PreparationMethods.Count);
        Assert.Equal("Slow cooker", meals[0].SelectedPreparation!.Name); // recommended one selected
    }

    [Fact]
    public async Task InvalidJson_ReturnsFailure()
    {
        var ai = new FakeAiChatClient("this is not json");
        var result = await Service(false, ai).GenerateMealPlanAsync(Request());

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public async Task StripsMarkdownCodeFences()
    {
        var ai = new FakeAiChatClient(
            "```json\n{\"meals\":[{\"name\":\"Soup\",\"description\":\"Warm\",\"prepTimeMinutes\":15,\"requiredEquipment\":[]}]}\n```");
        var result = await Service(false, ai).GenerateMealPlanAsync(Request());

        Assert.True(result.Success);
        Assert.Equal("Soup", result.Plan.Meals[0].Name);
    }

    [Fact]
    public async Task SuggestMeals_ParsesIdeas()
    {
        var ai = new FakeAiChatClient(
            """{"ideas":[{"name":"Lasagne","blurb":"Assemble ahead, bake to serve","cuisine":"Italian","mainIngredient":"Beef"},{"name":"Thai Green Curry","blurb":"Make the paste ahead","cuisine":"Thai","mainIngredient":"Chicken"}]}""");

        var (ideas, _, _, error) = await Service(true, ai).SuggestMealsAsync(new FamilyProfile(), 2, []);

        Assert.Null(error);
        Assert.Equal(2, ideas.Count);
        Assert.Equal("Lasagne", ideas[0].Name);
        Assert.Equal("Italian", ideas[0].Cuisine);
        Assert.Equal("Chicken", ideas[1].MainIngredient);
    }

    [Fact]
    public async Task RequestedMeals_AreInjectedIntoSelectionPrompt()
    {
        var ai = new FakeAiChatClient(
            """{"meals":[{"name":"Lasagne","description":"x","prepTimeMinutes":30,"servings":4,"requiredEquipment":["oven"],"ingredients":[{"name":"Pasta","quantity":"400 g"}],"prepAhead":"assemble","storage":"fridge","eatDay":"bake","eatDayActiveMinutes":5,"eatDayTotalMinutes":40,"toddlerVersion":"plain","adultBooster":"chili"}]}""",
            """{"adaptations":[]}""",
            """{"meals":[{"name":"Lasagne","methods":[{"name":"Oven","description":"bake","equipment":["oven"],"prepTimeMinutes":40,"recommended":true}]}]}""",
            """{"equipmentCoordination":"plan","familyAdaptations":"note"}""");

        var request = new MealPlanRequest
        {
            Family = new FamilyProfile { AvailableEquipment = ["oven"], PrepTimeMinutes = 90 },
            NumberOfMeals = 1,
            RequestedMeals = ["Lasagne"]
        };
        var result = await Service(true, ai).GenerateMealPlanAsync(request);

        Assert.True(result.Success);
        Assert.Equal("Lasagne", Assert.Single(result.Plan.Meals).Name);
        // The chosen dish must be passed into the selection prompt (first AI call).
        Assert.Contains("Lasagne", ai.Prompts[0]);
    }

    [Fact]
    public async Task GenerateShoppingList_ParsesItems()
    {
        var ai = new FakeAiChatClient(
            """{"items":[{"name":"Tortillas","category":"Bakery","quantity":"8"},{"name":"Beef","category":"Meat & Fish","quantity":"500g"}]}""");
        var plan = new MealPlan { Meals = [new Meal { Name = "Tacos", Description = "Yum" }] };

        var (items, _, _, error) = await Service(true, ai).GenerateShoppingListAsync(plan);

        Assert.Null(error);
        Assert.NotNull(items);
        Assert.Equal(2, items!.Count);
        Assert.Contains(items, i => i.Name == "Beef" && i.Category == "Meat & Fish");
    }

    [Fact]
    public async Task RegenerateMeal_ReturnsNewMeal()
    {
        var ai = new FakeAiChatClient(
            """{"name":"Stir-fry","description":"Veggies","prepTimeMinutes":15,"requiredEquipment":["wok"],"toddlerVersion":"Plain veg","adultBooster":"Sriracha"}""");
        var family = new FamilyProfile { AvailableEquipment = ["wok"], PrepTimeMinutes = 60 };
        var current = new Meal { Name = "Tacos" };

        var (meal, _, _, error) = await Service(true, ai).RegenerateMealAsync(family, current, ["Pasta"]);

        Assert.Null(error);
        Assert.NotNull(meal);
        Assert.Equal("Stir-fry", meal!.Name);
        Assert.Equal("Sriracha", meal.AdultBooster);
    }
}
