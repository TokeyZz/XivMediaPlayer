using System;
using System.Reflection;

class Program {
    static void Main() {
        Assembly dalamud = Assembly.LoadFrom(@"C:\Users\stel9\AppData\Roaming\XIVLauncher\addon\Hooks\dev\Dalamud.dll");
        Type t = dalamud.GetType("Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap");
        foreach(var m in t.GetMethods()) {
            Console.WriteLine(m.Name);
        }
    }
}
