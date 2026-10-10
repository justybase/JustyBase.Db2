using System.Text.Json;
using Db2Replay;

await using var server = new ReplayServer(new ReplayFixture(args.Length == 0 ? "Int" : args[0]),
    blockSizes: args.Length > 1 ? [int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)] : null);
Console.WriteLine(server.Port);
while (Console.ReadLine() is { } command)
{
    server.ThrowIfFailed();
    if (command == "stats") Console.WriteLine(JsonSerializer.Serialize(server.Counters));
    else if (command == "stop") break;
    else throw new InvalidOperationException("Unknown server control command.");
}
