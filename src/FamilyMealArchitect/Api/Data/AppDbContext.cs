using System.Text.Json;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<SavedMealPlan> MealPlans => Set<SavedMealPlan>();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Complex object graphs are stored as JSON text columns. This keeps the
        // schema trivial for the experimentation phase while remaining queryable
        // by id; a future rebuild can normalise into relational tables.
        var user = modelBuilder.Entity<User>();
        user.HasKey(u => u.Id);
        user.HasIndex(u => u.Email).IsUnique();
        user.Property(u => u.Name).IsRequired();
        user.Property(u => u.Email).IsRequired();
        user.Property(u => u.PasswordHash).IsRequired();
        user.Property(u => u.Family).HasConversion(JsonConverter<FamilyProfile>()).Metadata
            .SetValueComparer(JsonComparer<FamilyProfile>());
        user.Property(u => u.RecentMeals).HasConversion(JsonConverter<List<string>>()).Metadata
            .SetValueComparer(JsonComparer<List<string>>());
        user.HasMany(u => u.SavedPlans)
            .WithOne()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        var plan = modelBuilder.Entity<SavedMealPlan>();
        plan.HasKey(p => p.Id);
        plan.HasIndex(p => p.UserId);
        plan.Property(p => p.Plan).HasConversion(JsonConverter<MealPlan>()).Metadata
            .SetValueComparer(JsonComparer<MealPlan>());
        plan.Property(p => p.ShoppingList).HasConversion(JsonConverter<List<ShoppingItem>?>()).Metadata
            .SetValueComparer(JsonComparer<List<ShoppingItem>?>());

        base.OnModelCreating(modelBuilder);
    }

    private static ValueConverter<T, string> JsonConverter<T>() => new(
        v => JsonSerializer.Serialize(v, JsonOpts),
        v => JsonSerializer.Deserialize<T>(v, JsonOpts)!);

    // Compare and snapshot via JSON so EF detects deep changes (including in-place
    // mutations) and takes true copies — not just reference swaps.
    private static ValueComparer<T> JsonComparer<T>() => new(
        (a, b) => JsonSerializer.Serialize(a, JsonOpts) == JsonSerializer.Serialize(b, JsonOpts),
        v => v == null ? 0 : JsonSerializer.Serialize(v, JsonOpts).GetHashCode(),
        v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOpts), JsonOpts)!);
}
