using System.Text.Json.Serialization;

namespace Api.Models;

// Stage 2: family adaptation (toddler version + adult booster per meal).
public class AdaptationJsonResponse
{
    [JsonPropertyName("adaptations")]
    public List<AdaptationJson> Adaptations { get; set; } = new();
}

public class AdaptationJson
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("toddlerVersion")]
    public string ToddlerVersion { get; set; } = "";

    [JsonPropertyName("adultBooster")]
    public string AdultBooster { get; set; } = "";
}

// Stage: equipment orchestration / cascade cooking plan.
public class OrchestrationJsonResponse
{
    [JsonPropertyName("equipmentCoordination")]
    public string EquipmentCoordination { get; set; } = "";

    [JsonPropertyName("familyAdaptations")]
    public string FamilyAdaptations { get; set; } = "";
}

// Interactive step: lightweight dinner ideas before full recipes are generated.
public class SuggestionsJsonResponse
{
    [JsonPropertyName("ideas")]
    public List<MealIdeaJson> Ideas { get; set; } = new();
}

public class MealIdeaJson
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("blurb")]
    public string Blurb { get; set; } = "";

    [JsonPropertyName("cuisine")]
    public string Cuisine { get; set; } = "";

    [JsonPropertyName("mainIngredient")]
    public string MainIngredient { get; set; } = "";
}

// Stage: preparation options (2-3 methods per meal, one recommended).
public class PreparationOptionsJsonResponse
{
    [JsonPropertyName("meals")]
    public List<MealPreparationsJson> Meals { get; set; } = new();
}

public class MealPreparationsJson
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("methods")]
    public List<PreparationMethodJson> Methods { get; set; } = new();
}

public class PreparationMethodJson
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("equipment")]
    public List<string> Equipment { get; set; } = new();

    [JsonPropertyName("prepTimeMinutes")]
    public int PrepTimeMinutes { get; set; }

    [JsonPropertyName("recommended")]
    public bool Recommended { get; set; }
}

// Shopping list generation.
public class ShoppingListJsonResponse
{
    [JsonPropertyName("items")]
    public List<ShoppingItemJson> Items { get; set; } = new();
}

public class ShoppingItemJson
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "Other";

    [JsonPropertyName("quantity")]
    public string Quantity { get; set; } = "";
}

// Per-meal regeneration returns a bare meal object, parsed with MealJson
// (see Models/MealPlanJsonResponse.cs) — same shape, no separate DTO needed.
