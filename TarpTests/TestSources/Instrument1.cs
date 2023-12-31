using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarpTests.TestSources
{
    public class Instrument1
    {
        public void Foo(int x)
        {

        }

        public int Bar(int x) => x + 1;

        public void Method1(int a, int b, int c, int x, int y, int z)
        {
            int max;

            if (a > b && c < a)
                max = a;
            else
                max = b;

            for (; max > a; a++)
            {
                Foo(x);
            }

            while (b < z)
            {
                Foo(x);
            }

            do
            {
                Foo(x);
            } while (c < z);
        }
    }
}
