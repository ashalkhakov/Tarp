using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Tarp.Basics
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
            probeMethods.Add(method);
        }

        public int AddProbe(ProbeKind kind, TextSpan textSpan)
        {
            Debug.Assert(probeMethods.Count > 0);
            var probe = new ProbeInfo { Id = counter++, Kind = kind, TextSpan = textSpan };
            probeMethods.Last().Probes.Add(probe);

            return probe.Id;
        }

        public CompilationUnitSyntax GenerateInstrumentationSupportCode()
        {
            var usingSystemIo = UsingDirective(QualifiedName(IdentifierName("System"), IdentifierName("IO")));
            var usings = SyntaxList.Create([usingSystemIo]);
            var tree = CompilationUnit().WithUsings(usings);
            /*
            var tarpGen = IdentifierName(Identifier("TARPGEN"));
            var namespaceDecl = NamespaceDeclaration(tarpGen)
                .WithMembers();

            tree.WithMembers(SyntaxList.Create([NamespaceDeclaration()]));

            return tree;*/
            throw new NotImplementedException();
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
