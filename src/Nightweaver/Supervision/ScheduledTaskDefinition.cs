using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Nightweaver.Execution;

namespace Nightweaver.Supervision;

internal sealed record ScheduledTaskSnapshot(string Xml, bool Enabled, bool Running = false, int LastResult = 0);

internal interface ISupervisionScheduler
{
    ScheduledTaskSnapshot? Read(string name);
    void Validate(string xml, string sid);
    void Create(string name, string xml, string sid);
    void SetEnabled(string name, bool enabled);
    void Run(string name);
    void Delete(string name);
    string Canonicalize(string xml);
}

internal sealed record ScheduledTaskDefinition(string Name, string Xml, string Sid)
{
    internal static readonly XNamespace Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static ScheduledTaskDefinition Build(SupervisionIdentity identity, string executable,
        IReadOnlyList<string> arguments, string workingDirectory, DateTimeOffset? start = null,
        DateTimeOffset? end = null, string? fixtureNonce = null)
    {
        SupervisionValidation.Validate(identity);
        foreach (var path in new[] { executable, workingDirectory })
            if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
                throw new InvalidDataException("Scheduler executable and working directory must be absolute literal paths.");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(identity)))).ToLowerInvariant();
        var name = fixtureNonce is null ? "Nightweaver-" + key : "Nightweaver-Marker-" + fixtureNonce;
        if (fixtureNonce is not null) SupervisionValidation.ValidateId(fixtureNonce);
        var ns = Namespace;
        XElement E(string local, object? value) => new(ns + local, value);
        static string Boundary(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var boundary = Boundary(start ?? new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var time = E("TimeTrigger", new object[] {
            E("Repetition", new object[] { E("Interval", XmlConvert.ToString(SupervisionPolicy.TickInterval)), E("StopAtDurationEnd", false) }),
            E("StartBoundary", boundary) });
        if (end is not null) time.Add(E("EndBoundary", Boundary(end.Value)));
        time.Add(E("Enabled", true));
        var logon = E("LogonTrigger", new object[] { E("Enabled", true), E("UserId", identity.UserSid) });
        if (end is not null) logon.AddFirst(E("EndBoundary", Boundary(end.Value)));
        var xml = new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", new object[] { E("Description", "Nightweaver finite tick identity " + key + (fixtureNonce is null ? "" : " marker " + fixtureNonce)), E("URI", "\\" + name) }),
            E("Triggers", new object[] { time, logon }),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"),
                E("UserId", identity.UserSid), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", new object[] {
                E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false), E("StopIfGoingOnBatteries", false),
                E("AllowHardTerminate", true), E("StartWhenAvailable", true), E("RunOnlyIfNetworkAvailable", false),
                E("AllowStartOnDemand", true), E("Enabled", false), E("Hidden", false), E("WakeToRun", false),
                E("ExecutionTimeLimit", XmlConvert.ToString(SupervisionPolicy.SchedulerActionLimit)), E("Priority", 7) }),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"), E("Exec", new object[] {
                E("Command", executable), E("Arguments", string.Join(' ', arguments.Select(WindowsProcessLauncher.QuoteWindowsArgument))),
                E("WorkingDirectory", workingDirectory) }))));
        return new(name, xml.ToString(SaveOptions.DisableFormatting), identity.UserSid);
    }

    internal void RequireOwned(ISupervisionScheduler scheduler, ScheduledTaskSnapshot snapshot)
    {
        // Compare the complete scheduler-normalized definition, allowing only the mutable Enabled bit.
        string Normalize(string xml)
        {
            var document = XDocument.Parse(scheduler.Canonicalize(xml));
            document.Root!.Element(Namespace + "Settings")!.Element(Namespace + "Enabled")!.Value = "false";
            return document.ToString(SaveOptions.DisableFormatting);
        }
        if (Normalize(Xml) != Normalize(snapshot.Xml))
            throw new InvalidDataException($"Foreign or changed scheduler definition '{Name}'; no scheduler mutation authorized.");
    }
}
