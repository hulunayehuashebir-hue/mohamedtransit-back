using Microsoft.EntityFrameworkCore;

using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;

namespace MohamedTransit.API;

public static class CustomerRoleSeeder
{
    public static async Task SeedAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken = default)
    {
        var customerRoleExists = await context.Roles
            .AnyAsync(
                role => role.Name.ToLower() == "customer",
                cancellationToken);

        if (customerRoleExists)
        {
            return;
        }

        context.Roles.Add(Role.Create(
            "Customer",
            "Customer role for external users"));
        await context.SaveChangesAsync(cancellationToken);
    }
}
