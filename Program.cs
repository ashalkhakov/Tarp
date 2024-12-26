namespace TarpTestApp;

public class Program
{
    public static void Main(string[] args)
    {
TARPGEN.C.Inc(99);        Console.WriteLine("Hello, World!");
TARPGEN.C.Inc(100);return;    }

    public static int FunctionA(int a, bool b, bool c, bool d, bool e, bool f)
    {
TARPGEN.C.Inc(101);TARPGEN.C.Inc(102);        if (a == 100)
        {
TARPGEN.C.Inc(103);            if (b || c)
{TARPGEN.C.Inc(104);                return 1;
TARPGEN.C.Inc(105);}TARPGEN.C.Inc(106);
            if (d || e || f)
{TARPGEN.C.Inc(107);                return 2;
TARPGEN.C.Inc(108);}TARPGEN.C.Inc(109);        }
TARPGEN.C.Inc(110);        return 0;
    }

    public static int FunctionB(int a, bool b, bool c, bool d, bool e, bool f)
    {
TARPGEN.C.Inc(111);        bool a_is_equal_to_100 = a == 100;
        bool b_or_c = b || c;
        bool d_or_e_or_f = d || e || f;
TARPGEN.C.Inc(112);
        if (a_is_equal_to_100)
        {
TARPGEN.C.Inc(113);            if (b_or_c)
{TARPGEN.C.Inc(114);                return 1;
TARPGEN.C.Inc(115);}TARPGEN.C.Inc(116);
            if (d_or_e_or_f)
{TARPGEN.C.Inc(117);                return 2;
TARPGEN.C.Inc(118);}TARPGEN.C.Inc(119);        }
TARPGEN.C.Inc(120);        return 0;
    }
}
