namespace TarpTests.TestSources;

public class AllBooleanExpressions
{
    public static int FunctionA(int a, bool b, bool c, bool d, bool e, bool f)
    {
        if (a == 100)
        {
            if (b || c)
                return 1;

            if (d || e || f)
                return 2;
        }
        return 0;
    }

    public static int FunctionB(int a, bool b, bool c, bool d, bool e, bool f)
    {
        bool a_is_equal_to_100 = a == 100;
        bool b_or_c = b || c;
        bool d_or_e_or_f = d || e || f;

        if (a_is_equal_to_100)
        {
            if (b_or_c)
                return 1;

            if (d_or_e_or_f)
                return 2;
        }
        return 0;
    }
}
