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
TARPGEN.C.Inc(0);TARPGEN.C.Inc(1);return;
    }

    public int Bar(int x) {TARPGEN.C.Inc(2);{
#line(1,27)-(1,40)""
var __tmp_0= x + 1;TARPGEN.C.Inc(3);return __tmp_0;}};
    
    #region Label
    public int Method0(int a)
    {
TARPGEN.C.Inc(4);        int r;

        r = 1;
        r++;
        r *= 2;

        lab1:
        lab2: r += 3;
TARPGEN.C.Inc(5);
        return r;
    }
    #endregion
    
    #region If
    public int Method1(int a)
    {
TARPGEN.C.Inc(6);        int r;
TARPGEN.C.Inc(7);
        if (a > 0)
        {
            r = 1;
TARPGEN.C.Inc(8);        }
        else
        {
            r = -1;
TARPGEN.C.Inc(9);        }
TARPGEN.C.Inc(10);
        return r;
    }
    public int Method1_1(int a)
    {
TARPGEN.C.Inc(11);        int r;
TARPGEN.C.Inc(12);
        if (a > 0)
        {
            r = 1;
TARPGEN.C.Inc(13);        }
        else {TARPGEN.C.Inc(14);if (a < 0)
        {
            r = -1;
TARPGEN.C.Inc(15);        }
        else
        {
            r = 0;
TARPGEN.C.Inc(16);        }
TARPGEN.C.Inc(17);}TARPGEN.C.Inc(18);        return r;
    }
    public int Method1_2(int a)
    {
TARPGEN.C.Inc(19);TARPGEN.C.Inc(20);        if (a > 0)
        {
TARPGEN.C.Inc(21);            return 1;
TARPGEN.C.Inc(22);        }
{
#line(73,8)-(73,18)"C:\\Users\\User\\source\\projects\\McDcTestCoverage\\TarpTestApp\\Instrument1.cs"
        var __tmp_1=-1;TARPGEN.C.Inc(23);return __tmp_1;
}    }
    
    #endregion
    
    #region While

    public int Method2(int a)
    {
TARPGEN.C.Inc(24);        int r = 1;
TARPGEN.C.Inc(25);
        while (a > 0)
        {
            r *= a;
            a--;
TARPGEN.C.Inc(26);        }
TARPGEN.C.Inc(27);
        return r;
    }

    public void Method2_1(int a)
    {
TARPGEN.C.Inc(28);        var i = 0;
TARPGEN.C.Inc(29);        while (true)
        {
TARPGEN.C.Inc(30);            if (i > a)
{TARPGEN.C.Inc(31);                break;
TARPGEN.C.Inc(32);}            i++;
TARPGEN.C.Inc(33);        }
TARPGEN.C.Inc(34);return;    }

    public int Method3(int c)
    {
TARPGEN.C.Inc(35);        int r = 1;
TARPGEN.C.Inc(36);        do
        {
            r *= c;
            c--;
TARPGEN.C.Inc(37);        }
        while (c > 0);
TARPGEN.C.Inc(38);        return r;
    }
    #endregion

    #region For
    public int Method4(int c)
    {
TARPGEN.C.Inc(39);        int r;
TARPGEN.C.Inc(40);        for (r = 1; c > 0; c--)
        {
            r *= c;
TARPGEN.C.Inc(41);        }
TARPGEN.C.Inc(42);        return r;
    }
    #endregion

    #region Switch
    public int Method5(int c)
    {
TARPGEN.C.Inc(43);TARPGEN.C.Inc(44);        switch (c)
        {
            case 0:
{TARPGEN.C.Inc(45);                return 1;
}            case 1:
            default:
{TARPGEN.C.Inc(46);                return 3;
}            case 2:
            case 3:
{TARPGEN.C.Inc(47);                return 2;
}        }
TARPGEN.C.Inc(48);return;    }
    public int Method5_0(int c)
    {
TARPGEN.C.Inc(49);        int r;
TARPGEN.C.Inc(50);        switch (c)
        {
            case 0:
{                r = 1;
TARPGEN.C.Inc(51);                break;
}            case 1:
            default:
{                r = 3;
TARPGEN.C.Inc(52);                break;
}            case 2:
            case 3:
{                r = 2;
TARPGEN.C.Inc(53);                break;
}        }
TARPGEN.C.Inc(54);        return r;
    }

    public int Method5_1(int c)
    {
TARPGEN.C.Inc(55);TARPGEN.C.Inc(56);        switch (c)
        {
            case 0:
{TARPGEN.C.Inc(57);                return 1;
}            case 1:
{TARPGEN.C.Inc(58);                return 2;
}        }
TARPGEN.C.Inc(59);        return 3;
    }
    #endregion

    public void Method1(int a, int b, int c, int x, int y, int z)
    {
TARPGEN.C.Inc(60);        int max;
TARPGEN.C.Inc(61);
        if (a > b && c < a)
{            max = a;
TARPGEN.C.Inc(62);}        else
{            max = b;
TARPGEN.C.Inc(63);}TARPGEN.C.Inc(64);        for (; max > a; a++)
        {
            Foo(x);
TARPGEN.C.Inc(65);        }
TARPGEN.C.Inc(66);        do
        {
            Foo(x);
TARPGEN.C.Inc(67);        } while (c < z);
TARPGEN.C.Inc(68);
        switch (a)
        {
            case 0:
            case 1:
{                Foo(1);
TARPGEN.C.Inc(69);                break;
}            case 2:
{                Foo(2);
TARPGEN.C.Inc(70);                break;
}            default:
{                Foo(-1);
TARPGEN.C.Inc(71);                break;
}        }
TARPGEN.C.Inc(72);return;    }

    static int[] fibcache = new int[1000]; // initially 0s
    static int fib(int i) // fast Fibonacci
    {
TARPGEN.C.Inc(73);        int t;
TARPGEN.C.Inc(74);        switch (i)
        {
            case 0:
            case 1:
{TARPGEN.C.Inc(75);                return 1;
}            default:
{TARPGEN.C.Inc(76);                if (fibcache[i] > 0)
                {
{
#line(3,20)-(3,39)""
                    var __tmp_2=fibcache[i];TARPGEN.C.Inc(77);return __tmp_2;
}TARPGEN.C.Inc(78);                }
                else
                {
                    t = fib(i - 1);
                    fibcache[i] = t + fib(i - 2);
{
#line(9,20)-(9,39)""
                    var __tmp_3=fibcache[i];TARPGEN.C.Inc(79);return __tmp_3;
}TARPGEN.C.Inc(80);                }
}        }
TARPGEN.C.Inc(81);return;    }
}
