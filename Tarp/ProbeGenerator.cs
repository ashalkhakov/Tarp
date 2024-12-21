using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Tarp
{
    public class ProbeGenerator
    {
        public ProbeGenerator()
        {

        }

        private List<ProbeMethodInfo> probeMethods = new List<ProbeMethodInfo>();
        private int counter;

        public void StartMethod(Location location, string className, string methodName)
        {
            var method = new ProbeMethodInfo
            {
                Location = location,
                ClassName = className,
                Name = methodName,
                Probes = new()
            };
            this.probeMethods.Add(method);
        }

        public int AddProbe(ProbeKind kind, TextSpan textSpan)
        {
            Debug.Assert(this.probeMethods.Count > 0);
            var probe = new ProbeInfo { Id = counter++, Kind = kind, TextSpan = textSpan };
            this.probeMethods.Last().Probes.Add(probe);

            return probe.Id;
        }

        public void Dump()
        {
            foreach (var method in probeMethods)
            {
                Console.WriteLine("{0}: {1}.{2}", method.Location.ToString(), method.ClassName, method.Name);
                foreach (var probe in method.Probes)
                {
                    Console.WriteLine("{0}: {1}, {2}", probe.TextSpan, probe.Id, probe.Kind);
                }
            }
        }

        private class ProbeMethodInfo
        {
            public Location Location { get; set; }
            public string ClassName { get; set; }
            public string Name { get; set; }
            public List<ProbeInfo> Probes { get; set; }
        }

        private class ProbeInfo
        {
            public int Id { get; set; }
            public ProbeKind Kind { get; set; }
            public TextSpan TextSpan { get; set; }
        }

        public enum ProbeKind
        {
            Entry,
            Exit,
            Block,
            Decision,
            Condition
        }
    }
}
