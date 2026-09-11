using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace RemoteDebugger.Lab;

/// <summary>
/// Reads the receipt produced by the SYSTEM broker's RemoteDebuggerProvisionV1
/// guest setup.  The Lab never provisions, elevates, or trusts a path supplied
/// by the receipt without checking the bytes and protected installation roots.
/// </summary>
internal static class ProvisioningEvidence
{
    public const string EvidenceFileName = "remote-debugger-provisioning.json";
    public const string ProfileName = "RemoteDebuggerProvisionV1";
    private const int ExpectedFormatVersion = 1;
    private const uint TokenQuery = 0x0008;
    private const int ErrorInsufficientBuffer = 122;
    private const int TokenElevationTypeInformation = 18;
    private const int TokenIntegrityLevelInformation = 25;
    private const int SecurityMandatoryMediumRid = 0x2000;
    private const int SecurityMandatoryHighRid = 0x3000;
    private const int SecurityMandatorySystemRid = 0x4000;
    private const int SecurityMandatoryLowRid = 0x1000;

    internal sealed record BrokerReceipt(
        int FormatVersion,
        string Profile,
        string RequestId,
        string FixtureSha256,
        string ManagedExecutablePath,
        string ManagedExecutableSha256,
        string ServiceExecutablePath,
        string ServiceExecutableSha256,
        string ServiceName,
        string ServiceStatus,
        string ServiceStartMode,
        string PublisherThumbprint,
        string RegisteredUserSid,
        string ReceiptPath,
        DateTimeOffset ProvisionedUtc,
        string EvidencePath);

    internal sealed record ProductIdentity(
        int ProcessId,
        long StartTicks,
        string ExecutablePath,
        string UserSid,
        int SessionId,
        string IntegrityLevel,
        string ElevationType,
        bool MediumIntegrity,
        bool NonFullElevation,
        bool InteractiveSession,
        bool RegisteredUserMatches,
        bool PathMatches)
    {
        public bool Accepted => MediumIntegrity && NonFullElevation && InteractiveSession && RegisteredUserMatches && PathMatches;
    }

    internal sealed record GuestCommandObservation(int ExitCode, string Stdout, string Stderr);

    internal sealed record GuestServiceObservation(string Name, string State, string StartMode, string StartName, string PathName, string Error = "")
    {
        public bool HasError => !string.IsNullOrWhiteSpace(Error);
    }

    internal sealed record GuestObservation(
        int FormatVersion,
        string RequestId,
        DateTimeOffset CapturedUtc,
        GuestCommandObservation PowerRequests,
        GuestCommandObservation Firewall,
        GuestServiceObservation[] Services,
        string EvidencePath)
    {
        public bool Fresh => DateTimeOffset.UtcNow - CapturedUtc >= TimeSpan.Zero && DateTimeOffset.UtcNow - CapturedUtc <= TimeSpan.FromSeconds(10);
        public string[] ServiceErrors => Services.Where(service => service.HasError).Select(service => service.Error).ToArray();
    }

    public static string EvidencePath(string output) => Path.Combine(output, EvidenceFileName);

