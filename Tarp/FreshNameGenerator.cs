namespace Tarp;

public class FreshNameGenerator(string prefix)
{
    private int _stamp = 0;

    public string GetStampedName(string name) => $"{name}_{_stamp++}";

    public string GetFreshName()
    {
        return $"{prefix}_{_stamp++}";
    }
}
