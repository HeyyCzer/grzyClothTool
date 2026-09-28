using grzyClothTool.Optimization;

namespace grzyClothTool.Optimizer.Cli;

internal sealed class CliOptions
{
    public string? InputFolder { get; private set; }
    public string? OutputFolder { get; private set; }
    public bool InPlace { get; private set; }
    public int DiffuseLimit { get; private set; } = 1024;
    public int NormalLimit { get; private set; } = 1024;
    public int SpecularLimit { get; private set; } = 1024;
    public bool DryRun { get; private set; }
    public int Threads { get; private set; } = Math.Max(1, Environment.ProcessorCount - 1);
    public bool Verbose { get; private set; }
    public bool ShowHelp { get; private set; }

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CliOptions();

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help" or "/?":
                    options.ShowHelp = true;
                    return options;
                case "-o" or "--out":
                    options.OutputFolder = NextValue(args, ref i, arg);
                    break;
                case "--in-place":
                    options.InPlace = true;
                    break;
                case "--diffuse":
                    options.DiffuseLimit = NextSize(args, ref i, arg);
                    break;
                case "--normal":
                    options.NormalLimit = NextSize(args, ref i, arg);
                    break;
                case "--specular":
                    options.SpecularLimit = NextSize(args, ref i, arg);
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "-j" or "--threads":
                    options.Threads = NextInt(args, ref i, arg, min: 1);
                    break;
                case "-v" or "--verbose":
                    options.Verbose = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new ArgumentException($"Unknown option '{arg}'.");
                    }
                    if (options.InputFolder != null)
                    {
                        throw new ArgumentException($"Only one folder can be given (got '{options.InputFolder}' and '{arg}').");
                    }
                    options.InputFolder = arg.Trim('"');
                    break;
            }
        }

        if (options.InputFolder == null)
        {
            throw new ArgumentException("No folder given.");
        }
        if (options.InPlace && options.OutputFolder != null)
        {
            throw new ArgumentException("Use either --out or --in-place, not both.");
        }

        return options;
    }

    public FolderOptimizerOptions ToOptimizerOptions()
    {
        var input = Path.GetFullPath(InputFolder!).TrimEnd('\\', '/');
        return new FolderOptimizerOptions
        {
            InputFolder = input,
            OutputFolder = InPlace ? null : OutputFolder ?? input + "_optimized",
            DiffuseLimit = DiffuseLimit,
            NormalLimit = NormalLimit,
            SpecularLimit = SpecularLimit,
            DryRun = DryRun,
            MaxParallelism = Threads
        };
    }

    private static string NextValue(IReadOnlyList<string> args, ref int i, string option)
    {
        if (i + 1 >= args.Count)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }
        return args[++i].Trim('"');
    }

    private static int NextInt(IReadOnlyList<string> args, ref int i, string option, int min)
    {
        var value = NextValue(args, ref i, option);
        if (!int.TryParse(value, out var number) || number < min)
        {
            throw new ArgumentException($"{option} expects a number >= {min} (got '{value}').");
        }
        return number;
    }

    private static int NextSize(IReadOnlyList<string> args, ref int i, string option)
    {
        var size = NextInt(args, ref i, option, min: TextureRules.MinTextureSize);
        if (!TextureRules.IsPowerOfTwo(size))
        {
            throw new ArgumentException($"{option} must be a power of two, like 512, 1024 or 2048 (got {size}).");
        }
        return size;
    }
}
