using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Tarp.Cfg;

public class CFGRegion(int id, CFGRegionKind kind)
{
    List<CFGRegion> children;
    CFGRegion parent;

    public int Id { get; set; } = id;
    public CFGRegionKind Kind { get; set; } = kind;

    public CFGRegion Parent
    {
        get => parent;
    }

    public IReadOnlyCollection<CFGRegion> Children
    {
        get => children;
    }

    public void SanityCheck()
    {
        if (parent != null)
        {
            Debug.Assert(parent.Children.Contains(this));
        }
        foreach (var c in Children)
        {
            Debug.Assert(c.Parent == this);
        }

        switch (Kind)
        {
            case CFGRegionKind.Try:
                break;
            case CFGRegionKind.Catch:
                break;
            case CFGRegionKind.TryCatch:
                break;
            case CFGRegionKind.TryFinally:
                break;
            default:
                Debug.Assert(false, "Kind is not handled");
                break;
        }
    }
}
