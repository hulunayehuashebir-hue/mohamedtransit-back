using Microsoft.EntityFrameworkCore;
using System.Text;
using MohamedTransit.Application.Service;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;
namespace MohamedTransit.Application.DataSeeder;
public interface IDataSeederService
{
    Task SeedAsync();
}

public class DataSeederService : IDataSeederService
{
    private readonly ApplicationDbContext _context;
    private readonly PasswordService _passwordService;

    public DataSeederService(ApplicationDbContext context, PasswordService passwordService)
    {
        _context = context;
        _passwordService = passwordService;
    }

    public async Task SeedAsync()
    {
        // 1. Seed privileges and save
        await SeedPrivilegesAsync();
        await _context.SaveChangesAsync();

        // 2. Seed roles and save (ይህ በጣም ወሳኝ ነው!)
        await SeedRolesAsync();
        await _context.SaveChangesAsync();

        // 3. Seed users and save
        await SeedUsersAsync();
        await _context.SaveChangesAsync();

        // 4. Assign privileges to roles and save
        await AssignPrivilegesToRolesAsync();
        await _context.SaveChangesAsync();

        // 5. Assign roles to users and save
        await AssignRolesToUsersAsync();
        await _context.SaveChangesAsync();

        // 6. Seed development-only records for the Data Encoder dashboard
        await SeedDataEncoderDashboardRecordsAsync();
        await SeedManagerWorkspaceSamplesAsync();
        await BackfillCustomersWithoutKycDocumentsAsync();
    }

    private async Task BackfillCustomersWithoutKycDocumentsAsync()
    {
        var customers = await _context.Customers.ToListAsync();
        var customersWithDocuments = (await _context.CustomerDocuments
                .AsNoTracking()
                .Where(document => document.CustomerId.HasValue)
                .Select(document => document.CustomerId!.Value)
                .Distinct()
                .ToListAsync())
            .ToHashSet();
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var sampleDirectory = Path.Combine(webRoot, "Uploads", "CustomerDocuments");
        Directory.CreateDirectory(sampleDirectory);

        foreach (var customer in customers.Where(customer => !customersWithDocuments.Contains(customer.Id)))
        {
            var uploaderId = customer.CreatedByDataEncoderId;
            var attachedTypes = new HashSet<DocumentType>();

            AddExistingCustomerFile(
                customer,
                customer.BusinessLicense,
                DocumentType.BusinessLicense,
                uploaderId,
                attachedTypes);
            AddExistingCustomerFile(
                customer,
                customer.ImportLicense,
                DocumentType.ImportLicense,
                uploaderId,
                attachedTypes);

            var sampleDocuments = new[]
            {
                (DocumentType.BusinessLicense, "Business License"),
                (DocumentType.ImportLicense, "Import License"),
                (DocumentType.TINCertificate, "TIN Certificate")
            };
            foreach (var (documentType, label) in sampleDocuments)
            {
                if (attachedTypes.Contains(documentType))
                    continue;

                var fileName = $"seed-{customer.Id}-{documentType}.pdf";
                var physicalPath = Path.Combine(sampleDirectory, fileName);
                if (!File.Exists(physicalPath))
                    await File.WriteAllBytesAsync(physicalPath, BuildSampleKycPdf());

                var fileBytes = await File.ReadAllBytesAsync(physicalPath);
                _context.CustomerDocuments.Add(CustomerDocument.Create(
                    fileName,
                    Path.Combine("Uploads", "CustomerDocuments", fileName),
                    $"{label} - Development Sample.pdf",
                    ".pdf",
                    fileBytes.LongLength,
                    "application/pdf",
                    documentType,
                    customer.Id,
                    uploaderId,
                    "Seeded development KYC checklist document.",
                    isRequired: true));
                attachedTypes.Add(documentType);
            }
        }

        await _context.SaveChangesAsync();
    }

    private void AddExistingCustomerFile(
        Customer customer,
        string? storedPath,
        DocumentType documentType,
        long uploaderId,
        ISet<DocumentType> attachedTypes)
    {
        var physicalPath = ResolveExistingCustomerFile(storedPath);
        if (physicalPath is null)
            return;

        var extension = Path.GetExtension(physicalPath).ToLowerInvariant();
        var mimeType = extension switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream"
        };
        var fileInfo = new FileInfo(physicalPath);
        var fullPath = Path.GetFullPath(physicalPath);
        var webRoot = GetKycWebRoots()
            .FirstOrDefault(root => IsPathWithin(root, fullPath));
        var filePath = webRoot is null ? fullPath : Path.GetRelativePath(webRoot, fullPath);
        var fileName = Path.GetFileName(fullPath);

