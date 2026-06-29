namespace Api.Models;

public class FamilyProfile
{
    public List<string> AdultPreferences { get; set; } = [];
    public List<string> ToddlerPreferences { get; set; } = [];
    public List<string> DietaryRestrictions { get; set; } = [];
    public List<string> AvailableEquipment { get; set; } = [];
    public int PrepTimeMinutes { get; set; } = 120; // Default 2 hours for the prep-day session

    /// <summary>
    /// Max hands-on (active) cooking minutes allowed on a weekday "eat day".
    /// Passive oven/simmer time does not count against this.
    /// </summary>
    public int WeekdayActiveCookingMinutes { get; set; } = 15;
}

public class MealPlan
{
    public List<Meal> Meals { get; set; } = [];
    public string EquipmentCoordination { get; set; } = string.Empty;
    public string FamilyAdaptations { get; set; } = string.Empty;
    public int EstimatedPrepTime { get; set; }
}

public class Meal
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ToddlerVersion { get; set; } = string.Empty;
    public string AdultBooster { get; set; } = string.Empty;
    public List<string> RequiredEquipment { get; set; } = [];
    public int PrepTimeMinutes { get; set; }

    /// <summary>Ingredients with quantities for this meal (batch sized to Servings).</summary>
    public List<Ingredient> Ingredients { get; set; } = [];

    /// <summary>How many portions this batch yields (for prepping ahead).</summary>
    public int Servings { get; set; }

    /// <summary>The heavy lifting done on prep day (make-ahead components, par-cooking, sauces, assembly).</summary>
    public string PrepAhead { get; set; } = string.Empty;

    /// <summary>How to store the prepped components between prep day and eat day.</summary>
    public string StorageInstructions { get; set; } = string.Empty;

    /// <summary>The short weekday finishing step on the day you eat it (fry/bake/reheat).</summary>
    public string EatDayInstructions { get; set; } = string.Empty;

    /// <summary>Hands-on active minutes required on the eat day (should fit the weekday budget).</summary>
    public int EatDayActiveMinutes { get; set; }

    /// <summary>Total eat-day minutes including passive oven/simmer time.</summary>
    public int EatDayTotalMinutes { get; set; }

    /// <summary>2-3 alternative ways to prepare this meal; one is marked Recommended.</summary>
    public List<PreparationMethod> PreparationMethods { get; set; } = [];

    /// <summary>Id of the currently chosen preparation method (defaults to the recommended one).</summary>
    public string SelectedPreparationId { get; set; } = string.Empty;

    public PreparationMethod? SelectedPreparation =>
        PreparationMethods.FirstOrDefault(p => p.Id == SelectedPreparationId)
        ?? PreparationMethods.FirstOrDefault();
}

public class Ingredient
{
    public string Name { get; set; } = string.Empty;
    public string Quantity { get; set; } = string.Empty;
}

public class PreparationMethod
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = string.Empty;        // e.g. "One-pan stovetop"
    public string Description { get; set; } = string.Empty;  // short how-to (1-2 sentences)
    public List<string> Equipment { get; set; } = [];
    public int PrepTimeMinutes { get; set; }
    public bool Recommended { get; set; }
}

public class MealPlanRequest
{
    public FamilyProfile Family { get; set; } = new();
    public int NumberOfMeals { get; set; } = 5;
    public List<string> RecentMeals { get; set; } = [];

    /// <summary>
    /// Optional: specific dish ideas the user picked to turn into full recipes.
    /// When set, the AI builds one recipe per requested dish instead of choosing freely.
    /// </summary>
    public List<string> RequestedMeals { get; set; } = [];
}

/// <summary>A lightweight dinner idea shown to the user before full recipes are generated.</summary>
public class MealIdea
{
    public string Name { get; set; } = string.Empty;
    public string Blurb { get; set; } = string.Empty;
    public string Cuisine { get; set; } = string.Empty;
    public string MainIngredient { get; set; } = string.Empty;
}

public class MealPlanResponse
{
    public MealPlan Plan { get; set; } = new();
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public int TokensUsed { get; set; }
    public decimal EstimatedCost { get; set; }
}
