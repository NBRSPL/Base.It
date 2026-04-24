using Base.It.Core.Models;
using Base.It.Core.Parsing;
using Base.It.Core.Sql;

// Base.It Stage-0 smoke console.
//
// Usage:
//   Base.It.Smoke get-type  <connString> <schema.name>
//   Base.It.Smoke get       <connString> <schema.name> [--out file.sql]
//   Base.It.Smoke validate  <file.sql>
//
// Intentionally minimal — proves the Core library works end-to-end against a
// real database. The real CLI and UI come in later stages.

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "get-type" => await GetTypeAsync(args),
        "get"      => await GetAsync(args),
        "validate" => Validate(args),
        _          => BadCommand()
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 2;
}

static async Task<int> GetTypeAsync(string[] args)
{
    Require(args, 3);
    var scripter = new SqlObjectScripter();
    var id = ObjectIdentifier.Parse(args[2]);
    var type = await scripter.GetObjectTypeAsync(args[1], id);
    Console.WriteLine($"{id}: {type}");
    return type == SqlObjectType.Unknown ? 3 : 0;
}

static async Task<int> GetAsync(string[] args)
{
    Require(args, 3);
    var scripter = new SqlObjectScripter();
    var id = ObjectIdentifier.Parse(args[2]);
    var obj = await scripter.GetObjectAsync(args[1], id);
    if (obj is null)
    {
        Console.Error.WriteLine($"Not found: {id}");
        return 3;
    }

    var outIdx = Array.IndexOf(args, "--out");
    if (outIdx >= 0 && outIdx + 1 < args.Length)
    {
        File.WriteAllText(args[outIdx + 1], obj.Definition);
        Console.WriteLine($"{id}: {obj.Type}  hash={obj.Hash}  bytes={obj.Definition.Length}  -> {args[outIdx + 1]}");
    }
    else
    {
        Console.WriteLine($"-- {id} ({obj.Type}) hash={obj.Hash}");
        Console.WriteLine(obj.Definition);
    }
    return 0;
}

static int Validate(string[] args)
{
    Require(args, 2);
    var script = File.ReadAllText(args[1]);
    var r = TSqlValidator.Validate(script);
    if (r.IsValid)
    {
        Console.WriteLine($"OK: {args[1]}");
        return 0;
    }
    Console.Error.WriteLine($"INVALID: {args[1]}");
    foreach (var e in r.Errors) Console.Error.WriteLine($"  {e}");
    return 4;
}

static int BadCommand()
{
    PrintUsage();
    return 1;
}

static void Require(string[] args, int min)
{
    if (args.Length < min) throw new ArgumentException("Not enough arguments.");
}

static void PrintUsage()
{
    Console.WriteLine("Base.It Stage-0 smoke");
    Console.WriteLine("  get-type  <connString> <schema.name>");
    Console.WriteLine("  get       <connString> <schema.name> [--out file.sql]");
    Console.WriteLine("  validate  <file.sql>");
}
