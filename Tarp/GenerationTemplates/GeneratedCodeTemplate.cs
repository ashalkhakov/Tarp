using System.IO;

namespace TARPGEN
{
    public class C
    {
        private const int NumLocations = 1;

        private readonly static string[] _locations = new string[NumLocations]
        {
            "foo"
        };
        private readonly static int[] _counters = new int[NumLocations]
        {
            0
        };

        public static void Inc(int loc)
        {
            _counters[loc]++;
        }

        public static void Dump()
        {
            using var sr = new StreamWriter("out.cts");

            for (var i = 0; i < _locations.Length; i++)
            {
                sr.WriteLine("{0}: {1}", _locations[i], _counters[i]);
            }

            sr.Flush();
        }
    }
}
