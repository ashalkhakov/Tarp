namespace TarpTests.TestSources;

public class NestedConditionals
{
    public static int NestedDecision2_1(bool A, bool B, bool C, bool D)
    {
        // implicitly handled as:
        //    x = B ? C : D;
        //    if (A && x) ...
        // alternative coverage from one paper:
        //   (a ? b : c)
        // independence effect of condition [a] is shown by two truth vectors
        //              {a=true,b=true},{a=false,c=false} or
        //              {a=true,b=false},{a=false,c=true}
        // independence effect of condition b is shown by two truth vectors such as
        //              {a = true, b = true},
        //              {a = true, b = false}.
        // Independence effect of condition c is shown by two truth vectors such as {a = false, c = true}, {a = false, c = false}.
        if (A && (B ? C : D)) {
            return 1;
        }
        return -1;
    }

    /*
    public static int NestedDecision(int x)
    {
        return x > 0 ?
            (x > 10 ? 1 : -1)
            : 0;
    }

    public static bool NestedDecision2(int x)
    {
        return x > 0 && x < 10 ? (x > 5 ? true : false) : false;
    }*/

    public static int NestedDecision3(bool a, bool b, bool c)
    {
        // a == (b || c) is a decision
        // b || c is a decision also
        // and also [a] is a decision! because we don't interpet [==]
        if (a == (b || c))
        {
            return 1;
        }
        return -1;
    }
}