        _context.CustomerDocuments.Add(CustomerDocument.Create(
            fileName,
            filePath,
            fileName,
            extension,
            fileInfo.Length,
            mimeType,
            documentType,
            customer.Id,
            uploaderId,
            $"Backfilled from the customer's stored {documentType} file path.",
            isRequired: true));
        attachedTypes.Add(documentType);
    }

    private static string? ResolveExistingCustomerFile(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
            return null;

        var webRoots = GetKycWebRoots();
        var candidates = Path.IsPathRooted(storedPath)
            ? new[] { Path.GetFullPath(storedPath) }
            : webRoots.Select(root => Path.GetFullPath(Path.Combine(root, storedPath)));
        return candidates.FirstOrDefault(candidate =>
            File.Exists(candidate) &&
            webRoots.Any(root => IsPathWithin(root, candidate)));
    }

    private static string[] GetKycWebRoots() =>
    [
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot")),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"))
    ];

    private static bool IsPathWithin(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relativePath) &&
               relativePath != ".." &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private async Task SeedPrivilegesAsync()
    {
        var privileges = new List<Privilege>
        {
            // Existing System Privileges
            Privilege.Create("User-Create", "Create new users"),
            Privilege.Create("User-Update", "Update user information"),
            Privilege.Create("User-Delete", "Delete users"),
            Privilege.Create("User-GetAll", "View all users"),
            Privilege.Create("User-GetById", "View user details"),
            Privilege.Create("User-Logout", "User logout"),

            Privilege.Create("Roles-Create", "Create new roles"),
            Privilege.Create("Roles-Update", "Update role information"),
            Privilege.Create("Roles-Delete", "Delete roles"),
            Privilege.Create("Roles-GetAll", "View all roles"),
            Privilege.Create("Role-GetById", "View role details"),

            Privilege.Create("Privilege-Create", "Create new privileges"),
            Privilege.Create("Privilege-Update", "Update privilege information"),
            Privilege.Create("Privilege-Delete", "Delete privileges"),
            Privilege.Create("Privilege-GetAll", "View all privileges"),

            // MOT Service Management Privileges
            Privilege.Create("Service-Create", "Create new services"),
            Privilege.Create("Service-Update", "Update service information"),
            Privilege.Create("Service-Delete", "Delete services"),
            Privilege.Create("Service-GetAll", "View all services"),
            Privilege.Create("Service-GetById", "View service details"),
            Privilege.Create("Service-UpdateStatus", "Update service status"),
            Privilege.Create("Service-Assign", "Assign services to users"),

            // MOT Customer Management Privileges
            Privilege.Create("Customer-Create", "Create new customers"),
            Privilege.Create("Customer-Update", "Update customer information"),
            Privilege.Create("Customer-Delete", "Delete customers"),
            Privilege.Create("Customer-GetAll", "View all customers"),
            Privilege.Create("Customer-GetById", "View customer details"),
            Privilege.Create("Customer-Approve", "Approve customer registrations"),
            Privilege.Create("Customer-CreateServiceRequest", "Create service requests"),

            // Document Management Privileges
            Privilege.Create("Document-Upload", "Upload documents"),
            Privilege.Create("Document-Download", "Download documents"),
            Privilege.Create("Document-Verify", "Verify documents"),
            Privilege.Create("Document-Delete", "Delete documents"),
            Privilege.Create("Document-GetAll", "View all documents"),

            // Messaging Privileges
            Privilege.Create("Message-Send", "Send messages"),
            Privilege.Create("Message-GetAll", "View all messages"),
            Privilege.Create("Message-GetById", "View message details"),

            // Notification Privileges
            Privilege.Create("Notification-Create", "Create notifications"),
            Privilege.Create("Notification-GetAll", "View all notifications"),
            Privilege.Create("Notification-MarkAsRead", "Mark notifications as read"),

            // Reporting Privileges
            Privilege.Create("Report-GetAll", "View all reports"),
            Privilege.Create("Report-Export", "Export reports"),

            // Dashboard Privileges
            Privilege.Create("Dashboard-View", "View dashboard"),
            Privilege.Create("Dashboard-Manager", "View manager dashboard"),
            Privilege.Create("Dashboard-Assessor", "View assessor dashboard"),
            Privilege.Create("Dashboard-CaseExecutor", "View case executor dashboard"),
            Privilege.Create("Dashboard-DataEncoder", "View data encoder dashboard"),
            Privilege.Create("Dashboard-GetAdminDashboardDataQuery", "Get admin dashboard data"),
            Privilege.Create("Dashboard-GetAdminDashboardDataSortQuery", "Get admin dashboard data with sorting"),
            Privilege.Create("Dashboard-GetOrganizationDashboardData", "Get organization dashboard data"),
            Privilege.Create("Dashboard-GetOrganizationDashboardDataSortQuery", "Get organization dashboard data with sorting"),
        };

        var existingActions = (await _context.Privileges
                .Select(privilege => privilege.Action)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _context.Privileges.AddRange(
            privileges.Where(privilege => existingActions.Add(privilege.Action)));
    }

    private async Task SeedRolesAsync()
    {
        var roles = new List<Role>
        {
            Role.Create("SuperAdmin", "Super Administrator with full system access"),
            Role.Create("Manager", "Manager with oversight and management capabilities"),
            Role.Create("Assessor", "Assessor responsible for customer verification and approval"),
            Role.Create("CaseExecutor", "Case Executor responsible for service execution"),
            Role.Create("DataEncoder", "Data Encoder responsible for data entry and service creation"),
            Role.Create("Customer", "Customer role for external users"),
        };

        var existingNames = (await _context.Roles
                .Select(role => role.Name)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _context.Roles.AddRange(
            roles.Where(role => existingNames.Add(role.Name)));
    }

    private async Task SeedUsersAsync()
    {
        var users = new List<User>
        {
            // Super Admin
            User.CreateUser(
                "superadmin",
                "superadmin@Mohamedtransit.com",
                "Super",
                "Admin",
                "",
                "+1234567890",
                _passwordService.HashPassword("Admin123!"),
                true,
                AccountStatus.Approved
            ),

            // Manager
            User.CreateUser(
                "manager",
                "manager@Mohamedtransit.com",
                "Nadiya",
                "Manager",
                "",
                "+1234567891",
                _passwordService.HashPassword("Manager123!"),
                false,
                AccountStatus.Approved
            ),

            // Assessor
            User.CreateUser(
                "assessor",
                "hulunayehuashebir@gmail.com",
                "Hulu",
                "Assessor",
                "",
                "+1234567892",
                _passwordService.HashPassword("Assessor123!"),
                false,
                AccountStatus.Approved
            ),

            // Case Executor
            User.CreateUser(
                "caseexecutor",
                "caseexecutor@Mohamedtransit.com",
                "Nebil",
                "Executor",
                "",
                "+1234567893",
                _passwordService.HashPassword("Executor123!"),
                false,
                AccountStatus.Approved
            ),

            // Data Encoder
            User.CreateUser(
                "dataencoder",
                "dataencoder@Mohamedtransit.com",
                "Selam",
                "Encoder",
                "",
                "+1234567894",
                _passwordService.HashPassword("Encoder123!"),
                false,
                AccountStatus.Approved
            ),

            // Customer
            User.CreateUser(
                "customer",
                "customer@Mohamedtransit.com",
                "Customer",
                "User",
                "",
                "+1234567895",
                _passwordService.HashPassword("Customer123!"),
                false,
                AccountStatus.Approved
            ),

            // Development-only importer used by the encoder dashboard sample records
            User.CreateUser(
                "encoder-demo-importer",
                "encoder-demo-importer@example.invalid",
                "Development",
                "Importer",
                "",
                "+0000000000",
                _passwordService.HashPassword(Guid.NewGuid().ToString("N")),
                false,
                AccountStatus.Approved
            ),
        };

        var existingUsers = await _context.Users
            .Select(user => new { user.Username, user.Email })
            .ToListAsync();
        var existingUsernames = existingUsers
            .Select(user => user.Username)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingEmails = existingUsers
            .Select(user => user.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _context.Users.AddRange(users.Where(user =>
            existingUsernames.Add(user.Username) &&
            existingEmails.Add(user.Email)));
    }

    private async Task AssignPrivilegesToRolesAsync()
    {
        var roles = await _context.Roles.ToDictionaryAsync(role => role.Name);
        var allPrivileges = await _context.Privileges.ToListAsync();
        var existingAssignments = (await _context.RolePrivileges
                .Select(assignment => new { assignment.RoleId, assignment.PrivilegeId })
                .ToListAsync())
            .Select(assignment => (assignment.RoleId, assignment.PrivilegeId))
            .ToHashSet();
        var rolePrivileges = new List<RolePrivilege>();

        void AddAssignments(string roleName, Func<string, bool> hasPrivilege)
        {
            if (!roles.TryGetValue(roleName, out var role))
            {
                return;
            }

            foreach (var privilege in allPrivileges.Where(privilege => hasPrivilege(privilege.Action)))
            {
                if (existingAssignments.Add((role.Id, privilege.Id)))
                {
                    rolePrivileges.Add(RolePrivilege.Create(role.Id, privilege.Id));
                }
            }
        }

        AddAssignments("SuperAdmin", _ => true);
        AddAssignments("Manager", action =>
            action.Contains("Service-") ||
            action.Contains("Customer-") ||
            action.Contains("Document-") ||
            action.Contains("Message-") ||
            action.Contains("Notification-") ||
            action.Contains("Report-") ||
            action.Contains("Dashboard-") ||
            action.Contains("User-GetAll") ||
            action.Contains("Role-GetAll"));
        AddAssignments("Assessor", action =>
            action.Contains("Customer-Approve") ||
            action.Contains("Customer-GetAll") ||
            action.Contains("Customer-GetById") ||
            action.Contains("Document-Verify") ||
            action.Contains("Document-GetAll") ||
            action.Contains("Message-") ||
            action.Contains("Notification-") ||
            action.Contains("Dashboard-Assessor"));
        AddAssignments("CaseExecutor", action =>
            action.Contains("Service-Update") ||
            action.Contains("Service-UpdateStatus") ||
            action.Contains("Service-GetAll") ||
            action.Contains("Service-GetById") ||
            action.Contains("Document-Upload") ||
            action.Contains("Document-Download") ||
            action.Contains("Document-GetAll") ||
            action.Contains("Message-") ||
            action.Contains("Notification-") ||
            action.Contains("Dashboard-CaseExecutor"));
        AddAssignments("DataEncoder", action =>
            action.Contains("Service-Create") ||
            action.Contains("Service-GetAll") ||
            action.Contains("Service-GetById") ||
            action.Contains("Customer-Create") ||
            action.Contains("Customer-GetAll") ||
            action.Contains("Customer-GetById") ||
            action.Contains("Document-Upload") ||
            action.Contains("Document-GetAll") ||
            action.Contains("Message-") ||
            action.Contains("Notification-") ||
            action.Contains("Dashboard-DataEncoder"));
        AddAssignments("Customer", action =>
            action.Contains("Service-GetById") ||
            action.Contains("Customer-GetById") ||
            action.Contains("Customer-CreateServiceRequest") ||
            action.Contains("Document-Download") ||
            action.Contains("Message-Send") ||
            action.Contains("Message-GetAll") ||
            action.Contains("Notification-GetAll"));

        _context.RolePrivileges.AddRange(rolePrivileges);
    }

    private async Task AssignRolesToUsersAsync()
    {
        var roles = await _context.Roles.ToDictionaryAsync(role => role.Name);
        var users = await _context.Users.ToDictionaryAsync(user => user.Username);
        var existingAssignments = (await _context.UserRoles
                .Select(assignment => new { assignment.UserId, assignment.RoleId })
                .ToListAsync())
            .Select(assignment => (assignment.UserId, assignment.RoleId))
            .ToHashSet();
        var userRoles = new List<UserRole>();
        var roleAssignments = new[]
        {
            ("superadmin", "SuperAdmin"),
            ("manager", "Manager"),
            ("assessor", "Assessor"),
            ("caseexecutor", "CaseExecutor"),
            ("dataencoder", "DataEncoder"),
            ("customer", "Customer"),
            ("encoder-demo-importer", "Customer")
        };

        foreach (var (username, roleName) in roleAssignments)
        {
            if (users.TryGetValue(username, out var user) &&
                roles.TryGetValue(roleName, out var role) &&
                existingAssignments.Add((user.Id, role.Id)))
            {
                userRoles.Add(UserRole.Create(user.Id, role.Id));
            }
        }
 
        _context.UserRoles.AddRange(userRoles);
    }

    private async Task SeedDataEncoderDashboardRecordsAsync()
    {
        var dataEncoder = await _context.Users
            .FirstOrDefaultAsync(user => user.Username == "dataencoder");
        var demoImporter = await _context.Users
            .FirstOrDefaultAsync(user => user.Username == "encoder-demo-importer");

        if (dataEncoder == null || demoImporter == null)
        {
            return;
        }

        const string demoTin = "DEV-ENCODER-TEST-001";
        var customer = await _context.Customers
            .FirstOrDefaultAsync(record => record.TINNumber == demoTin);

        if (customer == null)
        {
            customer = Customer.Create(
                "Development Demo Importer",
                demoTin,
                "DEV-LICENSE-001",
                "Development test record",
                "Addis Ababa",
                "Addis Ababa",
                "0000",
                "Development Importer",
                "+0000000000",
                "encoder-demo-importer@example.invalid",
                "Development test data",
                "DEV-IMPORT-LICENSE-001",
                null,
                demoImporter.Id,
                dataEncoder.Id);

            _context.Customers.Add(customer);
            await _context.SaveChangesAsync();
        }
        await EnsureSampleKycDocumentsAsync(customer, demoImporter.Id);

        const string trackingNumber = "MT-DEV-ENCODER-SAMPLE-001";
        var serviceExists = await _context.Shipments
            .AnyAsync(shipment => shipment.TrackingNumber == trackingNumber);
        if (serviceExists)
        {
            return;
        }

        var service = Shipment.Create(
            trackingNumber,
            "Development dashboard sample shipment",
            demoImporter.Id,
            "Development dashboard sample shipment",
            TransportMode.MultiModalSeaRail,
            HubLocation.Mojo,
            "Development origin",
            "Mojo",
            dataEncoder.Id);
        service.UpdateServiceType(ServiceType.Multimodal);
        service.UpdateDetails(
            "Development dashboard sample shipment",
            "Mojo",
            1000m,
            "Development sample",
            "Development origin",
            null);
        service.SetCreatedByDataEncoder(dataEncoder.Id);

        _context.Shipments.Add(service);
        await _context.SaveChangesAsync();
    }

    private async Task SeedManagerWorkspaceSamplesAsync()
    {
        if (await _context.Shipments.AnyAsync(shipment => shipment.ServiceCode.StartsWith("SVC-DEV-")))
            return;

        var caseExecutor = await _context.Users.FirstOrDefaultAsync(user => user.Username == "caseexecutor");
        var assessor = await _context.Users.FirstOrDefaultAsync(user => user.Username == "assessor");
        var encoder = await _context.Users.FirstOrDefaultAsync(user => user.Username == "dataencoder");
        var demoImporter = await _context.Users.FirstOrDefaultAsync(user => user.Username == "encoder-demo-importer");
        var customerImporter = await _context.Users.FirstOrDefaultAsync(user => user.Username == "customer");
        if (caseExecutor is null || assessor is null || encoder is null ||
            demoImporter is null || customerImporter is null)
            return;

        var existingCustomer = await _context.Customers
            .FirstOrDefaultAsync(customer => customer.UserId == customerImporter.Id);
        if (existingCustomer is null)
        {
            existingCustomer = Customer.Create(
                "Aster Pharmaceutical Imports",
                "ET-001-884201",
                "LIC-2026-8842",
                "Bole Road, Addis Ababa",
                "Addis Ababa",
                "Addis Ababa",
                "1000",
                "Mekdes Tadesse",
                "+251911234567",
                customerImporter.Email,
                "Pharmaceutical importer",
                "IMP-2026-8842",
                null,
                customerImporter.Id,
                encoder.Id);
            _context.Customers.Add(existingCustomer);
        }

        var demoCustomer = await _context.Customers
            .FirstOrDefaultAsync(customer => customer.UserId == demoImporter.Id);
        if (demoCustomer is null)
        {
            demoCustomer = Customer.Create(
                "Blue Nile Machinery Trading",
                "ET-001-884202",
                "LIC-2026-8843",
                "Kality Industrial Zone, Addis Ababa",
                "Addis Ababa",
                "Addis Ababa",
                "1000",
                "Dawit Bekele",
                "+251922345678",
                demoImporter.Email,
                "Machinery importer",
                "IMP-2026-8843",
                null,
                demoImporter.Id,
                encoder.Id);
            _context.Customers.Add(demoCustomer);
        }

        await _context.SaveChangesAsync();
        await EnsureSampleKycDocumentsAsync(existingCustomer, encoder.Id);
        await EnsureSampleKycDocumentsAsync(demoCustomer, encoder.Id);

        var unimodal = CreateManagerSample(
            "MT-DEV-MANAGER-UNIMODAL",
            "SVC-DEV-2026-0448",
            "MT/UM/2026/0448",
            "Cold-chain pharmaceutical airfreight",
            customerImporter.Id,
            TransportMode.UniModalRoad,
            ServiceType.Unimodal,
            "Addis Ababa",
            "Mojo Dry Port",
            "ET719 — Ethiopian Cargo",
            "Bole International Airport → Mojo dry port",
            "TIN ET-001-884201; 4 insulated packages; 312 kg; AWB 071-884201",
            "Yellow",
            caseExecutor.Id,
            assessor.Id);
        var multimodal = CreateManagerSample(
            "MT-DEV-MANAGER-MULTIMODAL",
            "SVC-DEV-2026-0449",
            "MT/MM/2026/0449",
            "Industrial packaging equipment and spare parts",
            demoImporter.Id,
            TransportMode.MultiModalSeaRail,
            ServiceType.Multimodal,
            "Djibouti Port",
            "Modjo Dry Port",
            "MV Han Bright / Voy. 402S",
            "Djibouti Port → Dire Dawa rail terminal → Modjo dry port",
            "TIN ET-001-884202; 1 x 40ft container; 8,400 kg; BL HBL-402S-8849",
            "Green",
            caseExecutor.Id,
            assessor.Id);

        _context.Shipments.AddRange(unimodal, multimodal);
        await _context.SaveChangesAsync();
        await SeedStagesAsync(unimodal, 1, caseExecutor);
        await SeedStagesAsync(multimodal, 4, caseExecutor);
        await _context.SaveChangesAsync();
    }

    private async Task EnsureSampleKycDocumentsAsync(Customer customer, long uploadedByUserId)
    {
        var existingTypes = await _context.CustomerDocuments
            .AsNoTracking()
            .Where(document =>
                document.CustomerId == customer.Id &&
                document.RecordStatus == RecordStatus.Active)
            .Select(document => document.DocumentType)
            .ToListAsync();
        var sampleDocuments = new[]
        {
            (DocumentType.BusinessLicense, "Business License"),
            (DocumentType.ImportLicense, "Import License"),
            (DocumentType.TINCertificate, "TIN Certificate")
        };
        var uploadDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "wwwroot",
            "Uploads",
            "CustomerDocuments");
        Directory.CreateDirectory(uploadDirectory);
        var fileBytes = BuildSampleKycPdf();
        foreach (var (documentType, label) in sampleDocuments)
        {
            if (existingTypes.Contains(documentType))
                continue;

            var fileName = $"seed-{customer.Id}-{documentType}.pdf";
            var filePath = Path.Combine(uploadDirectory, fileName);
            if (!File.Exists(filePath))
                await File.WriteAllBytesAsync(filePath, fileBytes);

            _context.CustomerDocuments.Add(CustomerDocument.Create(
                fileName,
                Path.Combine("Uploads", "CustomerDocuments", fileName),
                $"{label} - Development Sample.pdf",
                ".pdf",
                fileBytes.LongLength,
                "application/pdf",
                documentType,
                customer.Id,
                uploadedByUserId,
                "Seeded development KYC checklist document.",
                isRequired: true,
                expiryDate: DateTime.UtcNow.Date.AddYears(1)));
        }

        if (sampleDocuments.Any(document => !existingTypes.Contains(document.Item1)))
            await _context.SaveChangesAsync();
    }

    private static byte[] BuildSampleKycPdf()
    {
        const string pageContent = "BT /F1 14 Tf 72 720 Td (Development KYC sample document) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(pageContent)} >>\nstream\n{pageContent}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(index + 1).Append(" 0 obj\n")
                .Append(objects[index]).Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n")
            .Append("0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1)
            .Append(" /Root 1 0 R >>\nstartxref\n").Append(xrefOffset).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static Shipment CreateManagerSample(
        string trackingNumber,
        string serviceCode,
        string referenceNumber,
        string title,
        long importerId,
        TransportMode mode,
        ServiceType serviceType,
        string origin,
        string destination,
        string carrier,
        string route,
        string consignment,
        string riskChannel,
        long caseExecutorId,
        long assessorId)
    {
        var service = Shipment.Create(
            trackingNumber,
            title,
            importerId,
            title,
            mode,
            HubLocation.Mojo,
            origin,
            destination);
        service.UpdateServiceType(serviceType);
        service.UpdateManagerWorkspace(
            serviceCode,
            referenceNumber,
            title,
            "InProgress",
            riskChannel,
            DateTime.UtcNow.AddDays(4),
            DateTime.UtcNow.AddDays(2),
            carrier,
            route,
            consignment,
            0);
        service.AssignCaseExecutor(caseExecutorId);
        service.AssignAssessor(assessorId);
        return service;
    }

    private async Task SeedStagesAsync(Shipment service, int activeStageNumber, User executor)
    {
        var stageDefinitions = (service.ServiceType == ServiceType.Unimodal
            ? new[]
            {
                (ShipmentStage.LocalPermission, new[] { "Import permit", "Commercial invoice" }),
                (ShipmentStage.Arrival, new[] { "Air waybill", "Temperature log", "Packing list" }),
                (ShipmentStage.Clearance, new[] { "Clearance request", "Commercial invoice" })
            }
            : new[]
            {
                (ShipmentStage.PrepaymentInvoice, new[] { "Proforma invoice", "Packing list" }),
                (ShipmentStage.TransitPermission, new[] { "Transit permit", "Commercial invoice" }),
                (ShipmentStage.Amendment, new[] { "Amendment request" }),
                (ShipmentStage.DropRisk, new[] { "Risk assessment", "Commercial invoice" }),
                (ShipmentStage.DeliveryOrder, new[] { "Delivery order", "Bill of lading" }),
                (ShipmentStage.WarehouseStatus, new[] { "Warehouse status report" }),
                (ShipmentStage.Inspection, new[] { "Inspection report", "Packing list" }),
                (ShipmentStage.AssessmentandTaxPayment, new[] { "Valuation worksheet", "Tax payment receipt" }),
                (ShipmentStage.Emergency, new[] { "Emergency authorization" }),
                (ShipmentStage.ExitandStoragePayment, new[] { "Exit authorization", "Storage payment receipt" }),
                (ShipmentStage.Transportation, new[] { "Transport license", "Delivery note" }),
                (ShipmentStage.Clearance, new[] { "Clearance certificate" })
            }).ToArray();

        var stages = stageDefinitions.Select((definition, index) =>
        {
            var number = index + 1;
            var stage = ServiceStageExecution.Create(service.Id, definition.Item1, executor.Id, number);
            if (number < activeStageNumber)
            {
                stage.UpdateManagerDetails(
                    number,
                    StageStatus.Completed,
                    service.ManagerRiskChannel,
                    "Stage completed and supporting records reviewed.",
                    "All stage requirements were reviewed and completed.",
                    executor.Id,
                    $"{executor.FirstName} {executor.LastName}");
            }
            else if (number == activeStageNumber)
            {
                stage.UpdateManagerDetails(
                    number,
                    StageStatus.InProgress,
                    service.ManagerRiskChannel,
                    "Work is in progress; verify outstanding documents before advancing.",
                    null,
                    executor.Id);
            }
            return (Stage: stage, RequiredTypes: definition.Item2);
        }).ToList();

        _context.ServiceStageExecutions.AddRange(stages.Select(item => item.Stage));
        await _context.SaveChangesAsync();
        foreach (var item in stages)
        {
            foreach (var documentType in item.RequiredTypes)
                _context.StageRequiredDocuments.Add(
                    StageRequiredDocument.Create(item.Stage.Id, documentType));
        }

        service.UpdateManagerWorkspace(
            service.ServiceCode,
            service.ReferenceNumber,
            service.Title,
            "InProgress",
            service.ManagerRiskChannel,
            service.ETA,
            service.SlaDeadline,
            service.CarrierOrVessel,
            service.LogisticsRouteSummary,
            service.ConsignmentDetails,
            (int)Math.Round(100d * (activeStageNumber - 1) / stages.Count));
    }
}
