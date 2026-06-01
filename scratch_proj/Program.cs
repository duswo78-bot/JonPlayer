using System;
using System.Reflection;
using SoundTouch;

class Program {
    static void Main() {
        try {
            var asm = System.Reflection.Assembly.Load("SoundTouch.Net");
            foreach(var type in asm.GetExportedTypes()) {
                if (type.Name == "SoundTouchProcessor") {
                    Console.WriteLine("Type: " + type.Name);
                    foreach(var m in type.GetMethods()) {
                        if (m.Name.Contains("Put") || m.Name.Contains("Receive")) {
                            var p = string.Join(", ", Array.ConvertAll(m.GetParameters(), x => x.ParameterType.Name));
                            Console.WriteLine($" - {m.Name}({p})");
                        }
                    }
                }
            }
        } catch (Exception ex) {
            Console.WriteLine(ex);
        }
    }
}
