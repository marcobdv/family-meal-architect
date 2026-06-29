using Api.Data;
using Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Tests;

/// <summary>
/// Exercises the repository against a real (in-memory) SQLite database so the
/// JSON value converters for FamilyProfile / lists / MealPlan are covered.
/// </summary>
public class UserRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public UserRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    private AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    private static User SampleUser() => new()
    {
        Name = "The Smiths",
        Email = "smiths@example.com",
        PasswordHash = "hash",
        Family = new FamilyProfile
        {
            AdultPreferences = ["spicy"],
            AvailableEquipment = ["oven"],
            PrepTimeMinutes = 45
        },
        RecentMeals = ["pizza", "tacos"]
    };

    [Fact]
    public async Task Create_And_GetById_RoundTripsJsonColumns()
    {
        string id;
        await using (var db = NewContext())
        {
            id = (await new UserRepository(db).CreateAsync(SampleUser())).Id;
        }

        await using (var db = NewContext())
        {
            var loaded = await new UserRepository(db).GetByIdAsync(id);
            Assert.NotNull(loaded);
            Assert.Equal("The Smiths", loaded!.Name);
            Assert.Equal(45, loaded.Family.PrepTimeMinutes);
            Assert.Equal(["spicy"], loaded.Family.AdultPreferences);
            Assert.Equal(["pizza", "tacos"], loaded.RecentMeals);
        }
    }

    [Fact]
    public async Task GetByEmail_And_EmailExists_AreCaseInsensitive()
    {
        await using var db = NewContext();
        var repo = new UserRepository(db);
        await repo.CreateAsync(SampleUser());

        Assert.NotNull(await repo.GetByEmailAsync("SMITHS@example.com"));
        Assert.True(await repo.EmailExistsAsync("smiths@EXAMPLE.com"));
        Assert.False(await repo.EmailExistsAsync("nobody@example.com"));
    }

    [Fact]
    public async Task UpdateRecentMeals_Persists()
    {
        string id;
        await using (var db = NewContext())
        {
            id = (await new UserRepository(db).CreateAsync(SampleUser())).Id;
        }
        await using (var db = NewContext())
        {
            var repo = new UserRepository(db);
            var user = await repo.GetByIdAsync(id);
            user!.RecentMeals = ["new-meal", "pizza"];
            await repo.UpdateAsync(user);
        }
        await using (var db = NewContext())
        {
            var user = await new UserRepository(db).GetByIdAsync(id);
            Assert.Equal(["new-meal", "pizza"], user!.RecentMeals);
        }
    }

    [Fact]
    public async Task InPlaceMutationOfJsonProperty_IsPersisted()
    {
        string id;
        await using (var db = NewContext())
        {
            id = (await new UserRepository(db).CreateAsync(SampleUser())).Id;
        }
        await using (var db = NewContext())
        {
            var repo = new UserRepository(db);
            var user = await repo.GetByIdAsync(id);
            // Mutate in place (no reassignment) — the JSON comparer must still detect this.
            user!.Family.AdultPreferences.Add("smoky");
            user.RecentMeals.Add("ramen");
            await repo.UpdateAsync(user);
        }
        await using (var db = NewContext())
        {
            var user = await new UserRepository(db).GetByIdAsync(id);
            Assert.Contains("smoky", user!.Family.AdultPreferences);
            Assert.Contains("ramen", user.RecentMeals);
        }
    }

    [Fact]
    public async Task SavedPlans_AddListGetDelete()
    {
        await using var db = NewContext();
        var repo = new UserRepository(db);
        var user = await repo.CreateAsync(SampleUser());

        var plan = await repo.AddPlanAsync(new SavedMealPlan
        {
            UserId = user.Id,
            NumberOfMeals = 2,
            Plan = new MealPlan { Meals = [new Meal { Name = "Tacos" }, new Meal { Name = "Pasta" }] }
        });

        var plans = await repo.GetPlansForUserAsync(user.Id);
        Assert.Single(plans);
        Assert.Equal(2, plans[0].Plan.Meals.Count);

        var fetched = await repo.GetPlanAsync(plan.Id, user.Id);
        Assert.NotNull(fetched);

        // A different user cannot fetch it.
        Assert.Null(await repo.GetPlanAsync(plan.Id, "other-user"));

        Assert.True(await repo.DeletePlanAsync(plan.Id, user.Id));
        Assert.Empty(await repo.GetPlansForUserAsync(user.Id));
    }

    [Fact]
    public async Task DeleteUser_CascadesToPlans()
    {
        await using var db = NewContext();
        var repo = new UserRepository(db);
        var user = await repo.CreateAsync(SampleUser());
        await repo.AddPlanAsync(new SavedMealPlan { UserId = user.Id, Plan = new MealPlan() });

        Assert.True(await repo.DeleteAsync(user.Id));
        Assert.Empty(await repo.GetPlansForUserAsync(user.Id));
    }

    public void Dispose() => _connection.Dispose();
}
