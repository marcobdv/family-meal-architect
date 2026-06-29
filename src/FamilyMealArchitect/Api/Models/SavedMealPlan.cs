namespace Api.Models;

/// <summary>A meal plan persisted in a user's history.</summary>
public class SavedMealPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MealPlan Plan { get; set; } = new();
    public int NumberOfMeals { get; set; }
    public int TokensUsed { get; set; }
    public decimal EstimatedCost { get; set; }

    /// <summary>Generated lazily the first time a shopping list is requested.</summary>
    public List<ShoppingItem>? ShoppingList { get; set; }
}

public class ShoppingItem
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "Other";
    public string Quantity { get; set; } = string.Empty;
}

/// <summary>Summary projection for listing a user's plan history.</summary>
public class SavedMealPlanSummary
{
    public string Id { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int NumberOfMeals { get; set; }
    public List<string> MealNames { get; set; } = [];
    public bool HasShoppingList { get; set; }
}

/// <summary>Per-meal preparation-method choices used to (re)build the cooking plan.</summary>
public class CookingPlanRequest
{
    public List<PreparationSelection> Selections { get; set; } = [];
}

public class PreparationSelection
{
    public int MealIndex { get; set; }
    public string MethodId { get; set; } = string.Empty;
}
