using System.Text.RegularExpressions;

namespace Shop.Core;

public static partial class Orders
{
    [GeneratedRegex("^[A-Z]{2}-[0-9]+$")]
    private static partial Regex OrderId();

    public static bool CanShip(bool paid, bool inStock, bool backorderAllowed)
    {
        if ((paid && inStock) || backorderAllowed)
            return true;
        return false;
    }

    public static bool IsValidId(string? id) =>
        id != null && id.Length < 20 && OrderId().IsMatch(id);

    public static string Describe(object? o)
    {
        if (o is string s && s.Length > 0)
            return s;
        return o is null ? "null" : "other";
    }

    public static int Parse(string text)
    {
        if (!int.TryParse(text, out var n) || n < 0)
            return -1;
        return n;
    }
}
