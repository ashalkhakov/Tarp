# JaCoCo

https://www.jacoco.org/jacoco/trunk/doc/flow.html

they describe what they do exactly.

- they use a different CFG representation
  - where we have basic block types, they have edge types:
	- entry, exit edges
	- sequence edge (corresponds to our "block" type)
	- jump edge (corresponds to our "jump" type)
	- exhandler edge (we don't this yet)
	- exexit edge (unhandled exception exit)

# GCC

http://www.ucw.cz/~hubicka/papers/proj/node18.html

- they also use edge kinds, rather than basic block kinds
- they have exception handling edges

# Structural Graph Source

https://www.cs.virginia.edu/~up3f/cs3250/slides/3250meet18-structural-graph-source.pdf

nicely presented. they also break down basic block types as follows:
- nodes are basic blocks (sequences of statements that always execute together in sequence)
- edges: control flow branching between nodes (representing transfer of control)
- initial nodes: entry points of a method
- final nodes: exit points of a method (e.g. return or throw in Java)
- decision nodes: choices in control flow (e.g. if or switch-case blocks or conditions for loops in Java)
