using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RemoteDebugger.Lab;

/// <summary>
/// Binds generic GuestSetupV1 evidence to this product's fixture, then independently
/// checks the installed receipt, bytes, publisher and service configuration.
/// Product-specific provisioning policy belongs here, not in the shared harness.
/// </summary>
internal static class ProvisioningEvidence
{
    public const string EvidenceFileName = "broker-guest-setup.json";
    public const string ContractName = "GuestSetupV1";
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
        string Contract,
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

    internal sealed record SetupReceipt(string UserSid, string StagedExecutablePath, DateTimeOffset CompletedUtc);

    public static string EvidencePath(string output) => Path.Combine(output, EvidenceFileName);
    private static string SetupRoot(string requestId) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CodexHarness", "GuestSetup", requestId);

    public static string SourceEvidencePath(string output)
    {
        string requestId = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)));
        if (requestId.Length == 0 || requestId.Length > 128 || requestId.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new InvalidDataException("The Lab output directory does not identify a valid broker request.");
        return Path.Combine(SetupRoot(requestId), "guest-setup.json");
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
                error = "The SYSTEM broker did not publish the GuestSetupV1 evidence file.";
                return false;
            }
            var evidenceInfo = new FileInfo(evidencePath);
            if (evidenceInfo.Length <= 0 || evidenceInfo.Length > 128 * 1024)
                throw new InvalidDataException("The provisioning evidence file is outside the bounded size limit.");

            string expectedFixture = RequireRegularFile(expectedFixturePath, "expected fixture");
            string fixtureHash = HashFile(expectedFixture);
            string payloadRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            string fixtureRelativePath = Path.GetRelativePath(payloadRoot, expectedFixture);
            string requestId = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)));
            JsonElement value = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(evidencePath));
            SetupReceipt setup = ValidateSetupReceipt(value, requestId, fixtureRelativePath, fixtureHash);
            string registeredSid = setup.UserSid;
            if (!string.Equals(registeredSid, WindowsIdentity.GetCurrent().User?.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The setup account does not match the interactive Lab user.");

            string stagedPath = RequireProtectedFile(setup.StagedExecutablePath, SetupRoot(requestId), "staged setup executable");
            if (!string.Equals(HashFile(stagedPath), fixtureHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged setup executable bytes do not match the requested fixture.");
            string managedPath = RequireProtectedFile(SupportPlatformPaths.ApplicationExecutable, SupportPlatformPaths.ProductDirectory, "managed application");
            string servicePath = RequireProtectedFile(SupportPlatformPaths.ServiceExecutable, SupportPlatformPaths.InstallDirectory, "support service");
            string managedHash = HashFile(managedPath);
            string serviceHash = HashFile(servicePath);
            if (!string.Equals(managedHash, fixtureHash, StringComparison.OrdinalIgnoreCase) || !string.Equals(serviceHash, fixtureHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The managed application and support service must both match the requested fixture bytes.");

            string publisher = AuthenticodeVerifier.InspectForEnrollment(expectedFixture).SignerThumbprint;
            string receiptPath = RequireProtectedFile(SupportPlatformPaths.ProvisioningReceiptPath, SupportPlatformPaths.StateDirectory, "product provisioning receipt");
            DateTimeOffset provisionedUtc = ValidateProductReceipt(receiptPath, managedPath, servicePath, publisher, registeredSid);
            using var service = new ServiceController(SupportPlatformPaths.ServiceName);
            using var serviceKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + SupportPlatformPaths.ServiceName);
            // The demand-started service deliberately stops after one idle minute.
            // Its liveness is asserted through the managed CLI after app launch.
            if (service.StartType != ServiceStartMode.Manual
                || !string.Equals(serviceKey?.GetValue("ObjectName") as string, "LocalSystem", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(serviceKey?.GetValue("ImagePath") as string, $"\"{servicePath}\" --platform-service", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed support service is not the demand-started LocalSystem fixture.");

            string collectedEvidencePath = EvidencePath(output);
            File.Copy(evidencePath, collectedEvidencePath, true);
            receipt = new BrokerReceipt(ExpectedFormatVersion, ContractName, requestId, fixtureHash, managedPath, managedHash, servicePath, serviceHash,
                SupportPlatformPaths.ServiceName, service.Status.ToString(), "demand", publisher, registeredSid, receiptPath, provisionedUtc, collectedEvidencePath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            receipt = null!;
            return false;
        }
    }

    internal static SetupReceipt ValidateSetupReceipt(JsonElement value, string requestId, string fixtureRelativePath, string fixtureHash)
    {
        if (RequiredInt(value, "FormatVersion") != ExpectedFormatVersion || RequiredString(value, "Contract") != ContractName)
            throw new InvalidDataException("The setup evidence is not GuestSetupV1 format 1.");
        if (RequiredString(value, "RequestId") != requestId) throw new InvalidDataException("The setup evidence belongs to another request.");
        string relative = RequiredString(value, "ExecutableRelativePath").Replace('/', '\\');
        if (!string.Equals(relative, fixtureRelativePath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The setup executable path does not match the requested fixture.");
        foreach (string key in new[] { "ExecutableSha256", "StagedExecutableSha256" })
            if (!string.Equals(NormalizeHash(RequiredString(value, key), key), fixtureHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{key} does not match the requested fixture.");
        if (!RequiredArray(value, "Arguments").EnumerateArray().Select(argument => argument.GetString()).SequenceEqual(new[] { "cli", "platform-provision" }))
            throw new InvalidDataException("The setup arguments do not match the supported provisioner.");
        if (!TryGetBoolean(value, "Succeeded", out bool succeeded) || !succeeded || RequiredInt(value, "ExitCode") != 0)
            throw new InvalidDataException("The setup process did not exit successfully.");
        JsonElement identity = RequiredObject(value, "Identity");
        if (!TryGetBoolean(identity, "IsAdministrator", out bool administrator) || !administrator)
            throw new InvalidDataException("The setup process did not run as administrator.");
        string sid = RequiredString(identity, "UserSid");
        _ = new SecurityIdentifier(sid);
        if (!DateTimeOffset.TryParse(RequiredString(value, "StartedUtc"), out var started)
            || !DateTimeOffset.TryParse(RequiredString(value, "CompletedUtc"), out var completed) || completed < started)
            throw new InvalidDataException("The setup evidence has invalid timestamps.");
        string stagedPath = RequiredString(value, "StagedExecutablePath");
        if (!PathsEqual(stagedPath, Path.Combine(SetupRoot(requestId), "stage", Path.GetFileName(relative))))
            throw new InvalidDataException("The setup executable is outside the request's protected staging path.");
        return new SetupReceipt(sid, stagedPath, completed);
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

    private static DateTimeOffset ValidateProductReceipt(string path, string managedPath, string servicePath, string publisher, string registeredSid)
    {
        JsonElement value = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
        if (!TryGetBoolean(value, "Provisioned", out bool provisioned) || !provisioned) throw new InvalidDataException("The product provisioning receipt is not marked Provisioned.");
        if (!PathsEqual(RequiredString(value, "ManagedApplicationPath"), managedPath)) throw new InvalidDataException("The product receipt names a different managed application.");
        if (!PathsEqual(RequiredString(value, "ServiceExecutablePath"), servicePath)) throw new InvalidDataException("The product receipt names a different support service.");
        if (!string.Equals(RequiredString(value, "PublisherThumbprint"), publisher, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The product receipt publisher does not match the signed fixture.");
        if (!string.Equals(RequiredString(value, "RegisteredUserSid"), registeredSid, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The product receipt interactive SID does not match the setup account.");
        if (!string.Equals(RequiredString(value, "ServiceStartMode"), "demand", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The product receipt is not demand-started.");
        if (!DateTimeOffset.TryParse(RequiredString(value, "ProvisionedUtc"), out var provisionedUtc)) throw new InvalidDataException("The product receipt has no valid provisioning timestamp.");
        return provisionedUtc;
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

    private static int RequiredInt(JsonElement value, string key)
    {
        if (TryGetProperty(value, key, out var result) && result.ValueKind == JsonValueKind.Number && result.TryGetInt32(out int number)) return number;
        throw new InvalidDataException($"Provisioning evidence is missing {key}.");
    }

    private static JsonElement RequiredObject(JsonElement value, string key)
    {
        if (TryGetProperty(value, key, out var result) && result.ValueKind == JsonValueKind.Object) return result;
        throw new InvalidDataException($"Setup evidence is missing {key}.");
    }

    private static JsonElement RequiredArray(JsonElement value, string key)
    {
        if (TryGetProperty(value, key, out var result) && result.ValueKind == JsonValueKind.Array) return result;
        throw new InvalidDataException($"Setup evidence is missing {key}.");
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
