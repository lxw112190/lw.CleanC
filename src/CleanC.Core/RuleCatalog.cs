namespace CleanC.Core;

public static class RuleCatalog
{
    public static readonly string[] LegacyIds = ["user-temp","crash-dumps","directx","chrome","edge","npm","pip","nuget","yarn","uv","vscode"];
    public static RuleDocument Upgrade(RuleDocument current, RuleDocument shipped, IEnumerable<string> seen, PathPolicy policy)
    {
        policy.Validate(current); policy.Validate(shipped);
        var known = new HashSet<string>(seen, StringComparer.OrdinalIgnoreCase);
        var rules = current.Rules.ToList();
        foreach (var rule in shipped.Rules.Where(r => !known.Contains(r.Id) && !rules.Any(x => x.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase))))
        {
            var paths = rules.SelectMany(r => r.Paths).Select(PathPolicy.ExpandRoot).ToArray();
            if (rule.Paths.Select(PathPolicy.ExpandRoot).Any(p => paths.Any(q => PathPolicy.Within(p,q) || PathPolicy.Within(q,p)))) continue;
            rules.Add(rule);
        }
        var merged = current with { Rules = rules }; policy.Validate(merged); return merged;
    }
}
