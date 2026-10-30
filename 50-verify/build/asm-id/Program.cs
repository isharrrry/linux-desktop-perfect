using System.Reflection;
using System.Text.Json;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: asm-id <assembly.dll> [...]");
    return 2;
}
var outList = new List<object>();
foreach (var p in args)
{
    try
    {
        var an = AssemblyName.GetAssemblyName(p);
        var token = an.GetPublicKeyToken();
        outList.Add(new
        {
            path = p,
            name = an.Name,
            version = an.Version == null ? null : an.Version.ToString(),
            token = token == null ? "" : Convert.ToHexString(token).ToLowerInvariant(),
            culture = an.CultureName ?? "",
        });
    }
    catch (Exception e)
    {
        outList.Add(new { path = p, error = e.GetType().Name + ": " + e.Message });
    }
}
Console.WriteLine(JsonSerializer.Serialize(outList));
return 0;
