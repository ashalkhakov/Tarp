using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TarpTests.TestSources;

public class Instrument1
{
    public void Foo(int x)
    {

    }

    public int Bar(int x) => x + 1;
    
    
    public int Method0(int a)
    {
        int r;

        r = 1;
        r++;
        r *= 2;

        lab1:
        lab2: r += 3;

        return r;
    }
    
    public int Method1(int a)
    {
        int r;

        if (a > 0)
        {
            r = 1;
        }
        else
        {
            r = -1;
        }

        return r;
    }
    public int Method2(int a)
    {
        int r = 1;

        while (a > 0)
        {
            r *= a;
            a--;
        }

        return r;
    }
    /*
    public void Mehtod01(int a)
    {
        var i = 0;
        while (i < a)
        {
            if (i % 2 == 0)
                break;
            i++;
        }
    }
    
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
        do
        {
            Foo(x);
        } while (c < z);

        switch (a)
        {
            case 0:
            case 1:
                Foo(1);
                break;
            case 2:
                Foo(2);
                break;
            default:
                Foo(-1);
                break;
        }
    }

    static int[] fibcache = new int[1000]; // initially 0s
    static int fib(int i) // fast Fibonacci
    {
        int t;
        switch (i)
        {
            case 0:
            case 1:
                return 1;
            default:
                if (fibcache[i] > 0)
                {
                    return fibcache[i];
                }
                else
                {
                    t = fib(i - 1);
                    fibcache[i] = t + fib(i - 2);
                    return fibcache[i];
                }
        }
    }*/
}
