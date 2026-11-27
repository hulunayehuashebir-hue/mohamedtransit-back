using MohamedTransit.Domain.Common;

namespace MohamedTransit.Domain.Entities;

public class Shipment : BaseEntity
{
    // Public Properties ከ Private Set ጋር (Encapsulation)
    public string TrackingNumber { get; private set; } = string.Empty;
    public string ServiceCode { get; private set; } = string.Empty;
    public string ReferenceNumber { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string WorkflowStatus { get; private set; } = string.Empty;
    public string ManagerRiskChannel { get; private set; } = string.Empty;
    public DateTime? ETA { get; private set; }
    public DateTime? SlaDeadline { get; private set; }
    public string CarrierOrVessel { get; private set; } = string.Empty;
    public string LogisticsRouteSummary { get; private set; } = string.Empty;
    public string ConsignmentDetails { get; private set; } = string.Empty;
    public int CompletionPercentage { get; private set; }
    public long ImporterId { get; private set; }

    // Navigation Property ለ EF Core
    public User Importer { get; private set; } = null!;

    public string Description { get; private set; } = string.Empty;
    public TransportMode Mode { get; private set; }
    public long? CreatedByDataEncoderId { get; private set; }
    public void UpdateServiceType(ServiceType serviceType)
    {
        ServiceType = serviceType;
        SetUpdated();
    }
    public void Submit()
    {
        UpdateStatus(ShipmentStatus.Submitted);
    }
    public User? CreatedByDataEncoder { get; private set; }
    public HubLocation AssignedHub { get; private set; }
    public ShipmentStatus Status { get; private set; } = ShipmentStatus.Submitted;
    public DateTime? CompletedAt { get; private set; }

    // Creator Information Properties
    public long? CreatedByUserId { get; private set; }
    public User? CreatedByUser { get; private set; }

    // Additional Properties ለ UpdateShipment Command
    public string RouteCategory { get; private set; } = string.Empty;
    public decimal DeclaredValue { get; private set; }
    public string TaxCategory { get; private set; } = string.Empty;
    public string CountryOfOrigin { get; private set; } = string.Empty;
    public RiskLevel? RiskLevel { get; private set; }

    // Additional Navigation & Assignment Properties
    public long? AssignedCaseExecutorId { get; private set; }
    public User? AssignedCaseExecutor { get; private set; }
    // ይህንን property በ Shipment.cs ውስጥ ያክሉት
    public ServiceType? ServiceType { get; private set; }
    public long? AssignedAssessorId { get; private set; }
    public User? AssignedAssessor { get; private set; }

    public string? AssignmentNotes { get; set; }

    // Navigation Property ለ ServiceStageExecution
    public ICollection<ServiceStageExecution> Stages { get; private set; } = new List<ServiceStageExecution>();

    // Navigation Properties
    public ICollection<ServiceDocument> Documents { get; private set; } = new List<ServiceDocument>();
    public ICollection<ServiceMessage> Messages { get; private set; } = new List<ServiceMessage>();

    // Value Object Properties (Route)
    public string Origin { get; private set; } = string.Empty;
    public string Destination { get; private set; } = string.Empty;

    // Private Constructor (EF Core Materialization)
    private Shipment() { }

    // Static Factory Method
    public static Shipment Create(
        string trackingNumber,
        string v,
        long importerId,
        string description,
        TransportMode mode,
        HubLocation assignedHub,
        string origin,
        string destination,
        long? createdByUserId = null)
    {
        if (string.IsNullOrWhiteSpace(trackingNumber))
            throw new ArgumentException("Tracking number is required.", nameof(trackingNumber));

        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Origin and Destination cannot be empty.");

        return new Shipment
        {
            TrackingNumber = trackingNumber,
            ImporterId = importerId,
            Description = description,
            Mode = mode,
            AssignedHub = assignedHub,
            Origin = origin,
            Destination = destination,
            Status = ShipmentStatus.Submitted,
            RecordStatus = RecordStatus.Active,
            CreatedByUserId = createdByUserId
        };
    }

    // Domain Methods
    public void UpdateStatus(ShipmentStatus newStatus)
    {
        if (newStatus == ShipmentStatus.Completed && Status != ShipmentStatus.Completed)
        {
            CompletedAt = DateTime.UtcNow;
        }
        else if (Status == ShipmentStatus.Completed)
        {
            CompletedAt = null;
        }

        Status = newStatus;
        SetUpdated();
    }

    public void UpdateDetails(
        string description,
        string routeCategory,
        decimal declaredValue,
        string taxCategory,
        string countryOfOrigin,
        RiskLevel? riskLevel)
    {
        Description = description;
        RouteCategory = routeCategory;
        DeclaredValue = declaredValue;
        TaxCategory = taxCategory;
        CountryOfOrigin = countryOfOrigin;
        RiskLevel = riskLevel;

        SetUpdated();
    }

    public void SetCreatedByUser(long userId)
    {
        CreatedByUserId = userId;
        SetUpdated();
    }

    public void SetCreatedByDataEncoder(long userId)
    {
        CreatedByDataEncoderId = userId;
    }

    public void AssignCaseExecutor(long caseExecutorId)
    {
        AssignedCaseExecutorId = caseExecutorId;
        SetUpdated();
    }

    public void AssignAssessor(long assessorId)
    {
        AssignedAssessorId = assessorId;
        SetUpdated();
    }

    public void AddDocument(ServiceDocument document)
    {
        Documents.Add(document);
        SetUpdated();
    }

    public void AddMessage(ServiceMessage message)
    {
        Messages.Add(message);
        SetUpdated();
    }
    public void UpdateRiskLevel(RiskLevel? riskLevel)
    {
        RiskLevel = riskLevel;
        SetUpdated();
    }

    public void UpdateAssessorRiskChannel(RiskLevel riskLevel)
    {
        ManagerRiskChannel = riskLevel.ToString();
        RiskLevel = riskLevel;
        SetUpdated();
    }

    public void UpdateManagerWorkspace(
        string serviceCode,
        string referenceNumber,
        string title,
        string workflowStatus,
        string riskChannel,
        DateTime? eta,
        DateTime? slaDeadline,
        string carrierOrVessel,
        string logisticsRouteSummary,
        string consignmentDetails,
        int completionPercentage)
    {
        ServiceCode = serviceCode;
        ReferenceNumber = referenceNumber;
        Title = title;
        WorkflowStatus = workflowStatus;
        ManagerRiskChannel = riskChannel;
        ETA = eta;
        SlaDeadline = slaDeadline;
        CarrierOrVessel = carrierOrVessel;
        LogisticsRouteSummary = logisticsRouteSummary;
        ConsignmentDetails = consignmentDetails;
        CompletionPercentage = Math.Clamp(completionPercentage, 0, 100);

        RiskLevel = riskChannel.ToLowerInvariant() switch
        {
            "green" => MohamedTransit.Domain.Common.RiskLevel.Green,
            "yellow" => MohamedTransit.Domain.Common.RiskLevel.Yellow,
            "blue" => MohamedTransit.Domain.Common.RiskLevel.Blue,
            "red" => MohamedTransit.Domain.Common.RiskLevel.Red,
            _ => RiskLevel
        };
        Status = workflowStatus switch
        {
            "Completed" => ShipmentStatus.Completed,
            "Blocked" => ShipmentStatus.InProgress,
            "Pending" => ShipmentStatus.Submitted,
            _ => ShipmentStatus.InProgress
        };
        SetUpdated();
    }

    public void UpdateDataEncoderWorkspace(string serviceCode, string referenceNumber, string title)
    {
        ServiceCode = serviceCode;
        ReferenceNumber = referenceNumber;
        Title = title;
        WorkflowStatus = Status.ToString();
        SetUpdated();
    }
}
