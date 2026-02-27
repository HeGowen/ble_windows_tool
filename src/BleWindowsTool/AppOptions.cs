namespace BleWindowsTool;

public sealed class AppOptions
{
    public string ConfigPath { get; set; } = string.Empty;
    public string LogRoot { get; set; } = string.Empty;
    public bool ScanOnly { get; set; }
    public string AddressOverride { get; set; } = string.Empty;
    public string NameOverride { get; set; } = string.Empty;
    public int SessionSecondsOverride { get; set; }
    public int ScanAttemptsOverride { get; set; }

    public static AppOptions Parse(string[] args)
    {
        var opt = new AppOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string NextOr(string fallback)
            {
                if (i + 1 >= args.Length)
                {
                    return fallback;
                }

                i++;
                return args[i];
            }

            switch (arg)
            {
                case "--config":
                    opt.ConfigPath = NextOr(opt.ConfigPath);
                    break;
                case "--log-root":
                    opt.LogRoot = NextOr(opt.LogRoot);
                    break;
                case "--scan-only":
                    opt.ScanOnly = true;
                    break;
                case "--address":
                    opt.AddressOverride = NextOr(string.Empty);
                    break;
                case "--name":
                    opt.NameOverride = NextOr(string.Empty);
                    break;
                case "--session-seconds":
                    opt.SessionSecondsOverride = ParseInt(NextOr("0"));
                    break;
                case "--scan-attempts":
                    opt.ScanAttemptsOverride = ParseInt(NextOr("0"));
                    break;
                default:
                    break;
            }
        }

        return opt;
    }

    private static int ParseInt(string text)
    {
        return int.TryParse(text, out var value) ? value : 0;
    }
}
