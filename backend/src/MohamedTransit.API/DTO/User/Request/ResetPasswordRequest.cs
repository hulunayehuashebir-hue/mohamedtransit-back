using System.ComponentModel.DataAnnotations;

namespace MohamedTransit.API.DTO.User.Request;

public class ResetPasswordRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [MaxLength(6)]
    public string? Code { get; set; }

    [MaxLength(32)]
    public string Token { get; set; } = string.Empty;

    [Required, MinLength(8), MaxLength(128)]
    public string NewPassword { get; set; } = string.Empty;
}

