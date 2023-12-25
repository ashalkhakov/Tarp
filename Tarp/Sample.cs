using System;
using System.Collections.Generic;
using System.Linq;

namespace Tarp
{
    public class Sample
    {
        /*
        public static int FactLoop(int x)
        {
            var r = 1;
            while (x > 0)
            {
                r *= x;
                x--;
            }
            return r;
        }

        public static int FactRec(int x)
        {
            return x > 1 ? x * FactRec(x - 1) : 1;
        }

        public static int NestedDecision(int x)
        {
            return x > 0 ?
                (x > 10 ? 1 : -1)
                : 0;
        }*/
        /*
        public static bool NestedDecision2(int x)
        {
            return x > 0 && x < 10 ? (x > 5 ? true : false) : false;
        }*/

        /*
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
        }*/

        public static int NestedDecision3(bool a, bool b, bool c)
        {
            // a == (b || c) is a decision
            // b || c is a decision also
            if (a == (b || c)) {
                return 1;
            }
            return -1;
        }

        /*
        public static int FactForLoop(int x)
        {
            int r;
            for (r = 1; x > 0; x--)
            {
                r *= x;
            }
            return r;
        }

        public static int IfEx(int x)
        {
            int result;
            if (x > 0)
            {
                result = 1;
            }
            else
            {
                result = 0;
            }
            return result;
        }

        public static int SwitchEx(int x)
        {
            switch (x)
            {
                case 0:
                    return 1;
                case 1:
                    return 0;
                default:
                    return 2;
            }
        }

        public static int function_a(int a, bool b, bool c, bool d, bool e, bool f)
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
        
        public static int function_b(int a, bool b, bool c, bool d, bool e, bool f)
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

        private static bool Foo1 = 5 % 2 != 0;

        public static IEnumerable<int> NestedBinder1(int x)
        {
            return Enumerable.Range(0, x).Where(x => x % 2 == 0);
        }*/
    }
}
