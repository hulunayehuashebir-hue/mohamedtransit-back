using MohamedTransit.Domain;
using MohamedTransit.Domain.Common;
namespace MohamedTransit.Domain.Entities;
public class Customer : BaseEntity
{
    private readonly List<CustomerDocument> _documents = new List<CustomerDocument>();
public string BusinessName { get; private set; } = string.Empty;
    public string TINNumber { get; private set; } = string.Empty;
    public string BusinessLicense { get; private set; } = string.Empty;
    public string BusinessAddress { get; private set; } = string.Empty;
    public DateTime? StartDate { get; private set; }
    public string City { get; private set; } = string.Empty;
    public string State { get; private set; } = string.Empty;
    public string PostalCode { get; private set; } = string.Empty;

   public string ContactPerson { get; private set; } = string.Empty;
    public string ContactPhone { get; private set; } = string.Empty;
    public string ContactEmail { get; private set; } = string.Empty;
    public string BusinessType { get; private set; } = string.Empty;
    public string ImportLicense { get; private set; } = string.Empty;
    public DateTime? ImportLicenseExpiry { get; private set; }
    public bool IsVerified { get; private set; }
    public bool IsReturnedForRevision { get; private set; }
    public string VerificationStatus { get; private set; } = "Pending";
    public DateTime? VerifiedAt { get; private set; }
    public long? VerifiedByUserId { get; private set; }
    public string? VerificationNotes { get; private set; }
    public string? PreviousReviewNote { get; private set; }

    // Foreign Keys
    public long? UserId { get; private set; }
    public long CreatedByDataEncoderId { get; private set; }

    // Navigation Properties
    public User User { get; set; } = null!;
    public User CreatedByDataEncoder { get; set; } = null!;
    public User? VerifiedByUser { get; set; }

    public ICollection<CustomerDocument> Documents => _documents;

    public bool IsRegistrationComplete() =>
        IsVerified ||
        HasRegistrationValue(BusinessName) &&
        HasRegistrationValue(TINNumber) &&
        HasRegistrationValue(BusinessAddress);

    public static Customer Create(
        string businessName,
        string tinNumber,
        string businessLicense,
        string businessAddress,
        string city,
        string state,
        string postalCode,
        string contactPerson,
        string contactPhone,
        string contactEmail,
        string businessType,
        string importLicense,
        DateTime? importLicenseExpiry,
        long userId,
        long createdByDataEncoderId,
        DateTime? startDate = null)
    {
        return new Customer
        {
            BusinessName = businessName,
            TINNumber = tinNumber,
            BusinessLicense = businessLicense,
            BusinessAddress = businessAddress,
            City = city,
            State = state,
            PostalCode = postalCode,
            ContactPerson = contactPerson,
            ContactPhone = contactPhone,
            ContactEmail = contactEmail,
            BusinessType = businessType,
            ImportLicense = importLicense,
            ImportLicenseExpiry = importLicenseExpiry,
            UserId = userId,
            CreatedByDataEncoderId = createdByDataEncoderId,
            IsVerified = false,
            VerificationStatus = "Pending",
            RecordStatus = RecordStatus.Active
        };
    }

    public void Verify(long verifiedByUserId, string? notes = null)
    {
        PreviousReviewNote = VerificationNotes;
        IsVerified = true;
        IsReturnedForRevision = false;
        VerificationStatus = "Verified";
        VerifiedAt = DateTime.UtcNow;
        VerifiedByUserId = verifiedByUserId;
        VerificationNotes = notes;
        RecordStatus = RecordStatus.Active;
        SetUpdated();
    }

    public void ReturnForRevision(string notes)
    {
        PreviousReviewNote = VerificationNotes;
        IsVerified = false;
        IsReturnedForRevision = true;
        VerificationStatus = "RequestChanges";
        VerifiedByUserId = null;
        VerifiedAt = null;
        RecordStatus = RecordStatus.Active;
        VerificationNotes = notes;
        SetUpdated();
    }

    public void RejectVerification(string? notes = null)
    {
        PreviousReviewNote = VerificationNotes;
        IsVerified = false;
        IsReturnedForRevision = false;
        VerificationStatus = "Rejected";
        VerifiedByUserId = null;
        VerifiedAt = null;
        RecordStatus = RecordStatus.Active;
        VerificationNotes = notes;
        SetUpdated();
    }

    public void UpdateBusinessInfo(
        string businessName,
        string businessAddress,
        string city,
        string state,
        string postalCode,
        string contactPerson,
        string contactPhone,
        string contactEmail)
    {
        BusinessName = businessName;
        BusinessAddress = businessAddress;
        City = city;
        State = state;
        PostalCode = postalCode;
        ContactPerson = contactPerson;
        ContactPhone = contactPhone;
        ContactEmail = contactEmail;
        if (!IsVerified)
            VerificationStatus = "Pending";
        IsReturnedForRevision = false;
        SetUpdated();
    }

    public void UpdateRegistrationDetails(
        string businessName,
        string tinNumber,
        string businessLicense,
        string businessAddress,
        string city,
        string state,
        string postalCode,
        string contactPerson,
        string contactPhone,
        string contactEmail,
        string businessType,
        string importLicense,
        DateTime? importLicenseExpiry)
    {
        BusinessName = businessName;
        TINNumber = tinNumber;
        BusinessLicense = businessLicense;
        BusinessAddress = businessAddress;
        City = city;
        State = state;
        PostalCode = postalCode;
        ContactPerson = contactPerson;
        ContactPhone = contactPhone;
        ContactEmail = contactEmail;
        BusinessType = businessType;
        ImportLicense = importLicense;
        ImportLicenseExpiry = importLicenseExpiry;
        if (!IsVerified)
            VerificationStatus = "Pending";
        IsReturnedForRevision = false;
        SetUpdated();
    }

    public void AddDocument(CustomerDocument document)
    {
        _documents.Add(document);
    }

    private static bool HasRegistrationValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().ToLowerInvariant() is not ("string" or "default" or "n/a" or "na" or "0");
}
