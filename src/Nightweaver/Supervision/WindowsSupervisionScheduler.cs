using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace Nightweaver.Supervision;

/// <summary>Current-user COM adapter. No credentials, elevation, shell or policy fallback.</summary>
internal sealed class WindowsSupervisionScheduler : ISupervisionScheduler, IDisposable
{
    private readonly dynamic service;
    private readonly dynamic folder;

    internal WindowsSupervisionScheduler()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        try { service.Connect(); folder = service.GetFolder("\\"); }
        catch { Marshal.FinalReleaseComObject(service); throw; }
    }

    public ScheduledTaskSnapshot? Read(string name)
    {
        dynamic? task = null;
        try { task = folder.GetTask(name); return new((string)task.Xml, (bool)task.Enabled, (int)task.State == 4, (int)task.LastTaskResult); }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80070002)) { return null; }
        catch (FileNotFoundException) { return null; }
        finally { Release(task); }
    }

    public void Validate(string xml, string sid) => Register("", xml, sid, 1 /* TASK_VALIDATE_ONLY */);
    public void Create(string name, string xml, string sid) => Register(name, xml, sid, 2 /* TASK_CREATE: never overwrite */);
    private void Register(string name, string xml, string sid, int flags)
    {
        dynamic? task = null;
        try { task = folder.RegisterTask(name, xml, flags, sid, null, 3 /* InteractiveToken */, null); }
        finally { Release(task); }
    }
    public void SetEnabled(string name, bool enabled) => WithTask(name, task => task.Enabled = enabled);
    public void Run(string name) => WithTask(name, task => { object? running = task.Run(null); Release(running); });
    public void Delete(string name) => folder.DeleteTask(name, 0);
    public string Canonicalize(string xml)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var document = XDocument.Parse(xml);
        foreach (var element in document.Descendants(ScheduledTaskDefinition.Namespace + "UserId"))
            element.Value = element.Value.StartsWith("S-1-", StringComparison.Ordinal) ? element.Value
                : new NTAccount(element.Value).Translate(typeof(SecurityIdentifier)).Value;
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName is "StartBoundary" or "EndBoundary"))
            element.Value = DateTimeOffset.Parse(element.Value, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        dynamic definition = service.NewTask(0);
        try { definition.XmlText = document.ToString(); return (string)definition.XmlText; }
        finally { Release(definition); }
    }
    private void WithTask(string name, Action<dynamic> action)
    {
        dynamic task = folder.GetTask(name);
        try { action(task); }
        finally { Release(task); }
    }
    private static void Release(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
    public void Dispose() { Release(folder); Release(service); }
}
