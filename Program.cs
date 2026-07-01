using System.Text.Json;
using TruID.Connect.SDK;
using TruID.Connect.SDK.Exceptions;

/// <summary>
/// End-to-end sample for the truID Connect .NET SDK.
///
/// Flow: Connect → Upload PDF → poll status → list products → download each product,
/// plus optional ZIP (products/all) and typed summary (products/summary).
///
/// Credentials are read from the environment (optionally seeded from a local .env file).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        LoadDotEnvIfPresent();

        var options = AppOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        var apiKey = ResolveEnv("TRUID_CLIENT_ID", "API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("Error: set TRUID_CLIENT_ID or API_KEY in your environment (see .env.example).");
            return 1;
        }

        if (options.Files.Count == 0)
        {
            Console.Error.WriteLine("Error: provide at least one PDF via --file <path>.");
            return 1;
        }

        foreach (var file in options.Files)
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"Error: file not found: {file}");
                return 1;
            }
        }

        var environment = ResolveEnv("TRUID_ENVIRONMENT", "ENV") ?? "tst";
        Directory.CreateDirectory(options.OutputDir);

        Console.WriteLine("truID Connect .NET SDK — example application");
        Console.WriteLine(new string('─', 52));
        Console.WriteLine($"Environment : {environment}");
        Console.WriteLine($"Consumer    : {options.Name} ({options.IdNumber})");
        Console.WriteLine($"PDF file(s) : {string.Join(", ", options.Files.Select(Path.GetFileName))}");
        Console.WriteLine($"Output dir  : {Path.GetFullPath(options.OutputDir)}");
        Console.WriteLine();

        var config = ClientConfig.Create(apiKey)
            .WithEnvironment(environment)
            .WithVersion("4.0")
            .WithVerifySsl(options.VerifySsl)
            .Build();

        try
        {
            using var client = new TruIDClient(config);

            Console.Write("[1/6] Connect  ... ");
            var ctx = await client.ConnectAsync();
            var brand = ctx.Brands.FirstOrDefault();
            Console.WriteLine($"OK — {ctx.Company?.Name ?? "n/a"}, brand={brand?.Name ?? "n/a"}");

            Console.Write("[2/6] Upload   ... ");
            var collection = await ctx.UploadAsync(
                options.Name,
                options.IdNumber,
                options.Files,
                brandId: options.BrandId ?? brand?.Id,
                idType: options.IdType,
                forceExtraction: options.ForceExtraction);
            Console.WriteLine($"OK — collectionId={collection.CollectionId}");

            Console.Write("[3/6] Status   ... ");
            var status = await collection.WaitForAsync(
                timeoutSeconds: options.WaitTimeoutSeconds,
                intervalSeconds: options.PollIntervalSeconds);
            var code = status.Latest?.Code ?? 0;
            Console.WriteLine($"OK — code={code}, message={status.Latest?.Message ?? "n/a"}");

            if (status.RequiresChallenge)
            {
                Console.WriteLine();
                Console.WriteLine("Collection requires a challenge (e.g. bank login). This sample covers upload-only flows.");
                return 3;
            }

            if (!status.IsTerminal)
            {
                Console.WriteLine();
                Console.WriteLine($"Timed out before terminal status (last code={code}). Try --wait {options.WaitTimeoutSeconds * 2}.");
                return 4;
            }

            Console.Write("[4/6] Products ... ");
            var products = await collection.GetProductsAsync();
            var productList = products.Products ?? [];
            Console.WriteLine($"OK — {productList.Count} product(s)");

            Console.Write("[5/6] Download ... ");
            var saved = 0;
            foreach (var product in productList)
            {
                var data = await collection.GetDataAsync(product.Id!);
                var fileName = SanitizeFileName($"{product.Id}_{product.Name}");
                var path = Path.Combine(options.OutputDir, fileName);
                await File.WriteAllBytesAsync(path, data.Data);
                saved++;
            }
            Console.WriteLine($"OK — {saved} file(s) → {options.OutputDir}/");

            Console.Write("[6/6] Bundle   ... ");
            var zipBytes = await collection.GetAllProductsAsync();
            var zipPath = Path.Combine(options.OutputDir, "products-all.zip");
            await File.WriteAllBytesAsync(zipPath, zipBytes);

            var summary = await collection.GetProductsSummaryAsync();
            var summaryPath = Path.Combine(options.OutputDir, "products-summary.json");
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
            Console.WriteLine($"OK — products-all.zip ({zipBytes.Length:N0} bytes), products-summary.json");

            Console.WriteLine();
            Console.WriteLine("Product catalogue (GET /delivery-api/.../products):");
            foreach (var p in productList)
                Console.WriteLine($"  • {p.Id} — {p.Name}");

            if (summary.Statement?.Customer?.Name is { } customerName)
                Console.WriteLine($"Summary customer: {customerName}");

            Console.WriteLine();
            Console.WriteLine("Done. Matches Postman delivery-api product endpoints:");
            Console.WriteLine("  GET .../products          → GetProductsAsync()");
            Console.WriteLine("  GET .../products/{id}     → GetDataAsync(productId)");
            Console.WriteLine("  GET .../products/all      → GetAllProductsAsync()");
            Console.WriteLine("  GET .../products/summary  → GetProductsSummaryAsync()");
            return 0;
        }
        catch (TruIDException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"SDK error [{ex.Code}]: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Unexpected error: {ex.Message}");
            return 1;
        }
    }

    private static string? ResolveEnv(string primary, string legacy)
        => Environment.GetEnvironmentVariable(primary)
           ?? Environment.GetEnvironmentVariable(legacy);

    /// <summary>
    /// Loads KEY=VALUE pairs from the first .env file found. Does not override variables
    /// already set in the process environment (shell exports win).
    /// </summary>
    private static void LoadDotEnvIfPresent()
    {
        var path = FindEnvFile();
        if (path is null)
            return;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            if (key.Length == 0)
                continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string? FindEnvFile()
    {
        foreach (var candidate in EnvFileCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnvFileCandidates()
    {
        var cwd = Directory.GetCurrentDirectory();
        yield return Path.Combine(cwd, ".env");
        yield return Path.Combine(cwd, "TruID.Connect.Example", ".env");

        var dir = AppContext.BaseDirectory;
        for (var depth = 0; depth < 6 && dir is not null; depth++)
        {
            yield return Path.Combine(dir, ".env");
            yield return Path.Combine(dir, "TruID.Connect.Example", ".env");
            dir = Directory.GetParent(dir)?.FullName;
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "product.bin" : name;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            truid-connect-example — end-to-end truID Connect .NET SDK sample

            Environment (required):
              TRUID_CLIENT_ID or API_KEY     Your truID API key
              TRUID_ENVIRONMENT or ENV       tst | prd (default: tst)

            A .env file in this project directory is loaded automatically
            (shell exports take precedence). See .env.example.

            Options:
              --file PATH         PDF bank statement to upload (repeatable)
              --output-dir DIR    Where to save downloaded products (default: ./output)
              --name NAME         Consumer name (default: Jane Doe)
              --id-number ID      Consumer ID number (default: 8501015009088)
              --id-type TYPE      ID type for upload (default: id)
              --brand-id ID       Brand ID (defaults to first brand from connect)
              --no-force          Skip forceExtraction on upload
              --wait SECONDS      Status poll timeout (default: 300)
              --interval SECONDS  Poll interval (default: 2)
              --no-verify-ssl     Disable TLS verification (local stub only)
              -h, --help          Show this help

            Example:
              cp .env.example .env   # set TRUID_CLIENT_ID, then:
              dotnet run --project TruID.Connect.Example -- --file statement.pdf
            """);
    }

    private sealed record AppOptions(
        IReadOnlyList<string> Files,
        string OutputDir,
        string Name,
        string IdNumber,
        string IdType,
        string? BrandId,
        bool ForceExtraction,
        int WaitTimeoutSeconds,
        int PollIntervalSeconds,
        bool VerifySsl,
        bool ShowHelp)
    {
        internal static AppOptions Parse(string[] args)
        {
            var files = new List<string>();
            var outputDir = "./output";
            var name = "Jane Doe";
            var idNumber = "8501015009088";
            var idType = "id";
            string? brandId = null;
            var forceExtraction = true;
            var wait = 300;
            var interval = 2;
            var verifySsl = true;
            var showHelp = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-h":
                    case "--help":
                        showHelp = true;
                        break;
                    case "--file":
                        if (++i >= args.Length) throw new ArgumentException("--file requires a path");
                        files.Add(args[i]);
                        break;
                    case "--output-dir":
                        if (++i >= args.Length) throw new ArgumentException("--output-dir requires a path");
                        outputDir = args[i];
                        break;
                    case "--name":
                        if (++i >= args.Length) throw new ArgumentException("--name requires a value");
                        name = args[i];
                        break;
                    case "--id-number":
                        if (++i >= args.Length) throw new ArgumentException("--id-number requires a value");
                        idNumber = args[i];
                        break;
                    case "--id-type":
                        if (++i >= args.Length) throw new ArgumentException("--id-type requires a value");
                        idType = args[i];
                        break;
                    case "--brand-id":
                        if (++i >= args.Length) throw new ArgumentException("--brand-id requires a value");
                        brandId = args[i];
                        break;
                    case "--no-force":
                        forceExtraction = false;
                        break;
                    case "--wait":
                        if (++i >= args.Length || !int.TryParse(args[i], out wait))
                            throw new ArgumentException("--wait requires seconds");
                        break;
                    case "--interval":
                        if (++i >= args.Length || !int.TryParse(args[i], out interval))
                            throw new ArgumentException("--interval requires seconds");
                        break;
                    case "--no-verify-ssl":
                        verifySsl = false;
                        break;
                    default:
                        if (args[i].StartsWith('-'))
                            throw new ArgumentException($"Unknown option: {args[i]}");
                        files.Add(args[i]);
                        break;
                }
            }

            return new AppOptions(
                files, outputDir, name, idNumber, idType, brandId,
                forceExtraction, wait, interval, verifySsl, showHelp);
        }
    }
}
