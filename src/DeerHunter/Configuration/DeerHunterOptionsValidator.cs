using Microsoft.Extensions.Options;

namespace DeerHunter.Configuration;

public sealed class DeerHunterOptionsValidator : IValidateOptions<DeerHunterOptions>
{
    public ValidateOptionsResult Validate(string? name, DeerHunterOptions options)
    {
        var failures = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in options.Processes)
        {
            if (string.IsNullOrWhiteSpace(process.Name))
            {
                failures.Add("Every process must define a name.");
                continue;
            }

            if (!names.Add(process.Name))
            {
                failures.Add($"Duplicate process name '{process.Name}'.");
            }

            if (string.IsNullOrWhiteSpace(process.Command))
            {
                failures.Add($"Process '{process.Name}' is missing a command.");
            }

            var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in process.Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Id))
                {
                    failures.Add($"Process '{process.Name}' contains a rule without an id.");
                }
                else if (!ruleIds.Add(rule.Id))
                {
                    failures.Add($"Process '{process.Name}' contains duplicate rule id '{rule.Id}'.");
                }

                if (string.IsNullOrWhiteSpace(rule.Pattern))
                {
                    failures.Add($"Process '{process.Name}' rule '{rule.Id}' is missing a pattern.");
                }

                if (rule.Actions.Count == 0)
                {
                    failures.Add($"Process '{process.Name}' rule '{rule.Id}' must contain at least one action.");
                }
            }
        }

        if (options.Api.Urls.Length == 0)
        {
            failures.Add("At least one local API URL must be configured.");
        }

        foreach (var url in options.Api.Urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                failures.Add($"API URL '{url}' is not a valid absolute URL.");
                continue;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"API URL '{url}' must use http.");
            }

            if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"API URL '{url}' must bind to localhost only.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
