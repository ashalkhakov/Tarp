using Microsoft.CodeAnalysis;
using System;

namespace Tarp.Cfg;

public class CFGBuilderException : Exception
{
    public CFGBuilderException(string message, Location location)
        : base(message)
    {
        this.location = location;
    }

    private Location location;

    public Location Location { get => location; }
}
