namespace Api.Models;

/// <summary>
/// A person/household using the app. Stores their family profile and recent meals
/// so meal plans can be generated from saved preferences, plus their saved plans.
/// </summary>
public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public FamilyProfile Family { get; set; } = new();
    public List<string> RecentMeals { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<SavedMealPlan> SavedPlans { get; set; } = [];

    /// <summary>Projection safe to return to clients (no password hash).</summary>
    public UserResponse ToResponse() => new()
    {
        Id = Id,
        Name = Name,
        Email = Email,
        Family = Family,
        RecentMeals = RecentMeals,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };
}

public class UserResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public FamilyProfile Family { get; set; } = new();
    public List<string> RecentMeals { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// --- Auth DTOs -------------------------------------------------------------

public class RegisterRequest
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public FamilyProfile? Family { get; set; }
    public List<string>? RecentMeals { get; set; }
}

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public UserResponse User { get; set; } = new();
}

/// <summary>Payload for updating the current user's profile/preferences.</summary>
public class UpdateProfileRequest
{
    public string Name { get; set; } = string.Empty;
    public FamilyProfile Family { get; set; } = new();
    public List<string> RecentMeals { get; set; } = [];
}

/// <summary>Optional overrides when generating a plan for the current user.</summary>
public class GenerateForUserRequest
{
    public int NumberOfMeals { get; set; } = 5;

    /// <summary>Dish ideas the user picked (from suggestions or their own); one recipe is built per item.</summary>
    public List<string> RequestedMeals { get; set; } = [];
}

/// <summary>Request for lightweight meal ideas before generating full recipes.</summary>
public class SuggestRequest
{
    public int Count { get; set; } = 6;
}
