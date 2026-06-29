using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

/// <summary>
/// Data access for users and their saved meal plans. Same async surface the
/// old JSON store exposed, now backed by EF Core + SQLite.
/// </summary>
public class UserRepository(AppDbContext db)
{
    private readonly AppDbContext _db = db;

    // --- Users ---

    public Task<User?> GetByIdAsync(string id) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id);

    public Task<User?> GetByEmailAsync(string email) =>
        _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower());

    public Task<bool> EmailExistsAsync(string email, string? excludeId = null) =>
        _db.Users.AnyAsync(u => u.Email.ToLower() == email.ToLower() && u.Id != excludeId);

    public async Task<User> CreateAsync(User user)
    {
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task UpdateAsync(User user)
    {
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            return false;
        }
        _db.Users.Remove(user);
        await _db.SaveChangesAsync();
        return true;
    }

    // --- Saved meal plans ---

    public async Task<SavedMealPlan> AddPlanAsync(SavedMealPlan plan)
    {
        _db.MealPlans.Add(plan);
        await _db.SaveChangesAsync();
        return plan;
    }

    /// <summary>
    /// Persist a newly generated plan and the (already-tracked) user's updated
    /// recent meals in a single SaveChanges so the two never drift apart.
    /// </summary>
    public async Task<SavedMealPlan> AddPlanAndUpdateUserAsync(SavedMealPlan plan, User user)
    {
        _db.MealPlans.Add(plan);
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return plan;
    }

    public Task<List<SavedMealPlan>> GetPlansForUserAsync(string userId) =>
        _db.MealPlans
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

    public Task<SavedMealPlan?> GetPlanAsync(string planId, string userId) =>
        _db.MealPlans.FirstOrDefaultAsync(p => p.Id == planId && p.UserId == userId);

    public Task SavePlanChangesAsync() => _db.SaveChangesAsync();

    public async Task<bool> DeletePlanAsync(string planId, string userId)
    {
        var plan = await _db.MealPlans.FirstOrDefaultAsync(p => p.Id == planId && p.UserId == userId);
        if (plan is null)
        {
            return false;
        }
        _db.MealPlans.Remove(plan);
        await _db.SaveChangesAsync();
        return true;
    }
}
