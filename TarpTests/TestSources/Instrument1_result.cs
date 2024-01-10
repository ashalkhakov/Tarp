using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarpTests.TestSources
{
    public class Instrument1_result
    {
        public void Foo(int x)
        {
            TARPGEN.C.Inc(0);
        }

        public int Bar(int x) { TARPGEN.C.Inc(1); return x + 1; }

        public void Method1(int a, int b, int c, int x, int y, int z)
        {
            TARPGEN.C.Inc(2); int max;

            if (a > b && c < a)
            {
                TARPGEN.C.Inc(3); max = a;
            }
            else
            {
                TARPGEN.C.Inc(3); max = b;
            }
            for (; max > a; a++)
            {
                TARPGEN.C.Inc(4); Foo(x);
            }
            TARPGEN.C.Inc(4);
            while (b < z)
            {
                TARPGEN.C.Inc(5); Foo(x);
            }
            TARPGEN.C.Inc(5); bool __first6 = true;
            do
            {
                if (!__first6) { TARPGEN.C.Inc(6); __first6 = false; }
                Foo(x);
            } while (c < z);
            TARPGEN.C.Inc(6);
            switch (a)
            {
                case 0:
                case 1:
                    TARPGEN.C.Inc(7); Foo(1);
                    break;
                case 2:
                    TARPGEN.C.Inc(8); Foo(2);
                    break;
                default:
                    TARPGEN.C.Inc(9); Foo(-1);
                    break;
            }
        }
    }
}
