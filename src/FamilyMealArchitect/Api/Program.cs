using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenAI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// --- OpenAI ---
var openAiKey = builder.Configuration["OpenAI:ApiKey"];
if (string.IsNullOrEmpty(openAiKey))
{
    throw new InvalidOperationException("OpenAI API key not found. Set OpenAI__ApiKey environment variable or add to appsettings.");
}
builder.Services.AddSingleton(new OpenAIClient(openAiKey));
builder.Services.AddSingleton<IAiChatClient, OpenAiChatClient>();
builder.Services.AddSingleton<AiMealPlanningService>();

// --- Database (EF Core + SQLite) ---
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDir);
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? $"Data Source={Path.Combine(dataDir, "familymeals.db")}";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<UserRepository>();

// --- Auth ---
var jwtKey = builder.Configuration["Jwt:Key"];
var jwtKeyWasGenerated = false;
if (string.IsNullOrWhiteSpace(jwtKey))
{
    if (!builder.Environment.IsDevelopment())
    {
        // Outside dev an unset key must be a hard failure: a silently generated key
        // invalidates every session on restart and breaks multi-instance deployments.
        throw new InvalidOperationException("Jwt:Key is not configured. Set the Jwt__Key environment variable to a stable signing key.");
    }
    // Dev fallback: generate an ephemeral key (tokens won't survive a restart).
    jwtKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    builder.Configuration["Jwt:Key"] = jwtKey;
    jwtKeyWasGenerated = true;
}
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "FamilyMealArchitect";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "FamilyMealArchitect";

builder.Services.AddSingleton<AuthService>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            NameClaimType = JwtRegisteredClaimNames.Sub
        };
    });
builder.Services.AddAuthorization();

// --- Rate limiting ---
// Endpoints that call OpenAI spend real money; cap them per user (per IP when anonymous).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ai", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1)
            }));
});

var app = builder.Build();

