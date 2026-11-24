using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MohamedTransit.Application;
using MohamedTransit.Application.Commands.UserAccount;
using MohamedTransit.Application.Service;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;

namespace MohamedTransit.UnitTests;

public class PasswordResetOtpTests
{
    private const string Email = "otp-test@example.com";
    private const string Code = "482915";

    [Fact]
    public async Task ValidCodeChangesPasswordAndCanOnlyBeUsedOnce()
    {
        await using var environment = await TestEnvironment.CreateAsync(Code);

        var result = await environment.Mediator.Send(
            new ResetPasswordCommand(Email, Code, "UpdatedPassword123"));

        Assert.False(result.IsError);
        Assert.True(environment.PasswordService.ValidatePassword(
            environment.User.Password,
            "UpdatedPassword123"));
        Assert.Null(environment.User.PasswordResetCodeHash);
        Assert.Null(environment.User.PasswordResetCodeExpiresAt);

        var reusedCodeResult = await environment.Mediator.Send(
            new ResetPasswordCommand(Email, Code, "AnotherPassword123"));

        Assert.True(reusedCodeResult.IsError);
    }

    [Fact]
    public async Task FifthIncorrectCodeInvalidatesTheOtp()
    {
        await using var environment = await TestEnvironment.CreateAsync(Code);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await environment.Mediator.Send(
                new ResetPasswordCommand(Email, "000000", "UpdatedPassword123"));

            Assert.True(result.IsError);
        }

        Assert.Null(environment.User.PasswordResetCodeHash);
        Assert.Null(environment.User.PasswordResetCodeExpiresAt);

        var correctCodeResult = await environment.Mediator.Send(
            new ResetPasswordCommand(Email, Code, "UpdatedPassword123"));

        Assert.True(correctCodeResult.IsError);
    }

    [Fact]
    public async Task ExpiredCodeIsRejectedAndCleared()
    {
        await using var environment = await TestEnvironment.CreateAsync(
            Code,
            DateTime.UtcNow.AddMinutes(-1));

        var result = await environment.Mediator.Send(
            new ResetPasswordCommand(Email, Code, "UpdatedPassword123"));

        Assert.True(result.IsError);
        Assert.Null(environment.User.PasswordResetCodeHash);
        Assert.Null(environment.User.PasswordResetCodeExpiresAt);
    }

    private sealed class TestEnvironment : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _serviceProvider;

        private TestEnvironment(
            SqliteConnection connection,
            ServiceProvider serviceProvider,
            User user,
            PasswordService passwordService)
        {
            _connection = connection;
            _serviceProvider = serviceProvider;
            User = user;
            PasswordService = passwordService;
            Mediator = serviceProvider.GetRequiredService<IMediator>();
        }

        public User User { get; }
        public PasswordService PasswordService { get; }
        public IMediator Mediator { get; }

        public static async Task<TestEnvironment> CreateAsync(
            string code,
            DateTime? expiresAt = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var context = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await context.Database.EnsureCreatedAsync();

            var codeHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(code)));
            var user = User.CreateUser(
                "otp-test",
                Email,
                "OTP",
                "Test",
                string.Empty,
                string.Empty,
                "OriginalPassword123",
                false,
                AccountStatus.Approved);
            user.SetPasswordResetCode(
                codeHash,
                expiresAt ?? DateTime.UtcNow.AddMinutes(15));
            context.Users.Add(user);
            await context.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddApplication();
            services.AddScoped<PasswordService>();
            services.AddScoped(_ => context);
            var serviceProvider = services.BuildServiceProvider();

            return new TestEnvironment(
                connection,
                serviceProvider,
                user,
                serviceProvider.GetRequiredService<PasswordService>());
        }

        public async ValueTask DisposeAsync()
        {
            await _serviceProvider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
