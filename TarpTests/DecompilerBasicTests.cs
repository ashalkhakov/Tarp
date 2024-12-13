using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tarp;

namespace TarpTests;

[TestClass]
public class DecompilerBasicTests
{
    [TestMethod]
    public void Test()
    {
        DecompilerBasic.Decompile("TarpTestApp.dll");
    }
}
