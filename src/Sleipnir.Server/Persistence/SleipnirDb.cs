using Microsoft.EntityFrameworkCore;
using Sleipnir.Core.Models;

namespace Sleipnir.Server.Persistence;

public class SleipnirDb(DbContextOptions<SleipnirDb> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<UserEntity>(user =>
        {
            user.HasKey(u => u.Id);
            user.Property(u => u.DisplayName).UseCollation("NOCASE").HasMaxLength(40).IsRequired();
            user.HasIndex(u => u.DisplayName).IsUnique();
            user.Property(u => u.Role).HasConversion(
                role => role.ToString().ToLowerInvariant(),
                text => Enum.Parse<UserRole>(text, ignoreCase: true));
        });

        model.Entity<DeviceEntity>(device =>
        {
            device.HasKey(d => d.Id);
            device.Property(d => d.Name).IsRequired();
            device.Property(d => d.TokenHash).IsRequired();
            device.HasIndex(d => d.TokenHash).IsUnique();
            device.HasOne<UserEntity>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
