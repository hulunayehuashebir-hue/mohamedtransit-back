using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MohamedTransit.Application.DTO;
using MohamedTransit.Application.Helper;
using MohamedTransit.Application.Services;
using MohamedTransit.Domain.Data;

namespace MohamedTransit.Application.Commands.UserAccount;

public record ForgotPasswordCommand(
    string Email,
    string Lang = "en"
) : IRequest<OperationResult<Unit>>;

public class ForgotPasswordCommandHandler
    : IRequestHandler<ForgotPasswordCommand, OperationResult<Unit>>
{
    private readonly ApplicationDbContext _context;
    private readonly EmailSenderService _emailSenderService;
    private readonly Settings _settings;

    public ForgotPasswordCommandHandler(
        ApplicationDbContext context,
        EmailSenderService emailSenderService,
        IOptions<Settings> settings)
    {
        _context = context;
        _emailSenderService = emailSenderService;
        _settings = settings.Value;
    }

    public async Task<OperationResult<Unit>> Handle(
        ForgotPasswordCommand request,
        CancellationToken cancellationToken)
    {
        var result = new OperationResult<Unit>();

        try
        {
            var emailAddress = request.Email?.Trim();
            if (string.IsNullOrWhiteSpace(emailAddress))
            {
                result.AddError(
                    ErrorCode.UnknownError,
                    "Email is required.");

                return result;
            }

            var userAccount = await _context.Users
                .FirstOrDefaultAsync(
                    x => x.Email.ToLower() == emailAddress.ToLower(),
                    cancellationToken);

            if (userAccount is null)
            {
                result.AddError(
                    ErrorCode.NotFound,
                    "Account Does not Exist");

                return result;
            }

            var email = userAccount.Email;

            if (string.IsNullOrWhiteSpace(email))
            {
                result.AddError(
                    ErrorCode.NotFound,
                    "No email address is associated with this account.");

                return result;
            }

            var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000)
                .ToString(CultureInfo.InvariantCulture);
            var codeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
            userAccount.SetPasswordResetCode(codeHash, DateTime.UtcNow.AddMinutes(15));
            await _context.SaveChangesAsync(cancellationToken);

            var lang = request.Lang is "am" ? "am" : "en";
            var clientBaseUrl = string.IsNullOrWhiteSpace(_settings.FrontendBaseUrl)
                ? "http://localhost:3000"
                : _settings.FrontendBaseUrl.TrimEnd('/');
            string callbackUrl = $"{clientBaseUrl}/{lang}/auth/reset-password?email={Uri.EscapeDataString(email)}";

            string message = $@"
                <div style='font-family: Arial, sans-serif; max-width: 550px; margin: auto; padding: 25px; border: 1px solid #e2e8f0; border-radius: 12px; background-color: #ffffff;'>
                    <h2 style='color: #1e3a8a; text-align: center; margin-bottom: 5px;'>Mohamed Transit</h2>
                    <h3 style='color: #1f2937; text-align: center; margin-top: 0;'>Password Reset Request</h3>
                    <p style='color: #4b5563; font-size: 14px; line-height: 1.6;'>
                        Hello, you recently requested to reset your password for your Mohamed Transit account.
                    </p>

                    <p style='color: #4b5563; font-size: 14px; text-align: center;'>
                        Enter this 6-digit code on the password reset page. It expires in 15 minutes.
                    </p>
                    <div style='margin: 24px 0; padding: 22px 12px; background-color: #f8fafc; border: 1px dashed #3b82f6; border-radius: 8px; text-align: center;'>
                        <span style='font-family: Consolas, monospace; font-size: 36px; font-weight: bold; letter-spacing: 8px; color: #0f172a;'>
                            {code}
                        </span>
                    </div>

                    <p style='color: #4b5563; font-size: 13px; text-align: center;'>
                        Open the reset page and enter the code above:
                    </p>

                    <div style='text-align: center; margin: 25px 0;'>
                        <a href='{callbackUrl}' style='background-color: #1e3a8a; color: #ffffff; padding: 12px 28px; text-decoration: none; border-radius: 8px; font-weight: bold; font-size: 14px; display: inline-block;'>
                            Continue to Password Reset
                        </a>
                    </div>

                    <hr style='border: none; border-top: 1px solid #e2e8f0; margin: 20px 0;' />
                    <p style='color: #94a3b8; font-size: 11px; text-align: center;'>
                        If you did not request a password reset, please ignore this email or contact support if you have concerns.
                    </p>
                </div>";

            await _emailSenderService.SendEmailAsync(
                message,
                "Forgot Password - Mohamed Transit",
                new[] { email },
                null,
                null,
                cancellationToken);

            result.Message = "If the account exists, a 6-digit password reset code has been sent.";
            result.Payload = Unit.Value;

            return result;
        }
        catch (Exception ex)
        {
            result.AddError(
                ErrorCode.ServerError,
                ex.Message);
        }

        return result;
    }

}