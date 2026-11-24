using System.ComponentModel.DataAnnotations;

namespace MohamedTransit.API.DTO.User.Request;

public class ForgotPasswordRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [RegularExpression("^(en|am)$")]
    public string Lang { get; set; } = "en";
}
