using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MohamedTransit.Application.Helper;
using MohamedTransit.Application.Service;
using MohamedTransit.Domain.Data;

namespace MohamedTransit.Application.Commands.UserAccount;

public record ResetPasswordCommand(string Email, string Code, string NewPassword)
    : IRequest<OperationResult<Unit>>;

internal class ResetPasswordCommandHandler : IRequestHandler<ResetPasswordCommand, OperationResult<Unit>>
{
    private const int MaximumPasswordResetAttempts = 5;

    private readonly ApplicationDbContext _context;
    private readonly PasswordService _passwordService;

    public ResetPasswordCommandHandler(
        ApplicationDbContext context,
        PasswordService passwordService)
    {
        _context = context;
        _passwordService = passwordService;
    }

    public async Task<OperationResult<Unit>> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        var result = new OperationResult<Unit>();
        var email = request.Email?.Trim();
        var code = request.Code?.Trim();
        if (string.IsNullOrWhiteSpace(email)
            || string.IsNullOrWhiteSpace(code)
            || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            result.AddError(ErrorCode.UnknownError, "Email, code, and new password are required.");
            return result;
        }

        if (!Regex.IsMatch(code, @"^\d{6}$", RegexOptions.CultureInvariant))
        {
            result.AddError(ErrorCode.UnAuthorized, "Invalid or expired reset code");
            return result;
        }

        var userAccount = await _context.Users
            .FirstOrDefaultAsync(x => x.Email.ToLower() == email.ToLower(), cancellationToken);

        if (userAccount is null)
        {
            result.AddError(ErrorCode.UnAuthorized, "Invalid or expired reset code");
            return result;
        }

        var now = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(userAccount.PasswordResetCodeHash)
            || userAccount.PasswordResetCodeExpiresAt is null
            || userAccount.PasswordResetCodeExpiresAt <= now
            || userAccount.PasswordResetAttempts >= MaximumPasswordResetAttempts)
        {
            userAccount.ClearPasswordResetCode();
            await _context.SaveChangesAsync(cancellationToken);
            result.AddError(ErrorCode.UnAuthorized, "Invalid or expired reset code");
            return result;
        }

        var submittedHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        byte[] storedHash;
        try
        {
            storedHash = Convert.FromHexString(userAccount.PasswordResetCodeHash);
        }
        catch (FormatException)
        {
            userAccount.ClearPasswordResetCode();
            await _context.SaveChangesAsync(cancellationToken);
            result.AddError(ErrorCode.UnAuthorized, "Invalid or expired reset code");
            return result;
        }

        if (storedHash.Length != submittedHash.Length
            || !CryptographicOperations.FixedTimeEquals(storedHash, submittedHash))
        {
            userAccount.RecordFailedPasswordResetAttempt();
            if (userAccount.PasswordResetAttempts >= MaximumPasswordResetAttempts)
            {
                userAccount.ClearPasswordResetCode();
            }

            await _context.SaveChangesAsync(cancellationToken);
            result.AddError(ErrorCode.UnAuthorized, "Invalid or expired reset code");
            return result;
        }

        var hashedPassword = _passwordService.HashPassword(request.NewPassword);
        userAccount.UpdatePassword(hashedPassword);
        userAccount.ClearPasswordResetCode();
        await _context.SaveChangesAsync(cancellationToken);

        result.Message = "Operation success";
        result.Payload = Unit.Value;
        return result;
    }
}
