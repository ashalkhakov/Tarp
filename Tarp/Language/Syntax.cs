using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tarp.Language;

public abstract class AbstractSyntax { }

public class Module : AbstractSyntax
{
    public string FilePath { get; set; }
    public string Namespace { get; set; }
    public List<Declaration> Declarations { get; set; } = new();
}

public class Declaration : AbstractSyntax
{
    
}

public class ClassDeclaration : AbstractSyntax
{
    public string Name { get; set; }
    public PropertyDeclaration Properties { get; set; }
    public List<MethodDeclaration> Methods { get; set; }
}

public class PropertyDeclaration : Declaration { }

public class MethodDeclaration : Declaration
{

}