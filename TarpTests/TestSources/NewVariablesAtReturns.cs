using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarpTests.TestSources
{
    internal class NewVariablesAtReturns
    {
        public int Foo(int x)
        {
            return x * 2 + 3;
        }

        public int Bar(int x)
        {
            return Foo(x);
        }

        public int Baz(int x) => 2 * Bar(x + 1);
    }
}
