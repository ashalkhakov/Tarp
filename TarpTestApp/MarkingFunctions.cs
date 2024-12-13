namespace TarpTests.TestSources;

public class MarkingFunctions
{
    public int F1(int a, bool b, bool c)
    {
        if ((a == 5) && (b || c))
        {
            return 1;
        }
        return 0;
    }
}
