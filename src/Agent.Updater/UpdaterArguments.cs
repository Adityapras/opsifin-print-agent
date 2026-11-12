namespace Agent.Updater;

internal sealed class UpdaterArguments(IReadOnlyDictionary<string, string> values)
{
    public static UpdaterArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Updater arguments must use --name value pairs.");
            }

            values[args[index][2..]] = args[index + 1];
        }

        return new UpdaterArguments(values);
    }

    public string Required(string name) => values.TryGetValue(name, out var value)
        && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Updater argument --{name} is required.");

    public int RequiredInt32(string name) => int.TryParse(
        Required(name),
        System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture,
        out var value)
            ? value
            : throw new ArgumentException($"Updater argument --{name} must be an integer.");
}
