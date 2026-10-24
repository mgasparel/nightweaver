using System.Text;
using System.Text.Json;

var recordPath = Environment.GetEnvironmentVariable("NIGHTWEAVER_AGENT_TRAP_RECORD");
if (string.IsNullOrWhiteSpace(recordPath))
{
    Console.Error.WriteLine("NIGHTWEAVER_AGENT_TRAP_RECORD is required");
    return 98;
}

var executable = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "unknown");
var invocation = JsonSerializer.Serialize(new
{
    executable,
    arguments = Environment.GetCommandLineArgs().Skip(1).ToArray()
});
await File.AppendAllTextAsync(recordPath, invocation + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
return 97;