// Create the database/schema on first run.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
if (jwtKeyWasGenerated)
{
    app.Logger.LogWarning("Using an ephemeral JWT signing key; tokens will be invalidated on restart. Set Jwt__Key for a stable key.");
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

static string? GetUserId(ClaimsPrincipal principal) =>
    principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

// ===========================================================================
// Public / experimentation endpoints
// ===========================================================================

app.MapGet("/health", () => new { status = "healthy", timestamp = DateTime.UtcNow });

app.MapGet("/test-ai", async (AiMealPlanningService aiService) =>
{
    var result = await aiService.TestConnectionAsync();
    return Results.Ok(new { aiResponse = result, timestamp = DateTime.UtcNow });
}).RequireAuthorization().RequireRateLimiting("ai");

// Ad-hoc generation (nothing saved). Requires an account: it spends OpenAI tokens.
app.MapPost("/generate", async (MealPlanRequest request, AiMealPlanningService aiService) =>
{
    if (request.Family is null)
        return Results.BadRequest(new { error = "A family profile is required" });

    var requestedMeals = SanitizeRequestedMeals(request.RequestedMeals, out var invalid);
    if (invalid is not null) return Results.BadRequest(new { error = invalid });

    var numberOfMeals = requestedMeals.Count > 0 ? requestedMeals.Count : request.NumberOfMeals;
    if (numberOfMeals <= 0 || numberOfMeals > 10)
        return Results.BadRequest(new { error = "Number of meals must be between 1 and 10" });
    if (request.Family.AvailableEquipment.Count == 0)
        return Results.BadRequest(new { error = "At least one piece of equipment must be available" });

    request.RequestedMeals = requestedMeals;
    request.NumberOfMeals = numberOfMeals;
    var response = await aiService.GenerateMealPlanAsync(request);
    return response.Success ? Results.Ok(response) : Results.BadRequest(response);
}).RequireAuthorization().RequireRateLimiting("ai");

app.MapGet("/sample-request", () => Results.Ok(new MealPlanRequest
{
    Family = new FamilyProfile
    {
        AdultPreferences = ["spicy food", "vegetables", "variety"],
        ToddlerPreferences = ["pasta", "mild flavors"],
        DietaryRestrictions = ["no nuts"],
        AvailableEquipment = ["dual ovens", "air fryer", "stovetop"],
        PrepTimeMinutes = 120
    },
    NumberOfMeals = 5,
    RecentMeals = ["spaghetti bolognese", "chicken stir-fry"]
}));

// ===========================================================================
// Auth
// ===========================================================================

app.MapPost("/auth/register", async (RegisterRequest req, UserRepository repo, AuthService auth) =>
{
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required" });
    if (string.IsNullOrWhiteSpace(req.Email)) return Results.BadRequest(new { error = "Email is required" });
    if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
        return Results.BadRequest(new { error = "Password must be at least 6 characters" });
    if (await repo.EmailExistsAsync(req.Email.Trim()))
        return Results.Conflict(new { error = "A user with that email already exists" });

    var user = new User
    {
        Name = req.Name.Trim(),
        Email = req.Email.Trim(),
        PasswordHash = auth.HashPassword(req.Password),
        Family = req.Family ?? new FamilyProfile(),
        RecentMeals = req.RecentMeals ?? []
    };
    await repo.CreateAsync(user);

    var (token, expiresAt) = auth.CreateToken(user);
    return Results.Created($"/users/{user.Id}", new AuthResponse { Token = token, ExpiresAt = expiresAt, User = user.ToResponse() });
});

app.MapPost("/auth/login", async (LoginRequest req, UserRepository repo, AuthService auth) =>
{
    var user = await repo.GetByEmailAsync(req.Email?.Trim() ?? "");
    if (user is null || !auth.VerifyPassword(user.PasswordHash, req.Password ?? ""))
        return Results.Json(new { error = "Invalid email or password" }, statusCode: StatusCodes.Status401Unauthorized);

    var (token, expiresAt) = auth.CreateToken(user);
    return Results.Ok(new AuthResponse { Token = token, ExpiresAt = expiresAt, User = user.ToResponse() });
});

// ===========================================================================
// Current user (requires auth)
// ===========================================================================

var me = app.MapGroup("/me").RequireAuthorization();

me.MapGet("", async (ClaimsPrincipal principal, UserRepository repo) =>
{
    var user = await repo.GetByIdAsync(GetUserId(principal)!);
    return user is null ? Results.NotFound() : Results.Ok(user.ToResponse());
});

me.MapPut("", async (UpdateProfileRequest req, ClaimsPrincipal principal, UserRepository repo) =>
{
    var user = await repo.GetByIdAsync(GetUserId(principal)!);
    if (user is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required" });

    user.Name = req.Name.Trim();
    user.Family = req.Family ?? new FamilyProfile();
    user.RecentMeals = req.RecentMeals ?? [];
    await repo.UpdateAsync(user);
    return Results.Ok(user.ToResponse());
});

// Suggest lightweight dinner ideas for the user to pick from (interactive step).
me.MapPost("/suggest", async (SuggestRequest? req, ClaimsPrincipal principal, UserRepository repo, AiMealPlanningService aiService) =>
{
    var user = await repo.GetByIdAsync(GetUserId(principal)!);
    if (user is null) return Results.NotFound();

    var count = req?.Count ?? 6;
    if (count <= 0 || count > 12)
        return Results.BadRequest(new { error = "Count must be between 1 and 12" });
    if (user.Family.AvailableEquipment.Count == 0)
        return Results.BadRequest(new { error = "Add at least one piece of equipment to your preferences first" });

    var (ideas, tokens, cost, error) = await aiService.SuggestMealsAsync(user.Family, count, user.RecentMeals);
    if (error is not null) return Results.BadRequest(new { error });
    return Results.Ok(new { ideas, tokensUsed = tokens, estimatedCost = cost });
}).RequireRateLimiting("ai");

// Generate from saved preferences, persist the plan, and track recent meals.
me.MapPost("/generate", async (GenerateForUserRequest? req, ClaimsPrincipal principal, UserRepository repo, AiMealPlanningService aiService) =>
{
    var user = await repo.GetByIdAsync(GetUserId(principal)!);
    if (user is null) return Results.NotFound();

    // If the user picked specific dishes, build one recipe per pick; else use the count.
    var requestedMeals = SanitizeRequestedMeals(req?.RequestedMeals, out var invalid);
    if (invalid is not null) return Results.BadRequest(new { error = invalid });
    var numberOfMeals = requestedMeals.Count > 0 ? requestedMeals.Count : (req?.NumberOfMeals ?? 5);
    if (numberOfMeals <= 0 || numberOfMeals > 10)
        return Results.BadRequest(new { error = "Number of meals must be between 1 and 10" });
    if (user.Family.AvailableEquipment.Count == 0)
        return Results.BadRequest(new { error = "Add at least one piece of equipment to your preferences first" });

    var response = await aiService.GenerateMealPlanAsync(new MealPlanRequest
    {
        Family = user.Family,
        NumberOfMeals = numberOfMeals,
        RecentMeals = user.RecentMeals,
        RequestedMeals = requestedMeals
    });
    if (!response.Success) return Results.BadRequest(response);

    var plan = new SavedMealPlan
    {
        UserId = user.Id,
        Plan = response.Plan,
        NumberOfMeals = numberOfMeals,
        TokensUsed = response.TokensUsed,
        EstimatedCost = response.EstimatedCost
    };

    // Append the new meal names to recent meals (deduped, most recent 20 kept), and
    // persist the plan + updated user atomically so they can't drift apart.
    user.RecentMeals = MergeRecentMeals(user.RecentMeals, response.Plan.Meals.Select(m => m.Name));
    var saved = await repo.AddPlanAndUpdateUserAsync(plan, user);

    return Results.Ok(new { planId = saved.Id, response.Plan, response.TokensUsed, response.EstimatedCost, success = true });
}).RequireRateLimiting("ai");

me.MapGet("/plans", async (ClaimsPrincipal principal, UserRepository repo) =>
{
    var userId = GetUserId(principal)!;
    var plans = await repo.GetPlansForUserAsync(userId);
    return Results.Ok(plans.Select(p => new SavedMealPlanSummary
    {
        Id = p.Id,
        CreatedAt = p.CreatedAt,
        NumberOfMeals = p.NumberOfMeals,
        MealNames = p.Plan.Meals.Select(m => m.Name).ToList(),
        HasShoppingList = p.ShoppingList is { Count: > 0 }
    }));
});

me.MapGet("/plans/{planId}", async (string planId, ClaimsPrincipal principal, UserRepository repo) =>
{
    var plan = await repo.GetPlanAsync(planId, GetUserId(principal)!);
    return plan is null ? Results.NotFound() : Results.Ok(plan);
});

me.MapDelete("/plans/{planId}", async (string planId, ClaimsPrincipal principal, UserRepository repo) =>
{
    var removed = await repo.DeletePlanAsync(planId, GetUserId(principal)!);
    return removed ? Results.NoContent() : Results.NotFound();
});

// Generate and persist a shopping list for a saved plan.
me.MapPost("/plans/{planId}/shopping-list", async (string planId, ClaimsPrincipal principal, UserRepository repo, AiMealPlanningService aiService) =>
{
    var plan = await repo.GetPlanAsync(planId, GetUserId(principal)!);
    if (plan is null) return Results.NotFound();

    var (items, _, _, error) = await aiService.GenerateShoppingListAsync(plan.Plan);
    if (items is null) return Results.BadRequest(new { error });

    plan.ShoppingList = items; // new reference -> persisted
    await repo.SavePlanChangesAsync();
    return Results.Ok(items);
}).RequireRateLimiting("ai");

// Regenerate a single meal within a saved plan.
me.MapPost("/plans/{planId}/meals/{index:int}/regenerate", async (string planId, int index, ClaimsPrincipal principal, UserRepository repo, AiMealPlanningService aiService) =>
{
    var user = await repo.GetByIdAsync(GetUserId(principal)!);
    if (user is null) return Results.NotFound();
    var plan = await repo.GetPlanAsync(planId, user.Id);
    if (plan is null) return Results.NotFound();
    if (index < 0 || index >= plan.Plan.Meals.Count)
        return Results.BadRequest(new { error = "Meal index out of range" });

    var current = plan.Plan.Meals[index];
    var otherNames = plan.Plan.Meals.Where((_, i) => i != index).Select(m => m.Name);
    var (meal, _, _, error) = await aiService.RegenerateMealAsync(user.Family, current, otherNames);
    if (meal is null) return Results.BadRequest(new { error });

    // Give the new meal its own preparation options so it behaves like the rest.
    await aiService.GeneratePreparationsAsync(user.Family, [meal]);

    // Rebuild the plan with a fresh reference so EF persists the JSON column.
    var meals = plan.Plan.Meals.ToList();
    meals[index] = meal;
    plan.Plan = new MealPlan
    {
        Meals = meals,
        EquipmentCoordination = plan.Plan.EquipmentCoordination,
        FamilyAdaptations = plan.Plan.FamilyAdaptations,
        EstimatedPrepTime = meals.Sum(m => m.SelectedPreparation?.PrepTimeMinutes ?? m.PrepTimeMinutes)
    };
    await repo.SavePlanChangesAsync();
    return Results.Ok(meal);
}).RequireRateLimiting("ai");

// Apply the user's per-meal preparation-method choices and (re)build the
// parallel/cascading cooking plan accordingly.
me.MapPost("/plans/{planId}/cooking-plan", async (string planId, CookingPlanRequest? req, ClaimsPrincipal principal, UserRepository repo, AiMealPlanningService aiService) =>
{
    var user = await repo.GetByIdAsync(GetUserId(principal)!);
    if (user is null) return Results.NotFound();
    var plan = await repo.GetPlanAsync(planId, user.Id);
    if (plan is null) return Results.NotFound();

    var meals = plan.Plan.Meals;
    // Apply valid selections; silently ignore unknown indexes/method ids.
    foreach (var sel in req?.Selections ?? [])
    {
        if (sel.MealIndex < 0 || sel.MealIndex >= meals.Count) continue;
        var meal = meals[sel.MealIndex];
        if (meal.PreparationMethods.Any(p => p.Id == sel.MethodId))
            meal.SelectedPreparationId = sel.MethodId;
    }

    var (coordination, familyAdaptations, _, _, error) = await aiService.GenerateCookingPlanAsync(user.Family, meals.ToList());
    if (error is not null) return Results.BadRequest(new { error });

    plan.Plan = new MealPlan
    {
        Meals = meals.ToList(),
        EquipmentCoordination = coordination,
        FamilyAdaptations = familyAdaptations,
        EstimatedPrepTime = meals.Sum(m => m.SelectedPreparation?.PrepTimeMinutes ?? m.PrepTimeMinutes)
    };
    await repo.SavePlanChangesAsync();
    return Results.Ok(plan);
}).RequireRateLimiting("ai");

app.Run();

// Trim and drop empty entries; reject absurdly long dish names (they go straight into prompts).
static List<string> SanitizeRequestedMeals(List<string>? requestedMeals, out string? error)
{
    var cleaned = (requestedMeals ?? [])
        .Select(m => m?.Trim() ?? "")
        .Where(m => m.Length > 0)
        .ToList();
    error = cleaned.Any(m => m.Length > 200)
        ? "Requested meal names must be 200 characters or fewer"
        : null;
    return cleaned;
}

// Keep the new meal names plus existing history, deduped (case-insensitive), newest first, capped at 20.
static List<string> MergeRecentMeals(List<string> existing, IEnumerable<string> newNames)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var merged = new List<string>();
    foreach (var name in newNames.Concat(existing))
    {
        if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
        {
            merged.Add(name);
        }
    }
    return merged.Take(20).ToList();
}

public partial class Program;