    public static string SourceEvidencePath(string output)
    {
        string requestId = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)));
        if (requestId.Length == 0 || requestId.Length > 128 || requestId.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new InvalidDataException("The Lab output directory does not identify a valid broker request.");
        return Path.Combine(@"C:\CodexGuest\Provisioning", requestId, EvidenceFileName);
    }

    public static bool TryValidate(string expectedFixturePath, string output, out BrokerReceipt receipt, out string error)
    {
        receipt = null!;
        error = "";
        try
        {
            string evidencePath = SourceEvidencePath(output);
            if (!File.Exists(evidencePath))
            {
                error = "The SYSTEM broker did not publish the RemoteDebuggerProvisionV1 evidence file.";
                return false;
            }
            var evidenceInfo = new FileInfo(evidencePath);
            if (evidenceInfo.Length <= 0 || evidenceInfo.Length > 128 * 1024)
                throw new InvalidDataException("The provisioning evidence file is outside the bounded size limit.");

            JsonElement value = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(evidencePath));
            int formatVersion = RequiredInt(value, "FormatVersion");
            string profile = RequiredString(value, "Profile");
            string requestId = RequiredString(value, "RequestId");
            string fixtureHash = NormalizeHash(RequiredString(value, "FixtureSha256"), "FixtureSha256");
            string managedPath = RequiredString(value, "ManagedExecutablePath");
            string managedHash = NormalizeHash(RequiredString(value, "ManagedExecutableSha256"), "ManagedExecutableSha256");
            string servicePath = RequiredString(value, "ServiceExecutablePath");
            string serviceHash = NormalizeHash(RequiredString(value, "ServiceExecutableSha256"), "ServiceExecutableSha256");
            string serviceName = RequiredString(value, "ServiceName");
            string serviceStatus = RequiredString(value, "ServiceStatus");
            string serviceStartMode = RequiredString(value, "ServiceStartMode");
            string publisher = NormalizeHash(RequiredString(value, "PublisherThumbprint"), "PublisherThumbprint");
            string registeredSid = RequiredString(value, "RegisteredUserSid");
            string receiptPath = RequiredString(value, "ReceiptPath");
            string provisionedUtcText = RequiredString(value, "ProvisionedUtc");
            if (!DateTimeOffset.TryParse(provisionedUtcText, out DateTimeOffset provisionedUtc))
                throw new InvalidDataException("ProvisionedUtc is not a round-trip timestamp.");

            if (formatVersion != ExpectedFormatVersion) throw new InvalidDataException($"Unsupported provisioning evidence format {formatVersion}.");
            if (!string.Equals(profile, ProfileName, StringComparison.Ordinal)) throw new InvalidDataException("The provisioning profile is not RemoteDebuggerProvisionV1.");
            if (requestId.Length == 0) throw new InvalidDataException("RequestId is empty.");
            if (!string.Equals(serviceName, "RemoteDebuggerSupport", StringComparison.Ordinal)) throw new InvalidDataException("The provisioning service name is not RemoteDebuggerSupport.");
            if (!string.Equals(serviceStatus, "Running", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The provisioning receipt does not attest a running support service.");
            if (!string.Equals(serviceStartMode, "demand", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The support service is not demand-started.");
            _ = new SecurityIdentifier(registeredSid);

            string expectedFixture = RequireRegularFile(expectedFixturePath, "expected fixture");
            string actualFixtureHash = HashFile(expectedFixture);
            if (!string.Equals(fixtureHash, actualFixtureHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"FixtureSha256 does not match the expected fixture ({actualFixtureHash}).");

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string productRoot = Path.Combine(programFiles, "RemoteDebugger");
            string expectedManagedPath = Path.Combine(productRoot, "RemoteDebugger.exe");
            string expectedServicePath = Path.Combine(productRoot, "Support", "RemoteDebugger.Support.exe");
            managedPath = RequireProtectedFile(managedPath, productRoot, "managed application");
            servicePath = RequireProtectedFile(servicePath, Path.Combine(productRoot, "Support"), "support service");
            if (!PathsEqual(managedPath, expectedManagedPath)) throw new InvalidDataException("ManagedExecutablePath is not the protected Program Files application.");
            if (!PathsEqual(servicePath, expectedServicePath)) throw new InvalidDataException("ServiceExecutablePath is not the protected Program Files support service.");

            string actualManagedHash = HashFile(managedPath);
            if (!string.Equals(managedHash, actualManagedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"ManagedExecutableSha256 does not match the managed executable ({actualManagedHash}).");
            if (!string.Equals(managedHash, actualFixtureHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The managed executable bytes do not match the requested fixture.");

            string actualServiceHash = HashFile(servicePath);
            if (!string.Equals(serviceHash, actualServiceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"ServiceExecutableSha256 does not match the support service ({actualServiceHash}).");

            receiptPath = RequireRegularFile(receiptPath, "product provisioning receipt");
            string expectedReceiptPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDebugger", "Support", "provisioning-receipt.json");
            if (!PathsEqual(receiptPath, expectedReceiptPath)) throw new InvalidDataException("ReceiptPath is outside the protected product state directory.");
            ValidateProductReceipt(receiptPath, managedPath, servicePath, publisher, registeredSid);

            string expectedRequestId = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)));
            if (!string.Equals(requestId, expectedRequestId, StringComparison.Ordinal)) throw new InvalidDataException("The provisioning evidence request does not match the Lab output directory.");
            string collectedEvidencePath = EvidencePath(output);
            File.Copy(evidencePath, collectedEvidencePath, true);
            receipt = new BrokerReceipt(formatVersion, profile, requestId, fixtureHash, managedPath, managedHash, servicePath, serviceHash, serviceName, serviceStatus, serviceStartMode, publisher, registeredSid, receiptPath, provisionedUtc, collectedEvidencePath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            receipt = null!;
            return false;
        }
    }

    public static bool TryReadObservation(string output, out GuestObservation observation, out string error, DateTimeOffset? capturedAfterUtc = null)
    {
        observation = null!;
        error = "";
        try
        {
            string sourcePath = SourceObservationPath(output);
            if (!File.Exists(sourcePath))
            {
                error = "The SYSTEM broker did not publish the read-only guest observation file.";
                return false;
            }
            var info = new FileInfo(sourcePath);
            if (info.Length <= 0 || info.Length > 256 * 1024) throw new InvalidDataException("The guest observation file is outside the bounded size limit.");
            // Preserve the raw snapshot before parsing. A service query can
            // fail independently of power/firewall collection, and its raw
            // error object must remain reviewable in the guest evidence.
            string collectedPath = Path.Combine(output, "remote-debugger-observation.json");
            File.Copy(sourcePath, collectedPath, true);
            JsonElement value = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(sourcePath));
            int formatVersion = RequiredInt(value, "FormatVersion");
            string requestId = RequiredString(value, "RequestId");
            string capturedText = RequiredString(value, "CapturedUtc");
            if (formatVersion != ExpectedFormatVersion) throw new InvalidDataException($"Unsupported guest observation format {formatVersion}.");
            string expectedRequestId = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)));
            if (!string.Equals(requestId, expectedRequestId, StringComparison.Ordinal)) throw new InvalidDataException("The guest observation request does not match the Lab output directory.");
            if (!DateTimeOffset.TryParse(capturedText, out DateTimeOffset capturedUtc)) throw new InvalidDataException("CapturedUtc is not a round-trip timestamp.");

            GuestCommandObservation power = ParseCommand(RequiredObject(value, "PowerRequests"));
            GuestCommandObservation firewall = ParseCommand(RequiredObject(value, "Firewall"));
            var services = ParseServices(RequiredArray(value, "Services"));
            if (DateTimeOffset.UtcNow - capturedUtc < TimeSpan.Zero || DateTimeOffset.UtcNow - capturedUtc > TimeSpan.FromSeconds(10))
                throw new InvalidDataException("The guest observation is older than the ten-second freshness bound.");
            if (capturedAfterUtc.HasValue && capturedUtc <= capturedAfterUtc.Value)
                throw new InvalidDataException("The guest observation predates the observed product exit.");
            observation = new GuestObservation(formatVersion, requestId, capturedUtc, power, firewall, services, collectedPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            observation = null!;
            return false;
        }
    }

    public static ProductIdentity InspectProduct(Process process, string expectedPath, string registeredSid)
    {
        process.Refresh();
        string executablePath = Path.GetFullPath(process.MainModule?.FileName ?? throw new UnauthorizedAccessException("Product executable path is unavailable."));
        if (!OpenProcessToken(process.Handle, TokenQuery, out var token)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            string sid = identity.User?.Value ?? "";
            string integrity = ReadIntegrityLevel(token);
            string elevation = ReadElevationType(token);
            int sessionId = process.SessionId;
            string currentSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
            bool medium = string.Equals(integrity, "Medium", StringComparison.Ordinal);
            bool nonFull = !string.Equals(elevation, "Full", StringComparison.Ordinal);
            return new ProductIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, executablePath, sid, sessionId, integrity, elevation, medium, nonFull, sessionId > 0, string.Equals(sid, registeredSid, StringComparison.OrdinalIgnoreCase), PathsEqual(executablePath, expectedPath));
        }
    }

    public static string[] LocalIPv4Addresses()
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up))
        foreach (var address in adapter.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            addresses.Add(address.Address.ToString());
        return addresses.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    public static bool IsRemoteCoordinationAddress(string host, IEnumerable<string> localAddresses, out string normalizedHost)
    {
        normalizedHost = "";
        if (!IPAddress.TryParse(host, out var address)) return false;
        address = NormalizeAddress(address);
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        normalizedHost = address.ToString();
        foreach (string localText in localAddresses)
            if (IPAddress.TryParse(localText, out var local) && NormalizeAddress(local).Equals(address)) return false;
        return true;
    }

    public static bool HasLocalSystemService(IEnumerable<string> rows, string expectedServicePath)
    {
        foreach (string row in rows)
        {
            string[] fields = row.Split('|');
            if (fields.Length < 5) continue;
            if (!fields[0].Contains("RemoteDebugger", StringComparison.OrdinalIgnoreCase)
                || !fields[1].Equals("Running", StringComparison.OrdinalIgnoreCase)
                || !fields[3].Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) continue;
            if (fields[4].Contains(expectedServicePath, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static string[] FormatServices(IEnumerable<GuestServiceObservation> services)
        => services.Select(service => service.HasError
            ? "[observer service error] " + service.Error
            : string.Join('|', service.Name, service.State, service.StartMode, service.StartName, service.PathName)).ToArray();

    private static void ValidateProductReceipt(string path, string managedPath, string servicePath, string publisher, string registeredSid)
    {
        JsonElement value = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
        if (!TryGetBoolean(value, "Provisioned", out bool provisioned) || !provisioned) throw new InvalidDataException("The product provisioning receipt is not marked Provisioned.");
        if (!PathsEqual(RequiredString(value, "ManagedApplicationPath"), managedPath)) throw new InvalidDataException("The product receipt names a different managed application.");
        if (!PathsEqual(RequiredString(value, "ServiceExecutablePath"), servicePath)) throw new InvalidDataException("The product receipt names a different support service.");
        if (!string.Equals(RequiredString(value, "PublisherThumbprint"), publisher, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The product receipt publisher does not match the broker evidence.");
        if (!string.Equals(RequiredString(value, "RegisteredUserSid"), registeredSid, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The product receipt interactive SID does not match the broker evidence.");
        if (!string.Equals(RequiredString(value, "ServiceStartMode"), "demand", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The product receipt is not demand-started.");
    }

    private static string SourceObservationPath(string output)
    {
        string requestId = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)));
        if (requestId.Length == 0 || requestId.Length > 128 || requestId.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new InvalidDataException("The Lab output directory does not identify a valid broker request.");
        return Path.Combine(@"C:\CodexGuest\Provisioning", requestId, "remote-debugger-observation.json");
    }

    private static GuestCommandObservation ParseCommand(JsonElement value)
    {
        int exitCode = RequiredInt(value, "ExitCode");
        string stdout = RequiredText(value, "Stdout");
        string stderr = RequiredText(value, "Stderr");
        return new GuestCommandObservation(exitCode, stdout, stderr);
    }

    private static GuestServiceObservation[] ParseServices(JsonElement value)
    {
        var services = new List<GuestServiceObservation>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Services contains a non-object value.");
            if (TryGetBoolean(item, "HasError", out bool hasError) && hasError)
            {
                string error = TryGetString(item, "Error", out var detail) && detail.Length > 0 ? detail : "The guest observer reported a service query error.";
                services.Add(new GuestServiceObservation("", "", "", "", "", error));
                continue;
            }
            if (TryGetString(item, "Error", out var serviceError) && serviceError.Length > 0 && !TryGetProperty(item, "Name", out _))
            {
                services.Add(new GuestServiceObservation("", "", "", "", "", serviceError));
                continue;
            }
            services.Add(new GuestServiceObservation(RequiredString(item, "Name"), RequiredString(item, "State"), RequiredString(item, "StartMode"), RequiredString(item, "StartName"), RequiredString(item, "PathName")));
        }
        return services.ToArray();
    }

    private static string RequireRegularFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException($"The {description} path is empty.");
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException($"The {description} does not exist.", full);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"The {description} is a reparse point.");
        return full;
    }

    private static string RequireProtectedFile(string path, string root, string description)
    {
        string full = RequireRegularFile(path, description);
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string prefix = rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The {description} is outside the protected installation root.");
        for (string? cursor = full; cursor != null && cursor.Length >= rootFull.Length; cursor = Path.GetDirectoryName(cursor))
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The {description} path contains a reparse point.");
            if (PathsEqual(cursor, rootFull)) break;
        }
        return full;
    }

    private static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string NormalizeHash(string value, string field)
    {
        string normalized = value.Trim();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException($"{field} must be a 64-character SHA-256 value.");
        return normalized.ToUpperInvariant();
    }

    private static string RequiredString(JsonElement value, string key)
    {
        if (TryGetString(value, key, out string result) && result.Length > 0) return result;
        throw new InvalidDataException($"Provisioning evidence is missing {key}.");
    }

    private static string RequiredText(JsonElement value, string key)
    {
        if (TryGetString(value, key, out string result)) return result;
        throw new InvalidDataException($"Provisioning evidence is missing {key}.");
    }

    private static int RequiredInt(JsonElement value, string key)
    {
        if (TryGetProperty(value, key, out var result) && result.ValueKind == JsonValueKind.Number && result.TryGetInt32(out int number)) return number;
        throw new InvalidDataException($"Provisioning evidence is missing {key}.");
    }

    private static JsonElement RequiredObject(JsonElement value, string key)
    {
        if (TryGetProperty(value, key, out var result) && result.ValueKind == JsonValueKind.Object) return result;
        throw new InvalidDataException($"Guest observation is missing {key}.");
    }

    private static JsonElement RequiredArray(JsonElement value, string key)
    {
        if (TryGetProperty(value, key, out var result) && result.ValueKind == JsonValueKind.Array) return result;
        throw new InvalidDataException($"Guest observation is missing {key}.");
    }

    private static bool TryGetString(JsonElement value, string key, out string result)
    {
        result = "";
        if (!TryGetProperty(value, key, out var property) || property.ValueKind != JsonValueKind.String) return false;
        result = property.GetString() ?? "";
        return true;
    }

    private static bool TryGetBoolean(JsonElement value, string key, out bool result)
    {
        result = false;
        if (!TryGetProperty(value, key, out var property)) return false;
        if (property.ValueKind == JsonValueKind.True) { result = true; return true; }
        if (property.ValueKind == JsonValueKind.False) return true;
        return false;
    }

    private static bool TryGetProperty(JsonElement value, string key, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)) { result = property.Value; return true; }
        }
        result = default;
        return false;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static IPAddress NormalizeAddress(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string ReadElevationType(SafeFileHandle token)
    {
        int value = ReadTokenInt(token, TokenElevationTypeInformation);
        return value switch { 1 => "Default", 2 => "Full", 3 => "Limited", _ => "Unknown" };
    }

    private static string ReadIntegrityLevel(SafeFileHandle token)
    {
        int length = 0;
        _ = GetTokenInformation(token, TokenIntegrityLevelInformation, IntPtr.Zero, 0, out length);
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || length <= 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevelInformation, buffer, length, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            string value = new SecurityIdentifier(sid).Value;
            int rid = int.Parse(value.Split('-').Last(), System.Globalization.CultureInfo.InvariantCulture);
            return rid switch { >= SecurityMandatorySystemRid => "System", >= SecurityMandatoryHighRid => "High", >= SecurityMandatoryMediumRid => "Medium", >= SecurityMandatoryLowRid => "Low", _ => "Untrusted" };
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int ReadTokenInt(SafeFileHandle token, int informationClass)
    {
        int length = sizeof(int);
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, informationClass, buffer, length, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.ReadInt32(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeFileHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeFileHandle tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
}
