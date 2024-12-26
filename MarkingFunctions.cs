namespace TarpTests.TestSources;

public class MarkingFunctions
{
    public int F1(int a, bool b, bool c)
    {
TARPGEN.C.Inc(82);TARPGEN.C.Inc(83);        if ((a == 5) && (b || c))
        {
TARPGEN.C.Inc(84);            return 1;
TARPGEN.C.Inc(85);        }
TARPGEN.C.Inc(86);        return 0;
    }
}
