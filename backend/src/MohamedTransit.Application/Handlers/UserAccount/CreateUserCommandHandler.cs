using MediatR;

using Microsoft.EntityFrameworkCore;

using MohamedTransit.Application.Commands.UserAccount;
using MohamedTransit.Application.Helper;
using MohamedTransit.Application.Service;
using MohamedTransit.Application.Services;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace MohamedTransit.Application.Handlers.UserAccount;

internal class CreateUserCommandHandler
    : IRequestHandler<CreateUserCommand, OperationResult<User>>
{
    private readonly ApplicationDbContext _context;
    private readonly PasswordService _passwordService;
    private readonly EmailSenderService _emailSenderService;
    private readonly ILogger<CreateUserCommandHandler> _logger;

    public CreateUserCommandHandler(
        ApplicationDbContext context,
        PasswordService passwordService,
        EmailSenderService emailSenderService,
        ILogger<CreateUserCommandHandler> logger)
    {
        _context = context;
        _passwordService = passwordService;
        _emailSenderService = emailSenderService;
        _logger = logger;
    }

    public async Task<OperationResult<User>> Handle(
        CreateUserCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Duplicate checks
        var normalizedUsername = request.Username.Trim().ToLowerInvariant();
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var existingUser = await _context.Users
            .AsNoTracking()
            .AnyAsync(
                user => user.Username.Trim().ToLower() == normalizedUsername ||
                        user.Email.Trim().ToLower() == normalizedEmail,
                cancellationToken);

        if (existingUser)
        {
            return OperationResult<User>.Failure(
                "An account with this email or username already exists.",
                ErrorCode.UserAlreadyExists);
        }

        var customerRole = await _context.Roles
            .FirstOrDefaultAsync(
                role => role.Name.ToLower() == "customer",
                cancellationToken);

        if (customerRole is null)
        {
            return OperationResult<User>.Failure(
                "The Customer role is not configured.",
                ErrorCode.ServerError);
        }

        // 2. Hash password during creation
        var hashedPassword = _passwordService.HashPassword(request.Password);

        var user = User.CreateUser(
            request.Username,
            request.Email,
            request.FirstName,
            request.LastName,
            request.ProfilePhoto,
            request.Phone,
            hashedPassword,
            false,
            AccountStatus.Approved);

        user.AddRole(new UserRole
        {
            User = user,
            RoleId = customerRole.Id
        });

        await using (var transaction = await _context.Database.BeginTransactionAsync(cancellationToken))
        {
            await _context.Users.AddAsync(user, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            var customerProfileExists = await _context.Customers
                .AnyAsync(customer => customer.UserId == user.Id, cancellationToken);
            if (!customerProfileExists)
            {
                var contactName = $"{request.FirstName} {request.LastName}".Trim();
                if (string.IsNullOrWhiteSpace(contactName))
                    contactName = request.Username;

                _context.Customers.Add(MohamedTransit.Domain.Entities.Customer.Create(
                    contactName,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    contactName,
                    request.Phone,
                    request.Email,
                    string.Empty,
                    string.Empty,
                    null,
                    user.Id,
                    user.Id));
                await _context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        try
        {
            await _emailSenderService.SendEmailAsync(
                "<p>Welcome to Mohamed Transit. Your account has been created successfully.</p>",
                "Welcome to Mohamed Transit",
                new[] { request.Email },
                cancellationToken: CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "User {UserId} was created, but the welcome email could not be sent.",
                user.Id);
        }

        return OperationResult<User>.Success(
            user,
            "User created successfully.");
    }
}
