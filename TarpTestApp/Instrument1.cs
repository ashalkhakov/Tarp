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
    
    #region Label
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
    #endregion
    
    #region If
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
    public int Method1_1(int a)
    {
        int r;

        if (a > 0)
        {
            r = 1;
        }
        else if (a < 0)
        {
            r = -1;
        }
        else
        {
            r = 0;
        }
        return r;
    }
    public int Method1_2(int a)
    {
        if (a > 0)
        {
            return 1;
        }
        return -1;
    }
    
    #endregion
    
    #region While

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

    public void Method2_1(int a)
    {
        var i = 0;
        while (true)
        {
            if (i > a)
                break;
            i++;
        }
    }

    public int Method3(int c)
    {
        int r = 1;
        do
        {
            r *= c;
            c--;
        }
        while (c > 0);
        return r;
    }
    #endregion

    #region For
    public int Method4(int c)
    {
        int r;
        for (r = 1; c > 0; c--)
        {
            r *= c;
        }
        return r;
    }
    #endregion

    #region Switch
    public int Method5(int c)
    {
        switch (c)
        {
            case 0:
                return 1;
            case 1:
            default:
                return 3;
            case 2:
            case 3:
                return 2;
        }
    }
    public int Method5_0(int c)
    {
        int r;
        switch (c)
        {
            case 0:
                r = 1;
                break;
            case 1:
            default:
                r = 3;
                break;
            case 2:
            case 3:
                r = 2;
                break;
        }
        return r;
    }

    public int Method5_1(int c)
    {
        switch (c)
        {
            case 0:
                return 1;
            case 1:
                return 2;
        }
        return 3;
    }
    #endregion

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
    }
}
