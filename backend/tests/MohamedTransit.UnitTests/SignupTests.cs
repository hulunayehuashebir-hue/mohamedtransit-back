using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MohamedTransit.Application;
using MohamedTransit.Application.Commands.UserAccount;
using MohamedTransit.Application.Helper;
using MohamedTransit.Application.Service;
using MohamedTransit.Application.Services;
using MohamedTransit.API.Controllers.MOT;
using MohamedTransit.API.DTO.MasterData.Request;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;
using MohamedTransit.Domain.Common;

namespace MohamedTransit.UnitTests;

public class SignupTests
{
    [Fact]
    public async Task EmailSenderExposesOnlyAFriendlyErrorWhenConfigurationIsInvalid()
    {
        var emailSender = new EmailSenderService(
            Options.Create(new Settings()),
            NullLogger<EmailSenderService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => emailSender.SendEmailAsync(
                "<p>Test</p>",
                "Test",
                new[] { "recipient@example.test" }));

        Assert.Equal("Email could not be sent. Please try again later.", exception.Message);
    }

    [Fact]
    public async Task SignupCreatesCustomerProfileAndSucceedsWhenWelcomeEmailCannotBeSent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);
        await context.Database.EnsureCreatedAsync();
        context.Roles.Add(Role.Create("Customer", "Customer account"));
        await context.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddScoped<PasswordService>();
        services.AddScoped(_ => context);
        services.AddSingleton<IOptions<Settings>>(Options.Create(new Settings()));
        services.AddScoped<EmailSenderService>();
        await using var serviceProvider = services.BuildServiceProvider();

        var result = await serviceProvider
            .GetRequiredService<IMediator>()
            .Send(new CreateUserCommand
            {
                Username = "new-customer",
                Email = "new-customer@example.test",
                FirstName = "New",
                LastName = "Customer",
                Phone = "+251900000000",
                Password = "StrongPassword123"
            });

        Assert.False(result.IsError);
        var createdUser = Assert.IsType<User>(result.Payload);

        var customer = await context.Customers.SingleAsync(
            profile => profile.UserId == createdUser.Id);
        Assert.Equal("New Customer", customer.BusinessName);
        Assert.Equal("New Customer", customer.ContactPerson);
        Assert.Equal("new-customer@example.test", customer.ContactEmail);

        var assignedRole = await context.UserRoles
            .Include(userRole => userRole.Role)
            .SingleAsync(userRole => userRole.UserId == createdUser.Id);
        Assert.Equal("Customer", assignedRole.Role.Name);

        var duplicateResult = await serviceProvider
            .GetRequiredService<IMediator>()
            .Send(new CreateUserCommand
            {
                Username = "another-user",
                Email = "NEW-CUSTOMER@example.test",
                FirstName = "Another",
                LastName = "User",
                Password = "StrongPassword123"
            });

        Assert.True(duplicateResult.IsError);
        Assert.Equal("An account with this email or username already exists.", duplicateResult.Errors.Single().Message);
        Assert.Equal(1, await context.Users.CountAsync());
    }

    [Fact]
    public async Task CustomerRegistrationUpdatesAnIncompleteSignupProfileAndRejectsACompletedOne()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);
        await context.Database.EnsureCreatedAsync();

        var user = User.CreateUser(
            "customer-user",
            "customer@example.test",
            "Customer",
            "User",
            string.Empty,
            "+251900000000",
            "hashed-password",
            false,
            AccountStatus.Approved);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var provisionalProfile = Customer.Create(
            "Customer User",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "Customer User",
            "+251900000000",
            "customer@example.test",
            string.Empty,
            string.Empty,
            null,
            user.Id,
            user.Id);
        context.Customers.Add(provisionalProfile);
        await context.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        var token = new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(claims: new[] { new Claim("id", user.Id.ToString()) }));
        httpContext.Request.Headers["Authorization"] = $"Bearer {token}";

        var controller = new CustomerController(
            context,
            new HttpContextAccessor { HttpContext = httpContext },
            null!,
            null!)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        var request = new CreateCustomerRequest
        {
            BusinessName = "Example Imports",
            TINNumber = "TIN-12345",
            BusinessAddress = "10 Main Street",
            City = "Addis Ababa",
            ContactPerson = "Customer User",
            ContactPhone = "+251900000000",
            ContactEmail = "customer@example.test"
        };

        var updateResult = await controller.RegisterAsCustomer(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(updateResult);
        var updatedProfile = await context.Customers.SingleAsync();
        Assert.Equal("Example Imports", updatedProfile.BusinessName);
        Assert.Equal("TIN-12345", updatedProfile.TINNumber);
        Assert.Equal("10 Main Street", updatedProfile.BusinessAddress);

        var duplicateRegistrationResult = await controller.RegisterAsCustomer(request, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(duplicateRegistrationResult);
    }
}
