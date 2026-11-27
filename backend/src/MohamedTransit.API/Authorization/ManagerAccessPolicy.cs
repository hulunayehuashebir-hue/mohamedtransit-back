using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;

namespace MohamedTransit.API.Authorization;

public sealed class ManagerAccessRequirement : IAuthorizationRequirement
{
}

public sealed class ManagerAccessHandler(ApplicationDbContext context)
    : AuthorizationHandler<ManagerAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext authorizationContext,
        ManagerAccessRequirement requirement)
    {
        var idValue = authorizationContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? authorizationContext.User.FindFirstValue("id");
        if (idValue is null)
        {
            var userName = authorizationContext.User.FindFirstValue("userName")
                ?? authorizationContext.User.Identity?.Name;
            if (!string.IsNullOrWhiteSpace(userName))
            {
                idValue = await context.Users
                    .AsNoTracking()
                    .Where(user => user.Username == userName)
                    .Select(user => user.Id)
                    .Select(id => id.ToString())
                    .FirstOrDefaultAsync();
            }
        }
        if (!long.TryParse(idValue, out var userId))
            return;

        var hasManagerRole = await context.UserRoles
            .AsNoTracking()
            .AnyAsync(userRole =>
                userRole.UserId == userId &&
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                (userRole.Role.Name.ToLower() == "manager" ||
                 userRole.Role.Name.ToLower() == "admin" ||
                 userRole.Role.Name.ToLower() == "superadmin"));
        if (hasManagerRole)
            authorizationContext.Succeed(requirement);
    }
}
