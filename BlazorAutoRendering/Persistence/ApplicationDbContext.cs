using Microsoft.EntityFrameworkCore;

namespace BlazorAutoRendering.Persistence;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<AppUser.AppUser> AppUsers { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser.AppUser>()
            .HasIndex(u => new { u.IdpName, u.IdpSubject })
            .IsUnique();
    }
}