using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarpTests.TestSources
{
    internal class LineCoverageExamples
    {
        // basic block
        public void M1()
        {
            // <-- probe: entry into method
            Console.WriteLine("hello");
            Console.WriteLine("there");
            // <-- probe goes here and spans both of the above statements (exit from method)
        }

        // variable declaration
        public void M1var()
        {
            // <-- entry probe
            var x = 1;
            var y = x * 2 + 3;
            Console.WriteLine(x);
            Console.WriteLine(y);
            // <-- probe for the whole code block
            // <-- exit probe
        }

        // if-then-else
        public void M2(int x)
        {
            // <-- probe: entry into method
            if (x > 0) // need a probe for the decision
            {
                Console.WriteLine("x");
                // <-- probe goes here (end of block)
            }
            else
            {
                Console.WriteLine("y");
                // <-- probe goes here (end of block)
            }
            // <-- probe: end of method
        }

        // switch
        public void M3(int x)
        {
            // <-- probe: entry into method
            switch (x) // <-- this probe will be covered anyways I think
            {
                case 0:
                    // <-- probe for decision x==0
                    Console.WriteLine("0");
                    // <-- probe
                    break;
                case 1:
                    // <-- probe for decision !(x==0) && x==1
                    Console.WriteLine("1");
                    // <-- probe
                    break;
                default:
                    // <-- probe for decision !(x==0 || x==1)
                    Console.WriteLine("default");
                    // <-- probe
                    break;
            }
            // <-- probe: exit of method
        }

        // while loops
        public void M4(int x)
        {
            // probe: entry of method
            var i = 0;
            // probe: i = 0 has executed
            while (i < x)
            {
                // <-- probe: decision i < x == true
                Console.WriteLine(i);
                i++;
            }
            // <-- probe: decision i < x == false
            // <-- probe: exit of method
        }

        // do loops
        public void M5(int x)
        {
            // probe: entry of method
            var i = 0;
            // probe: i = 0 has executed <-- this is 
            do
            {
                Console.WriteLine(i);
                i++;
            } while (i < x);
        }

        // for loops
        public void M6(int x)
        {
            // <-- probe: entry into method
            // <-- probe for [var i = 0] (whatever is the declaration block in the [for] loop below
            for (var i = 0; i < x; i++/*probe for the statement*/) // NOTE: each of these needs their own probing...
            {
                // <-- probe: decision i < x == true
                Console.WriteLine(i);
                // <-- probe for the block
            }
            // <-- probe: decision i < x == false
            // <-- probe: exit of method
        }

        // foreach loops
        public void M7(int x)
        {
            // <-- entry probe
            /*
             * compiles to:
             * IEnumerator<int> tmp;
             * tmp = Enumerable.Range(0, x).GetEnumerator() // of type IEnumerable or whatever
             * try {
             *   goto IL_001a;
             *   Console.WriteLine(tmp.Current);
             *   IL_001a: if (tmp.MoveNext()) {goto IL_000f;} else {goto IL_002e;}
             * }
             * finally {
             *   tmp.Dispose();
             * }
             * IL_002e:
             */
            foreach (var i in Enumerable.Range(0, x)) // FIXME: how to put a probe onto the evaluation of Enumerable.Range(0,x) itself???
            {
                // <-- probe: the decision in [foreach (var i in Enumerable.Range(0, x))] evaluates to true
                // <-- probe: [var i] has executed
                Console.WriteLine(i);
                // <-- probe: loop body
            }
            // <-- probe: the decision in [foreach (var i in Enumerable.Range(0, x))] evaluates to false
            // <-- exit probe
        }
    }
}
