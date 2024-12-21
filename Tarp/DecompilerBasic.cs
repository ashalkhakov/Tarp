using System;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;

namespace Tarp;

public class DecompilerBasic
{
    public static void Decompile(string fileName)
    {
        var decompiler = new CSharpDecompiler(fileName, new DecompilerSettings());
        var tree = decompiler.DecompileWholeModuleAsSingleFile();

        foreach (var module in decompiler.TypeSystem.Modules)
        {
            foreach (var type in module.TopLevelTypeDefinitions)
            {
                foreach (var method in type.Methods)
                {
                    Console.WriteLine(method.FullName);
                }
            }
        }
        
        var sequencePoints = decompiler.CreateSequencePoints(tree);
        foreach (var (func, points) in sequencePoints)
        {
            var context = decompiler.CreateILTransformContext(func);

        }
    }
}
