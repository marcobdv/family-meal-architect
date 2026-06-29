using System.Text.Json.Serialization;

namespace Api.Models;

public class MealPlanJsonResponse
{
    [JsonPropertyName("meals")]
    public List<MealJson> Meals { get; set; } = new();
}

public class MealJson
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("prepTimeMinutes")]
    public int PrepTimeMinutes { get; set; }

    [JsonPropertyName("requiredEquipment")]
    public List<string> RequiredEquipment { get; set; } = new();

    [JsonPropertyName("ingredients")]
    public List<IngredientJson> Ingredients { get; set; } = new();

    [JsonPropertyName("servings")]
    public int Servings { get; set; }

    [JsonPropertyName("prepAhead")]
    public string PrepAhead { get; set; } = "";

    [JsonPropertyName("storage")]
    public string Storage { get; set; } = "";

    [JsonPropertyName("eatDay")]
    public string EatDay { get; set; } = "";

    [JsonPropertyName("eatDayActiveMinutes")]
    public int EatDayActiveMinutes { get; set; }

    [JsonPropertyName("eatDayTotalMinutes")]
    public int EatDayTotalMinutes { get; set; }

    [JsonPropertyName("toddlerVersion")]
    public string ToddlerVersion { get; set; } = "";

    [JsonPropertyName("adultBooster")]
    public string AdultBooster { get; set; } = "";
}

public class IngredientJson
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("quantity")]
    public string Quantity { get; set; } = "";
}
