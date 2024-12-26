namespace TarpTests.TestSources;

public class SimpleDecisions
{
    public static bool Example1(bool A, bool B, bool C, bool D)
    {
TARPGEN.C.Inc(121);{
#line(6,8)-(6,29)"C:\\Users\\User\\source\\projects\\McDcTestCoverage\\TarpTestApp\\SimpleDecisions.cs"
        var __tmp_7=(A && B) || C;TARPGEN.C.Inc(122);return __tmp_7;
}    }

    public static bool Example2(bool A, bool B)
    {
TARPGEN.C.Inc(123);{
#line(11,8)-(11,23)"C:\\Users\\User\\source\\projects\\McDcTestCoverage\\TarpTestApp\\SimpleDecisions.cs"
        var __tmp_8=A && !B;TARPGEN.C.Inc(124);return __tmp_8;
}    }
}
