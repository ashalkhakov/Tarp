given a program Program.cs:

1 > public class Program {
2 > 	public static void Main(string[] args) {
3 >			Console.WriteLine("Hello")
4 > 		Console.WriteLine("World");
5 > 	}
6 > }

we want to have it instrumented thus:

1 > public class Program {
2 > 	public static void Main(string[] args) {
3 >			Console.WriteLine("Hello");
4 >			Console.WriteLine("World");
5 >			INSTR(0);
6 >		}
7 > }

and have the following info on hand in addition to the actual counters we get by running it:

point 0: function Program.Main, file Program.cs, lines 3-4

for line coverage, I think we can just build a CFG, and then go over the basic blocks.
we'll only need to insert probes at the end of each basic block. so a probe would
tell us that the block executed, which may cover multiple lines.

as for conditions and decisions, you can turn expressions like

A || B

into

decision(0,condition(0,A) || condition(1,B))

coupled with information:

decision 0 = A || B:
  condition 0 = A
  condition 1 = B

the functions are identities, but they have the side-effect of incrementing the corresponding counter.