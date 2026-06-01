using System;
using System.Reflection;
using Vortice.D3DCompiler;

class Program {
    static void Main() {
        foreach (var m in typeof(Compiler).GetMethods()) {
            if (m.Name == "Compile") {
                Console.Write("Compile(");
                var p = m.GetParameters();
                for (int i=0; i<p.Length; i++) {
                    Console.Write((p[i].IsOut ? "out " : "") + p[i].ParameterType.Name + " " + p[i].Name + (i < p.Length-1 ? ", " : ""));
                }
                Console.WriteLine(") -> " + m.ReturnType.Name);
            }
        }
    }
}
