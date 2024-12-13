namespace TarpTests.TestSources;

public class SimpleDecisions
{
    public static bool Example1(bool A, bool B, bool C, bool D)
    {
        return (A && B) || C;
    }

    public static bool Example2(bool A, bool B)
    {
        return A && !B;
    }
}
