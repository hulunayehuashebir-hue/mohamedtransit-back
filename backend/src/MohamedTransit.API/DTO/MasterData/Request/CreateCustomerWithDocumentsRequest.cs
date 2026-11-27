using Microsoft.AspNetCore.Http;

namespace MohamedTransit.API.DTO.MasterData.Request;

public sealed class CreateCustomerWithDocumentsRequest
{
    public string BusinessName { get; set; } = string.Empty;
    public string TINNumber { get; set; } = string.Empty;
    public string BusinessLicense { get; set; } = string.Empty;
    public string BusinessAddress { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string BusinessType { get; set; } = string.Empty;
    public string ImportLicense { get; set; } = string.Empty;
    public DateTime? ImportLicenseExpiry { get; set; }
    public long UserId { get; set; }
    public IFormFile? BusinessLicenseFile { get; set; }
    public DateTime? BusinessLicenseExpiresAt { get; set; }
    public IFormFile? ImportLicenseFile { get; set; }
    public IFormFile? TINCertificateFile { get; set; }
    public DateTime? TINCertificateExpiresAt { get; set; }
    public IFormFile? LegalDocumentFile { get; set; }
    public DateTime? LegalDocumentExpiresAt { get; set; }
}
